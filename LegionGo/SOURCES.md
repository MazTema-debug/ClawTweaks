# Source provenance

The first Legion target was authored 2026-10-10 in MazTema-debug/ClawTweaks.

## Optional RTSS OSD publisher (0.1.18)

The memory adapter, service, formatter, UI and tests are original AGPL-3.0-only port
code. They reimplement the documented byte layout; no SDK implementation, RTSS
executable, hook DLL, renderer, installer or third-party wrapper is redistributed.
RTSS is installed/configured separately by its user. Official distribution:
https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/

Primary ABI reference: RTSS 7.3.5 SDK, `SDK/Include/RTSSSharedMemory.h`, SHA256
`615D6E295FDEFDE1A166A5FDCC6F84BD848E61DA5E31BBA6DCA05A3AC9390EC8`.
The corresponding `SDK/Samples/SharedMemory/RTSSSharedMemorySample/RTSSSharedMemorySampleDlg.cpp`
documents nonprimary OSD slots, exact owner IDs, extended 4096-byte text and the
`<FR>`/`<FT>` game-specific tags (lines 771–773 of this SDK). Lock bit 0 of `dwBusy`
is defined for ABI 2.14 onward. Version 0.1.19 accepts the reviewed ABI through 2.21.
The 2.21 additions in `RTSS_SHARED_MEMORY_APP_ENTRY` are timing statistics; they do
not change the 96-byte header or the OSD owner/text offsets used here. Those added
application fields are not read by the port. Later versions require another SDK
review. The adapter uses the legacy extended
text field, as the SDK SharedMemory sample does; it does not use the v2.20 32 KiB
text extension or embedded graphs.

The byte bounds, owner checks and concurrency rules are tested with simulated
memory. A successful write does not establish that a game hooks, renders the text
or reports accurate frame statistics. No game-application table or process names
are read by this integration, and no telemetry driver is added.

## Saved-profile comparison, CSV review and freshness (0.1.17)

`Core/ProfileComparison.cs`, `Core/SessionSummary.cs`, `Core/TelemetryFreshness.cs`
and their widget controls/tests are original AGPL-3.0-only port code. No external
CSV, plotting or telemetry package is added. The session reader consumes only the
existing port CSV schemas through a manually selected UWP file. It never executes
text cells, uploads records or queries hardware. File picker APIs are linked below.
Snapshot UTC and local monotonic receipt clocks describe data freshness, not sensor
accuracy, input latency or measured performance gains.

## Legion Space, profiles and assisted player 1 (0.1.10, in development)

`Core/LegionSpace.cs`, `Helper/LegionSpaceBackend.cs`, `Core/ProfileTransfer.cs`
and their widget controls/tests are new AGPL-3.0-only code for this port. No
GoTweaks service-control implementation is copied. The backend uses Windows SCM
APIs for the fixed `DAService` name and validates its Lenovo installation path.
Only startup mode, delayed-start flag and running state are changed; dependent
services are not recursively stopped. No service-restoration action is tied to
profile application, startup, power restoration, shutdown or uninstall.

Microsoft API references:

- [QueryServiceConfigW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceconfigw),
  [QueryServiceConfig2W](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceconfig2w)
  and [QueryServiceStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryservicestatusex).
- [ChangeServiceConfigW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfigw),
  [ChangeServiceConfig2W](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfig2w)
  and [SERVICE_DELAYED_AUTO_START_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_delayed_auto_start_info).
- [StartServiceW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicew)
  and [ControlService](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-controlservice).
