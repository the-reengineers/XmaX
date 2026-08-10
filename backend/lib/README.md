# Binary Dependencies

These binary files are from open-source hardware control projects. They are signed kernel driver blobs required for low-level hardware access on Windows.

## Files

| File | Size | Source | Purpose |
|------|------|--------|---------|
| **RyzenSMU.bin** | 39K | [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) | SMU mailbox for AMD Strix Halo TDP control |
| **LpcIO.bin** | 18K | [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) | Super I/O for EC RAM writes (charge limit) |

## Licensing

### PawnIO.Modules (RyzenSMU.bin, LpcIO.bin)
```
Copyright (c) namazso and contributors
Licensed under LGPL-2.1 (GNU Lesser General Public License v2.1)
https://github.com/namazso/PawnIO.Modules
```

**LGPL-2.1 is FOSS-friendly:**
- ✅ Allows commercial and non-commercial use
- ✅ Allows linking with code under different licenses
- ✅ Only requires modifications to these files remain open source
- ✅ No copyleft restrictions on your main codebase

**License file:** `PawnIO/COPYING.LGPL-2.1` (included as required by LGPL-2.1)

## GPU Metrics (ADLX)

GPU telemetry is retrieved via the **official AMD ADLX SDK** using COM interfaces provided by the AMD driver:
- No binary dependency (no DLL required)
- SDK uses **local-first** strategy: local copy in `backend/lib/ADLX/` with FetchContent fallback
- Licensed under AMD SDK License Agreement (royalty-free for use with AMD hardware)
- COM interfaces are provided by the AMD graphics driver itself

**Benefits:**
- ✅ No third-party wrapper DLL
- ✅ Official AMD SDK with full documentation
- ✅ Royalty-free license
- ✅ Commercial use allowed
- ✅ Direct from AMD (no third-party dependency)
- ✅ Offline builds work (local SDK copy)
- ✅ Automatic fallback to FetchContent if local files missing

## Updates

**PawnIO.Modules files (LGPL-2.1):**
These binaries are stable and rarely need updates. To update:
1. Download from: `https://github.com/namazso/PawnIO.Modules`
   - `RyzenSMU.bin`
   - `LpcIO.bin`
2. Replace files in `backend/lib/PawnIO/`
3. Commit with message: "Update PawnIO binaries"

**ADLX SDK (local-first with FetchContent fallback):**
Local copy stored in `backend/lib/ADLX/`. To update:

1. Download from: `https://github.com/GPUOpen-LibrariesAndSDKs/ADLX`
2. Replace contents of `backend/lib/ADLX/`
3. Commit with message: "Update ADLX SDK"

If local files are missing, CMake will automatically download from GitHub (FetchContent fallback). To pin to a specific version, edit `backend/CMakeLists.txt`:
```cmake
FetchContent_Declare(
    adlx
    GIT_REPOSITORY https://github.com/GPUOpen-LibrariesAndSDKs/ADLX.git
    GIT_TAG main  # Or use a specific tag/commit hash
)
```

## Build Strategy: Local-First with FetchContent Fallback

The PawnIO binary blobs use a **local-first** approach with **FetchContent fallback**:

1. **Primary: Local copies** (`backend/lib/PawnIO/`)
   - Fast, offline builds
   - Explicit LGPL compliance (files + license in repo)
   - Version controlled with the codebase
   - No network dependency

2. **Fallback: FetchContent** (automatic download)
   - Triggered when local files are missing (e.g., fresh clone without binaries)
   - Downloads from `https://github.com/namazso/PawnIO.Modules`
   - Cached by CMake after first download
   - Binaries copied to build output directory

**Why this approach?**
- Binary blobs rarely change (signed kernel drivers are stable)
- 57KB total size is negligible for git storage
- Offline builds work out of the box
- Fresh clones still build successfully
- LGPL compliance is explicit and auditable

The ADLX SDK uses FetchContent (source-only) because it's compiled source code with stable release tags on GitHub.
