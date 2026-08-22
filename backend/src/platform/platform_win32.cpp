#include "platform.h"
#include "../logger.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <sddl.h>
#include <aclapi.h>
#include <wbemidl.h>
#include <taskschd.h>
#include <comdef.h>
#include <tlhelp32.h>

#include <cstdlib>
#include <iomanip>
#include <iostream>
#include <memory>
#include <sstream>
#include <mutex>
#include <shlobj.h>
#include <shellapi.h>
#include <gdiplus.h>
#include <dxgi.h>

// =====================================================================
// GDI+ Helper for loading PNG as icon
// =====================================================================

static HICON load_png_as_icon(const wchar_t* path, int size = 16) {
    Gdiplus::Bitmap* bitmap = new Gdiplus::Bitmap(path);
    if (bitmap->GetLastStatus() != Gdiplus::Ok) {
        delete bitmap;
        return nullptr;
    }

    // Scale to requested size
    Gdiplus::Bitmap* scaled = new Gdiplus::Bitmap(size, size, PixelFormat32bppARGB);
    Gdiplus::Graphics graphics(scaled);
    graphics.SetInterpolationMode(Gdiplus::InterpolationModeHighQualityBicubic);
    graphics.DrawImage(bitmap, 0, 0, size, size);

    HICON hIcon = nullptr;
    scaled->GetHICON(&hIcon);

    delete scaled;
    delete bitmap;
    return hIcon;
}

// =====================================================================
// WMI COM Infrastructure for EC Read/Write (thread-local)
// =====================================================================
// WMI COM requires per-thread apartment initialization. Each thread that
// calls ec_read/ec_write must initialize its own COM apartment and obtain
// its own IWbemServices connection. These thread-local variables track
// the WMI connection state per thread.

struct WmiThreadState {
    bool initialized = false;
    bool failed = false;  // Cache failure to prevent log spam
    IWbemServices* service = nullptr;
    IWbemClassObject* uma_instance = nullptr;
    IWbemClassObject* uma_class = nullptr;
    BSTR uma_path = nullptr;
};

static thread_local WmiThreadState g_wmi_state;

// Initialize WMI COM for the current thread
static bool wmi_init_thread() {
    if (g_wmi_state.initialized) return true;
    if (g_wmi_state.failed) return false;  // Previously failed, don't retry

    HRESULT hr;

    // Initialize COM apartment (multithreaded)
    hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) {
        log_error("[WMI] CoInitializeEx failed: 0x" + std::to_string(hr));
        g_wmi_state.failed = true;
        return false;
    }

    // Initialize COM security (ignore if already initialized)
    hr = CoInitializeSecurity(nullptr, -1, nullptr, nullptr,
                              RPC_C_AUTHN_LEVEL_DEFAULT,
                              RPC_C_IMP_LEVEL_IMPERSONATE,
                              nullptr, EOAC_NONE, nullptr);
    if (FAILED(hr) && hr != RPC_E_TOO_LATE) {
        log_error("[WMI] CoInitializeSecurity failed: 0x" + std::to_string(hr));
        CoUninitialize();
        g_wmi_state.failed = true;
        return false;
    }

    // Create WMI locator
    IWbemLocator* locator = nullptr;
    hr = CoCreateInstance(CLSID_WbemLocator, nullptr, CLSCTX_INPROC_SERVER,
                          IID_IWbemLocator, reinterpret_cast<void**>(&locator));
    if (FAILED(hr)) {
        log_error("[WMI] CoCreateInstance(WbemLocator) failed: 0x" + std::to_string(hr));
        CoUninitialize();
        g_wmi_state.failed = true;
        return false;
    }

    // Connect to ROOT\WMI namespace
    hr = locator->ConnectServer((BSTR)L"ROOT\\WMI", nullptr, nullptr, nullptr, 0, nullptr, nullptr, &g_wmi_state.service);
    locator->Release();
    if (FAILED(hr)) {
        log_error("[WMI] ConnectServer(ROOT\\WMI) failed: 0x" + std::to_string(hr));
        CoUninitialize();
        g_wmi_state.failed = true;
        return false;
    }

    // Set proxy security
    hr = CoSetProxyBlanket(g_wmi_state.service, RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE, nullptr,
                           RPC_C_AUTHN_LEVEL_CALL, RPC_C_IMP_LEVEL_IMPERSONATE, nullptr, EOAC_NONE);
    if (FAILED(hr)) {
        log_error("[WMI] CoSetProxyBlanket failed: 0x" + std::to_string(hr));
        g_wmi_state.service->Release();
        g_wmi_state.service = nullptr;
        CoUninitialize();
        g_wmi_state.failed = true;
        return false;
    }

    // Find UMAInterface SETUMAWMI instance
    IEnumWbemClassObject* enumerator = nullptr;
    hr = g_wmi_state.service->ExecQuery((BSTR)L"WQL", (BSTR)L"SELECT * FROM UMAInterface",
                                        WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY,
                                        nullptr, &enumerator);
    if (FAILED(hr)) {
        log_error("[WMI] ExecQuery(UMAInterface) failed: 0x" + std::to_string(hr));
        g_wmi_state.service->Release();
        g_wmi_state.service = nullptr;
        CoUninitialize();
        g_wmi_state.failed = true;
        return false;
    }

    ULONG result_count = 0;
    while (enumerator) {
        IWbemClassObject* obj = nullptr;
        hr = enumerator->Next(WBEM_INFINITE, 1, &obj, &result_count);
        if (FAILED(hr) || result_count == 0) break;

        VARIANT instance_name;
        VariantInit(&instance_name);
        hr = obj->Get(L"InstanceName", 0, &instance_name, nullptr, nullptr);
        if (SUCCEEDED(hr) && instance_name.vt == VT_BSTR && instance_name.bstrVal) {
            if (wcsstr(instance_name.bstrVal, L"SETUMAWMI")) {
                g_wmi_state.uma_instance = obj;
                VariantClear(&instance_name);
                break;
            }
            VariantClear(&instance_name);
        }
        obj->Release();
    }
    enumerator->Release();

    if (!g_wmi_state.uma_instance) {
        log_error("[WMI] UMAInterface SETUMAWMI instance not found");
        g_wmi_state.service->Release();
        g_wmi_state.service = nullptr;
        CoUninitialize();
        g_wmi_state.failed = true;
        return false;
    }

    // Get __PATH for ExecMethod calls
    VARIANT path_variant;
    VariantInit(&path_variant);
    hr = g_wmi_state.uma_instance->Get(L"__PATH", 0, &path_variant, nullptr, nullptr);
    if (SUCCEEDED(hr) && path_variant.vt == VT_BSTR && path_variant.bstrVal) {
        g_wmi_state.uma_path = SysAllocString(path_variant.bstrVal);
    }
    VariantClear(&path_variant);

    if (!g_wmi_state.uma_path) {
        log_error("[WMI] Failed to get __PATH");
        g_wmi_state.uma_instance->Release();
        g_wmi_state.uma_instance = nullptr;
        g_wmi_state.service->Release();
        g_wmi_state.service = nullptr;
        CoUninitialize();
        g_wmi_state.failed = true;
        return false;
    }

    // Get class object for method definitions
    hr = g_wmi_state.service->GetObject((BSTR)L"UMAInterface", 0, nullptr, &g_wmi_state.uma_class, nullptr);
    if (FAILED(hr) || !g_wmi_state.uma_class) {
        log_error("[WMI] GetObject(UMAInterface class) failed: 0x" + std::to_string(hr));
        SysFreeString(g_wmi_state.uma_path);
        g_wmi_state.uma_path = nullptr;
        g_wmi_state.uma_instance->Release();
        g_wmi_state.uma_instance = nullptr;
        g_wmi_state.service->Release();
        g_wmi_state.service = nullptr;
        CoUninitialize();
        g_wmi_state.failed = true;
        return false;
    }

    g_wmi_state.initialized = true;
    return true;
}

// Cleanup WMI COM for the current thread
static void wmi_cleanup_thread() {
    if (!g_wmi_state.initialized && !g_wmi_state.failed) return;

    if (g_wmi_state.uma_class) {
        g_wmi_state.uma_class->Release();
        g_wmi_state.uma_class = nullptr;
    }
    if (g_wmi_state.uma_instance) {
        g_wmi_state.uma_instance->Release();
        g_wmi_state.uma_instance = nullptr;
    }
    if (g_wmi_state.uma_path) {
        SysFreeString(g_wmi_state.uma_path);
        g_wmi_state.uma_path = nullptr;
    }
    if (g_wmi_state.service) {
        g_wmi_state.service->Release();
        g_wmi_state.service = nullptr;
    }

    CoUninitialize();
    g_wmi_state.initialized = false;
    g_wmi_state.failed = false;
}

// =====================================================================
// PawnIO Constants
// =====================================================================

static constexpr uint32_t PIO_DEVICE_TYPE   = 41394u << 16;  // 0xA1B20000
static constexpr uint32_t IOCTL_LOAD_BINARY = PIO_DEVICE_TYPE | (0x821 << 2);  // 0xA1B22084
static constexpr uint32_t IOCTL_EXECUTE_FN  = PIO_DEVICE_TYPE | (0x841 << 2);  // 0xA1B22104
static constexpr int      FN_NAME_LENGTH    = 32;

// =====================================================================
// SMU Mailbox Constants (Strix Halo)
// =====================================================================

// MP1 mailbox addresses
static constexpr uint32_t MP1_CMD   = 0x03B10928;
static constexpr uint32_t MP1_RSP   = 0x03B10978;
static constexpr uint32_t MP1_ARGS  = 0x03B10998;

