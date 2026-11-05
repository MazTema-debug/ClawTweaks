# PawnIO CPU probe — original Legion Go / Z1 Extreme

Standalone diagnostic for Windows x64 / .NET Framework 4.8. This is separate from
the installed helper/widget and is not part of the application package. It neither
installs PawnIO nor changes services, CPU limits, power, controller or RTSS settings.

The requested metrics are **effective frequency** and **maximum observed frequency
during the capture**. A new capture starts a new maximum. A session peak is a
measurement statistic; future minimum/maximum frequency controls require separate
configuration/readback support.

## Measurements and limitations

- Effective frequency has **eight primary per-core values**: the arithmetic mean
  of both SMT logical effective frequencies in the same sweep, using Windows'
  actual core masks. Observed idle zero participates in the mean; a missing or
  invalid sibling makes the core N/D. No previous sweep is reused. For example,
  4000 and 0 MHz yield a 2000 MHz effective average, not a physical PLL clock.
  Logical frequencies still come from read-only APERF deltas divided by monotonic
  elapsed seconds and are retained only in the diagnostic file.
- Maximum observed frequency is tracked per **physical core**, across its siblings,
  from hardware-Pstate FID/DFS reads with a 200 MHz reference. The instantaneous
  decoded value is used internally to update that peak; it is not an output metric.
  Raw register values remain in the CSV for inspection. Peak timestamps identify
  historical maxima, rather than representing a new current reading.
- The first counter sample, a failed read, counter reset/wrap, long sampling gap or
  invalid timestamp produces empty frequency fields. A new baseline is required
  after errors. Zero observed cycle progress is distinguished from missing data;
  a capture cannot claim counter support unless all sixteen logical processors
  demonstrate positive progress during the run.
- Values are diagnostic candidates pending independent validation under idle/load,
  suspension and game conditions. Sampling wakes threads and may affect idle
  behavior. No synthetic CPU load is started. No live overlay integration is added.

Basic native validation on the original Z1 Extreme passed with two ten-second
captures, including a separate four-second workload pinned to one logical
processor. Effective readings followed that workload and returned low afterwards;
per-session peaks and affinity restoration were confirmed. Revision 2 adds the
user-selected SMT mean: the Windows suite passed 860 cases, including 21 probe
cases. A further five-second native capture produced 80 core rows (8 initial N/D,
72 valid means), checked against their diagnostic siblings, with zero read errors.
Independent comparison, broader SMT workloads, suspension and game behavior remain
pending. HWiNFO's effective-clock sensors are the preferred comparison candidate;
its sensor grouping must be checked before equating it with our explicit mean.

## Build and prepare

```powershell
dotnet build LegionGo/Tools/PawnIoCpuProbe/LegionTweaks.PawnIoCpuProbe.csproj -c Release
powershell.exe -NoProfile -File LegionGo/Tools/PawnIoCpuProbe/Prepare-Module.ps1 -OutputDirectory C:\Private\CpuProbe
```

Preparation downloads the fixed official PawnIO.Modules **0.2.11** archive, verifies
its SHA-256, and extracts only `AMDFamily17.bin` and the release's `COPYING`. It does
not execute or load them. Keep these private generated files outside the repository.
The application installer is not used. Source provenance is in `LegionGo/SOURCES.md`.

## Run

With no `--read`, the tool only inventories identity and Windows topology:

```powershell
.\LegionTweaks.PawnIoCpuProbe.exe --output C:\Private\CpuProbe --label inventory
```

For an explicit hardware capture, run the following **as administrator**:

```powershell
.\LegionTweaks.PawnIoCpuProbe.exe --read --module C:\Private\CpuProbe\AMDFamily17.bin --output C:\Private\CpuProbe --seconds 10 --interval-ms 500 --label ambient
```

The tool does not elevate itself. Duration is restricted to 2–60 seconds and interval
to 250–2000 ms. Ctrl+C requests a normal stop. A unique subdirectory contains
`cores.csv` (primary, eight cores per sweep), `samples.csv` (diagnostic logical
readings) and `summary.json`; existing captures are not overwritten. An error
returns a nonzero exit code and remains in the summary, including affinity readback.
Do not infer completed validation from exit code alone: inspect error counts,
counter progress, module provenance and the exact scope of the capture.

## Native boundary

Model, CPU vendor/name/signature and 8C/16T counts are checked separately from USB.
PawnIOLib is loaded only from protected Program Files, with Windows trust/publisher
verification and restricted DLL search. File version **2.2.0** and exported API/ABI
**2.0.0** are separate checks. Unknown ABI majors are refused.

Only the fixed, hashed official module can be loaded. The wrapper exposes only
`ioctl_read_msr` for `C0010293`, `C00000E7` and `C00000E8`. Return counts and HRESULTs
are checked. There is no MSR write, SMN/PCI access, arbitrary operation name,
counter reset, CPPC/P-state configuration or service initialization. Affinity is
changed only for the sampling thread, verified before/after each tuple and restored
in `finally`. Failure to confirm restoration aborts the run. Handles and library
are closed on normal and error paths.
