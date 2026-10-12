# xmax.spec — Fedora/RHEL packaging for the XmaX Linux backend + GNOME Shell
# extension.
#
# Build (COPR / fedpkg flow, which provides autorelease + autochangelog):
#   fedpkg --release f44 local
# or, from a git checkout:
#   mock -r fedora-44-x86_64 --spec packaging/linux/xmax.spec --sources .
#
# Plain `rpmbuild` outside a git repo / without rpmautospec: replace
#   Release: the autorelease with a fixed Release, and
#   the autochangelog directive below with a manual changelog entry.
#
# TODO (license): the project has no top-level LICENSE yet. Set the License tag
# to the matching SPDX expression and add a license file once the project
# declares its own license. The bundled RyzenAdj is LGPL-3.0-only
# (backend/lib/ryzenadj/LICENSE) and its license is shipped below.

Name:           xmax
Version:        0.1.0
Release:        %autorelease
Summary:        Hardware control daemon for AMD laptops (TDP, fan, charge limit)

# TODO: pick an SPDX expression for the project's own code (unlicensed today).
License:        TODO-CHOOSE-SPDX
URL:            https://example.invalid/xmax
Source0:        %{url}/archive/refs/tags/v%{version}/%{name}-%{version}.tar.gz

BuildRequires:  gcc-c++
BuildRequires:  cmake
BuildRequires:  ninja-build
BuildRequires:  pkgconfig(libsystemd)
BuildRequires:  pkgconfig(libpci)
BuildRequires:  nlohmann-json-devel
BuildRequires:  gtest-devel
BuildRequires:  systemd-rpm-macros

Requires:       polkit

%description
XmaX is a background daemon that exposes AMD laptop hardware controls — CPU
TDP (STAPM/Fast/Slow), fan mode, and battery charge limit — over a D-Bus
interface (org.xmax.Service), gated by polkit, plus a command-line client
(xmaxctl).

%package -n xmax-gnome-extension
Summary:        GNOME Shell extension for XmaX
Requires:       gnome-shell
Requires:       %{name} = %{version}-%{release}

%description -n xmax-gnome-extension
Top-bar readouts and Quick Settings controls for the XmaX daemon.

%prep
%autosetup -p1

%build
%set_build_flags

# Backend daemon + CLI (+ the unit-test binary used by %%check). The backend's
# CMake prefers system nlohmann_json/GTest via find_package, so this stays offline.
cmake -S backend -B backend-build -G Ninja \
    -DCMAKE_INSTALL_PREFIX=%{_prefix}
cmake --build backend-build

# Bundled RyzenAdj — a shared library dlopen'd by xmaxd at runtime (see
# backend/src/platform/linux/ryzenadj.cpp), built from the vendored source.
cmake -S backend/lib/ryzenadj -B ryzenadj-build -G Ninja -DBUILD_SHARED_LIBS=ON
cmake --build ryzenadj-build --target libryzenadj

%install
# Backend + CLI (install rules live in backend/CMakeLists.txt).
DESTDIR=%{buildroot} cmake --install backend-build

# RyzenAdj -> private libdir. It is an unversioned runtime plugin only ever
# loaded via XMAX_RYZENADJ_LIB (set in the unit), so it stays out of the shared
# system libdir and needs no ldconfig.
install -D -m 0755 ryzenadj-build/libryzenadj.so \
    %{buildroot}%{_libdir}/xmax/libryzenadj.so

# D-Bus system-bus allow policy.
install -D -m 0644 packaging/linux/org.xmax.Service.conf \
    %{buildroot}%{_datadir}/dbus-1/system.d/org.xmax.Service.conf

# polkit actions (rewrite the dev-only path to the packaged daemon path).
install -D -m 0644 packaging/linux/org.xmax.policy \
    %{buildroot}%{_datadir}/polkit-1/actions/org.xmax.policy
sed -i 's|/usr/local/bin/xmaxd|%{_sbindir}/xmaxd|g' \
    %{buildroot}%{_datadir}/polkit-1/actions/org.xmax.policy

# systemd unit: rewrite ExecStart to the packaged path, and point the runtime
# plugin resolution at the private libdir.
install -D -m 0644 packaging/linux/xmaxd.service \
    %{buildroot}%{_unitdir}/xmaxd.service
sed -i \
    -e 's|ExecStart=/usr/local/bin/xmaxd|ExecStart=%{_sbindir}/xmaxd|' \
    -e '/^ExecStart=/a Environment=XMAX_RYZENADJ_LIB=%{_libdir}/xmax/libryzenadj.so' \
    %{buildroot}%{_unitdir}/xmaxd.service

# GNOME Shell extension (subpackage). The GSettings schema is installed
# system-wide; the extension resolves it by id (settings-schema: org.xmax.shell).
install -d %{buildroot}%{_datadir}/gnome-shell/extensions/xmax@local
cp -p frontend/gnome/xmax@local/extension.js \
      frontend/gnome/xmax@local/indicator.js \
      frontend/gnome/xmax@local/quickSettings.js \
      frontend/gnome/xmax@local/shellCompat.js \
      frontend/gnome/xmax@local/dbus.js \
      frontend/gnome/xmax@local/prefs.js \
      frontend/gnome/xmax@local/metadata.json \
      %{buildroot}%{_datadir}/gnome-shell/extensions/xmax@local/
install -D -m 0644 frontend/gnome/xmax@local/schemas/org.xmax.shell.gschema.xml \
    %{buildroot}%{_datadir}/glib-2.0/schemas/org.xmax.shell.gschema.xml

%check
ctest --test-dir backend-build --output-on-failure

%post
%systemd_post xmaxd.service
# Ask the message bus to reload its config drop-ins after the policy changed.
systemctl try-reload dbus-broker.service 2>/dev/null || systemctl reload dbus.service 2>/dev/null || :

%preun
%systemd_preun xmaxd.service

%postun
%systemd_postun xmaxd.service
systemctl try-reload dbus-broker.service 2>/dev/null || systemctl reload dbus.service 2>/dev/null || :

%posttrans -n xmax-gnome-extension
glib-compile-schemas %{_datadir}/glib-2.0/schemas >/dev/null 2>&1 || :

%files
%license backend/lib/ryzenadj/LICENSE
%{_sbindir}/xmaxd
%{_bindir}/xmaxctl
%{_datadir}/dbus-1/system.d/org.xmax.Service.conf
%{_datadir}/polkit-1/actions/org.xmax.policy
%{_unitdir}/xmaxd.service
%dir %{_libdir}/xmax
%{_libdir}/xmax/libryzenadj.so

%files -n xmax-gnome-extension
%{_datadir}/gnome-shell/extensions/xmax@local/
%{_datadir}/glib-2.0/schemas/org.xmax.shell.gschema.xml

%changelog
%autochangelog