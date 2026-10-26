# Desktop taskbar and foreground candidate 0.1.42

The 0.1.41 minimize/X taskbar-removal and restore-over-another-app checks failed.
Explicit interface Exit, helper/virtual-target survival, and desktop/widget reload
with live inputs passed separately. A grey taskbar hover preview is not the same
as a grey hosted widget. Keep those outcomes separate when retesting this version.

Software checks cover hide rollback after refused Shell removal, restoration even
when AddTab fails, ownership loss without foreign-window writes, visibility and
minimized readback, cached-hidden revalidation, serialized restore/close ordering,
queue release after failure, and gesture expiry/new input/tick
wraparound. They do not establish real taskbar membership or foreground behavior.

After the complete signed package, test one at a time:

1. Minimize the desktop with its button. The running-window taskbar entry must
   disappear while one notification icon remains; a pinned shortcut may remain.
   Open from the tray with another application in front: the same page should
   become foreground. Repeat X, including repeated close while hiding is pending.
2. Repeat widget-first/desktop-first launches and reopening the desktop shortcut;
   retain a single desktop and tray. Compact Mode must work without pinning.
3. Exit through the tray while a hosted widget and virtual player 1 exist. Verify
   the UI/tray terminate but helper/target remain. Reopen desktop and then Game
   Bar: normal widget, live input/release, no manual widget close/reopen or new
   controller activation. Keep native-bar navigation results separate.
4. Once actual tray hiding passes, suspend/resume while hidden with virtual active
   and then manually off. Restore through the tray, check its page/icon and live
   readings; a stopped virtual must stay off. A taskbar-minimized window does not
   satisfy the hidden-in-tray precondition.
5. In development, simulate refused hide/DeleteTab/AddTab, invalid/ambiguous frame
   and connection/SDK-close failures. Do not induce device/helper/driver failures
   in an ordinary session. No automatic fallback may activate a controller.

The diagnostic trace may omit an exit event, but writes the next complete snapshot
before replacing the previous file. It is private, bounded and nonfatal, not a
public artifact or durable audit log. Public Shell APIs acknowledge requests but
offer no taskbar-membership getter; actual disappearance needs visual validation.
The deferred input-redirection change remains excluded.

# Desktop lifecycle candidate 0.1.41

Retest the three 0.1.40 failures separately in Compact Mode, without pinning.

1. Minimize from a desktop-first launch. Its **running-window taskbar button**
   must disappear, with one notification icon remaining. An application shortcut
   pinned to the taskbar may remain; distinguish it from a running window. Restore
   the same page using the tray. Repeat with the widget already open first.
2. X must hide the desktop without terminating its tray or hosted widget. Repeat
   an X press while a hide is in flight; no duplicate view or icon should appear.
3. With a hosted widget open, use the tray's explicit interface Exit. Reopen desktop
   normally and then the widget: no grey panel or manual widget close/reopen should
   be needed. Check that the independent helper and existing virtual target survive.
4. Repeat suspend/resume and physical-off behavior, and verify automatic input
   refresh/release in desktop and hosted views. Source routing is unchanged.
5. A refused native window operation or a missing/ambiguous frame must not be
   reported as hidden. A failed SDK widget close must leave the interface alive.
   Development fault injection remains separate from ordinary device checks.

Software tests cover selection of the correct shell frame, rejection of foreign,
unverified and ambiguous frames, repeated-close state, and exit acknowledgment,
failure and timeout. A read-only adapter probe can inspect the actual frame state;
it does not test hiding/restoration or claim that the native navigation issue is
fixed. LocalState/desktop-tray-lifecycle.txt is private evidence, not a public
artifact. Preserve it if a lifecycle check fails before repeating that check.

# 0.1.39 controller recovery and physical return candidate

