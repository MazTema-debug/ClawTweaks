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

## Game Bar overlay

- Microsoft widget API: https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widget
- Widget-to-widget activation: https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widgetcontrol
- Pinning/click-through: https://learn.microsoft.com/en-us/xbox/game-bar/guide/click-through

`OverlayPage` uses a separate `LegionOverlay` app extension, Game Bar visibility and
opacity events, and read-only polling. It does not inject code into games or
capture frames. Actual Game Bar rendering remains a device validation step.
