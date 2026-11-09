# Configurable controller mapping — bounded Y3 integration

The general Mando redesign remains a foundation. Catalog editing is connected to
the helper's named pipe and protected storage. Local source now integrates one
explicit trial: FPS Y3 → one keyboard pulse for at most two minutes. It uses the
shared passive reader, action engine, a bounded native substitution and owned key
output. Software/build checks do not replace package or physical integration
validation. Installed .46 and the old fixed .44 experiment lack this new route.
General activation remains unavailable.

## Shared configuration

`Core/ControllerRemap.cs` defines version 1 `RemapProfile`, `RemapBinding` and
`RemapAction`. A profile has an independent GUID, name, mode and binding list;
it is not a power profile. `RemapProfiles` encodes a bounded UTF-8 catalog without
an active/enabled field. Existing Desktop/General/Isaac profiles are unchanged.

Destinations are Original, None, Keyboard, MouseButton, MouseMove, MouseWheel,
GamepadButton, GamepadAxis and Function. Keyboard actions accept up to six
distinct Windows virtual keys, including modifiers, F1-F24, OEM and media keys.
Only fields for the selected destination may be populated. Gamepad destinations
specify player 1, or player 2 in Split mode. Functions are a closed catalog:
GameBar, PauseRemap, ReturnOriginal. No paths, scripts or arbitrary commands.

Each physical direction has a normalized input lane, so a stick can map its
directions independently. For example `LeftUp` can hold W, `LeftRight` can drive
RX positively, and Y3 can hold a chord. LT/RT supply a normalized scalar. Sensor,
touch and wheel lanes supply positive relative counts consumed once per sample.
These are adapter contracts, not newly established HID offsets or capabilities.

Bindings specify Hold, Tap or Toggle, threshold 1-100 and amount 1-100. Functions
and wheel outputs require Tap; relative inputs cannot toggle. Analog thresholds
apply to digital destinations. Continuous outputs preserve analog magnitude;
button-to-axis strength is controlled by Amount. A tap lasts 30 milliseconds and
does not repeat while held. Relative mouse counts represent displacement;
button/analog mouse motion represents velocity (1000 units/s at full amount),
with elapsed time bounded to avoid a catch-up burst. The native output adapter
must accumulate fractional mouse units rather than round every sample separately.

## Engine and editor boundaries

`ControllerRemapEngine` owns a validated copy on explicit Activate. Capability
checks run before replacing an existing active map. Every changed input,
including None, requires verified reading **and native substitution**. Every
output destination also requires support from that route. The trusted hardware
provider supplies these capabilities; never accept a UI claim as verification.

The engine produces desired owned keys/buttons, two gamepad frames, relative
mouse movement, wheel events and function events. It does not inject anything.
Generic Shift/Ctrl/Alt keys use the left modifier identity, so they share
ownership with explicit left modifiers. Shared keys/modifiers stay down while
any binding owns them. Opposed stick and
D-pad directions cancel; axes clamp to their ranges. GameBar fires once per
press. Pause releases output and requires explicit Resume; ReturnOriginal stops
the engine. Resume and activation require neutral input before rearming.

Observe requires a monotonic clock, source sequence/capture time, foreground
identity and a route-ready flag covering both desktop access and continued
substitution/output ownership. Invalid/lost/stale input, focus/clock changes and
reordered samples clear held/toggled state and require neutral input. Duplicates
cannot replay events or renew freshness; taps can still expire. A physical mode
change stops the engine. The owner must also feed the returned neutral output to
the native adapter on interruption, pause, stop, suspension and shutdown.

`ControllerRemapWorkspace` separates draft, saved catalog and active snapshot.
Load/select/save never activate; Save during an active session leaves its copy
unchanged. The storage seam requires atomic compare-and-replace under a shared
storage lock and verified readback. Failed/invalid/concurrent saves leave the
in-memory catalog intact. The helper's `RemapProfileStorage` is the explicit
protected disk adapter; merely constructing it creates no files. Read preserves
invalid data. Save/delete compare expected bytes under an exclusive storage
lock, atomically replace the catalog, retain a previous valid copy and verify
readback. The catalog is limited to 48,000 UTF-8 bytes to leave IPC envelope room.
The UI can keep incomplete form input locally; only complete validated DTOs cross
SetDraft/Save. Deleting an active profile requires stopping it first.

