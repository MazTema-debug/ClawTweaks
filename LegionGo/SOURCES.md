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
