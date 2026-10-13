# Source provenance

The first Legion target was authored 2026-10-10 in MazTema-debug/ClawTweaks.

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
