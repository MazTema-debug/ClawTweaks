# LegionTweaks Lab — Legion Go Z1 Extreme port

Experimental Windows/Game Bar port in **MazTema-debug/ClawTweaks**. The widget adapts
ClawTweaks' Game Bar activation, named-pipe transport technique and Next Gen Claw
visual theme. The open hardware backend is adapted from GoTweaks' Lenovo WMI and
Windows power controls. It is an independent derivative, not an official release
of ClawTweaks, GoTweaks, Lenovo or MSI.

**This is a first functional port of the core controls, not full ClawTweaks feature
parity. Lenovo modes, TDP and CPU setting readbacks, the maximum/automatic fan
transition and the restore command have been tested on the user's original Legion
Go. Effective power/frequency behavior and the new overlay/sensor support are still
under physical validation.**
The public ClawTweaks branch contains no hardware helper; its BUILDING.md explicitly
identifies that component as private. This port supplies its own source-available
helper rather than requiring the private MSI/Intel service.

## 0.1.4 — Fix nearly invisible overlay

The first device screenshot showed the overlay registered, running and receiving
live readings, but its entire contents were almost transparent. The renderer
incorrectly divided Game Bar's already-normalized `RequestedOpacity` by 100,
turning full opacity (1) into 0.01. It now applies the 0–1 value directly, matching
Microsoft's executable Game Bar sample, while retaining the bounds check.

The user confirmed successful AMD ADL readings on the Radeon 780M: GPU 41 °C,
GPU 760 mV, SoC 698 mV, GPU 800 MHz / 1% / 1 W and ASIC 4 W. All sensor reads in
that diagnostic succeeded. CPU power 0 W is the integer driver reading, not proof
of zero physical CPU consumption. This update fixes presentation; the now-working
AMD telemetry and hardware control behavior are unchanged. Full visibility with
0.1.4 and pinned operation still require checking on the device.

## 0.1.3 — Pinned Game Bar overlay and AMD GPU telemetry

Open **Abrir overlay del juego** inside the Game Bar widget, or **Win+G → Widgets →
LegionTweaks Overlay**. Press the overlay's title-bar **pin** and close Win+G to keep
it visible while playing. Game Bar controls positioning, opacity and click-through.
The overlay polls every two seconds while visible and stops polling when closed.

The overlay shows CPU/GPU temperature, GPU and SoC voltage, GPU clock/load,
GPU/CPU/ASIC power readings, the three configured power limits, fan RPM, battery
and AC/DC state. Voltage uses **V** (driver values are **mV**); power uses **W**.
ASIC, GPU and CPU power remain separately labeled: they may overlap and must not be
added together or assumed to equal the configured SPL. There is no CPU-core voltage
or FPS counter in this update.

AMD telemetry uses a read-only subset of ADL through the installed `atiadlxx.dll`
in System32. It checks PMLog's support flag for every sensor, then uses OD5 as a
fallback for GPU temperature/voltage/clock/load, plus ODN EDGE temperature. Valid Lenovo GPU temperature is a
last fallback; zero is unavailable, never a real 0 °C reading or a copy of CPU
temperature. No GPU tuning calls, additional kernel drivers or AMD SDK binaries
are installed. Availability depends on the installed AMD driver. If multiple AMD
GPUs are detected, the port refuses to guess which one is the built-in GPU.

Unavailable/invalid sensors display **N/D**. Disconnects and samples older than six
seconds clear the overlay's numbers. Each JSON reading includes its source/units
where applicable; snapshots include their capture time. A separate read-only
`telemetry` request polls only the Lenovo mode/limits and sensors. Control edits and
overlay polls share a serialized pipe, preventing overlapping requests.

The user confirmed 0.1.2's fan, battery and power-light switches remain usable after
repeated changes. Their latest readback was Custom, 10/10/10 W, Windows efficiency,
AC Boost on/EPP 50 and DC Boost off/EPP 80. Reaching the actual 80% charge cutoff,
effective package power under load and pinned overlay behavior remain to be
verified on the device. AMD sensor readback was subsequently confirmed as described
above. Automated tests do not establish physical sensor accuracy.

## 0.1.2 — System switches remain usable after applying changes