Software checks cover delayed HID readiness after SDL returns, one bounded outage
budget, held controls until release, cancellation, same target slots, hiding before
physical restoration, partial protection failure, and selective hide verification.
Physical return checks require one controller, skip an unnecessary cycle, use at
most one supervised cycle and reject an unconfirmed result. Known recovery reasons
survive diagnostic sanitization; private appended text does not.

After complete helper/UWP packaging, check one at a time on the device:

1. Start with games closed, attached halves and FPS OFF. Activate Desktop once;
   check live input and one position per short D-pad press in BOTH the native Game
   Bar icon strip and the widget. Repeat after an ordinary Windows reboot, with
   Steam initially open and then closed if the failure remains.
2. From two virtual players, close games and use Devolver mando físico once. Wait
   for completion; confirm both targets and hiding are gone, the checked physical
   slot is player 1, and native/widget D-pad/A work without a manual FPS cycle.
   Preserve any error before another attempt. Do not use a slot alone as proof
   of working navigation.
3. With Desktop active and Game Bar foreground, test FPS ON for three seconds then
   OFF once. Release controls and wait up to 20 seconds. Check the same virtual
   player, fresh HID/SDL input, release gating and native navigation. If it fails,
   retain the current diagnostic rather than repeating the cycle.
4. Recheck automatic readings with desktop minimized/closed and a passive pinned
   widget, both split presets, Isaac independent inputs, detach/reattach and
   suspend/resume. A manually stopped virtual must remain off after waking.
5. Cancel a manual FPS preparation at its waiting step; confirm no additional
   mode cycle starts and the instructions still require FPS OFF.

The native double-navigation cause and the hardware results remain unconfirmed.
No GameInput exception removal, OptiScaler fix or RTSS redesign is part of this
candidate. Do not inject driver/supervisor failures during an ordinary session.

# 0.1.38 Game Bar focus continuity candidate

Software checks distinguish an interactive focused widget from a passive pinned
one, retain a verified foreground host after the widget lease expires, and stop
direct input after both sources end. Windows also checks that an ordinary process
and an invalid process ID cannot claim the native Game Bar host identity.

After full UWP packaging, check one at a time on the device:

1. Desktop virtual player 1: open Game Bar and keep Mando visible for at least
   30 seconds without manual refresh. Hold/release LT, RT, sticks and A/B; the
   direct-input label and live physical/virtual readings must remain current.
2. Move focus between the native icon bar and the widget. Check a short D-pad
   press advances exactly one position. Repeat with desktop minimized and closed,
   and in compact mode. A timestamp alone does not establish fresh input.
3. Repeat live refresh for General players 1 and 2, then Isaac. Check independent
   outputs and release without repeatedly pressing Actualizar estado del mando.
4. Pin the widget, dismiss Game Bar and return to gameplay. A passive pinned view
   must not retain the direct route; held inputs must wait for physical release.
5. Return the physical controller. Check virtual targets are removed and hiding
   is restored. If native navigation stops, follow the displayed normal Game Bar
   close/reopen instruction; do not claim an automatic host reconnection fix.
6. Check suspend/resume and detach/reattach retain the existing recovery behavior.

Intermittent double navigation and startup failures are not declared resolved by
these software checks. This candidate changes no GameInput/HidHide exemption,
OptiScaler shortcut or RTSS appearance setting.

# 0.1.37 foreground input candidate

Software checks cover each standard button, independent analog triggers, stick
signs/range/neutral, both accepted report offsets, invalid packets, cache expiry,
single-reader sharing, preparation/suspend/capture invalidation, per-view lease
ownership and out-of-order/expired observations. Source changes require neutral;
a filtered SDL zero cannot release a still-held physical Game Bar button.
Status context requests must not activate a controller or write energy/profile
settings. Failure evidence must exclude messages and source paths.
Lifecycle close events must queue behind a busy request with their original
timestamp. Intentional button-identification capture must pause output without
being treated as a failed HID source; release controls before it rearms.

After a full UWP package build, check on the device one at a time:

