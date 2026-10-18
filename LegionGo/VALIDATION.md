# 0.1.19 development validation

The ABI 2.21 correction must accept its documented header and extended OSD entries,
preserve application data/other owners during publication and retirement, and keep
rejecting newer unreviewed versions without writes. These are simulated-memory
checks, not confirmation of native rendering or game compatibility.

387 automated tests cover the simulated RTSS shared memory, owner isolation,
primary-slot exclusion, ABI/bounds checks, bounded lock attempts, reopen after a
server restart, single-flight sampling, slow reads, OFF/suspend/cleanup, formatting,
IPC capability and ADL context lifetime. These tests do not open a real RTSS mapping,
launch RTSS, hook a game or move a hardware control. Full UWP packaging is separate.

On-device validation, with the user present, remains pending:

1. Back up and install the verified candidate. Confirm initial RTSS OFF, unchanged
   profiles/power/Legion Space, and that the Game Bar overlay still works.
2. Start RTSS separately and enable its OSD for a supported game using RTSS's own
   controls. In LegionTweaks press Activar overlay RTSS, close Game Bar, and compare
   temperatures/units/limits against the normal telemetry panel. Check FPS/frame
   time from RTSS, the UTC capture time, Unicode-free text and six-line layout.
3. With a separate RTSS OSD client active, enable/disable this port and confirm only
   its own text is added/retired. Do not reset another client's slot or global flags.
4. Test RTSS closed/reopened, occupied slots and a lost helper connection. Confirm
   state messages, no invented numbers, and cleanup/recovery of only this owner.
   Forced process termination may leave old timestamped text until explicit recovery.
5. Suspend/resume separately: publication must return OFF and require manual ON.
   Verify button behavior and OFF while a sensor read is delayed in a controlled
   development setup; the software suite alone does not verify native driver timing.

P1 automatic reconnection and virtual startup remain unresolved/disabled. No RTSS
validation result establishes controller compatibility or a fix for CM 0x17.
Keep private game/hardware results out of this public checklist.

## Previous 0.1.17 development validation

329 automated tests pass locally. The .NET Framework 4.8 x64 helper compiles
without warnings or errors. Full UWP packaging is verified by the Windows CI job.

Software checks cover saved-profile comparison without mutation, CSV schemas and
quoted multiline fields, input/row/field/time bounds, cancellation, invalid metrics,
separate sensor provenance, and capture/receipt freshness under visibility changes,
repeated timestamps and clock changes. They do not read private session files or
execute hardware controls. Helper and UWP compilation are checked separately.

On-device validation remains pending for this candidate:

1. With a dirty profile editor, compare two saved profiles. Change the comparison
   choice and close the dialog; selection, draft and saved catalog must be retained.
   Verify custom/unchanged limits, AC/DC and physical/virtual layouts are clear.
2. Select a completed CSV through Registro. Confirm sample counts and sensor units
   against its contents, including zeros, unavailable metrics and observed gaps.
   Cancel the picker and a running analysis; neither should change the file,
   recording state or hardware. Unsupported/malformed files must report an error.
3. Check that a manual main-panel reading is labelled previous after expiry without
   periodic hardware refresh. Refresh explicitly to renew it. Hide/show the overlay,
   interrupt its helper connection and verify that old numbers do not appear current.
4. Record a new short session and verify app_version matches the actual helper;
   previously saved CSVs remain unchanged. Recheck normal telemetry/recording.

Startup controller activation remains disabled and conditional on successful P1
validation. No PnP/controller trial or installation is implied by the software suite.
Private hardware results must not be added to this public checklist.

## Previous 0.1.16 development validation

300 automated tests and full Linux/Windows CI passed for the previous candidate.
Software checks include L/R map compatibility, a single simulated vendor reader,
Game Bar reservation, neutral/stale release, capture cancellation/timeout, closure
before P1, exact PnP evidence correlation, profile draft utilities and mouse settings
persistence under slow/failed writes. New L/R mappings, Y/M identification, Game Bar
utilities and preference persistence still require physical validation separately.
Y/M mapping remains disabled; CM 0x17 and startup activation remain unresolved.