Fan, charge-limit and power-light switches could stay gray after a refresh or
change even when their hardware readings were valid. `Send` temporarily disables
the parent panel while rendering, and the old renderer copied the button's
effective (inherited) `IsEnabled=false` to the switch's local state.

The renderer now assigns availability directly from the reading to both controls.
Missing/nonbinary readings still disable the affected control; a later valid
reading restores it. The parent panel still blocks all input while a request is
running or the device is unsupported/disconnected. Hardware writes, limits and
restoration behavior are unchanged.

Three regression tests exercise the production renderer with test-only controls
that model UWP's documented inherited `IsEnabled` behavior. They cover repeated
refresh/apply cycles, unavailable readings, and recovery. These are not live
UWP/Game Bar UI tests; the corrected behavior still needs an on-device check.

Update with the new archive's `Install-LegionGo.ps1` under the same Windows user,
then refresh and confirm that the System switches remain usable after an applied
change. The previous version does not need to be uninstalled.

## 0.1.1 — WMI property-name fix

The first physical test on an 83E1 / Z1 Extreme confirmed installation, helper
communication and Lenovo mode switching. The 0.1.0 diagnostic reported
`WMI no devolvió Value.` for the custom power limits, fan controls, charge limit
and temperature/RPM readings, all of which use `GetFeatureValue`.

0.1.1 preserves WMI's case-insensitive property lookup when copying a method
response. A provider field named `value` now satisfies a request for `Value`,
as it does in GoTweaks. Missing/null fields still fail closed, nonzero return
codes still fail, and missing-field errors list the returned field names.
Regression tests exercise the same output parser used by the Windows helper.
The user subsequently confirmed the corrected readings on an 83E1 / Z1 Extreme:
TDP 15/15/15 -> 12/12/12 -> 15/15/15, maximum fan about 8075 RPM then automatic
about 3227 RPM, and separate AC/DC Boost and EPP writes. The restore command
reported success and CPU settings returned to their captured values. These
readbacks do not establish effective package power/frequencies under load.
At that version GPU temperature returned zero; 0.1.3 rejects that placeholder and
adds AMD-driver readings. The user subsequently confirmed the charge-limit and
power-light switches work; the physical 80% charge cutoff remains to be tested.

To update, close the widget, extract the new artifact into a fresh folder, and
run its `Install-LegionGo.ps1` as administrator under the same Windows account.
There is no need to uninstall the previous port: the installer updates the
package/helper while preserving saved originals and profiles. Run `--diagnose`
and refresh the widget before testing any newly available control.

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
| Readings | CPU temperature/fan via Lenovo, GPU temperature/voltage/load/clock/power via AMD ADL, battery and power source. Missing readings are N/D. |
| In-game overlay | Separate pinnable Game Bar widget, visible-only polling every 2 s, timestamps and stale-data clearing. |
| Restore | Durable original-value journal; restore on demand or during uninstall. CPU entries retain their original plan and AC/DC identity. |

Not integrated in this version: Radeon/ADLX features, controller remapping, gyro,
RGB stick lighting, virtual controllers, RTSS integration, FPS measurement, Lossless Scaling, automatic
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

`dotnet run --project LegionGo/Tests/LegionTweaks.Tests.csproj -c Release` runs 36
behavioral tests against the real control engine with a simulated device: hardware
gating, no writes at startup, range validation, Custom-mode confirmation, failed
reads/writes, coupled-register drift, rollback, durable originals, AC/DC and power-plan
isolation, concurrency and protocol serialization, plus WMI output parsing and
switch rendering under a disabled parent, read-only overlay polling, AMD ABI layout,
sensor support/range handling, GPU selection and voltage/unit serialization. They cannot establish physical
WMI compatibility, cooling performance, Game Bar rendering or installation success.
The Windows CI job builds the actual helper and UWP package. Complete physical
validation on an original Legion Go Z1 Extreme is still required for the new overlay and sensors.

See [SOURCES.md](SOURCES.md) for exact upstream revisions, implementation mappings
and license attribution. The combined derivative remains AGPL-3.0; the GoTweaks MIT
notice is included in [LICENSE.GoTweaks](LICENSE.GoTweaks). The AMD interop subset
retains LibreHardwareMonitor's MPL-2.0 notice and [license](LICENSE.LibreHardwareMonitor).