// RSMU/PSMU mailbox addresses
static constexpr uint32_t RSMU_CMD  = 0x03B10A20;
static constexpr uint32_t RSMU_RSP  = 0x03B10A80;
static constexpr uint32_t RSMU_ARGS = 0x03B10A88;

// SMU protocol constants
static constexpr uint32_t SMU_OK          = 0x01;
static constexpr int      SMU_RETRIES_MAX = 8096;

// =====================================================================
// Super I/O Constants (IT87 for EC RAM writes)
// =====================================================================

static constexpr uint16_t SIO_REG_PORT    = 0x4E;
static constexpr uint16_t SIO_DAT_PORT    = 0x4F;
static constexpr uint16_t EC_BATTERY_LIMIT = 0x04A3;

// =====================================================================
// ADLX GPU Telemetry (Official AMD ADLX SDK)
// =====================================================================
#include "ADLXHelper.h"
#include "IPerformanceMonitoring3.h"
#include "ISystem3.h"

using namespace adlx;

// =====================================================================
// Task Scheduler Constants
// =====================================================================

static constexpr const wchar_t* TASK_NAME = L"XmaX Service";
static constexpr const wchar_t* TASK_FOLDER = L"\\";

// Read EC register via WMI UMAInterface
static Result<uint8_t> ec_read_impl(uint16_t reg) {
    if (!wmi_init_thread()) {
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    IWbemClassObject* in_def = nullptr;
    IWbemClassObject* in_instance = nullptr;
    IWbemClassObject* out_instance = nullptr;
    HRESULT hr;

    // Get method definition
    hr = g_wmi_state.uma_class->GetMethod(L"GetEcValue", 0, &in_def, nullptr);
    if (FAILED(hr) || !in_def) {
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    // Spawn instance for input parameters
    hr = in_def->SpawnInstance(0, &in_instance);
    if (FAILED(hr) || !in_instance) {
        in_def->Release();
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    // Set Index parameter (MUST be VT_I4, not VT_UI2)
    VARIANT index_variant;
    index_variant.vt = VT_I4;
    index_variant.lVal = static_cast<LONG>(reg);
    hr = in_instance->Put(L"Index", 0, &index_variant, 0);
    if (FAILED(hr)) {
        in_def->Release();
        in_instance->Release();
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    // Execute method
    hr = g_wmi_state.service->ExecMethod(g_wmi_state.uma_path, (BSTR)L"GetEcValue", 0, nullptr, in_instance, &out_instance, nullptr);
    in_def->Release();
    in_instance->Release();

    if (FAILED(hr) || !out_instance) {
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    // Get Data result
    VARIANT data_variant;
    VariantInit(&data_variant);
    hr = out_instance->Get(L"Data", 0, &data_variant, nullptr, nullptr);
    out_instance->Release();

    if (FAILED(hr)) {
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    // Extract value (handle different return types)
    uint8_t result = 0;
    if (data_variant.vt == VT_UI1) {
        result = data_variant.bVal;
    } else if (data_variant.vt == VT_I4) {
        result = static_cast<uint8_t>(data_variant.lVal & 0xFF);
    } else if (data_variant.vt == VT_UI4) {
        result = static_cast<uint8_t>(data_variant.ulVal & 0xFF);
    }
    VariantClear(&data_variant);

    return result;
}

// Write EC register via WMI UMAInterface
static Result<void> ec_write_impl(uint16_t reg, uint8_t val) {
    if (!wmi_init_thread()) {
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    IWbemClassObject* in_def = nullptr;
    IWbemClassObject* in_instance = nullptr;
    IWbemClassObject* out_instance = nullptr;
    HRESULT hr;

    // Get method definition
    hr = g_wmi_state.uma_class->GetMethod(L"SetEcValue", 0, &in_def, nullptr);
    if (FAILED(hr) || !in_def) {
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    // Spawn instance for input parameters
    hr = in_def->SpawnInstance(0, &in_instance);
    if (FAILED(hr) || !in_instance) {
        in_def->Release();
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    // Set Data parameter
    VARIANT data_variant;
    data_variant.vt = VT_UI1;
    data_variant.bVal = val;
    hr = in_instance->Put(L"Data", 0, &data_variant, 0);
    if (FAILED(hr)) {
        in_def->Release();
        in_instance->Release();
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    // Set Index parameter (MUST be VT_I4, not VT_UI2)
    VARIANT index_variant;
    index_variant.vt = VT_I4;
    index_variant.lVal = static_cast<LONG>(reg);
    hr = in_instance->Put(L"Index", 0, &index_variant, 0);
    if (FAILED(hr)) {
        in_def->Release();
        in_instance->Release();
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    // Execute method
    hr = g_wmi_state.service->ExecMethod(g_wmi_state.uma_path, (BSTR)L"SetEcValue", 0, nullptr, in_instance, &out_instance, nullptr);
    in_def->Release();
    in_instance->Release();

    if (FAILED(hr) || !out_instance) {
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    out_instance->Release();
    return {};
}

// Tray icon callback storage
struct TrayCallbacks {
    std::function<void()> on_left_click;
    std::function<void()> on_right_click;
};

// Window procedure for tray icon messages
static LRESULT CALLBACK TrayWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (msg == WM_USER + 1) {
        auto* callbacks = reinterpret_cast<TrayCallbacks*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (!callbacks) return 0;

        switch (LOWORD(lParam)) {
            case WM_LBUTTONUP:
                log_debug("[tray] Left click received");
                if (callbacks->on_left_click) {
                    callbacks->on_left_click();
                }
                return 0;

            case WM_RBUTTONUP:
                log_debug("[tray] Right click received");
                if (callbacks->on_right_click) {
                    callbacks->on_right_click();
                }
                return 0;
        }
    }

    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

class Win32Platform : public Platform {
public:
    Win32Platform() = default;
    ~Win32Platform() override {
        cleanup_task_scheduler();
        cleanup_adlx();
        cleanup_pawnio();
        if (gdiplus_token_) {
            Gdiplus::GdiplusShutdown(gdiplus_token_);
        }
        if (job_handle_) {
            CloseHandle(job_handle_);
        }
    }

    // Initialize all hardware connections (call once after construction)
    bool init_hardware() {
        bool success = true;

        // Initialize GDI+ for image loading (tray icon)
        Gdiplus::GdiplusStartupInput gdiplusStartupInput;
        if (Gdiplus::GdiplusStartup(&gdiplus_token_, &gdiplusStartupInput, nullptr) != Gdiplus::Ok) {
            log_warn("[Hardware] GDI+ initialization failed (tray icon will use fallback)");
        }

        // Initialize PawnIO driver and load blobs
        if (!init_pawnio()) {
            log_warn("[Hardware] PawnIO initialization failed (SMU and Super I/O writes will fail)");
            success = false;
        }

        // Initialize ADLX GPU telemetry
        if (!init_adlx()) {
            log_warn("[Hardware] ADLX initialization failed (GPU metrics will be unavailable)");
            success = false;
        }

        // Initialize Task Scheduler (lazy init on first use, but try to connect early)
        if (!init_task_scheduler()) {
            log_warn("[Hardware] Task Scheduler initialization failed (auto-start will be unavailable)");
            success = false;
        }

        return success;
    }

    // Initialize PawnIO driver and load blobs (call once at startup)
    bool init_pawnio() {
        if (pawnio_device_ != INVALID_HANDLE_VALUE) {
            return true;  // Already initialized
        }

        // Connect to PawnIO driver
        pawnio_device_ = CreateFileW(L"\\\\?\\GLOBALROOT\\Device\\PawnIO",
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, OPEN_EXISTING, 0, nullptr);

        if (pawnio_device_ == INVALID_HANDLE_VALUE) {
            pawnio_device_ = CreateFileW(L"\\\\.\\PawnIO",
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                nullptr, OPEN_EXISTING, 0, nullptr);
        }

        if (pawnio_device_ == INVALID_HANDLE_VALUE) {
            log_error("[PawnIO] Cannot open PawnIO device (error " + std::to_string(GetLastError()) + ")");
            return false;
        }

        // Create serialization mutex
        pawnio_mutex_ = CreateMutexW(nullptr, FALSE, L"Global\\Access_PCI");
        if (!pawnio_mutex_) {
            log_error("[PawnIO] Cannot create mutex (error " + std::to_string(GetLastError()) + ")");
            CloseHandle(pawnio_device_);
            pawnio_device_ = INVALID_HANDLE_VALUE;
            return false;
        }

        // Resolve blob paths relative to executable directory
        wchar_t exe_path[MAX_PATH];
        DWORD len = GetModuleFileNameW(nullptr, exe_path, MAX_PATH);
        if (len == 0 || len >= MAX_PATH) {
            log_error("[PawnIO] Cannot get executable path");
            CloseHandle(pawnio_mutex_);
            pawnio_mutex_ = nullptr;
            CloseHandle(pawnio_device_);
            pawnio_device_ = INVALID_HANDLE_VALUE;
            return false;
        }

        std::filesystem::path exe_dir = std::filesystem::path(exe_path).parent_path();
        std::filesystem::path smu_blob = exe_dir / "lib" / "RyzenSMU.bin";
        std::filesystem::path lpcio_blob = exe_dir / "lib" / "LpcIO.bin";

        // Load RyzenSMU.bin
        std::string smu_path = smu_blob.string();
        if (pawnio_load_blob(smu_path.c_str())) {
            pawnio_smu_loaded_ = true;
        } else {
            log_warn("[PawnIO] RyzenSMU.bin not loaded (SMU writes will fail)");
        }

        // Load LpcIO.bin
        std::string lpcio_path = lpcio_blob.string();
        if (pawnio_load_blob(lpcio_path.c_str())) {
            pawnio_lpcio_loaded_ = true;
        } else {
            log_warn("[PawnIO] LpcIO.bin not loaded (Super I/O writes will fail)");
        }

        return true;
    }

private:
    // Load a binary blob into PawnIO
    bool pawnio_load_blob(const char* path) {
        FILE* f = fopen(path, "rb");
        if (!f) {
            log_error("[PawnIO] Cannot open blob: " + std::string(path));
            return false;
        }
        fseek(f, 0, SEEK_END);
        long size = ftell(f);
        fseek(f, 0, SEEK_SET);

        auto data = std::make_unique<uint8_t[]>(size);
        size_t read = fread(data.get(), 1, size, f);
        fclose(f);

        if (read != static_cast<size_t>(size)) {
            log_error("[PawnIO] Failed to read blob: " + std::string(path));
            return false;
        }

        DWORD bytes_returned = 0;
        BOOL ok = DeviceIoControl(pawnio_device_, IOCTL_LOAD_BINARY,
            data.get(), static_cast<DWORD>(size), nullptr, 0, &bytes_returned, nullptr);

        if (!ok) {
            DWORD error = GetLastError();
            // ERROR_ALREADY_INITIALIZED (1247) means blob is already loaded by another process
            // This is fine - we can still call functions from it
            if (error == ERROR_ALREADY_INITIALIZED) {
                return true;
            }
            log_error("[PawnIO] Blob load failed (error " + std::to_string(error) + "): " + std::string(path));
            return false;
        }

        return true;
    }

    // Cleanup PawnIO resources
    void cleanup_pawnio() {
        if (pawnio_mutex_) {
            CloseHandle(pawnio_mutex_);
            pawnio_mutex_ = nullptr;
        }
        if (pawnio_device_ != INVALID_HANDLE_VALUE) {
            CloseHandle(pawnio_device_);
            pawnio_device_ = INVALID_HANDLE_VALUE;
        }
        pawnio_smu_loaded_ = false;
        pawnio_lpcio_loaded_ = false;
    }

public:
    // Execute a PawnIO function
    bool pawnio_execute(const char* func_name, const uint64_t* in_args, int in_count,
                        uint64_t* out_args, int out_count) {
        if (pawnio_device_ == INVALID_HANDLE_VALUE) {
            return false;
        }

        int in_buf_size = FN_NAME_LENGTH + (in_count * 8);
        auto in_buf = std::make_unique<uint8_t[]>(in_buf_size);
        std::memset(in_buf.get(), 0, in_buf_size);

        int name_len = static_cast<int>(std::strlen(func_name));
        if (name_len > FN_NAME_LENGTH - 1) name_len = FN_NAME_LENGTH - 1;
        std::memcpy(in_buf.get(), func_name, name_len);

        for (int i = 0; i < in_count; i++) {
            std::memcpy(in_buf.get() + FN_NAME_LENGTH + (i * 8), &in_args[i], 8);
        }

        int out_buf_size = out_count * 8;
        auto out_buf = std::make_unique<uint8_t[]>(out_buf_size > 0 ? out_buf_size : 1);
        std::memset(out_buf.get(), 0, out_buf_size > 0 ? out_buf_size : 1);

        DWORD bytes_returned = 0;
        BOOL ok = DeviceIoControl(pawnio_device_, IOCTL_EXECUTE_FN,
            in_buf.get(), static_cast<DWORD>(in_buf_size),
            out_buf.get(), static_cast<DWORD>(out_buf_size),
            &bytes_returned, nullptr);

        if (ok && out_args && bytes_returned > 0) {
            int elems = static_cast<int>(bytes_returned / 8);
            if (elems > out_count) elems = out_count;
            std::memcpy(out_args, out_buf.get(), elems * 8);
        }

        return ok != 0;
    }

    // Read SMU register via PawnIO
    bool read_smu_reg(uint32_t addr, uint32_t* value) {
        uint64_t in[1] = { addr };
        uint64_t out[1] = { 0 };
        if (!pawnio_execute("ioctl_read_smu_register", in, 1, out, 1))
            return false;
        *value = static_cast<uint32_t>(out[0]);
        return true;
    }

    // Write SMU register via PawnIO
    bool write_smu_reg(uint32_t addr, uint32_t value) {
        uint64_t in[2] = { addr, value };
        return pawnio_execute("ioctl_write_smu_register", in, 2, nullptr, 0);
    }

    // Wait for SMU mailbox to be ready (RSP register != 0)
    bool wait_mailbox_ready(uint32_t rsp_addr) {
        uint32_t val = 0;
        int retries = SMU_RETRIES_MAX;
        bool ok;
        do {
            ok = read_smu_reg(rsp_addr, &val);
        } while ((!ok || val == 0) && --retries > 0);
        return retries > 0 && val > 0;
    }

    // Send command to SMU mailbox with full protocol
    bool send_mailbox(uint32_t cmd_addr, uint32_t rsp_addr, uint32_t args_addr,
                      uint32_t command, const uint32_t* args, int arg_count,
                      uint32_t* response, int resp_count) {
        // Acquire serialization mutex
        if (!pawnio_mutex_) return false;
        DWORD wait_result = WaitForSingleObject(pawnio_mutex_, 5000);
        if (wait_result != WAIT_OBJECT_0 && wait_result != WAIT_ABANDONED) {
            return false;
        }

        // Wait for mailbox ready
        if (!wait_mailbox_ready(rsp_addr)) {
            ReleaseMutex(pawnio_mutex_);
            return false;
        }

        // Clear response register
        write_smu_reg(rsp_addr, 0);

        // Write arguments (up to 6, each at args_addr + i*4)
        for (int i = 0; i < 6; i++) {
            uint32_t val = (args && i < arg_count) ? args[i] : 0;
            if (!write_smu_reg(args_addr + static_cast<uint32_t>(i * 4), val)) {
                ReleaseMutex(pawnio_mutex_);
                return false;
            }
        }

        // Write command to trigger execution
        if (!write_smu_reg(cmd_addr, command)) {
            ReleaseMutex(pawnio_mutex_);
            return false;
        }

        // Wait for response
        if (!wait_mailbox_ready(rsp_addr)) {
            ReleaseMutex(pawnio_mutex_);
            return false;
        }

        // Read response status
        uint32_t rsp_status = 0;
        read_smu_reg(rsp_addr, &rsp_status);
        if (rsp_status != SMU_OK) {
            ReleaseMutex(pawnio_mutex_);
            return false;
        }

        // Read response arguments (up to 6)
        for (int i = 0; i < 6; i++) {
            uint32_t val = 0;
            read_smu_reg(args_addr + static_cast<uint32_t>(i * 4), &val);
            if (response && i < resp_count) response[i] = val;
        }

        ReleaseMutex(pawnio_mutex_);
        return true;
    }

    // Super I/O port output via PawnIO
    void pio_outb(uint16_t port, uint8_t val) {
        uint64_t in[2] = { port, val };
        pawnio_execute("ioctl_pio_outb", in, 2, nullptr, 0);
    }

    // Select PawnIO slot
    void select_slot(int slot) {
        uint64_t in[1] = { static_cast<uint64_t>(slot) };
        pawnio_execute("ioctl_select_slot", in, 1, nullptr, 0);
    }

    // Enter IT87 configuration mode (unlock sequence)
    void it87_enter() {
        pio_outb(SIO_REG_PORT, 0x87);
        pio_outb(SIO_REG_PORT, 0x01);
        pio_outb(SIO_REG_PORT, 0x55);
        pio_outb(SIO_REG_PORT, 0xAA);
    }

    // Exit IT87 configuration mode (no-op for port 0x4E)
    void it87_exit() {
        // No-op for port 0x4E per IT87 spec
    }

    // Write to EC RAM via Super I/O indexed registers
    void ecram_write(uint16_t addr, uint8_t data) {
        uint8_t hi = static_cast<uint8_t>((addr >> 8) & 0xFF);
        uint8_t lo = static_cast<uint8_t>(addr & 0xFF);

        // Register 0x11 = address high byte
        pio_outb(SIO_REG_PORT, 0x2E);
        pio_outb(SIO_DAT_PORT, 0x11);
        pio_outb(SIO_REG_PORT, 0x2F);
        pio_outb(SIO_DAT_PORT, hi);

        // Register 0x10 = address low byte
        pio_outb(SIO_REG_PORT, 0x2E);
        pio_outb(SIO_DAT_PORT, 0x10);
        pio_outb(SIO_REG_PORT, 0x2F);
        pio_outb(SIO_DAT_PORT, lo);

        // Register 0x12 = data
        pio_outb(SIO_REG_PORT, 0x2E);
        pio_outb(SIO_DAT_PORT, 0x12);
        pio_outb(SIO_REG_PORT, 0x2F);
        pio_outb(SIO_DAT_PORT, data);
    }

    // ===== Transport =====

    auto listen() -> Result<TransportServer> override {
        // Create security descriptor for current user only
        PSID current_user_sid = nullptr;
        PACL acl = nullptr;
        SECURITY_DESCRIPTOR sd;

        HANDLE process_token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &process_token)) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        DWORD token_info_len = 0;
        GetTokenInformation(process_token, TokenUser, nullptr, 0, &token_info_len);
        if (token_info_len == 0) {
            CloseHandle(process_token);
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        auto token_buffer = std::make_unique<BYTE[]>(token_info_len);
        if (!GetTokenInformation(process_token, TokenUser, token_buffer.get(), token_info_len, &token_info_len)) {
            CloseHandle(process_token);
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        CloseHandle(process_token);

        TOKEN_USER* token_user = reinterpret_cast<TOKEN_USER*>(token_buffer.get());
        current_user_sid = token_user->User.Sid;

        // Create ACL allowing only current user
        EXPLICIT_ACCESSW ea{};
        ea.grfAccessPermissions = FILE_GENERIC_READ | FILE_GENERIC_WRITE;
        ea.grfAccessMode = SET_ACCESS;
        ea.grfInheritance = NO_INHERITANCE;
        ea.Trustee.TrusteeForm = TRUSTEE_IS_SID;
        ea.Trustee.ptstrName = reinterpret_cast<LPWSTR>(current_user_sid);

        if (SetEntriesInAclW(1, &ea, nullptr, &acl) != ERROR_SUCCESS) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        if (!InitializeSecurityDescriptor(&sd, SECURITY_DESCRIPTOR_REVISION)) {
            LocalFree(acl);
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        if (!SetSecurityDescriptorDacl(&sd, TRUE, acl, FALSE)) {
            LocalFree(acl);
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        SECURITY_ATTRIBUTES sa{};
        sa.nLength = sizeof(SECURITY_ATTRIBUTES);
        sa.lpSecurityDescriptor = &sd;
        sa.bInheritHandle = FALSE;

        // Create named pipe (byte-stream mode, overlapped for immediate data delivery).
        // FILE_FLAG_OVERLAPPED ensures WriteFile pushes data to the pipe buffer
        // immediately — without it, data sits in the server's output buffer until
        // the client initiates a read/write cycle, causing multi-second latency.
        HANDLE pipe = CreateNamedPipeW(
            L"\\\\.\\pipe\\xmaxsvc",
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_WAIT,
            1,  // Max instances
            65536,  // Output buffer size (64KB)
            65536,  // Input buffer size (64KB)
            0,  // Default timeout
            &sa
        );

        LocalFree(acl);

        if (pipe == INVALID_HANDLE_VALUE) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        TransportServer server;
        server.handle = pipe;
        return server;
    }

    auto verify_peer(PeerId peer) -> Result<PeerInfo> override {
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, static_cast<DWORD>(peer.process_id));
        if (!process) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        wchar_t path_buffer[MAX_PATH];
        DWORD path_size = MAX_PATH;
        if (!QueryFullProcessImageNameW(process, 0, path_buffer, &path_size)) {
            CloseHandle(process);
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        CloseHandle(process);

        PeerInfo info;
        info.executable_path = std::filesystem::path(path_buffer).string();

        // Verify it's XmaX.exe
        std::filesystem::path client_path(path_buffer);
        std::string filename = client_path.filename().string();

        // Verify client is XmaX.exe in the same directory
        wchar_t self_buffer[MAX_PATH];
        DWORD self_size = MAX_PATH;
        if (QueryFullProcessImageNameW(GetCurrentProcess(), 0, self_buffer, &self_size)) {
            std::filesystem::path self_dir = std::filesystem::path(self_buffer).parent_path();
            std::filesystem::path client_dir = client_path.parent_path();
            info.verified = (filename == "XmaX.exe" && client_dir == self_dir);
            log_debug("[verify_peer] client: " + client_path.string()
                      + " (filename=" + filename + ")");
            log_debug("[verify_peer] self_dir: " + self_dir.string());
            log_debug("[verify_peer] client_dir: " + client_dir.string());
            log_debug("[verify_peer] verified: " + std::string(info.verified ? "true" : "false"));
        } else {
            info.verified = false;
            log_error("[verify_peer] QueryFullProcessImageNameW(self) failed");
        }

        return info;
    }

    auto accept_connection(TransportServer& server) -> Result<PeerId> override {
        HANDLE pipe = static_cast<HANDLE>(server.handle);
        if (!pipe || pipe == INVALID_HANDLE_VALUE) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        // Overlapped ConnectNamedPipe with a timed wait.
        // The 1-second timeout lets the caller re-check its running flag between
        // attempts, preventing a shutdown hang if close_server() races with the
        // creation of a new pipe handle (CancelIoEx would target the old handle).
        OVERLAPPED ov{};
        ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!ov.hEvent) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        BOOL connected = ConnectNamedPipe(pipe, &ov);
        if (!connected) {
            DWORD error = GetLastError();
            if (error == ERROR_PIPE_CONNECTED) {
                // Client already connected — success
                CloseHandle(ov.hEvent);
            } else if (error == ERROR_IO_PENDING) {
                // Wait for connection or cancellation (close_server calls CancelIoEx).
                // Timed wait: on timeout, cancel the pending operation and return
                // an error so the caller can check its running flag and retry.
                DWORD wait = WaitForSingleObject(ov.hEvent, 1000);
                if (wait == WAIT_TIMEOUT) {
                    CancelIoEx(pipe, &ov);
                    DWORD discard = 0;
                    GetOverlappedResult(pipe, &ov, &discard, FALSE);  // reap the cancelled op
                    CloseHandle(ov.hEvent);
                    return std::unexpected(ErrorCode::HardwareBusy);
                }
                CloseHandle(ov.hEvent);
                if (wait != WAIT_OBJECT_0) {
                    return std::unexpected(ErrorCode::HardwareBusy);
                }
                DWORD bytes = 0;
                if (!GetOverlappedResult(pipe, &ov, &bytes, FALSE)) {
                    return std::unexpected(ErrorCode::HardwareBusy);
                }
            } else {
                CloseHandle(ov.hEvent);
                return std::unexpected(ErrorCode::HardwareBusy);
            }
        } else {
            CloseHandle(ov.hEvent);
        }

        // Get client process ID
        DWORD client_pid = 0;
        if (!GetNamedPipeClientProcessId(pipe, &client_pid)) {
            DisconnectNamedPipe(pipe);
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        PeerId peer;
        peer.process_id = client_pid;
        return peer;
    }

    auto read_data(PeerId peer, char* buffer, size_t size) -> Result<size_t> override {
        // For simplicity, we'll use the pipe handle stored in TransportServer
        // In a real implementation, we'd need to track per-peer handles
        // This is a placeholder - actual implementation will be in transport.cpp
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    auto write_data(PeerId peer, const char* data, size_t size) -> Result<void> override {
        // Placeholder - actual implementation will be in transport.cpp
        return std::unexpected(ErrorCode::HardwareBusy);
    }

    void close_connection(PeerId peer) override {
        // Placeholder - actual implementation will be in transport.cpp
    }

    void close_server(TransportServer& server) override {
        HANDLE pipe = static_cast<HANDLE>(server.handle);
        if (pipe && pipe != INVALID_HANDLE_VALUE) {
            // Cancel any pending overlapped operations (ConnectNamedPipe, ReadFile)
            // so blocked threads unblock promptly.
            CancelIoEx(pipe, nullptr);
            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
            server.handle = nullptr;
        }
    }

    // ===== Pipe I/O =====

    auto pipe_read(TransportServer& server, char* buffer, size_t size) -> Result<size_t> override {
        HANDLE pipe = static_cast<HANDLE>(server.handle);
        if (!pipe || pipe == INVALID_HANDLE_VALUE) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        OVERLAPPED ov{};
        ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!ov.hEvent) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        DWORD bytes_read = 0;
        BOOL success = ReadFile(pipe, buffer, static_cast<DWORD>(size), &bytes_read, &ov);
        if (success) {
            CloseHandle(ov.hEvent);
            return static_cast<size_t>(bytes_read);
        }

        DWORD err = GetLastError();
        if (err == ERROR_BROKEN_PIPE || err == ERROR_PIPE_NOT_CONNECTED) {
            CloseHandle(ov.hEvent);
            return std::unexpected(ErrorCode::SensorUnavailable);  // Client disconnected
        }
        if (err != ERROR_IO_PENDING) {
            CloseHandle(ov.hEvent);
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        // Wait for read to complete (unblocked by close_server's CancelIoEx)
        if (!GetOverlappedResult(pipe, &ov, &bytes_read, TRUE)) {
            DWORD read_err = GetLastError();
            CloseHandle(ov.hEvent);
            if (read_err == ERROR_BROKEN_PIPE || read_err == ERROR_PIPE_NOT_CONNECTED
                || read_err == ERROR_OPERATION_ABORTED) {
                return std::unexpected(ErrorCode::SensorUnavailable);
            }
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        CloseHandle(ov.hEvent);
        return static_cast<size_t>(bytes_read);
    }

    auto pipe_write(TransportServer& server, const char* data, size_t size) -> Result<void> override {
        HANDLE pipe = static_cast<HANDLE>(server.handle);
        if (!pipe || pipe == INVALID_HANDLE_VALUE) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        OVERLAPPED ov{};
        ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!ov.hEvent) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        DWORD bytes_written = 0;
        BOOL success = WriteFile(pipe, data, static_cast<DWORD>(size), &bytes_written, &ov);
        if (success) {
            CloseHandle(ov.hEvent);
            if (bytes_written != static_cast<DWORD>(size)) {
                return std::unexpected(ErrorCode::HardwareBusy);
            }
            return {};
        }

        DWORD err = GetLastError();
        if (err != ERROR_IO_PENDING) {
            CloseHandle(ov.hEvent);
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        // Wait for write to complete — overlapped WriteFile delivers data to the
        // pipe buffer immediately, no FlushFileBuffers needed.
        if (!GetOverlappedResult(pipe, &ov, &bytes_written, TRUE)) {
            CloseHandle(ov.hEvent);
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        CloseHandle(ov.hEvent);
        if (bytes_written != static_cast<DWORD>(size)) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        return {};
    }

    void pipe_flush(TransportServer& server) override {
        // No-op: overlapped WriteFile delivers data to the pipe buffer immediately.
        // FlushFileBuffers is not needed (and blocks indefinitely on named pipes).
    }

    void pipe_disconnect(TransportServer& server) override {
        HANDLE pipe = static_cast<HANDLE>(server.handle);
        if (pipe && pipe != INVALID_HANDLE_VALUE) {
            DisconnectNamedPipe(pipe);
        }
    }

    // ===== Hardware: EC (Embedded Controller) =====

    auto ec_read(uint16_t reg) -> Result<uint8_t> override {
        return ec_read_impl(reg);
    }

    auto ec_write(uint16_t reg, uint8_t val) -> Result<void> override {
        return ec_write_impl(reg, val);
    }

    // ===== Hardware: SMU (System Management Unit) =====

    auto smu_send(uint32_t msg, uint32_t arg) -> Result<uint32_t> override {
        // Check if PawnIO is initialized and SMU blob is loaded
        if (pawnio_device_ == INVALID_HANDLE_VALUE || !pawnio_smu_loaded_) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        // Send command via MP1 mailbox with single argument
        uint32_t args[1] = { arg };
        uint32_t response[1] = { 0 };

        if (!send_mailbox(MP1_CMD, MP1_RSP, MP1_ARGS, msg, args, 1, response, 1)) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        return response[0];
    }

    // ===== Hardware: Charge Limit (Super I/O) =====

    auto charge_limit_write(uint8_t percent) -> Result<void> override {
        // Check if PawnIO is initialized and LpcIO blob is loaded
        if (pawnio_device_ == INVALID_HANDLE_VALUE || !pawnio_lpcio_loaded_) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        // Acquire serialization mutex
        if (!pawnio_mutex_) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }
        DWORD wait_result = WaitForSingleObject(pawnio_mutex_, 5000);
        if (wait_result != WAIT_OBJECT_0 && wait_result != WAIT_ABANDONED) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        // Write charge limit via Super I/O
        select_slot(1);
        it87_enter();
        ecram_write(EC_BATTERY_LIMIT, percent);
        it87_exit();

        ReleaseMutex(pawnio_mutex_);
        return {};
    }

    // ===== GPU Telemetry =====
    // (gpu_metrics implemented in ADLX section below)

    // ===== Process Management =====

    // Helper to enable a privilege on the current process token
    static bool enable_privilege(const wchar_t* privilege_name) {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token)) {
            return false;
        }

        LUID luid{};
        if (!LookupPrivilegeValueW(nullptr, privilege_name, &luid)) {
            CloseHandle(token);
            return false;
        }

        TOKEN_PRIVILEGES tp{};
        tp.PrivilegeCount = 1;
        tp.Privileges[0].Luid = luid;
        tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;

        bool result = AdjustTokenPrivileges(token, FALSE, &tp, sizeof(tp), nullptr, nullptr) != 0;
        CloseHandle(token);
        return result;
    }

    auto spawn_frontend(const std::filesystem::path& exe_path, bool debug) -> Result<ChildProcess> override {
        // Create Job Object if not already created
        if (!job_handle_) {
            HANDLE job = CreateJobObjectW(nullptr, nullptr);
            if (!job) {
                return std::unexpected(ErrorCode::HardwareBusy);
            }

            // Set JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE -- when backend exits,
            // the OS terminates all processes in the job (including frontend)
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION jeli{};
            jeli.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &jeli, sizeof(jeli))) {
                CloseHandle(job);
                return std::unexpected(ErrorCode::HardwareBusy);
            }

            // Assign backend process to the job
            if (!AssignProcessToJobObject(job, GetCurrentProcess())) {
                CloseHandle(job);
                return std::unexpected(ErrorCode::HardwareBusy);
            }

            job_handle_ = job;
        }

        std::wstring path_wide = exe_path.wstring();
        // Build command line: "path" [--debug]
        std::wstring cmd_line = L"\"" + path_wide + L"\"";
        if (debug) {
            cmd_line += L" --debug";
        }

        STARTUPINFOW si{};
        si.cb = sizeof(STARTUPINFOW);
        PROCESS_INFORMATION pi{};

        // Check if backend is elevated
        BOOL is_admin = FALSE;
        SID_IDENTIFIER_AUTHORITY nt_authority = SECURITY_NT_AUTHORITY;
        PSID admin_group = nullptr;
        if (AllocateAndInitializeSid(&nt_authority, 2,
                                      SECURITY_BUILTIN_DOMAIN_RID,
                                      DOMAIN_ALIAS_RID_ADMINS,
                                      0, 0, 0, 0, 0, 0, &admin_group)) {
            CheckTokenMembership(nullptr, admin_group, &is_admin);
            FreeSid(admin_group);
        }

        log_debug("[spawn_frontend] Backend elevated: " + std::string(is_admin ? "yes" : "no"));

        BOOL success = FALSE;

        if (is_admin) {
            // Backend is elevated - launch frontend at medium integrity (non-elevated)
            // by using the token from explorer.exe and setting integrity level to Medium

            DWORD explorer_pid = 0;
            HWND explorer_hwnd = FindWindowW(L"Progman", nullptr); // Desktop window
            if (explorer_hwnd) {
                GetWindowThreadProcessId(explorer_hwnd, &explorer_pid);
                log_debug("[spawn_frontend] Found explorer via Progman, PID: " + std::to_string(explorer_pid));
            }

            if (explorer_pid == 0) {
                // Fallback: find explorer.exe by name
                HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snapshot != INVALID_HANDLE_VALUE) {
                    PROCESSENTRY32W pe{};
                    pe.dwSize = sizeof(pe);
                    if (Process32FirstW(snapshot, &pe)) {
                        do {
                            if (_wcsicmp(pe.szExeFile, L"explorer.exe") == 0) {
                                explorer_pid = pe.th32ProcessID;
                                log_debug("[spawn_frontend] Found explorer via snapshot, PID: " + std::to_string(explorer_pid));
                                break;
                            }
                        } while (Process32NextW(snapshot, &pe));
                    }
                    CloseHandle(snapshot);
                }
            }

            if (explorer_pid != 0) {
                HANDLE explorer_process = OpenProcess(PROCESS_QUERY_INFORMATION, FALSE, explorer_pid);
                if (explorer_process) {
                    log_debug("[spawn_frontend] Opened explorer process");
                    HANDLE explorer_token = nullptr;
                    if (OpenProcessToken(explorer_process, TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT, &explorer_token)) {
                        log_debug("[spawn_frontend] Got explorer token");
                        HANDLE duplicated_token = nullptr;
                        if (DuplicateTokenEx(explorer_token, TOKEN_ALL_ACCESS, nullptr, SecurityImpersonation, TokenPrimary, &duplicated_token)) {
                            log_debug("[spawn_frontend] Duplicated token, calling CreateProcessWithTokenW");
                            success = CreateProcessWithTokenW(
                                duplicated_token,
                                0,
                                cmd_line.data(),
                                nullptr,
                                0,
                                nullptr,
                                nullptr,
                                &si,
                                &pi
                            );
                            if (!success) {
                                log_error("[spawn_frontend] CreateProcessWithTokenW failed: " + std::to_string(GetLastError()));
                            } else {
                                log_debug("[spawn_frontend] CreateProcessWithTokenW result: success");
                            }
                            CloseHandle(duplicated_token);
                        } else {
                            log_error("[spawn_frontend] DuplicateTokenEx failed: " + std::to_string(GetLastError()));
                        }
                        CloseHandle(explorer_token);
                    } else {
                        log_error("[spawn_frontend] OpenProcessToken failed: " + std::to_string(GetLastError()));
                    }
                    CloseHandle(explorer_process);
                } else {
                    log_error("[spawn_frontend] OpenProcess failed: " + std::to_string(GetLastError()));
                }
            } else {
                log_error("[spawn_frontend] Could not find explorer process");
            }

            if (!success) {
                // Fallback: use CreateProcessW (will inherit elevated token)
                log_debug("[spawn_frontend] Falling back to CreateProcessW");
                success = CreateProcessW(
                    nullptr,
                    cmd_line.data(),
                    nullptr,
                    nullptr,
                    FALSE,
                    0,
                    nullptr,
                    nullptr,
                    &si,
                    &pi
                );
            }
        } else {
            // Backend is not elevated - use normal CreateProcessW
            log_debug("[spawn_frontend] Using CreateProcessW (non-elevated)");
            success = CreateProcessW(
                nullptr,
                cmd_line.data(),
                nullptr,
                nullptr,
                FALSE,
                0,
                nullptr,
                nullptr,
                &si,
                &pi
            );
        }

        if (!success) {
            log_error("[spawn_frontend] Final CreateProcess failed: " + std::to_string(GetLastError()));
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        // Close thread handle, keep process handle
        CloseHandle(pi.hThread);

        // Assign child process to the Job Object
        if (job_handle_) {
            AssignProcessToJobObject(job_handle_, pi.hProcess);
        }

        ChildProcess child;
        child.pid = pi.dwProcessId;
        child.process_handle = pi.hProcess;

        log_debug("[spawn_frontend] Frontend spawned with PID: " + std::to_string(child.pid));

        return child;
    }

    auto show_window(ChildProcess& process, bool visible) -> Result<void> override {
        HWND target_hwnd = nullptr;

        // If we have a valid process handle, find window by PID
        if (process.process_handle != nullptr) {
            struct EnumData {
                DWORD pid;
                HWND hwnd;
            };

            EnumData data{static_cast<DWORD>(process.pid), nullptr};

            EnumWindows([](HWND hwnd, LPARAM lParam) -> BOOL {
                EnumData* data = reinterpret_cast<EnumData*>(lParam);
                DWORD window_pid = 0;
                GetWindowThreadProcessId(hwnd, &window_pid);
                // Match top-level windows only (no owner)
                if (window_pid == data->pid && GetWindow(hwnd, GW_OWNER) == nullptr) {
                    data->hwnd = hwnd;
                    return FALSE;
                }
                return TRUE;
            }, reinterpret_cast<LPARAM>(&data));

            target_hwnd = data.hwnd;
        }

        // Fallback: find window by title "XmaX"
        if (!target_hwnd) {
            target_hwnd = FindWindowW(nullptr, L"XmaX");
        }

        if (target_hwnd) {
            if (visible) {
                ShowWindow(target_hwnd, SW_SHOW);
                SetForegroundWindow(target_hwnd);
            } else {
                ShowWindow(target_hwnd, SW_HIDE);
            }
        }

        return {};
    }

    auto wait_for_process(ChildProcess& process) -> Result<int> override {
        HANDLE handle = static_cast<HANDLE>(process.process_handle);
        if (!handle) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        DWORD wait_result = WaitForSingleObject(handle, INFINITE);
        if (wait_result != WAIT_OBJECT_0) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        DWORD exit_code = 0;
        GetExitCodeProcess(handle, &exit_code);

        return static_cast<int>(exit_code);
    }

    void terminate_process(ChildProcess& process) override {
        HANDLE handle = static_cast<HANDLE>(process.process_handle);
        if (handle) {
            TerminateProcess(handle, 1);
            CloseHandle(handle);
            process.process_handle = nullptr;
        }
    }

    // ===== System =====

    auto tray_icon(TrayConfig config) -> Result<TrayHandle> override {
        // Create hidden window for tray messages
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(WNDCLASSEXW);
        wc.lpfnWndProc = TrayWndProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"XmaXTrayWindow";

        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        HWND hwnd = CreateWindowExW(
            0, L"XmaXTrayWindow", L"", 0, 0, 0, 0, 0,
            HWND_MESSAGE, nullptr, GetModuleHandleW(nullptr), nullptr
        );

        if (!hwnd) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        // Store callbacks in window user data
        auto* callbacks = new TrayCallbacks{config.on_left_click, config.on_right_click};
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(callbacks));

        // Load icon from assets/logo.png (relative to executable)
        wchar_t exe_path[MAX_PATH];
        GetModuleFileNameW(nullptr, exe_path, MAX_PATH);
        std::wstring logo_path = std::wstring(exe_path);
        size_t last_slash = logo_path.find_last_of(L"\\/");
        if (last_slash != std::wstring::npos) {
            logo_path = logo_path.substr(0, last_slash) + L"\\assets\\logo.png";
        }
        // Convert wstring to string for logging
        int wide_len = static_cast<int>(logo_path.size());
        int utf8_len = WideCharToMultiByte(CP_UTF8, 0, logo_path.c_str(), wide_len, nullptr, 0, nullptr, nullptr);
        std::string logo_path_str(utf8_len, '\0');
        if (utf8_len > 0) {
            WideCharToMultiByte(CP_UTF8, 0, logo_path.c_str(), wide_len, &logo_path_str[0], utf8_len, nullptr, nullptr);
        }
        log_debug("[Tray] Loading icon from: " + logo_path_str);
        HICON hIcon = load_png_as_icon(logo_path.c_str(), 16);
        if (!hIcon) {
            log_error("[Tray] Failed to load PNG, using fallback icon");
            hIcon = LoadIconW(nullptr, MAKEINTRESOURCEW(32512));  // IDI_APPLICATION fallback
        } else {
            log_debug("[Tray] Loaded PNG icon successfully");
        }

        // Add tray icon
        NOTIFYICONDATAW nid{};
        nid.cbSize = sizeof(NOTIFYICONDATAW);
        nid.hWnd = hwnd;
        nid.uID = 1;
        nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
        nid.uCallbackMessage = WM_USER + 1;
        nid.hIcon = hIcon;

        // Copy tooltip (max 127 chars + null)
        std::wstring tooltip_wide(config.tooltip.begin(), config.tooltip.end());
        wcsncpy(nid.szTip, tooltip_wide.c_str(), 127);

        if (!Shell_NotifyIconW(NIM_ADD, &nid)) {
            DestroyWindow(hwnd);
            delete callbacks;
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        TrayHandle handle;
        handle.handle = hwnd;
        return handle;
    }

    auto update_tray_tooltip(TrayHandle& handle, const std::string& tooltip) -> Result<void> override {
        HWND hwnd = static_cast<HWND>(handle.handle);
        if (!hwnd) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        NOTIFYICONDATAW nid{};
        nid.cbSize = sizeof(NOTIFYICONDATAW);
        nid.hWnd = hwnd;
        nid.uID = 1;
        nid.uFlags = NIF_TIP;

        std::wstring tooltip_wide(tooltip.begin(), tooltip.end());
        wcsncpy(nid.szTip, tooltip_wide.c_str(), 127);

        if (!Shell_NotifyIconW(NIM_MODIFY, &nid)) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        return {};
    }

    void remove_tray_icon(TrayHandle& handle) override {
        HWND hwnd = static_cast<HWND>(handle.handle);
        if (hwnd) {
            NOTIFYICONDATAW nid{};
            nid.cbSize = sizeof(NOTIFYICONDATAW);
            nid.hWnd = hwnd;
            nid.uID = 1;
            Shell_NotifyIconW(NIM_DELETE, &nid);

            auto* callbacks = reinterpret_cast<TrayCallbacks*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
            delete callbacks;
            DestroyWindow(hwnd);
            handle.handle = nullptr;
        }
    }

    auto data_dir() -> std::filesystem::path override {
        PWSTR local_app_data = nullptr;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local_app_data))) {
            std::filesystem::path path(local_app_data);
            CoTaskMemFree(local_app_data);
            return path / "xmax";
        }

        // Fallback
        return std::filesystem::path(std::getenv("LOCALAPPDATA")) / "xmax";
    }

    auto self_exe_path() -> std::filesystem::path override {
        wchar_t buffer[MAX_PATH];
        DWORD size = MAX_PATH;
        if (QueryFullProcessImageNameW(GetCurrentProcess(), 0, buffer, &size)) {
            return std::filesystem::path(buffer);
        }
        return {};
    }

    auto single_instance_lock() -> Result<InstanceLock> override {
        HANDLE mutex = CreateMutexW(nullptr, FALSE, L"Global\\XmaX_SingleInstance");
        if (!mutex) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        if (GetLastError() == ERROR_ALREADY_EXISTS) {
            CloseHandle(mutex);
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        InstanceLock lock;
        lock.handle = mutex;
        return lock;
    }

    void release_instance_lock(InstanceLock& lock) override {
        HANDLE mutex = static_cast<HANDLE>(lock.handle);
        if (mutex) {
            CloseHandle(mutex);
            lock.handle = nullptr;
        }
    }

    // ===== System: Auto-start =====

    auto set_auto_start(bool enabled, const std::filesystem::path& exe_path) -> Result<void> override {
        if (!init_task_scheduler()) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        // Get root folder
        ITaskFolder* root_folder = nullptr;
        BSTR folder_path = SysAllocString(TASK_FOLDER);
        HRESULT hr = task_service_->GetFolder(folder_path, &root_folder);
        SysFreeString(folder_path);

        if (FAILED(hr) || !root_folder) {
            log_error("[TaskScheduler] Cannot get root folder (error 0x" + std::to_string(hr) + ")");
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        if (!enabled) {
            // Delete task (ignore "not found" error)
            BSTR task_name = SysAllocString(TASK_NAME);
            hr = root_folder->DeleteTask(task_name, 0);
            SysFreeString(task_name);
            root_folder->Release();

            if (FAILED(hr) && hr != 0x80070002) {  // 0x80070002 = file not found
                log_error("[TaskScheduler] Cannot delete task (error 0x" + std::to_string(hr) + ")");
                return std::unexpected(ErrorCode::HardwareBusy);
            }
            return {};
        }

        // Create task definition
        ITaskDefinition* task_def = nullptr;
        hr = task_service_->NewTask(0, &task_def);
        if (FAILED(hr) || !task_def) {
            root_folder->Release();
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        // Get registration info
        IRegistrationInfo* reg_info = nullptr;
        task_def->get_RegistrationInfo(&reg_info);
        if (reg_info) {
            BSTR desc = SysAllocString(L"XmaX hardware control service");
            reg_info->put_Description(desc);
            SysFreeString(desc);
            reg_info->Release();
        }

        // Get principal (run with highest privileges)
        IPrincipal* principal = nullptr;
        task_def->get_Principal(&principal);
        if (principal) {
            principal->put_RunLevel(TASK_RUNLEVEL_HIGHEST);
            principal->Release();
        }

        // Get trigger collection and add logon trigger
        ITriggerCollection* triggers = nullptr;
        task_def->get_Triggers(&triggers);
        if (triggers) {
            ITrigger* trigger = nullptr;
            hr = triggers->Create(TASK_TRIGGER_LOGON, &trigger);
            if (SUCCEEDED(hr) && trigger) {
                ILogonTrigger* logon_trigger = nullptr;
                hr = trigger->QueryInterface(IID_ILogonTrigger, reinterpret_cast<void**>(&logon_trigger));
                if (SUCCEEDED(hr) && logon_trigger) {
                    logon_trigger->Release();
                }
                trigger->Release();
            }
            triggers->Release();
        }

        // Get action collection and add execute action
        IActionCollection* actions = nullptr;
        task_def->get_Actions(&actions);
        if (actions) {
            IAction* action = nullptr;
            hr = actions->Create(TASK_ACTION_EXEC, &action);
            if (SUCCEEDED(hr) && action) {
                IExecAction* exec_action = nullptr;
                hr = action->QueryInterface(IID_IExecAction, reinterpret_cast<void**>(&exec_action));
                if (SUCCEEDED(hr) && exec_action) {
                    std::wstring exe_str = exe_path.wstring();
                    BSTR path_bstr = SysAllocString(exe_str.c_str());
                    exec_action->put_Path(path_bstr);
                    SysFreeString(path_bstr);
                    exec_action->Release();
                }
                action->Release();
            }
            actions->Release();
        }

        // Register task
        IRegisteredTask* registered_task = nullptr;
        BSTR task_name = SysAllocString(TASK_NAME);
        hr = root_folder->RegisterTaskDefinition(task_name, task_def,
                                                  TASK_CREATE_OR_UPDATE,
                                                  _variant_t(), _variant_t(),
                                                  TASK_LOGON_INTERACTIVE_TOKEN,
                                                  _variant_t(L""),
                                                  &registered_task);
        SysFreeString(task_name);
        task_def->Release();
        root_folder->Release();

        if (FAILED(hr) || !registered_task) {
            log_error("[TaskScheduler] Cannot register task (error 0x" + std::to_string(hr) + ")");
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        registered_task->Release();
        return {};
    }

    auto is_auto_start_enabled() -> Result<bool> override {
        if (!init_task_scheduler()) {
            return std::unexpected(ErrorCode::HardwareBusy);
        }

        // Get root folder
        ITaskFolder* root_folder = nullptr;
        BSTR folder_path = SysAllocString(TASK_FOLDER);
        HRESULT hr = task_service_->GetFolder(folder_path, &root_folder);
        SysFreeString(folder_path);

        if (FAILED(hr) || !root_folder) {
            return false;
        }

        // Get task
        IRegisteredTask* registered_task = nullptr;
        BSTR task_name = SysAllocString(TASK_NAME);
        hr = root_folder->GetTask(task_name, &registered_task);
        SysFreeString(task_name);
        root_folder->Release();

        if (FAILED(hr) || !registered_task) {
            return false;
        }

        // Check if enabled
        VARIANT_BOOL enabled = VARIANT_FALSE;
        registered_task->get_Enabled(&enabled);
        registered_task->Release();

        return enabled == VARIANT_TRUE;
    }

    // ===== System: Message Loop =====

    void run_message_loop() override {
        MSG msg;
        while (GetMessageW(&msg, nullptr, 0, 0)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    void quit_message_loop() override {
        PostQuitMessage(0);
    }

private:
    HANDLE job_handle_ = nullptr;  // Job Object for frontend lifecycle
    ULONG_PTR gdiplus_token_ = 0;  // GDI+ initialization token

    // PawnIO driver state
    HANDLE pawnio_device_ = INVALID_HANDLE_VALUE;
    HANDLE pawnio_mutex_ = nullptr;
    bool pawnio_smu_loaded_ = false;
    bool pawnio_lpcio_loaded_ = false;

    // ADLX GPU telemetry state (Official AMD ADLX SDK)
    ADLXHelper* adlx_helper_ = nullptr;
    bool adlx_initialized_ = false;
    std::mutex adlx_mutex_;

    // Task Scheduler state
    ITaskService* task_service_ = nullptr;

public:
    // Initialize ADLX GPU telemetry (Official AMD ADLX SDK)
    bool init_adlx() {
        if (adlx_initialized_) return true;

        // Create ADLX helper
        adlx_helper_ = new ADLXHelper();

        // Initialize ADLX (uses COM interfaces provided by AMD driver)
        ADLX_RESULT res = adlx_helper_->Initialize();
        if (ADLX_FAILED(res)) {
            log_error("[ADLX] Initialization failed (result: " + std::to_string(res) + ")");
            delete adlx_helper_;
            adlx_helper_ = nullptr;
            return false;
        }

        adlx_initialized_ = true;
        return true;
    }

    // Cleanup ADLX resources
    void cleanup_adlx() {
        if (adlx_initialized_ && adlx_helper_) {
            adlx_helper_->Terminate();
        }
        if (adlx_helper_) {
            delete adlx_helper_;
            adlx_helper_ = nullptr;
        }
        adlx_initialized_ = false;
    }

    // Initialize Task Scheduler COM connection
    bool init_task_scheduler() {
        if (task_service_) return true;

        HRESULT hr = CoCreateInstance(CLSID_TaskScheduler, nullptr, CLSCTX_INPROC_SERVER,
                                      IID_ITaskService, reinterpret_cast<void**>(&task_service_));
        if (FAILED(hr) || !task_service_) {
            log_error("[TaskScheduler] Cannot create ITaskService (error 0x" + std::to_string(hr) + ")");
            task_service_ = nullptr;
            return false;
        }

        // Connect to Task Scheduler (nullptr = local machine, current user)
        hr = task_service_->Connect(_variant_t(), _variant_t(), _variant_t(), _variant_t());
        if (FAILED(hr)) {
            log_error("[TaskScheduler] Cannot connect (error 0x" + std::to_string(hr) + ")");
            task_service_->Release();
            task_service_ = nullptr;
            return false;
        }

        return true;
    }

    // Cleanup Task Scheduler resources
    void cleanup_task_scheduler() {
        if (task_service_) {
            task_service_->Release();
            task_service_ = nullptr;
        }
    }

    // Get GPU metrics via ADLX (Official AMD ADLX SDK)
    auto gpu_metrics() -> Result<GpuTelemetry> override {
        // Check if ADLX is initialized
        if (!adlx_initialized_ || !adlx_helper_) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        // Lock mutex to serialize ADLX calls
        std::lock_guard<std::mutex> lock(adlx_mutex_);

        GpuTelemetry telemetry{};

        // Get Performance Monitoring services
        IADLXPerformanceMonitoringServicesPtr perfService;
        ADLX_RESULT res = adlx_helper_->GetSystemServices()->GetPerformanceMonitoringServices(&perfService);
        if (ADLX_FAILED(res) || !perfService) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        // Get GPU list
        IADLXGPUListPtr gpuList;
        res = adlx_helper_->GetSystemServices()->GetGPUs(&gpuList);
        if (ADLX_FAILED(res) || !gpuList || gpuList->Size() == 0) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        // Use first GPU
        IADLXGPUPtr gpu;
        res = gpuList->At(gpuList->Begin(), &gpu);
        if (ADLX_FAILED(res) || !gpu) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        // Get GPU metrics support interface
        IADLXGPUMetricsSupportPtr metricsSupport;
        res = perfService->GetSupportedGPUMetrics(gpu, &metricsSupport);
        if (ADLX_FAILED(res) || !metricsSupport) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        // Get current GPU metrics
        IADLXGPUMetricsPtr metrics;
        res = perfService->GetCurrentGPUMetrics(gpu, &metrics);
        if (ADLX_FAILED(res) || !metrics) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        // Extract metrics
        adlx_bool supported = false;
        adlx_double value = 0;
        adlx_int intValue = 0;

        // GPU Usage (%)
        if (ADLX_SUCCEEDED(metricsSupport->IsSupportedGPUUsage(&supported)) && supported) {
            if (ADLX_SUCCEEDED(metrics->GPUUsage(&value))) {
                telemetry.util_pct = static_cast<float>(value);
            }
        }

        // GPU Clock Speed (MHz)
        if (ADLX_SUCCEEDED(metricsSupport->IsSupportedGPUClockSpeed(&supported)) && supported) {
            if (ADLX_SUCCEEDED(metrics->GPUClockSpeed(&intValue))) {
                telemetry.clock_mhz = static_cast<uint32_t>(intValue);
            }
        }

        // GPU Temperature (°C)
        if (ADLX_SUCCEEDED(metricsSupport->IsSupportedGPUTemperature(&supported)) && supported) {
            if (ADLX_SUCCEEDED(metrics->GPUTemperature(&value))) {
                telemetry.temp_c = static_cast<uint32_t>(value);
            }
        }

        // GPU Power (W)
        if (ADLX_SUCCEEDED(metricsSupport->IsSupportedGPUPower(&supported)) && supported) {
            if (ADLX_SUCCEEDED(metrics->GPUPower(&value))) {
                telemetry.power_w = static_cast<float>(value);
            }
        }

        // GPU VRAM (ADLX reports MB, convert to bytes)
        if (ADLX_SUCCEEDED(metricsSupport->IsSupportedGPUVRAM(&supported)) && supported) {
            if (ADLX_SUCCEEDED(metrics->GPUVRAM(&intValue))) {
                telemetry.vram_used_bytes = static_cast<uint64_t>(intValue) * 1024ULL * 1024ULL;
            }
        }

        // Get total dedicated VRAM via DXGI (ADLX doesn't expose this directly)
        IDXGIFactory1* dxgi_factory = nullptr;
        if (SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&dxgi_factory))) {
            IDXGIAdapter1* adapter = nullptr;
            for (UINT i = 0; dxgi_factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; i++) {
                DXGI_ADAPTER_DESC1 desc;
                if (SUCCEEDED(adapter->GetDesc1(&desc))) {
                    // Pick first discrete GPU (has dedicated video memory, not software renderer)
                    if (desc.DedicatedVideoMemory > 0 && (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0) {
                        telemetry.vram_total_bytes = static_cast<uint64_t>(desc.DedicatedVideoMemory);
                        adapter->Release();
                        break;
                    }
                }
                adapter->Release();
            }
            dxgi_factory->Release();
        }

        return telemetry;
    }

    // ===== UMA (Variable Graphics Memory) =====

    auto uma_supported() -> Result<bool> override {
        if (!adlx_initialized_ || !adlx_helper_) {
            log_debug("[UMA] ADLX not initialized");
            return false;
        }

        std::lock_guard<std::mutex> lock(adlx_mutex_);

        // Get IADLXSystem3 from existing IADLXSystem
        IADLXSystem3Ptr system3;
        ADLX_RESULT res = adlx_helper_->GetSystemServices()->QueryInterface(
            IADLXSystem3::IID(), reinterpret_cast<void**>(&system3));
        if (ADLX_FAILED(res) || !system3) {
            log_debug("[UMA] QueryInterface IADLXSystem3 failed: " + std::to_string(res));
            return false;
        }

        IADLXVariableGraphicsMemoryPtr vgm;
        res = system3->GetVariableGraphicsMemory(&vgm);
        if (ADLX_FAILED(res) || !vgm) {
            log_debug("[UMA] GetVariableGraphicsMemory failed: " + std::to_string(res));
            return false;
        }

        adlx_bool supported = false;
        res = vgm->IsSupported(&supported);
        if (ADLX_FAILED(res)) {
            log_debug("[UMA] IsSupported check failed: " + std::to_string(res));
            return false;
        }

        log_debug("[UMA] Variable Graphics Memory supported: " + std::string(supported ? "true" : "false"));
        return supported != false;
    }

    auto uma_available_options() -> Result<std::vector<UmaOption>> override {
        if (!adlx_initialized_ || !adlx_helper_) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        std::lock_guard<std::mutex> lock(adlx_mutex_);

        IADLXSystem3Ptr system3;
        ADLX_RESULT res = adlx_helper_->GetSystemServices()->QueryInterface(
            IADLXSystem3::IID(), reinterpret_cast<void**>(&system3));
        if (ADLX_FAILED(res) || !system3) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        IADLXVariableGraphicsMemoryPtr vgm;
        res = system3->GetVariableGraphicsMemory(&vgm);
        if (ADLX_FAILED(res) || !vgm) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        IADLXVariableGraphicsMemoryOptionListPtr options;
        res = vgm->GetAvailableOptions(&options);
        if (ADLX_FAILED(res) || !options) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        std::vector<UmaOption> result;
        adlx_uint count = options->Size();

        for (adlx_uint i = 0; i < count; i++) {
            IADLXVariableGraphicsMemoryOptionPtr option;
            res = options->At(i, &option);
            if (ADLX_FAILED(res) || !option) continue;

            UmaOption uma_opt;

            const char* name = nullptr;
            if (ADLX_SUCCEEDED(option->Name(&name)) && name) {
                uma_opt.name = name;
            }

            ADLX_VARIABLE_GRAPHICS_MEMORY_MODE mode;
            if (ADLX_SUCCEEDED(option->Mode(&mode))) {
                uma_opt.mode = (mode == VARIABLE_GRAPHICS_MEMORY_MODE_CUSTOM)
                    ? UmaOption::Mode::Custom
                    : UmaOption::Mode::Auto;
            }

            adlx_double carved = 0.0;
            if (ADLX_SUCCEEDED(option->MemoryCarved(&carved))) {
                uma_opt.memory_carved_gb = carved;
            }

            adlx_double remaining = 0.0;
            if (ADLX_SUCCEEDED(option->MemoryRemaining(&remaining))) {
                uma_opt.memory_remaining_gb = remaining;
            }

            // Generate unique id: "<mode>:<memory_carved_gb>"
            // memory_carved_gb is unique per option, making this a reliable key.
            {
                std::string mode_str = (uma_opt.mode == UmaOption::Mode::Custom) ? "custom" : "auto";
                // Format carved with 1 decimal place for consistency
                std::ostringstream oss;
                oss << std::fixed << std::setprecision(1) << uma_opt.memory_carved_gb;
                uma_opt.id = mode_str + ":" + oss.str();
            }

            log_debug("[UMA] Option[" + std::to_string(i) + "]: id='" + uma_opt.id
                + "', name='" + uma_opt.name
                + "', mode=" + std::to_string(static_cast<int>(uma_opt.mode))
                + ", carved=" + std::to_string(uma_opt.memory_carved_gb)
                + " GB, remaining=" + std::to_string(uma_opt.memory_remaining_gb) + " GB");

            result.push_back(std::move(uma_opt));
        }

        return result;
    }

    auto uma_current_option() -> Result<UmaOption> override {
        if (!adlx_initialized_ || !adlx_helper_) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        std::lock_guard<std::mutex> lock(adlx_mutex_);

        IADLXSystem3Ptr system3;
        ADLX_RESULT res = adlx_helper_->GetSystemServices()->QueryInterface(
            IADLXSystem3::IID(), reinterpret_cast<void**>(&system3));
        if (ADLX_FAILED(res) || !system3) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        IADLXVariableGraphicsMemoryPtr vgm;
        res = system3->GetVariableGraphicsMemory(&vgm);
        if (ADLX_FAILED(res) || !vgm) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        IADLXVariableGraphicsMemoryOptionPtr option;
        res = vgm->GetOption(&option);
        if (ADLX_FAILED(res) || !option) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        UmaOption uma_opt;

        const char* name = nullptr;
        if (ADLX_SUCCEEDED(option->Name(&name)) && name) {
            uma_opt.name = name;
        }

        ADLX_VARIABLE_GRAPHICS_MEMORY_MODE mode;
        if (ADLX_SUCCEEDED(option->Mode(&mode))) {
            uma_opt.mode = (mode == VARIABLE_GRAPHICS_MEMORY_MODE_CUSTOM)
                ? UmaOption::Mode::Custom
                : UmaOption::Mode::Auto;
        }

        adlx_double carved = 0.0;
        if (ADLX_SUCCEEDED(option->MemoryCarved(&carved))) {
            uma_opt.memory_carved_gb = carved;
        }

        adlx_double remaining = 0.0;
        if (ADLX_SUCCEEDED(option->MemoryRemaining(&remaining))) {
            uma_opt.memory_remaining_gb = remaining;
        }

        // Generate unique id (same scheme as uma_available_options)
        {
            std::string mode_str = (uma_opt.mode == UmaOption::Mode::Custom) ? "custom" : "auto";
            std::ostringstream oss;
            oss << std::fixed << std::setprecision(1) << uma_opt.memory_carved_gb;
            uma_opt.id = mode_str + ":" + oss.str();
        }

        return uma_opt;
    }

    auto uma_set_option(const std::string& option_id) -> Result<void> override {
        if (!adlx_initialized_ || !adlx_helper_) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        std::lock_guard<std::mutex> lock(adlx_mutex_);

        IADLXSystem3Ptr system3;
        ADLX_RESULT res = adlx_helper_->GetSystemServices()->QueryInterface(
            IADLXSystem3::IID(), reinterpret_cast<void**>(&system3));
        if (ADLX_FAILED(res) || !system3) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        IADLXVariableGraphicsMemoryPtr vgm;
        res = system3->GetVariableGraphicsMemory(&vgm);
        if (ADLX_FAILED(res) || !vgm) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        // Find the option by name
        IADLXVariableGraphicsMemoryOptionListPtr options;
        res = vgm->GetAvailableOptions(&options);
        if (ADLX_FAILED(res) || !options) {
            return std::unexpected(ErrorCode::SensorUnavailable);
        }

        adlx_uint count = options->Size();

        for (adlx_uint i = 0; i < count; i++) {
            IADLXVariableGraphicsMemoryOptionPtr option;
            res = options->At(i, &option);
            if (ADLX_FAILED(res) || !option) continue;

            // Build the same unique id we use in uma_available_options
            const char* raw_name = nullptr;
            if (!ADLX_SUCCEEDED(option->Name(&raw_name))) continue;

            ADLX_VARIABLE_GRAPHICS_MEMORY_MODE mode_val;
            if (!ADLX_SUCCEEDED(option->Mode(&mode_val))) continue;

            adlx_double carved_val = 0.0;
            if (!ADLX_SUCCEEDED(option->MemoryCarved(&carved_val))) continue;

            std::string mode_str = (mode_val == VARIABLE_GRAPHICS_MEMORY_MODE_CUSTOM) ? "custom" : "auto";
            std::ostringstream oss;
            oss << std::fixed << std::setprecision(1) << carved_val;
            std::string id = mode_str + ":" + oss.str();

            if (id == option_id) {
                // Found it - set this option (triggers reboot)
                res = vgm->SetOption(option);
                if (ADLX_FAILED(res)) {
                    return std::unexpected(ErrorCode::HardwareBusy);
                }
                return {};
            }
        }

        return std::unexpected(ErrorCode::ProfileNotFound);
    }
};

std::unique_ptr<Platform> create_platform() {
    return std::make_unique<Win32Platform>();
}