## Previous 0.1.15 development validation

This candidate adds passive, bounded tracing to explicit automatic player-1
preparation. Validate ordering around preflight, ViGEm creation, disable failure,
physical opening and reader disposal. A module flag is an observation of loading,
not an attribution of an open controller handle. Trace errors must not prevent
cleanup or change a failed transition into success. Compare trace timestamps to
the independent Windows device events; do not infer successful P1 from recovery.

256 automated tests pass locally, including nine cases for bounds, validation,
privacy, snapshot copying, failed optional storage and stale-writer generations.
The .NET Framework 4.8 x64 helper compiles without warnings or errors. Full UWP
packaging and signing are checked separately by the Windows CI build.

The trace must remain within its count/size bounds and omit input data, profiles,
SID, recovery token, device identifiers and paths. Test cancellation and failure
as software behavior; real Windows vetoes, latency and recovery require a separate
explicit hardware test. No firmware setters or new native disable flags are used.

## Previous recovery correction (0.1.14)

247 automated tests pass locally. Six new cases cover confirmed journal retirement, fresh matching outcomes, incomplete/foreign outcomes and present replacement journals. The .NET Framework 4.8 x64 helper compiles with
no warnings or errors. New cases cover isolated XInput report validation, explicit
busy-reader selection and model/profile gating, mouse neutral/focus/gap/deadzone
behavior, delayed native observations with a fake output, x64 INPUT ABI, safe
diagnostic serialization and draft comparison. These are software checks; no
native movement or device transitions are performed by the suite.

The full UWP/XAML package still needs the Windows CI build before installation.
On-device checks, performed explicitly with games closed:

1. Start from the physical in P1 and try ordinary automatic preparation. Inspect
   the result and recovery; a driver veto remains a valid failure, not success.
2. If needed, separately select the unchecked busy-reader option, read its warning
   and test its journaled recovery on the exact endpoint. Confirm virtual P1 and
   changing input/output; repeat from already-active P1 without reconnection.
3. Test the mouse explicitly: neutral, movement, sensitivity/deadzone, right-stick
   suppression in the virtual output, OFF, stop and a later reconnect staying OFF.
   Inspect lock/suspend behavior separately without claiming a software test proves it.
4. Edit a profile and test cancel/confirm on selection and reload. Verify saved
   data is unchanged when canceling, and Game Bar text/picker behavior separately.
5. Query/copy/export diagnostics and compare package/helper versions, cached slot
   timestamps and recovery status. Check that reports contain no private identifiers.

Startup activation remains disabled until the P1 mechanism has been validated.
Private hardware results are not published in this checklist.

## Previous 0.1.12 development validation

## Automatic reconnection candidate

192 automated tests pass locally, including 26 automatic sequence tests, 19
physical recovery rules tests and six automatic IPC tests. The .NET Framework
4.8 x64 helper compiles without warnings. Native workers and physical device
transitions still require separate hardware checks; fakes do not verify a driver.

The recovery tests check the disable flag against the Windows SDK numeric ABI
and distinguish a consumed journal from corrupted data. They also preserve the
first causal failure through later recovery results and reject foreign tokens.

With the game and Steam closed and FPS OFF, start the automatic P1 button from
a saved virtual profile. Confirm the brief physical disconnect, physical return,
virtual P1, moving input/output and no pending recovery journal. Repeat from an
already active P1 and confirm it does not reconnect. Verify cancellation and
recovery in a controlled test before considering startup automation. Record
power-original/profile hashes and confirm that Legion Space and the active power
plan remain unchanged. Keep the manual FPS path as a fallback.

Hardware results from a private session must not be published in this checklist.

## Previous 0.1.10 development checks

Reference: `port/legion-go-z1e`, `bda098ac033d64c2644aab7ac74ffb6b06581b3b`
(0.1.9). Development branch: `codex/legion-space-profiles`.

## Automated checks

