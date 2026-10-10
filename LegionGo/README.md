# LegionTweaks Lab — Legion Go Z1 Extreme port

Experimental Windows/Game Bar port in **MazTema-debug/ClawTweaks**. The widget adapts
ClawTweaks' Game Bar activation, named-pipe transport technique and Next Gen Claw
visual theme. The open hardware backend is adapted from GoTweaks' Lenovo WMI and
Windows power controls. It is an independent derivative, not an official release
of ClawTweaks, GoTweaks, Lenovo or MSI.

**This is a first functional port of the core controls, not full ClawTweaks feature
parity. Hardware behavior has not yet been validated on a physical Legion Go.**
The public ClawTweaks branch contains no hardware helper; its BUILDING.md explicitly
identifies that component as private. This port supplies its own source-available
helper rather than requiring the private MSI/Intel service.

## Implemented

| Control | Backend and behavior |
| --- | --- |
| Device detection | LENOVO, 83E1/Legion Go 8APU1, AMD Ryzen Z1 Extreme. Other devices cannot write. |
| Lenovo power mode | Quiet, Balanced, Performance, Custom using `LENOVO_GAMEZONE_DATA`. |
| Custom power limits | SPL / SPPT / FPPT via GoTweaks' `LENOVO_OTHER_METHOD` IDs. All three are restricted to 5–30 W, with SPL ≤ SPPT ≤ FPPT. |
| TDP transition | Confirm Custom mode first; read back all limits; attempt rollback on rejection or partial failure. |
| Manual power profiles | Save up to 20 named SPL/SPPT/FPPT tuples. Explicit apply only. |
| CPU Boost | Enable (Windows mode 2) / disable (mode 0), independently for AC/DC. Exact previous mode is preserved for restoration. |
| CPU EPP | 0–100 for AC/DC in the active Windows power plan. |
| Windows power mode | Efficiency / Balanced / Performance using PowrProf overlay schemes. |
| Cooling | Firmware automatic mode or maximum fan speed. No custom fan curves / EC writes. |
| Battery | 80% charge limiter, if the Lenovo WMI capability is exposed. |
| Power LED | Lenovo lighting ID 4, if supported by the installed firmware. |
| Readings | CPU/GPU temperature, fan RPM, battery and power source. Explicit refresh; unavailable reads are shown as unavailable. |
| Restore | Durable original-value journal; restore on demand or during uninstall. CPU entries retain their original plan and AC/DC identity. |

Not integrated in this version: Radeon/ADLX features, controller remapping, gyro,
RGB stick lighting, virtual controllers, RTSS/OSD, Lossless Scaling, automatic
per-game switching, automatic AC/DC TDP profiles, Intel/MSI controls and upstream
self-updaters. The original ClawTweaks source remains available in the repository;
the Legion target does not compile or invoke its MSI-specific code.

The UI is Spanish, supports touch and standard UWP gamepad focus navigation, and
uses explicit Apply buttons. Startup, status refresh and profile saving are read-only
with respect to hardware. It does not install kernel drivers or change the BIOS.

## Obtain a build

In this fork, open **Actions → Legion Go Z1E port**, select a successful run for
`port/legion-go-z1e`, and download `LegionTweaks-Z1E-experimental` under Artifacts.
An artifact is available only after the Windows packaging job succeeds; a green
core-test job alone is not a Windows build.

The artifact contains a development-signed widget, helper, public certificate,
installer, uninstaller and SHA-256 checksums. It is not a production-signed release.
The private development signing key is deleted by the build script and is never
uploaded. Source corresponding to each artifact is its workflow run's commit.

## Build locally on Windows

Required: Windows 10/11 x64, .NET SDK 8, Visual Studio 2022 with **Universal Windows
Platform development**, .NET Framework 4.8 targeting tools and Windows SDK
**10.0.26100.0** (including SignTool). Xbox Game Bar must be installed to host the widget.

From the repository root, in Windows PowerShell:

