# AMD Radeon graphics controls

Local development feature for the original Legion Go 83E1 / Z1 Extreme. This is
not part of the currently installed 0.1.44 FPS package. No game DLL, Steam setting,
AMD runtime or driver is installed by this integration.

The AMD Radeon card offers an explicit query, RSR on/off, RSR sharpness, AFMF
on/off and AMD-only restoration. These are **global driver settings**, not game
profiles. Merely opening the view or moving the sharpness slider does not write.
No AMD setting is applied at helper startup, by profile selection or telemetry.
Driver enabled/disabled state is reported separately from in-game activity, which
remains unknown. The base AFMF API does not establish the installed AFMF variant.

Only one AMD GPU and one AMD display are writable in the first version. All
relevant supported settings must be readable. RSR can also enable GPU scaling and
disable Boost, Radeon Image Sharpening or integer scaling. Those dependencies and
Chill and Anti-Lag are included in the configuration snapshot. Unsupported and unreadable are
different states. No GPU power, tuning, telemetry or controller calls are added.

Each apply request carries a configuration revision. The helper refreshes its
interfaces/topology, rejects stale selections, atomically flushes originals before
writing, and checks the outcome by reading. A partial failure attempts rollback.
If integer scaling has a dormant value hidden while GPU scaling is off, an
explicit RSR enable transaction first enables GPU scaling, reads and durably saves
that value, and only then requests RSR. Restoration verifies the dormant value
before turning GPU scaling back off. A preparatory failure rolls back without
requesting RSR. This preparation is never run by an inspection.
Unfinished recovery blocks further edits. External changes, different topology,
or changed runtime version block restoration rather than overwriting them.
ADLX does not provide an application-wide atomic compare-and-swap with Adrenalin:
avoid editing the same graphics settings simultaneously in another application.

Originals are stored in `amd-graphics-originals.json` in the helper's existing
protected user data directory. They survive helper restarts. AMD restoration,
general Restore and console `--restore` restore them when present; a failed or
conflicting restoration retains the copy and reports failure, including during
uninstall. No AMD restoration runs automatically at startup.

## Read-only diagnosis

Build/run `Tools/AmdGraphicsProbe/LegionTweaks.AmdGraphicsProbe.csproj` on Windows
x64 with .NET Framework 4.8. The standalone tool requires no elevation and accepts
no write command or parameters. It validates model/CPU through WMI, initializes
the existing ADLX runtime normally, reads interfaces and releases them. It starts
no helper service, controller, recovery task or recorder and writes no journal.
The helper also accepts `--diagnose-amd-graphics` through its elevated executable.

In the 29 October 2026 device query, ADLX 1.5.0.124 reported RSR and AFMF supported
and disabled, RSR sharpness 80 with range 0–100/step 1, GPU scaling disabled, and
integer scaling unsupported in that state. This is a dated observation, not a live
status or proof of in-game functionality.

## Validation boundaries

Automated tests simulate configuration, driver partial failures, journal failures,
recovery and external changes. The standalone probe supplies actual read-only
hardware evidence. Native writes, full UWP compilation/package, installation and
in-game scaling/frame generation require their own validation. No real activation
is authorized by running tests or the probe. See [SOURCES.md](SOURCES.md) for ABI
references and the boundary around restricted AMD SDK material.