```powershell
dotnet run --project LegionGo/Tests/LegionTweaks.Tests.csproj -c Release
dotnet build LegionGo/Helper/LegionTweaks.Helper.csproj -c Release
```

141 tests pass; helper builds for .NET Framework 4.8 x64. Service tests use fakes
for every SCM operation; journal tests use temporary files. No device settings or
installed components are changed by these checks.

The temporary validation harness compiles all widget C# against
Microsoft.Windows.SDK.Contracts 10.0.26100.1 and the project's actual Game Bar
7.3.2506120 WinMD, using generated declarations for named XAML elements. This
checks C# API names/types, not XAML compilation, package contents or runtime UI.
It reports one unused-field warning from the generated `Equalize` declaration.
The local UWP build is blocked by missing `Microsoft.Windows.UI.Xaml.CSharp.targets`.
Build the actual package on a Windows development machine/CI with the dependencies
in `scripts/Build-LegionGo.ps1` before installing this candidate.

## Pending manual checks on the original Go

These steps are a future checklist, not a record of executed hardware tests.
The existing installed 0.1.9 and its settings were retained during implementation.

1. Build the UWP package; review the diff and install only when ready for testing.
2. At startup, open Sistema and query Legion Space. Compare running state and
   startup mode with Services. Test a stopped/disabled baseline; opening, querying
   and closing the widget must preserve it.
3. If testing writes, record the existing service state first. Use the explicit
   start/manual button, then restore. Starting from disabled/stopped must restore
   disabled/stopped, including after restarting the helper between actions.
   Check that stopping/disabling affects only `DAService`.
4. Confirm that general device restoration, profile application and closing the
   widget do not re-enable Legion Space. Its saved original remains separate.
   Do not uninstall the app merely to test this branch.
5. In Game Bar at 400x520 and normal/large sizes, edit a new and an existing profile
   name with touch buttons and a physical keyboard. Check ñ/accents, uppercase,
   spaces, selection replacement, backspace, 40-character limit, duplicate names,
   Cancel, Use name and explicit Save. Reopen and confirm persistence. Record what
   happens if Windows' own keyboard appears; the new editor is not evidence of
   the earlier failure's cause.
6. Export saved profiles, cancel each picker, and import a valid file with a
   duplicate name. Confirm the preview, distinct IDs, retained originals and local
   backup. Import a corrupt/unknown-format file and one that would exceed 20 profiles;
   confirm no partial import. Export/import must not apply hardware settings.
7. Test selectors inside Game Bar and from the standalone Start-menu app. Check
   scrolling, accessibility labels and error notices. For cloud destinations,
   only a completed provider update should be reported as export success.
8. Open two editor windows, save in one, then try saving/importing stale data in
   the other. Expect a reload notice, preservation of the first save, and an
   explicit discard confirmation before reloading the second editor.

9. Test the new player-1 assistant separately from the normal profile button.
   Select a saved virtual profile with unsaved editor changes, start the assistant
   and verify it uses the saved layout without touching any power setting/journal.
   Close games and exit Steam before starting. Confirm that active Diablo/Steam
   causes rejection before disconnecting a working virtual session.
10. Starting with a physical P1, follow FPS ON and OFF instructions and confirm
    the final virtual is P1 and the physical returns as its source. Starting with
    a free P1, confirm no unnecessary FPS step. Check LT press/release with an
    independent reader and then all buttons, sticks, triggers and rumble in a game.
11. Cancel at each step, let the FPS wait time out, close/reopen Game Bar, and
    attempt another profile from another window. Confirm cancellation remains
    available and profile rejection happens before power writes. Always return
    the physical FPS switch to OFF. No automatic device reset should occur.
12. Check a foreign controller arrival, ambiguous source, lost P1, supervisor
    termination, SDL open failure and HidHide activation/restoration failure in a
    controlled development setup. Verify cleanup/pending-originals reporting;
    do not introduce failures during an ordinary gaming session.

The new assistant is not validated by a manual/external reservation procedure
performed with an older installed version. No automated result establishes game
compatibility, stability after reconnect/resume or performance gains.