1. Prepare Desktop as player 1, then open Game Bar first. Check the direct-input
   label, increasing reports, each standard control and release. Check actual
   D-pad/A/B navigation, rather than inferring it from the viewer alone.
2. Open/close and minimize the desktop app with the widget present. Both views
   must show the same fresh input while Game Bar is foreground. Repeat opening
   Game Bar over the desktop and another application, without reactivating.
3. Hold B while leaving Game Bar. Return all controls to rest before resuming the
   game. Confirm a held navigation button is not replayed into the game and SDL
   input resumes. Repeat closing only the widget while Game Bar stays open.
4. With Game Bar closed, confirm normal sticks/triggers, rumble and the saved
   controller layout. Then test both split presets without crossed players.
5. Check detach/reattach and suspend/resume with an active virtual. A user-stopped
   virtual must stay stopped. Confirm no held input survives a missing report.
6. Check one Windows startup with games closed. If preparation fails, preserve
   its current diagnosis and protected failure evidence before another attempt.
   An occupied player-one slot alone does not prove that input is arriving.

No claim is made that all Game Bar focus/lifecycle cases or intermittent startup
failures are resolved before these hardware checks. OptiScaler and RTSS visual
redesign remain outside this change.

# 0.1.36 Game Bar views and compatible launch accesses

Software checks cover a status read and queued commands completing after their
originating synchronization context stops servicing continuations. They also
cover game-access validation, independent catalog copies and option persistence.
On Windows, a harmless test executable is launched through the generated access
with spaces, Unicode, percent, exclamation, caret, ampersand and parentheses in
its path. It verifies the child environment and working directory with the option
on/off and checks that the parent environment remains unchanged. These checks do
not validate real Game Bar rendering, Steam URI handling or game input.

On-device checks after a full UWP build, one at a time:

1. Open Game Bar first, Mando, with an already active virtual. Hold/release
   sticks, LT/RT and A/B. Confirm the received timestamp/frame count and physical
   and virtual values update without manual refresh, including compact mode.
2. Open desktop as well, then close only desktop while Game Bar has a pending
   read. The widget must retain its content and continue receiving fresh input.
   Repeat the reverse opening order and close/reopen only the widget. Preserve
   a draft in one view and verify reactivation does not replace it.
3. Hide/reopen Game Bar and suspend/resume. The visible page must resume updates;
   touch Actualizar estado del mando without losing the panel or activating a
   controller. A disconnect must show an unconfirmed/stale reading, not fresh
   zeros or a new controller session.
4. In both desktop and Game Bar, Consultar and Abrir Lossless Scaling. Confirm
   installed/stopped and then running by a separate consultation. URI acceptance
   alone is not sufficient. Do not change LS settings or enable scaling for this
   check.
5. Desktop → Sistema → Accesos compatibles: choose BG3's executable, name it,
   select the SDL option and save. Confirm nothing starts. Reopen the app and
   verify the saved game/option; unsaved changes must not be exported as saved.
6. Generate the access using the file picker. Cancel once (no launch), then save.
   Start the game from that access with the virtual already prepared. Check
   movement and A do not add an unwanted second player. Compare a separately
   generated access with the option off only when deliberately investigating.
   Normal library launches do not inherit this saved preference automatically.
7. Cross-view saves must reject a stale catalog. Invalid saved data must be
   preserved/reported. Test these faults only with isolated test data.

The SDL option is not a claimed solution for MD2's Steam desktop translation.
OptiScaler fixes and RTSS layout redesign remain outside this block.

# 0.1.35 Lossless Scaling and startup history

Software checks use fake application/registry probes for missing executables,
same-name unrelated processes, denied image access, ambiguous installations,
bounded version text and strict read-only IPC. Background status must not inspect
applications, read sensors or write power/controller settings. History checks cover
save/rearm persistence, one record per run, eight-run retention, immutable copies,
corrupt records, interrupted guards and logging failures independent of input.