- [UWP FileOpenPicker](https://learn.microsoft.com/en-us/uwp/api/windows.storage.pickers.fileopenpicker),
  [FileSavePicker](https://learn.microsoft.com/en-us/uwp/api/windows.storage.pickers.filesavepicker)
  and [CachedFileManager](https://learn.microsoft.com/en-us/uwp/api/windows.storage.cachedfilemanager).

The name editor uses ordinary UWP controls and direct draft edits from its own
touch buttons. It neither hooks keyboard input globally nor invokes an external
keyboard process. No Game Bar input or picker compatibility is inferred from
the core tests or C# metadata compilation.

`Core/PlayerOnePreparation.cs`, `Helper/PlayerOnePort.cs` and
`Widget/MainPage.PlayerOne.cs` are original AGPL-3.0-only code. The assistant uses
the existing ViGEm API for its final target, readonly SetupAPI enumeration and
System32 `XInputGetState`. The user operates the physical FPS switch; there are
no PNP disable/enable/reset calls or controller firmware commands in this path.
The existing selective HidHide journal and supervisor remain responsible for
physical visibility. No Handheld Companion or GoTweaks slot-management code is copied.

- [XInputGetState](https://learn.microsoft.com/en-us/windows/win32/api/xinput/nf-xinput-xinputgetstate).
- [SetupDiEnumDeviceInterfaces](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdienumdeviceinterfaces).

Windows documents controller indexes 0–3 and distinguishes disconnected devices
from other errors. The adapter preserves that distinction, while reporting player
numbers 1–4 in the widget. These API contracts do not establish a game's selection
policy or compatibility with the virtual controller.

## ClawTweaks

- Repository: https://github.com/enterTheVoidCode/ClawTweaks
- Fork base: https://github.com/MazTema-debug/ClawTweaks/commit/8e34189693fa2d8dd31de11d7637d773f9aaa634
- Branch: `release/v0.3.98.0`
- License: AGPLv3 (root `LICENSE`); pre-existing MIT portions retain root `LICENSE.MIT`.
- `XboxGamingBar/App.xaml.cs`: Game Bar activation lifecycle / retained widget object.
- `XboxGamingBar/IPC/NamedPipeClient.cs`: UWP CreateFileW + async FileStream transport technique.
- `XboxGamingBar/GamingWidget.xaml.cs`, `Next Gen Claw`: blue/violet palette and card styling.
- `BUILDING.md`: documents the absence of the private hardware helper.

`LegionGo/Widget` adapts these pieces into a separate UWP target. It does not claim to
port the unavailable helper or all original UI features. The new geometric icon is
not the upstream ClawTweaks/MSI logo.

## GoTweaks

- Repository: https://github.com/corando98/GoTweaks
- Source commit: https://github.com/corando98/GoTweaks/commit/99ed5f5f8c22bb78bfffee9b37f9a04094f30482
- Branch: `release/v0.3.98.0`
- License: MIT, preserved verbatim in `LICENSE.GoTweaks`.
- Attribution: corando98 and GoTweaks contributors; original license credits Microsoft Corporation.

| Adapted backend | GoTweaks source |
| --- | --- |
| Original Go identity | `XboxGamingBarHelper/Devices/LegionGo/LegionGoConfig.cs` |
| Capability IDs | `XboxGamingBarHelper/Devices/Libraries/Legion/LegionGoLibrary.cs` |
| WMI classes, methods and argument names | `XboxGamingBarHelper/Devices/Libraries/Legion/LenovoWMIService.cs` |
| SPL/SPPT/FPPT mapping and Custom transition | `XboxGamingBarHelper/Devices/Libraries/Legion/LegionManager.cs`, `SetCustomTDP` / `ApplyTDPValues` |
| Windows CPU/overlay controls | `XboxGamingBarHelper/Power/PowerManager.cs`, `XboxGamingBarHelper/Windows/PowerGuids.cs` |
| Original Legion front-button report layouts (0.1.8) | `XboxGamingBarHelper/Labs/LegionButtonMonitor.cs`: 64-byte reports, 04:00:A1 offset 16 / 04:3C:74 offset 18, L/R masks 80/40 |
| Vendor HID selection (0.1.8) | `XboxGamingBarHelper/Devices/Libraries/Legion/LegionGoController.cs`: Lenovo usage page FFA0; 0.1.8 initially restricted PIDs to 6182–6185; corrected in 0.1.9 |

The 0.1.8 reader and shortcut state machine are new, minimal implementations.
They do not copy GoTweaks' initialization, firmware remapping, gyro, service
disabling or controller-emulation paths. They issue no HID output/feature writes.
Win+G uses Microsoft's documented `SendInput` keyboard sequence pattern, with
x64 INPUT layout and modifier/desktop guards:
https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput

The 0.1.9 device-ID correction uses the two tablet PID families already listed in
GoTweaks' `LegionButtonMonitor.cs` (`LEGION_TABLET_PIDS`). Handheld Companion's
`HandheldCompanion/Devices/Lenovo/LegionGoTablet.cs` at commit
`a250b19a4b7e29dac3b383f2d1ac517d1af43e4e` independently identifies 61EB–61EE as
2025-firmware IDs for the original Go and specifies FFA0:0001. Only this hardware
identity information was consulted; no Handheld Companion implementation is copied.
The port still gates the machine model and CPU separately.

HID capability sizes and collection usage follow Microsoft's HIDP_CAPS contract:
https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/ns-hidpi-_hidp_caps

The ID mapping intentionally follows `LegionManager.ApplyTDPValues`: SPL →
`0x0101FF00`, SPPT → `0x0102FF00`, FPPT → `0x0103FF00`. Some upstream XML comments
call these short/long in an inconsistent order; the actual manager call sites are
the reference. Lenovo mode bytes are 1/2/3/255. Charge limit uses `0x03010001`; fan
maximum uses `0x04020000`. These reverse-engineered capabilities are not claimed as
an official Lenovo SDK contract; firmware may reject them and the port reports that.

The new engine adds stricter Z1 Extreme gating, conservative 5–30 W limits,
confirmed mode transitions, readback, rollback, persistent originals, and tests.
It never interprets a cached/default value as a successful hardware read.

## LibreHardwareMonitor — read-only AMD interop

- Repository: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- Source commit: `eb4f29f77afc58be3a2037f88b9353b4982305dc`.
- `LibreHardwareMonitorLib/Interop/AtiAdlxx.cs`: ADL2 P/Invoke signatures, ANSI
  adapter records, PMLog records, ODN EDGE temperature and OD5 temperature/activity layouts.
- `LibreHardwareMonitorLib/Hardware/Gpu/AmdGpu.cs`: sensor IDs/units, EDGE/GFX
  temperature fallback, millivolt conversion and GPU/ASIC/CPU power domains.
- License: MPL-2.0, retained in `LICENSE.LibreHardwareMonitor` and `Helper/AmdNative.cs`.

The adapted `AmdNative.cs` keeps only enumeration, telemetry and context-lifetime
entry points, renamed for this target. It adds no tuning calls. `AmdTelemetry.cs`
adds per-reading support/range validation, unique physical-GPU selection, fresh
sample errors and OD5/ODN fallbacks. The original complete hardware-monitor library,
kernel driver and AMD SDK are not redistributed. Source for the adapted file is
included in this public repository at the workflow commit producing each package.

## AMD power audit and recovery (0.1.6)

- Official ADL reference commit: `84c881cb2136f04287eb3af12de00a9e2867aa9e`.
- [ADLSensorType definitions](https://github.com/GPUOpen-LibrariesAndSDKs/display-library/blob/84c881cb2136f04287eb3af12de00a9e2867aa9e/include/adl_defines.h).
- [ADLPMLogDataOutput layout](https://github.com/GPUOpen-LibrariesAndSDKs/display-library/blob/84c881cb2136f04287eb3af12de00a9e2867aa9e/include/adl_structures.h).
- [API release notes](https://github.com/GPUOpen-LibrariesAndSDKs/display-library/blob/84c881cb2136f04287eb3af12de00a9e2867aa9e/Public-Documents/README.md).
- [Overdrive8 API documentation](https://github.com/GPUOpen-LibrariesAndSDKs/display-library/blob/84c881cb2136f04287eb3af12de00a9e2867aa9e/Public-Documents/html/group__OVERDRIVE8API.html).

| PMLog reading | Sensor ID | Integer unit |
| --- | --- | --- |
| GFX clock | 1 | MHz |
| EDGE temperature | 8 | °C |
| SoC voltage | 16 | mV |
| GFX activity | 19 | % |
| GFX voltage | 21 | mV |
| ASIC power | 23 | W |
| GFX temperature fallback | 28 | °C |
| GFX power | 30 | W |
| CPU power | 33 | W |

The named IDs preserve the earlier LibreHardwareMonitor mapping. Each record is
`int supported; int value`, and the query output is an integer size followed by
256 records (2052 bytes). It has no timestamp. The audit did not find a swapped
power field or justify a scaling correction. The three domains are read from the
same output buffer; non-additivity and driver freshness remain unverified.

AMD deprecates `ADL2_New_QueryPMLogData_Get` for efficiency and documents an
Overdrive8 shared-memory replacement with support/start/read/stop lifecycle.
That deprecation alone does not establish incorrect watt readings.
Version 0.1.6 retains the known-working reader while adding raw provenance and
context recovery; it does not silently introduce another backend or redistribute
the SDK. `AmdSession` contains the existing native context/enumeration/fallback
code; `AmdTelemetry` owns the testable recovery policy. Only context management
and sensor reads are invoked. No GPU tuning or Windows setting writes are added.

## Game Bar overlay

- Microsoft widget API: https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widget
- Widget-to-widget activation: https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widgetcontrol
- Pinning/click-through: https://learn.microsoft.com/en-us/xbox/game-bar/guide/click-through
- Opacity implementation: https://github.com/microsoft/XboxGameBarSamples/blob/master/Samples/WidgetAdvSampleCS/Widget1.xaml.cs

For opacity, the executable Microsoft sample's `SetBackgroundOpacity` uses
`widget.RequestedOpacity` directly as a XAML 0–1 value. This corrects the extra
percentage conversion in 0.1.4. Some prose
API documentation describes a 0–100 scale; the runtime/sample behavior is used here.

`OverlayPage` uses a separate `LegionOverlay` app extension, Game Bar visibility and
opacity events, and read-only polling. It does not inject code into games or
capture frames. Version 0.1.4 corrects near-transparent rendering. Version 0.1.5
also labels the live Lenovo mode and distinguishes stored custom registers from
an active Custom mode.

## CSV recording

`TelemetryCsv`, `TelemetryRecorder` and `HelperSession` are original code for this
port, under its AGPL-3.0-only license. They use the existing read-only status path;
no additional telemetry driver, SDK, RTSS component or hardware writer is added.
The CSV format, error handling and gap markers are documented in README.md.

- Explorer launch API: https://learn.microsoft.com/en-us/uwp/api/windows.system.launcher.launchfolderpathasync?view=winrt-26100

Microsoft documents that this API must run on the UI thread and requires the user
to have folder access, but not the calling UWP app. The helper grants the owner
read access to its CSV subfolder while keeping the journal and output-path creation
protected. Sampling gaps use observed UTC intervals, not unverified suspend events.

## Combined profiles and controller backend (0.1.7)

- Handheld Companion `a250b19a4b7e29dac3b383f2d1ac517d1af43e4e` was reviewed for
  the separation of physical input, per-profile layouts, virtual output and HidHide.
  Files reviewed include ControllerManager.XInput, LegionControllerXInput,
  Xbox360Target/VIIPERTarget, OpenXInput and Misc/HidHide. Its current license is
  CC-BY-NC-SA-4.0 and its current output backend is VIIPER. No HC code, assets or
  binaries are copied or linked into this AGPL port. Our implementation uses the
  public APIs below. It does not claim HC feature or compatibility parity.
- SDL 2.32.10, commit `5d249570393f7a37e037abf22cd6012a4cc56a71`, zlib license.
  Public SDL_gamecontroller.h/SDL_joystick.h APIs supply standard input. The Windows
  x64 runtime release ZIP is pinned by SHA-256 in Build-LegionGo.ps1.
  https://github.com/libsdl-org/SDL/tree/release-2.32.10
- Nefarius.ViGEm.Client 1.21.256 (NuGet), MIT; Xbox 360 output and feedback API.
  https://github.com/nefarius/ViGEm.NET
  Driver retirement: https://docs.nefarius.at/projects/ViGEm/End-of-Life/
  ViGEmBus is an external optional prerequisite, not distributed by this package.
- HidHide public IOCTL interface: https://docs.nefarius.at/projects/HidHide/API-Documentation/
  Constants and buffer ABI checked against Nefarius.Drivers.HidHide (MIT); the
  thin local bridge holds one handle across each read/modify/write transaction.
  The driver and its .NET package are not bundled. Attribution included.
- Windows SetupAPI enumerates GUID_DEVINTERFACE_XUSB_DEVICE to identify the single
  physical Lenovo endpoint. No arbitrary device IDs can be sent through IPC.
  Library source: https://github.com/nefarius/Nefarius.Drivers.HidHide

New core tests exercise legacy migration, bounded CRUD/storage, whole-profile
validation/preflight/rollback, AC/DC identity, original boost-mode restoration,
button-map composition and release, axis bounds/deadzones, selective HidHide
cleanup and preservation of foreign entries. Windows builds compile the native
interop and actual UWP editor; physical gaming/input behavior is not simulated
into a claim of device validation.
# Configuration Manager device transitions (0.1.11)

The optional automatic player-1 operation uses Microsoft's documented
Configuration Manager APIs for the exact original Lenovo XUSB MI_00 endpoint.
Its implementation is original to this port. It does not use Handheld Companion
or VIIPER code and does not write vendor HID/firmware commands.

- https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode
- https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_enable_devnode
- https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_status
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/porting-from-setupapi-to-cfgmgr32

Disable flags exclude CM_DISABLE_PERSIST. This documents the API contract, not
successful hardware validation or guaranteed cancellation of a blocked driver.

Windows SDK numeric definitions: `CM_DISABLE_UI_NOT_OK = 0x4`,
`CM_DISABLE_HARDWARE = 0x2`, `CM_DISABLE_PERSIST = 0x8`:
https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/cfgmgr32.h

## Explicit busy-reader reconnection and cursor output (0.1.13)

Original port code; no upstream controller/firmware implementation is copied.
The opt-in busy-reader operation uses the Windows SDK header's ABSOLUTE (1) and
UI_NOT_OK (4) flags, without HARDWARE (2) or PERSIST (8). Learn's function page
documents UI_NOT_OK/PERSIST; the header definitions do not establish successful
reconnection on any particular driver. The default operation remains polite.

- https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/cfgmgr32.h
- https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-input
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openinputdesktop
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow

The x64 INPUT record is 40 bytes, with its MOUSEINPUT union at offset 8. Cursor
output uses relative movement only. Desktop/focus observations narrow the allowed
output window; they are not a guarantee that focus cannot change immediately
before Windows processes input. No keyboard hooks or firmware mappings are added.

## Driver-reported slot state

ViGEm.NET v1.21.256, commit 50422c0f9703059405b133ef61f27b516339f82b,
Xbox360Controller.UserIndex reads a cached field updated by an LED notification.
The port timestamp records a property read, not a new independent XInput query.
https://github.com/nefarius/ViGEm.NET/blob/50422c0f9703059405b133ef61f27b516339f82b/ViGEmClient/Targets/Xbox360Controller.cs

## Passive button sharing and PnP evidence (0.1.16)

The shared reader, input gates, candidate-bit observer, profile utilities and
protected mouse preferences are original port code. L/R use the already attributed
passive report formats; Y/M candidate masks are observations, not physical mappings.
No Handheld Companion/VIIPER implementation or firmware setter is included.
The candidate byte is offset 18 for `04 00 A1` or 20 for `04 3C 74`, masked with
FC to exclude Back/Start, separate from stick/trigger fields in the pinned monitor:
https://github.com/corando98/GoTweaks/blob/99ed5f5f8c22bb78bfffee9b37f9a04094f30482/XboxGamingBarHelper/Labs/LegionButtonMonitor.cs

Version 0.1.20 extends the original decoder and layout/gate/UI code to the
identified Y1/Y2/Y3 (80/40/20) and M2/M3 (04/08) digital masks in that candidate
byte. These protocol constants are reimplemented; no monitor implementation,
firmware mode switch, latched register setter or HID initializer is copied.
The original Go's physical labels are documented by Lenovo:
https://download.lenovo.com/pccbbs/pubs/legion_go_8apu1/user_guide/es/index.html
RB/M1 is one control with a mode-specific name, not another independently exposed
rear input. The decoder does not assign the remaining 10/03 bits as auxiliaries.
Synthetic reports exercise both supported layouts; private captures are not
distributed and tests do not establish compatibility with every firmware.

PnP diagnostics read existing Windows events through the .NET Eventing reader.
An explicit XPath uses UTC SystemTime; no log/channel is enabled or cleared.
Missing records remain unknown rather than proof of absent vetoes.

- https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader.eventlogquery
- https://learn.microsoft.com/en-us/windows/win32/wes/consuming-events
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/cfg/ne-cfg-pnp_veto_type