```powershell
.\LegionGo\scripts\Build-LegionGo.ps1
```

Output: `artifacts\LegionTweaks-Z1E`. The script runs the core tests, builds the
helper and UWP widget, and creates a one-year development certificate to sign this
experimental build. `-Unsigned` is available for compilation-only checks; the installer
intentionally refuses an unsigned build. The legacy `XboxGamingBar.sln` is not the
Legion target.

## Install and first hardware check

1. Extract the whole artifact, including its `Widget` and `Helper` folders.
2. Keep Lenovo's device/WMI drivers installed. Exit other tools that actively
   control TDP/fan/CPU settings while checking the port (GoTweaks, Handheld Companion,
   UXTU, etc.), so their writes do not overwrite one another.
3. Run `Install-LegionGo.ps1` from an elevated **Windows PowerShell** window under
   your Windows account. The script prints and trusts this build's development
   certificate, installs only `MazTema.LegionTweaks`, and registers the current-user
   `LegionTweaks-Z1E` logon task. Helper binaries reside in Program Files with
   administrator-only write access. No password is saved.
4. Open **Win+G → Widgets → LegionTweaks Lab** (or its Start menu entry).
5. Start with **Actualizar lecturas**. Check model/CPU, existing mode/limits, fan
   and battery. Missing WMI controls are disabled. Capture the displayed readings.
6. Apply Balanced mode and confirm the app reports verification. Then try
   SPL/SPPT/FPPT = **15/15/15 W**. The port should enter Custom mode and show those
   values after readback. Verify power behavior in your usual monitoring tool;
   a firmware limit is not a measurement of instantaneous power consumption.
7. Test CPU Boost AC/DC separately, fan maximum → automatic, and charge limit.
   Use **Restaurar valores originales** to return to the values captured before
   the port's first edits. Do not run intensive loads merely to validate installation.

The helper does not reapply settings at logon, reconnect or resume. Firmware/Windows
may retain or reset prior settings; refresh to see the actual state. The 30 W ceiling
also applies to the short/peak limits for this initial port, even if the OEM mode
reports higher original values. The live readout preserves those original readings;
the editable sliders cannot exceed this port's ceiling.

## Diagnostics / uninstall

In elevated Windows PowerShell:

```powershell
& "$env:ProgramFiles\LegionTweaks-Z1E\LegionTweaks.Helper.exe" --diagnose
& "$env:ProgramFiles\LegionTweaks-Z1E\Uninstall-LegionGo.ps1"
```

`--diagnose` is read-only, even while the service runs. It prints device identity,
per-control read failures and telemetry as JSON; it does not export serial numbers.
The helper must be stopped before `--restore` is used directly. The uninstaller
handles that sequence and stops if restoration fails, retaining the backup for retry.

The journal and helper errors are in `%ProgramData%\LegionTweaks\<Windows-user-SID>`.
The helper uses a separate, versioned local pipe. It validates both the connecting
Windows user and the installed widget package family, bounds requests, and accepts
only enumerated operations (no command execution or arbitrary WMI IDs).

## Verification

`dotnet run --project LegionGo/Tests/LegionTweaks.Tests.csproj -c Release` runs 20
behavioral tests against the real control engine with a simulated device: hardware
gating, no writes at startup, range validation, Custom-mode confirmation, failed
reads/writes, coupled-register drift, rollback, durable originals, AC/DC and power-plan
isolation, concurrency and protocol serialization. They cannot establish physical
WMI compatibility, cooling performance, Game Bar rendering or installation success.
The Windows CI job builds the actual helper and UWP package. Physical validation must
follow on an original Legion Go Z1 Extreme before calling the port hardware-tested.

See [SOURCES.md](SOURCES.md) for exact upstream revisions, implementation mappings
and license attribution. The combined derivative remains AGPL-3.0; the GoTweaks MIT
notice is included in [LICENSE.GoTweaks](LICENSE.GoTweaks).