On-device checks, one at a time:

1. With Lossless Scaling installed, press Consultar in Sistema. Compare installed,
   running and file-version information with the actual process/file. Opening the
   panel or querying other status must not launch it or enable scaling.
2. Close Lossless Scaling normally. Consult again: stopped. Press Abrir Lossless
   Scaling and then Consultar: Steam handles launch; confirm the process separately.
   Repeat the access from Game Bar. A URI accepted by Windows is not proof of focus
   or active scaling. No game/LSFG configuration should change.
3. Test absent/unreadable installation and unavailable Steam handling in a suitable
   test environment; no arbitrary path picker, launch arguments or direct elevated
   execution should appear. Do not uninstall the user's tools for this check.
4. Desktop: inspect last startup and Consultar historial de inicio. Save the existing
   startup preference without changing it. The last recorded decision/reason must
   remain while the current preference confirmation changes. No controller starts.
5. On a later intentional helper restart, confirm a new decision persists, an
   in-progress activation updates its own record, and later controller session
   errors do not overwrite that startup result. Validate corrupt/denied storage
   only in an isolated test environment, not by editing the live protected guard.

The 0.1.34 hardware checks below remain pending until individually confirmed.
OptiScaler fixes are outside this block. The UI redesign is a separate later task.

# 0.1.34 RTSS, Game Bar status and diagnostic corrections

The fake RTSS memory tests start with old text in all three owned text fields and
duplicate exact-owner slots. Publication must leave one extended text string,
retire the duplicate, keep foreign/reserved bytes and graphics, and retain bounded
locking/cleanup. Formatting tests cover the top-left inset, final line boundary,
preview without positioning markup, and text-size fallback. Request-channel tests
cover skipped busy polls, a fixed read-only payload, shorter budget, recovery after
transport failure, and fresh held/released helper status without hardware writes.

On-device checks, separately from software checks:

1. In Game Bar, open Mando with an active virtual controller. Hold sticks, LT/RT
   and A/B, then release; compare both input and output with the standalone app.
   Touch Actualizar estado del mando: focus and Game Bar must remain available.
   Close/reopen Game Bar and change widgets; live status must resume. A stopped
   connection must show no current sample rather than retain a held input as live.
2. Change an RTSS draft from saved 125% to 150%. Draft, saved and helper summaries
   must remain distinct. Activate: Cancel leaves OFF; Use saved publishes 125%
   and retains the draft; Save and activate explicitly stores/publishes 150%.
   Saving by itself must never turn the overlay on. Reopen and verify persistence.
3. Render Minimal, Full and Custom in a supported application. Check one block,
   no second header, top-left margin, correct metrics/scale/orientation and complete
   disappearance on OFF. If other OSD clients are present, verify their own text
   and configuration are retained. Positioning and rendering need actual RTSS.
4. With a recovered historical PnP failure and no pending PnP journal, diagnostic
   text must label that result historical and its error as belonging to that
   operation. Actual controller errors and outstanding recovery stay explicit.
5. Recheck the split presets below. OptiScaler behavior is outside this change.

# 0.1.34 General and Isaac split presets

Software checks cover General's four rotated ABXY positions on both halves,
256 D-pad/face combinations with neutral RS, simultaneous independent movement,
deadzone/range preservation, menu/auxiliary ownership, full release, legacy Isaac
behavior, unknown-preset rejection, explicit immutable IPC, older-helper refusal,
capability/status/diagnostic serialization and pending Desktop cancellation.

Device checks, with games closed and the matching helper/widget:

1. Select General without activating. The current session and active-preset label
   must stay unchanged. Cancel its activation dialog and check again.
2. Activate General with both halves attached and FPS OFF; confirm players 1/2
   and the General active label. Hold horizontally as documented in CONTROLLERS.md.
   Check each stick's movement/release on its own LS. Neither RS may move.