## Editor IPC

`Response.SupportsRemapEditing` advertises catalog editing, never native mapping.
`remap_status` takes no payload and returns `Response.Remap`: EditingAvailable,
ActivationAvailable, Revision, Catalog, Message and optional Y3Trial.
ActivationAvailable remains false for general mapping. Older helpers omit support;
the UI must keep activation unavailable and retain its local draft.

| Operation | Request.Remap |
| --- | --- |
| remap_status | absent |
| remap_save | Revision from the last status, complete Profile; no Id |
| remap_delete | Revision and saved Id; no Profile |
| remap_activate | Revision and saved Id; currently returns failure without activating |
| remap_y3_test | Revision and saved Id; explicit bounded FPS Y3 trial, no draft |
| remap_y3_stop | absent; return is independent of catalog validity |

Save/delete return the full new catalog and revision. Refresh after a conflict;
never silently retry a stale save over another editor. The revision is a SHA-256
of the validated catalog. These operations reject power/profile/display/overlay,
Game Bar context and generic Key/Values payloads. No catalog operation applies
power, opens HID, starts recording or activates an existing controller mode.
Storage/service tests use seams; the protected native file adapter still needs
its own deployment/integration verification. No catalog file was written to the
running installation during development checks.

## Local widget editor

The local UWP source now presents **Mando a tu medida** in Mando. It edits
Gamepad/FPS/Split distributions separately from the existing Desktop/General/Isaac
controls and power profiles. Opening, editing and saving never activate a mapping.
The Y3 trial buttons require a compatible helper and a clean saved eligible
distribution. Installed .46 has the earlier editor/backend without this trial.

`Widget/RemapForm.cs` retains incomplete local form values and builds only the
fields belonging to the chosen destination. `RemapEditor.cs` owns each view's
drafts, catalog revision and replaceable service boundary; `MainPage.Remap.cs`
adapts those states to UWP controls. It never runs the action engine, accesses the
protected catalog file directly or attaches Game Bar context to editing requests.

Selecting controls, modes or distributions preserves each local draft. Saving
requires an advertised editing capability and a fresh catalog revision. A failed
or conflicting request preserves the draft and requires an explicit reload,
without a silent write retry. A save is confirmed only by matching the returned
catalog entry to the submitted snapshot. A response from an unloaded view cannot
replace a newer query. An old helper leaves saving unavailable. Unsaved drafts
are held by the view, not persisted across destruction of that view.

The editor supports up to six keyboard keys selected by name, mouse buttons,
cardinal motion/wheel directions, virtual buttons/axes and the closed function
catalog. J2 is offered only in Split; behavior, threshold and intensity controls
follow the shared contract. A catalog entry or drawn control is not evidence of
verified physical reading or native substitution. General activation remains
disabled; the separate Y3 trial is limited as described below.

The C# model/service checks, UWP/XAML compilation, package and physical behavior
are separate checks. The local source still needs the normal UWP build and device
validation before deployment.

## FPS Y3 trial

The only changed binding must be Y3, Keyboard, Tap, exactly one of A–Z, 0–9,
F1–F24, Insert or Escape. Every other control must retain Original. Hold, Toggle,
chords, modifiers, mouse, gamepad, functions and other physical modes are rejected
before native acquisition. This first test does not limit the final editor scope.

FpsY3TrialService consumes copied, identified snapshots from the existing
LegionShortcutService reader. It validates product, path, header, sequence and
250 ms physical-input freshness. Activation and focus interruptions require
release. The isolated verifier process is prepared at activation and accepts only
verification requests. Before every candidate pulse, it performs the full native
check again; it holds no HID handle between requests.

For this experimental Y3 trial only, the original press can be up to 1000 ms old
at SendInput. A later sample never renews that press. The sample checked after
verification must still be no more than 250 ms old immediately before sending;
if it expires, the pulse is discarded and a release is required to rearm. A short
press may consequently produce its pulse after release, with up to one second of
delay from the original sample. This larger window does not apply to the general
remapping engine or extend the two-minute trial. Latency on real hardware remains
unmeasured. Counters confirm accepted paired SendInput calls, not a game's action.

