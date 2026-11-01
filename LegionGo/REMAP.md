# Configurable controller mapping — logic foundation

This code is a foundation for the Mando redesign, not an active input route.
Catalog editing is connected to the helper's named pipe and protected storage.
The installed .44 FPS experiment remains separate. The new action engine is not
wired into the input worker, startup, firmware, HID or native output adapters.

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
ActivationAvailable, Revision, Catalog and Message. ActivationAvailable is false
until a real native route is integrated and verified. Older helpers omit support;
the UI must keep activation unavailable and retain its local draft.

| Operation | Request.Remap |
| --- | --- |
| remap_status | absent |
| remap_save | Revision from the last status, complete Profile; no Id |
| remap_delete | Revision and saved Id; no Profile |
| remap_activate | Revision and saved Id; currently returns failure without activating |

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
controls and power profiles. It does not activate a mapping. The installed .44
FPS package does not contain this editor or its catalog backend.

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
verified physical reading or native substitution. Activation remains disabled;
there is no simulated activation or hardware test button in the product editor.

The C# model/service checks, UWP/XAML compilation, package and physical behavior
are separate checks. The local source still needs the normal UWP build and device
validation before deployment.

## Integration still required

The future native route service must serialize input/output on its input worker,
preserve existing sessions/recovery/slots and
advertise only real capabilities. It must connect normalized physical input,
targeted native substitution, owned keyboard/mouse output and virtual targets.
Suppressed native groups need verified passthrough for unchanged directions.
Desired output is not an acknowledgment of OS delivery: partial writes need
ownership-aware cleanup/retry, foreign keyboard/mouse input must be preserved,
and native release/recovery failures must remain visible. HidHide alone cannot
substitute FPS keyboard/mouse output.

No universal FPS decoder, firmware map writer, keyboard hook, driver installation
or HC/VIIPER implementation is included. The unverified route must remain
unavailable rather than report successful remapping from a synthetic frame.

## Verification

The .NET 8 suite includes configuration validation, chord ownership, hold/tap/
toggle, neutral/freshness/focus gates, duplicate samples, motion, P1/P2, function
transitions and catalog failure/concurrency cases. Synthetic capability fixtures
are not evidence that the target receiver can replace native output. The helper
also compiles these files for net48 x64. UWP, package, hardware and game checks
remain separate.