3. Check every left D-pad and right face position independently: down A, right B,
   left X, up Y on its own player. Check multi-button input and release; no action
   may cross players. Recheck Menu/View on P1, M3 Start on P2 and auxiliaries.
4. With games closed, select Isaac without activating: General must stay active.
   Activate Isaac explicitly and recheck directional RS aiming and neutral release.
   Test each layout in an appropriate game separately; software checks and a slot
   number are not evidence of game compatibility or Steam Input routing.
5. Check detach/reconnect and suspend/resume retain the chosen layout and both
   slots, neutralize held input and require release before forwarding resumes.
   Return the physical controller and verify both outputs retire. Recheck Desktop
   and an editable combined layout keep their upright mapping and saved settings.
6. With an older helper, General activation must stay unavailable. Isaac may be
   used only if that helper advertises its existing split capability. No fallback
   operation, power application or Steam configuration write is permitted.

# 0.1.33 Horizontal split-controller directions

Software checks cover the two opposite 90-degree rotations, every movement and
aiming cardinal, full signed-axis endpoints, radial diagonals, opposing directions,
neutral/release, half ownership and unchanged Desktop/editable layouts. They do
not establish physical isolation, Steam Input behavior or game compatibility.

On the device, with matching 0.1.33 helper/widget, close games and prepare split
with both halves attached and FPS OFF. After preparation hold each horizontally,
buttons facing the player: LB/LT left, RB/RT right. Check one half at a time:

1. Push its stick toward player-view up/right/down/left. Only that player's LS
   must follow the same directions and return to zero when released.
2. Check horizontal D-pad and face-button aim: A left, Y right, X up, B down on
   the right half. Only the corresponding RS moves. Check diagonals and release.
3. Recheck Menu/View on P1, M3 Start on P2, clicks, auxiliaries and simultaneous
   movement/aim on both halves. Return the physical controller, then check Desktop
   still follows the ordinary upright orientation.

Only after those checks test cooperative input in the game. A physical legacy
input path or a separate Steam mapping can still produce shared player actions;
rotating the split reports does not fix that separate problem.

# 0.1.32 Display diagnostics, provisional modes and recovery

Automated checks use simulated clocks, display backends and temporary journals;
they never call native display enumeration/change APIs. They cover timeout,
confirmation, early cancel, parent exit, late/slow confirmation, partial failure,
unverified recovery, changed identity/orientation, foreign modes, clock changes,
durable original/result storage, stale selections and IPC isolation. ABI tests
marshal synthetic Unicode structures without invoking Windows display APIs.

On-device checks, only with the user physically present:

1. Finish controller/display recovery and close games before installing the
   matching helper/widget. Preserve the existing profiles and power/Space state.
2. Open Sistema → Pantalla. No display query or mode change occurs automatically.
   Press Consultar pantalla y modos. Compare dimensions, integer refresh rate,
   orientation and monitor/adapter with Windows. Copy the diagnostic. Selection,
   copying, visiting tabs and reopening the panel must not change the display.
3. With one active local display, select a different listed mode and start one
   trial. Do not confirm: check the original returns after the 15-second window,
   including when Game Bar is closed. Verify current mode by a fresh query.
4. Start another trial and use Volver al anterior. Then separately confirm one
   trial, wait past 15 seconds and query again. The worker must acknowledge the
   exact mode. Confirmed changes are session-only, without a registry write.
5. Test helper exit, locked desktop and suspend during a trial in a controlled
   session. Recovery must verify the original or retain the journal and expose
   Recuperar modo anterior pendiente. After unlocking, recover explicitly if
   required. Do not infer rollback from a timeout message or successful API code.
6. Test an extra/duplicated display and remote sessions: diagnostics may be read,
   but a new trial must be unavailable. A changed identity/orientation or third
   mode during recovery must retain evidence and leave that state untouched.