FpsY3Lease persists a protected per-user journal before mutation. Its allowlist
permits index/map/USB queries and only right FPS row 30 at index 1, code 6 → 0 → 6.
It checks original 83E1/Z1 Extreme independently of USB, exact shared-reader path,
unique vendor collection, container, protocol 000101A0 and original Y3=6/M3=5.
Readback compares all 31 returned rows; two catalog-only rows are not guessed.
No full default map, initializer, reset, service or HidHide change is performed.

The owner holds no native writer. Each native I/O operation has a 7.5 s deadline;
only isolated children may be killed. The prepared verifier is closed on stop,
owner pipe closure or its bounded lifetime; each verification still uses the
existing journal/owner/watcher guards and native identity, index and map reads.
An independent watcher attempts restoration on
owner exit, stop or the two-minute deadline. Journal identity/original cannot
change or move back from restoration. Failed return stays pending and blocks new
trials. Keep FPS ON and close Legion Space/Handheld Companion for recovery.
Startup and --recover-controller retry the journal; they never activate a trial.

Stop, suspension, source loss and route changes stop output and attempt return.
Partial injection releases/retries only the sender's selected owned key. Foreign
keys/modifiers are preserved. A process crash cannot promise cleanup of a partial
key injection; the watcher restores the native map. Corrupt catalog data cannot
prevent return or be presented as a confirmed empty catalog.

The helper keeps at most 32 sanitized Y3 attempt records in memory; an explicit
`remap_y3_diagnostics` request exports the snapshot and dropped count. Normal
status responses do not include the trace. Its per-process session ordinal and
active/recovery-at-snapshot flags distinguish the historical attempts from current
state; compare the ordinal to `FpsY3Trial.SessionOrdinal` in a separately queried
status if state may have changed concurrently. The explicit trace response does
not attach ordinary controller, recording, profile or key status. Relative monotonic times identify
source age, preflight/read/desktop/engine, total/native verification, subsequent
read and output. SubsequentAgeBeforeSendMs records the age of that last checked
physical sample at the send guard; its absence means that guard was not reached
or the record came from an older helper. A later sample is recorded separately and
never refreshes the originating press. AcceptedEvents 2 means Windows accepted a pair; -1 means no
acceptance count is available, and 0/1 can record a failed/partial send. There are
no raw reports, key values, window handles, device paths, tokens or other-app text
in the trace. The ring is reset on the next successful trial and lost when the
helper exits; it is not written automatically. Only an authenticated package
client may use the existing pipe. A user-facing export still needs UI integration;
do not weaken the pipe's package/identity checks to access it.

A stop request is signalled before waiting for the trial lock; an in-flight
verification or output gate discards its candidate when stop is requested. The
status/stop calls may still wait for a bounded native operation and recovery; they
must not report native return before it is verified. `Error` is the latest rejection
or error and may remain visible after stopping; a successful pulse/new trial clears
it. The diagnostic history is a separate snapshot, not an assertion that its last
event describes the present state.

## Integration still required for full remapping

The future native route service must serialize input/output on its input worker,
preserve existing sessions/recovery/slots and
advertise only real capabilities. It must connect normalized physical input,
targeted native substitution, owned keyboard/mouse output and virtual targets.
Suppressed native groups need verified passthrough for unchanged directions.
Desired output is not an acknowledgment of OS delivery: partial writes need
ownership-aware cleanup/retry, foreign keyboard/mouse input must be preserved,
and native release/recovery failures must remain visible. HidHide alone cannot
substitute FPS keyboard/mouse output.

No universal FPS decoder/map writer, keyboard hook, driver installation
or HC/VIIPER implementation is included. Unverified routes must remain
unavailable rather than report successful remapping from a synthetic frame.

## Verification

The .NET 8 suite includes configuration validation, chord ownership, hold/tap/
toggle, neutral/freshness/focus gates, duplicate samples, motion, P1/P2, function
transitions and catalog failure/concurrency cases. Synthetic capability fixtures
are not evidence that the target receiver can replace native output. The helper
also compiles these files for net48 x64. UWP, package, hardware and game checks
remain separate.
