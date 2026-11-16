# Synthetic maintenance harness

This net48 x64 test executable compiles the current Core/Helper sources and the
gate/admission tests. Its startup object is the global synthetic `Program`, never
`LegionTweaks.Program.Main`. GateHarness.cs and GateHarnessSupport.cs are the
project's original AGPL-3.0-only fixture entry/support sources previously used by
the isolated Mando validation; they contain no private notes or machine paths.

Build with `dotnet build LegionTweaks.MaintenanceHarness.csproj -c Release` on
Windows. Run the resulting executable for its focused tests. The PowerShell runner
also loads `Program+FixtureAdmission` and `Program+FixtureMaintenanceEnvironment`
from this assembly to exercise the real maintenance session against synthetic
resources. Pin the freshly compiled executable by SHA-256 when passing it to the
runner. A regular helper executable cannot substitute for this fixture.

This harness and the separate readonly-host build are validation outputs. Neither
is copied to the shipped package. Return49 tests additionally require the exact
preserved .49 ZIP, verified by Get-Return49Fixture.ps1; it is read, never executed.