7. Confirm normal helper startup with no pending journal never selects/enumerates
   a new mode. Confirm uninstall/update refuse an outstanding trial and retain
   the recovery worker. Recheck controller, RTSS, profiles and power independence.

Full UWP compilation is separate from C# metadata checks. Native graphics-driver
failure, display hot-plug, real blackout duration and Game Bar focus remain
unverified until the above tests. Do not publish private diagnostics automatically.

# 0.1.31 Game Bar controller navigation and package validation

Compare the actual compiled MSIX manifest with the pinned Game Bar SDK readme:
one main LegionWidget application extension, no LegionOverlay extension, and a
package-level MBM proxy/stub registration with every SDK interface name/GUID.
Confirm the referenced WinMD exists in the package and the removed OverlayPage
is absent. XML/C# checks alone do not establish Game Bar input behavior.

On-device checks, when the user has access:

1. Open Win+G, select LegionTweaks Lab using Game Bar's own controls and enter the
   widget. Test D-pad/left-stick focus movement and A activation on harmless
   controls, tabs, selectors, sliders and a cancelable dialog. B should back out
   as supported by the host/control; LB/RB are reserved by Game Bar for switching
   widgets. Check visible focus and scrolling to controls outside the viewport.
2. Close/reopen Game Bar, leave/reenter the widget and resize it. Repeat from the
   standalone app. Record the exact first control or transition that fails; do
   not infer working navigation from a successful controller slot check.
3. Verify there is no LegionTweaks Overlay entry/launch button and that the main
   panel and Legion shortcut remain. RTSS must stay OFF until explicitly enabled.
4. With a working game input baseline, open/close Game Bar without applying a
   profile. Confirm the game responds afterward and no controller session or
   power setting was changed by browsing. Repeat RTSS appearance checks below.

These are pending manual checks, not reported hardware results.

# 0.1.30 RTSS appearance and OptiScaler access validation

Software checks cover default layout compatibility, all levels/scales, custom
ordering, independent power domains, malformed/oversized/versioned preferences,
unavailable sensors, optional IPC capabilities, sample freshness, OFF configuration
and absence of additional reads on an appearance edit. Fixed keyboard tests use a
fake foreground/keyboard port: close-before-send, stable target, unavailable or
blocked windows, changed handle/PID, timeout, failure propagation and x64 Insert ABI.
They do not invoke native keyboard injection, RTSS memory or controller transitions.

On-device checks, when the user has access to the Legion:

1. Close games and return the physical controller before installing the matching
   helper/widget. Confirm saved profiles, power, Legion Space and startup preference.
2. With RTSS OFF, edit a preset/custom order/size and save. No OSD should appear.
   Close/reopen the widget; the saved choices must remain. Cancel/leave unsaved edits
   and verify activation uses the saved choices, not the draft.
3. Activate RTSS in a supported application. Compare levels, orientation, text size,
   sensor units and custom ordering. Change appearance during publication; the next
   normal update should change only this port's OSD. Other OSD clients and RTSS global
   placement/font settings must be retained. Unavailable readings stay N/D.
4. Disable, restart the helper and activate again: appearance persists, activation
   remains manual. Check stale data, lost RTSS and suspend behavior separately.
5. In a game with working OptiScaler/Insert, open this panel from Game Bar and press
   Abrir menú once. Confirm Game Bar closes, the game regains focus and the menu opens.
   Check that Insert is released. Test desktop/standalone, held modifier and focus
   changes separately; cancellation must not send Insert to an unintended window.
6. Verify the actual loaded upscaler/model in OptiScaler. A sent key or selected
   FSR label is not proof of FSR 4/Lite or a performance/battery improvement.

No private hardware results are included in this checklist.

# 0.1.26 suspend/resume validation

Simulated checks cover native-event coalescing, duplicate resumes, neutral output
during sleep, delayed device arrival, spaced/bounded recovery, cancellation before
and during open, a second sleep, long sampling gaps, held controls, propagated
errors and sanitized diagnostic metadata. Installer dependency selection is tested
without installing packages. The real net48 helper and full UWP package require
their separate builds; software checks do not establish on-device behavior.

On-device checks, with the user present and matching helper/widget:

1. Activate P1, verify fresh input/output, then suspend and resume with Steam and
   a desktop utility open. Confirm the helper process survives, native event/time,
   recovery counter, retained P1 and fresh input/output. Test buttons/auxiliaries,
   sticks, triggers and rumble separately.
2. Hold a control through wake: output stays neutral until release/centering, then
   fresh presses work. Stick mouse stays off and power/profile settings stay intact.
3. Repeat one sleep cycle, then return physical and sleep again. Virtual stays off.
4. Check a running game separately. Do not infer game compatibility merely from
   P1 or software tests. If the virtual target/slot is lost or source does not
   recover, verify physical restoration and retain failure evidence before retry.

No private hardware test results are included in this checklist.

## Previous 0.1.20 development validation

391 automated tests pass locally, and the .NET Framework 4.8 x64 helper compiles
with zero warnings/errors. Added coverage exercises independent Y1/Y2/Y3/M2/M3
decoding in both recognized report layouts, combined press/release, unrelated
fields, auxiliary-only layouts, standard-map composition, copies, opposed D-pad,
neutral/TTL recovery, Game Bar reservation and one shared reader during capture.
These checks use simulated input; no controller or PnP mutation is performed.

Full UWP packaging and on-device output validation remain separate checks:

1. With matching helper/widget, assign each auxiliary to a different Xbox output,
   save and explicitly activate a virtual profile. Check individual presses,
   combinations and release while the input/output panel updates.
2. Verify L/R reservations for Game Bar and independent auxiliary output. After
   capture, suspend/reconnect or stale input, held buttons must not resume until
   neutral. Confirm an auxiliary-only layout starts its source.
3. Check older-helper capability fallback and refusal of unsupported saved layouts.
   Verify selection/copy of the capture text inside Game Bar.
4. Check the intended games separately; a raw bit or a passing unit test is not
   confirmation that a game received the mapped virtual output.

Native automatic-P1 flags and startup behavior are unchanged. CM 0x17 and automatic
virtual startup remain unresolved. Keep private capture/trial results out of this
public checklist.

## Previous 0.1.19 development validation

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
   profiles/power/Legion Space, and that the main Game Bar settings panel still works.
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
# Desktop tray candidate 0.1.40

Validate on the original Legion Go in **Game Bar Compact Mode**; no pinned widgets
or switch to Desktop Mode are prerequisites. A package build alone is not proof.
Keep controller preparation separate from interface window actions.

1. Open desktop LegionTweaks. Confirm one tray icon. Minimize, confirm the taskbar
   window is hidden, then click the tray icon and confirm the same page returns.
2. Close desktop with X, then restore through **Abrir LegionTweaks**. Repeat and
   launch the normal desktop shortcut: one desktop view and one tray icon remain.
3. With an already active virtual player 1 and games closed, minimize/close desktop
   to the tray. Reopen Compact Game Bar without selecting LegionTweaks first:
   check native-bar navigation. Open the widget and check live input/release,
   then restore desktop and compare. The earlier navigation bug remains pending
   unless this exact native-bar case passes; opening a widget first masks it.
4. Exercise desktop-first and widget-first launch orders, with the widget both
   open and closed, and ensure only desktop enters the tray.
5. From the tray choose **Salir de la interfaz**. Confirm its icon and interface
   close, the helper and existing controller remain, and a normal launch works.
6. Suspend/resume with desktop in the tray; restore it and compare live input and
   navigation. Repeat with virtual off: no window action may activate it.
7. Test missing/failed companion startup and connection loss using a development
   build: no hidden, unreachable desktop; normal close remains available or the
   desktop reappears. Do not kill the hardware helper or recovery workers.
