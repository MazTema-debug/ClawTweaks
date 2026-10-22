# LegionTweaks Lab — Legion Go Z1 Extreme port

Experimental Windows/Game Bar port in **MazTema-debug/ClawTweaks**. The widget adapts
ClawTweaks' Game Bar activation, named-pipe transport technique and Next Gen Claw
visual theme. The open hardware backend is adapted from GoTweaks' Lenovo WMI and
Windows power controls. It is an independent derivative, not an official release
of ClawTweaks, GoTweaks, Lenovo or MSI.

**This is an experimental port of the core controls, not full ClawTweaks feature
parity. Automated checks validate software behavior; physical hardware and game
compatibility require separate on-device testing.**
The public ClawTweaks branch contains no hardware helper; its BUILDING.md explicitly
identifies that component as private. This port supplies its own source-available
helper rather than requiring the private MSI/Intel service.

## 0.1.32 — Display modes with a supervised confirmation window

**Sistema → Pantalla → Consultar pantalla y modos** reads the primary display,
adapter/monitor, current pixel dimensions, integer refresh rate and orientation.
It lists Windows-enumerated compatible 32-bit progressive modes in the current
orientation. Selecting a mode and copying the diagnostic do not apply settings.
This first version permits changes only with one identified active display,
without duplication/extension, remote sessions or virtual/mirrored displays.

**Probar modo seleccionado** requires explicit confirmation before starting.
An independent helper worker owns the native change, verifies the result and
allows 15 seconds to confirm in the panel. The interval includes the apply call;
polling, reopening Game Bar and a slow driver cannot renew it. Cancel, timeout
or loss of the owning helper requests restoration of the captured original.
Confirmation is acknowledged by the worker only after another readback.

The protected original precedes all writes. Failed or unverified restoration
retains recovery data and blocks another trial; **Recuperar modo anterior
pendiente** retries restoration after checking display identity and the owned
target. A changed monitor, topology, orientation or unrelated mode is not
overwritten. An unfinished trial can request recovery at helper startup/resume;
normal startup performs no new mode selection or display enumeration.
Updating/uninstalling requires finishing display recovery first.

Changes are dynamic for the current Windows session: even a confirmed mode is
not written to the registry for reboot. Orientation, DPI, HDR, power profiles,
virtual controllers, RTSS and other applications' settings are not configured.
There is no automatic per-game resolution. Native driver/lock/suspend behavior
and the real confirmation UI still need the separate device checks in VALIDATION.md;
a stalled graphics driver can delay native calls beyond a software timer.

## 0.1.31 — Game Bar input registration and RTSS-only telemetry overlay

The package now includes the package-level Metadata Based Marshaling proxy/stub
registration required by the pinned Xbox Game Bar SDK 7.3.2506120, including its
private navigation interfaces. This corrects a manifest omission; controller
navigation inside the Game Bar host still needs on-device verification. No extra
input reader, synthetic navigation keys or forced focus changes are introduced.

The separate Game Bar telemetry widget, its launch button and page are removed.
Game Bar retains the main LegionTweaks settings panel, Legion L/R shortcuts and
OptiScaler menu access. RTSS supplies the telemetry overlay, including the levels
and custom appearance added in 0.1.30. Selecting or opening the panel does not
enable RTSS publication or apply power/controller settings.

## 0.1.30 — RTSS appearance and OptiScaler menu access

**Sistema → Overlay RTSS** adds Minimal, Basic, Full and Custom levels. Custom
metrics can be added, removed and reordered; vertical/horizontal orientation and
75/100/125/150% relative text size are explicit choices. The preview shows N/D and
RTSS frame-metric placeholders, never simulated measurements. The previous full
six-line layout is retained by default. Saving stores appearance for this widget's
Windows user and updates an already active publisher on its next ordinary tick;
it never enables publication, starts sensors or applies a power/controller profile.
An explicit activation sends the saved appearance, including after a helper restart.
Unsaved edits stay in the editor. Corrupt preferences are preserved until explicit
save; the previous string is backed up in widget LocalSettings before replacement.
These choices affect this port's own RTSS text only; RTSS's global
placement/font/application settings remain independent.

**Sistema → OptiScaler → Abrir menú** is available from the Game Bar widget with
a matching helper. One helper-owned operation closes Game Bar, waits briefly for
a stable eligible foreground window and sends fixed extended-key Insert down/up.
Unavailable/secure desktops, a subsequent focus change, held modifiers and timeout
cancel the action. There is no retry or arbitrary-key payload. Sending the shortcut
does not establish that OptiScaler loaded or which upscaler/model the game uses.
OptiScaler must already be installed in that game with Insert as its menu shortcut.
This is menu access, not a DLL installer, FSR configuration manager, keyboard mapping
destination or a claim of FSR 4/Lite compatibility on the original Z1 Extreme.

Both features are independent of the virtual controller. Software checks and full
Windows packaging do not establish real RTSS rendering, focus behavior or game
compatibility; those need the separate on-device procedure in VALIDATION.md.

## 0.1.29 — Keep physical menu buttons on their own half

In split mode, both physical Menu and View belong to the left half. They now
send Start and View respectively to player 1. The right half uses M3 for Start
instead of X, so it can join or pause independently while Legion R keeps its
global Game Bar priority. Other split mappings, the combined Desktop layout,
editable profiles and the source/protocol adapters are unchanged.

## 0.1.28 — Explicit two-player split controller

The separate **Dos jugadores** button partitions one combined Lenovo XInput source
into two Xbox outputs: left half P1, right half P2. Both slots must be confirmed
before restoring the physical source; a physical controller already in P2 also
requires freeing that slot. This uses the existing supervised temporary-mode
operation, with the same identity binding, deadlines and physical recovery.
No second reader, driver, firmware remap, keyboard injection or power setting is
introduced. Stop removes both outputs. Suspend/reconnect retains both assigned
slots and requires neutral controls before input resumes.

The fixed layout uses each stick for movement; left D-pad and right face buttons
produce each player's aiming stick. See CONTROLLERS.md for action/menu mappings
and the test procedure. Mouse and rumble are disabled in split mode. Editable
profile application requires returning the physical controller first. Desktop
and its automatic startup remain one full controller; split is explicit only.
Two correct XInput slots do not establish game or detached-button compatibility.

## 0.1.27 — Recover a transient physical-input disconnect

An already active virtual session also tolerates a temporary loss of physical
input. Output is neutralized and the same physical identity is reopened every
250 ms, for at most 20 seconds. The target, slot, layout and HidHide lease remain
owned by the current session; the receiver mode is not changed. A repeatedly
flapping source cannot renew the deadline until one second of fresh input.
Controls must return to neutral before forwarding resumes, and the helper's
stick mouse stays off. Cancel, a changed identity/slot, a missing supervisor or
timeout use normal physical restoration. Diagnostics distinguish reconnects from
power recovery. This is a bounded recovery attempt, not proof of detached/game
compatibility. The separate two-player mode is introduced in 0.1.28.

Steam has its own desktop controller layout. Turning off LegionTweaks' stick
mouse does not disable a separate mouse mapping in Steam.

## 0.1.26 — Preserve the active controller across suspend/resume

The helper registers native suspend/resume notifications, including the desktop
Modern Standby opt-in. An already running virtual session keeps its target, slot,
HidHide lease and controller layout. The existing worker neutralizes output,
closes SDL input and reopens the same physical identity after wake, with spaced
attempts for at most 20 awake seconds. A long sampling gap also discards a stale
reader, without claiming it was a Windows power notification.

Fresh input and the retained virtual slot must be confirmed before forwarding.
Held controls require neutral before actions resume. Stop/cancel, identity/slot
changes and recovery failure use the existing physical restoration path. There
is no new Desktop activation, firmware mode cycle, power-profile application or
automatic stick-mouse activation on resume. A manually stopped session stays off.
Software checks and on-device standby/game validation are separate.

The installer also reuses matching healthy framework dependencies already
registered for the current user; fresh installs still deploy missing/older ones.

## 0.1.25 — Recognize a Steam-hosted desktop utility

The mode-based preparation guard recognizes Lossless Scaling as a desktop utility
when its process name and exact Steam-library executable suffix both match.
Its presence no longer blocks Desktop startup merely because it is installed in
Steam. Other executables in that library folder are not exempt. This does not
claim general game recognition or Steam Input/game compatibility, and does not
change controller recovery, Steam settings or suspend/resume behavior.

## 0.1.24 — Player one with the Steam client open

Desktop startup and the mode-based automatic P1 button now permit the Steam client
to remain open. This includes Steam starting with Windows; no Steam startup,
Steam Input or desktop-layout settings are changed, and no process is closed.
Known games and executables inside a `steamapps` directory block preparation.
This is a limited game detector: close other games manually before preparing P1.

Checks run before the current controller is stopped and again during preparation,
including the separately supervised temporary-mode writer. They do not gate
restoration of the physical controller. Model/receiver identity, extra-device and
slot validation, deadlines, durable recovery and one attempt remain mandatory.
The older PnP and manual FPS paths still require Steam closed. Suspend/resume
reactivation is not included. Hardware startup with Steam needs separate testing.

## 0.1.23 — Protected Desktop controller and optional startup

**Mando → Desktop** is an integrated, read-only controller layout independent of
the editable profile catalog. The helper constructs a fresh standard Xbox map:
10% stick deadzones, 100% rumble and unassigned Legion auxiliaries. Its manual P1
button accepts no editable profile/layout payload and applies no power settings.

An explicitly saved switch enables Desktop for the next normal helper startup.
Saving never starts/stops input or applies energy; the startup path also leaves
stick mouse off. Each helper launch gets one attempt after pending controller
recovery. Detected Steam/Diablo or an existing controller session skip that launch.
A durable starting/failed record blocks later automatic attempts until the user
checks recovery and explicitly saves the setting again. There are no retry timers
or extra input readers. Updates suppress this activation once while preserving
the setting. Suspend/resume does not automatically reactivate the controller.

Startup remains disabled until configured. Software tests do not establish
physical startup, reboot, game compatibility or driver-failure recovery; those
need separate hardware verification. The explicit P1 and FPS controls remain.

## 0.1.22 — Explicit mode-based player one preparation

The automatic P1 button now requests a separately advertised, explicit helper
operation. If the identified physical controller occupies P1, it uses the bounded
mode supervisor to temporarily leave XInput, connects the final Xbox virtual
target once, confirms P1, and restores the physical source before opening SDL and
enabling selective HidHide. The saved controller layout is copied; no profile
power settings are applied. Steam/games must be closed and FPS remain OFF.

Unknown identities, modes, additional controllers, cancellation or deadline
failures abort preparation and preserve recovery intent until verified. The FPS
assistant remains available. Older PnP opcodes retain their original semantics;
the updated UI requires the new capability and never silently falls back to them.
That version did not include virtual startup activation. Integrated P1, input/rumble and game
compatibility still require hardware testing; a mode-only cycle does not prove them.
The visible app version now comes from the installed package instead of a fixed
label, matching the diagnostic version.

## 0.1.21 — Supervised controller mode-cycle diagnostic

The installed, elevated helper can run the explicit diagnostic
`--test-controller-mode-cycle` while the main helper task is stopped and games
and Steam are closed. It tests XInput → DirectInput → original XInput without a
virtual target or power-profile application. The first adapter accepts only the
reviewed 61EB/61EC receiver pair on the separately gated original Go/Z1 Extreme.

A fresh mode query and exact receiver identity precede protected, atomic recovery
storage. A separate supervisor owns bounded writer processes and restores the
original mode on timeout or owner exit. Startup, controller recovery, update and
uninstall recognize the mode journal. The journal is retained until independent
mode and physical-XInput checks succeed. A missing/unknown response is an error;
it never supplies a default original. No firmware initialization, PnP reset,
service change or button-firmware mapping is part of this diagnostic.

In 0.1.21 the passive reader and normal activation paths were unchanged; its
automatic P1 button still used the existing Windows path. The diagnostic is an experimental
admin CLI operation, with no new Game Bar control. Automated failure simulations
and compilation do not validate the firmware transition or its recovery on a
physical machine. Use the FPS assistant for the existing manual preparation.

## 0.1.20 — Auxiliary buttons in virtual profiles

**Perfiles → Botones especiales Legion** can assign Legion L/R, Y1/Y2/Y3 and
M2/M3 independently to standard Xbox buttons or leave them unassigned. These
assignments use the existing passive reader and require an active virtual
controller. No firmware initialization or remapping command is sent.

The global Game Bar shortcut still reserves only its selected L/R buttons.
Auxiliaries do not open Game Bar. Capture, reconnect, stale input and suspension
require neutral input before mappings resume. An auxiliary-only profile also
enables the shared input reader; it adds no separate polling thread.

Both widget and helper must support each mapping. With an older helper, the
widget retains only the advertised controls and refuses activation of a saved
layout containing unsupported assignments. The input panel labels normalized
special buttons, and identification text can be selected/copied.

Software tests and helper compilation are separate from UWP packaging and on-device
remapping checks. This version does not change native P1 flags or startup behavior;
automatic reconnection and virtual activation at helper startup remain pending.

## 0.1.19 — RTSS shared-memory format 2.21

The RTSS adapter now accepts the SDK-documented shared-memory ABI 2.21. Its new
application timing fields do not change the header or OSD text fields used by this
port. Version 0.1.18 rejected this format before writing; this correction keeps all
range, ownership and lock checks and continues rejecting unreviewed newer ABIs.
Tests use simulated memory; real rendering still requires game validation.

## 0.1.18 — Explicit RTSS overlay

**Sistema → Overlay RTSS** can publish a compact OSD while Game Bar is closed.
Start RTSS separately, then press **Activar overlay RTSS**. RTSS must already be
configured to display its OSD in the game. The former Game Bar telemetry overlay
was retired in 0.1.31. No RTSS installation, launch, global hotkey,
application detection, framerate limit or game-profile setting is changed by the port.

LegionTweaks supplies CPU/GPU temperatures, validated AMD ADL power/voltage/load/clock,
fan, battery and labelled custom limits. CPU, GFX and ASIC power stay separate.
Fixed RTSS `<FR>`/`<FT>` tags resolve FPS and frame time for each supported game;
the helper's sampling interval is not an FPS measurement. The OSD includes a UTC
capture time. Unavailable or expired telemetry is replaced with a fixed N/D marker.
Sending text successfully is not proof that a game rendered it.

Publication starts OFF and is not persisted. Suspend switches it OFF and requires
explicit reactivation. While enabled, a 500 ms presentation loop requests a sensor
sample about every two seconds only after RTSS accepts publication. It reuses recent
command/recorder samples, permits only one outstanding sensor read, and keeps OFF
and cleanup independent of a slow read. Missing/busy/full/incompatible RTSS does
not trigger new sensor sampling. The ADL context lifetime is coordinated with any
read still active during helper shutdown.

The original shared-memory adapter opens only an existing `RTSSSharedMemoryV2`,
validates its ranges and supported ABI 2.14–2.21, and uses one exact owner ID in a
nonprimary slot. It preserves other clients, app entries, graphics and global
configuration. Invalid metadata or an unconfirmed unlock are reported as failures.
OFF retries retiring only this port's text, with a bounded cleanup budget. A forced
termination can leave the previous text behind; its timestamp remains visible.
The next explicit activation can reclaim the same owner, which OFF can then retire. Do not interpret
an interrupted process as confirmation that RTSS removed its text.

Real RTSS rendering, Game Bar controls and coexistence with other OSD clients still
need explicit on-device/game validation. This version changes no controller flags,
PnP sequence or virtual-controller startup behavior.

## 0.1.17 — Saved-profile comparison and session review

**Perfiles → Comparar con otro perfil guardado** displays differences between two
saved profiles without replacing a draft, saving or applying either profile.
It distinguishes AC/DC, unchanged settings, custom limits and stored controller
layouts that are inactive in physical mode. This is a comparison of saved intentions,
not a live hardware read or a prediction of game performance.

**Registro → Resumen de una sesión CSV** opens only a manually selected recording.
The streaming reader is bounded, cancellable and runs away from the UI thread.
It handles the port's CSV schemas, excludes invalid/unavailable samples, reports
observed gaps and keeps sensor domains and sources separate. Min/max and the mean
of valid samples are not a time-weighted average, energy estimate, FPS measurement
or battery-life prediction. No upload or automatic export is performed.

The main panel labels manual readings as previous after they expire. The overlay
clears expired input, rejects ambiguous or implausibly future timestamps and
invalidates in-flight results across visibility changes. A monotonic receipt clock
prevents a repeated sample or wall-clock rollback from extending its lifetime.
No additional telemetry or input polling is added. Recorder timestamp validation
uses the same freshness rules; new CSV rows carry the producing assembly version
instead of the old fixed 0.1.9 label. Existing files are not rewritten.

These changes require Game Bar/file-picker checks on the device. Automatic P1,
virtual-controller startup and rear-button assignments remain pending; this
candidate does not change native device transition flags or controller drivers.

## 0.1.16 — Legion mappings, passive identification and editing utilities

Saved virtual layouts can map **Legion L/R** to standard Xbox buttons. One passive
vendor reader supplies both mappings and the existing Game Bar shortcut. Buttons
reserved by the global shortcut do not also reach the virtual output. Startup,
capture completion, stale input and reconnects require neutral input before rearming.
The reader must acknowledge closure before player-1 preparation proceeds.

**Mando → Identificar botones especiales** is an explicit, cancellable 30-second
observation, with games and Steam closed. It temporarily pauses Legion mappings
and Game Bar. Only the known report header and masked candidate digital bits are
shown in memory; no firmware commands, raw packets, axes or keyboard capture are
added. Y1/Y2/Y3/M2/M3 mappings remain disabled until physical evidence identifies
their bits. A capture result is not itself proof of a named physical button.

**Sistema → Diagnóstico** can correlate the last native disable failure with
existing Kernel-PnP events 225/1000 by exact endpoint and UTC XPath window. Reads
are bounded and deferred while recovery is uncertain or in progress. Export keeps
only fixed process labels and aggregate evidence; missing/partial records leave
the cause unknown. This does not resolve CM 0x17 or activate a controller at startup.

Profiles can be duplicated as a new draft and controller settings reset within
the draft, retaining power, name and Conserve/Physical/Virtual selection. Neither
action saves or applies automatically. Sensitivity and deadzone are persisted in
a bounded protected mouse-preferences file; mouse Enabled is never persisted and
always starts OFF. Slow/failed saving cannot block controller frames or prevent OFF.

The stick transform avoids per-frame Tuple allocation and SDL button order is
allocated once. Software equivalence/allocation measurements are separate from
physical latency, game performance and battery tests. New UI, L/R mappings and
capture still require on-device validation; automatic P1 and conditional startup
activation remain pending.

## 0.1.15 — Bounded preparation trace

Explicit automatic player-1 preparation records a bounded local trace of its
stages, process/module flags and whether opening the SDL physical source was
requested or completed. It uses no extra input reader and changes no native
disable flags. Module presence is not proof of a device handle or its cause.
Markers before the failure and before/after reader disposal distinguish loading
observed during preparation from loading observed only during cleanup.

The trace stores no input values, button presses, profiles, complete device IDs,
SID, recovery token or paths. Its fixed protected file contains only the latest
attempt, and diagnostic failures do not authorize or prevent device recovery.
This instrumentation does not fix removal vetoes, enable startup activation or
add rear-button remapping. The FPS assistant remains available.

## 0.1.14 — Confirmed recovery after supervisor completion

The parent can race with its supervisor after a native failure: the supervisor
may finish recovery, retire its journal and close coordination events while the
parent is still opening one. The parent now accepts completion only when the
protected snapshot has no current journal, belongs to the same SID/token, was
captured after that operation began and confirms both recovery and completion.
A present, replacement, corrupt or unreadable journal never becomes success.
The original disable failure remains recorded. Native error messages now report
the failed call without asserting that recovery is still pending afterwards.

The displayed slot time is the time we read the driver's reported state.
ViGEm.NET's UserIndex is cached and updated by LED notification; reading that
property is not an independent XInput identity query. Device/game checks remain
necessary. This change does not resolve Windows removal vetoes or enable startup
activation. The explicit FPS assistant remains available.

## 0.1.13 — Explicit cursor mode, diagnostics and draft protection

The player-1 inspector now queries XInput in a bounded, short-lived child process
and requires that reader to exit before attempting a device transition. The normal
operation still respects removal vetoes. The confirmation dialog offers a separate,
unchecked option to attempt reconnection with busy readers. Only that explicit
operation journals `AllowBusyReaders` and uses `CM_DISABLE_ABSOLUTE | CM_DISABLE_UI_NOT_OK`
(0x5). It excludes HARDWARE and PERSIST and retains the same model, MI_00/service,
watchdog and recovery checks. This experimental option does not guarantee that
Windows or a driver will allow reconnection. Canceling during a native operation
may report a wait before the worker/supervisor finishes recovery. A failed reader
termination aborts preparation before any device disable; a reader can still
outlive the caller if Windows cannot terminate it. Startup activation remains
conditional on successful hardware validation and is not enabled by this version.

**Mouse con stick derecho** is an explicit, memory-only cursor mode requiring an
active virtual controller. It reuses the existing physical frames and consumes
the physical right stick before remapping; it adds no second XInput/SDL reader.
Sensitivity and radial deadzone are bounded. The mode starts off, requires neutral
to arm, and clears accumulated time on focus changes, gaps or a locked/secure
desktop. Stop, disconnect, suspend and player-1 preparation switch it off. It emits
relative cursor motion only, without clicks or changes to another application's
configuration. Disable it before using the right stick in a game.

**Sistema → Diagnóstico** reads the cached controller status and bounded recovery
files. HidHide GET counts are deferred during preparation. Manual copy/export uses
an explicit DTO: no SID, token, complete device ID, profile, private path or input
frames. A completed recovery is not presented as successful virtual activation.
The UWP package version and helper assembly version are compared separately from
the widget assembly version. File pickers and clipboard still need Game Bar checks.

Profile editing now marks unsaved changes and asks before replacing a draft by
selection, creation, deletion, reload or import. Applying with a dirty editor
identifies the saved version being applied; it neither saves nor applies the draft.
A failed reload retains the current editor and catalog. Saving remains explicit
and never applies power or controller settings.

## 0.1.12 — Automatic reconnection for player 1

This candidate corrects the Configuration Manager disable flag to the Windows
SDK value `CM_DISABLE_UI_NOT_OK` (0x4), without `CM_DISABLE_HARDWARE` (0x2) or
`CM_DISABLE_PERSIST` (0x8). It also distinguishes completed recovery from a
corrupt journal and retains the original worker failure in a protected local
diagnostic result. Hardware validation remains a separate step.

The separate **Activar virtual como jugador 1 (automático)** button uses only the
saved controller layout. It does not apply power settings or run at startup.
Close games and exit Steam, keep FPS OFF, then start the operation explicitly.
If the original Lenovo XUSB function occupies player 1, the helper temporarily
disables only that exact MI_00 function, connects the final virtual target,
confirms player 1, re-enables the physical source and opens SDL again. A virtual
already confirmed in player 1 is retained. Additional controllers cause rejection.

The temporary device state is saved before mutation in the protected
`controller-device-recovery.json`. A separate supervisor starts before disabling,
runs bounded worker processes and requests recovery after a 30-second deadline
even if the parent helper remains alive. Startup, console recovery, update and
uninstall also recover a pending device transition. The operation never disables
the USB composite parent, changes controller firmware or persists a disabled
state across reboot. Driver calls can still fail; terminating a worker is not a
guarantee that Windows immediately cancels its native operation. Failed recovery
retains its journal and reports an error.

**Preparar jugador 1 con interruptor FPS** retains the previous assisted method.
The new software reconnection requires separate hardware validation; automated
tests and success with the manual FPS method do not establish its behavior on
the device. Automatic activation when the helper starts remains deferred.

## 0.1.10 — Legion Space, profiles and assisted player 1

This work is isolated from the installed 0.1.9 reference. It adds the manual
tools below and an explicit assisted path for reserving player 1. Standard
profile application retains its existing behavior.

The **Mando** button **Activar virtual como jugador 1 (asistido)** uses the
controller layout of the saved profile selected in Perfiles, without applying
its power settings. Close games and exit Steam first, starting with FPS OFF.
If player 1 is occupied, the helper asks for FPS ON, connects the final virtual
target when slots are free, then asks for FPS OFF. It waits for one Lenovo
physical source to return before opening SDL again, activating the existing
HidHide lease and checking player 1. When player 1 is already free, it connects
without an unnecessary FPS step.

The assistant runs independently of the widget and accepts cancellation through
**Cancelar preparación y devolver físico**. Its steps share a 90-second budget;
failure disconnects our target and restores our HidHide changes. The user must
return the physical FPS switch to OFF. Native driver calls can still delay
completion/cancellation; their behavior requires on-device testing. The helper
does not disable PNP devices, reset USB, write controller firmware, stop Steam,
or reapply a profile at startup. It never reports another virtual slot as a
successful player-1 preparation. While preparing, profile application is rejected
before any power writes. Optional IPC capability fields keep the button disabled
with older helpers. Input values are now explicitly labelled as unavailable when
the virtual is inactive; a separate refresh button queries controller status.

The **Sistema** tab offers manual control of the fixed Legion Space service
`DAService`: query, stop and disable, start with manual startup, or restore its
saved state. The helper validates the Lenovo executable path before changing it.
Before the first change it durably saves the actual startup mode, delayed-start
flag and running/stopped state in `legion-space-original.json` in its protected
data directory. Each action reads back the result and attempts rollback on failure.
If Legion Space was already disabled, restoring that backup leaves it disabled.
No profile, startup, shutdown, update or uninstall action re-enables this service.
Restoring device/power originals and restoring Legion Space are separate actions.
This controls the service only; it does not launch or close Legion Space windows.

The **Perfiles** tab adds a modal name editor with touch buttons, uppercase,
spaces, backspace and selection replacement. Physical keyboard entry remains
available. **Usar nombre** changes the draft; **Guardar cambios del perfil**
persists it. This provides another entry path; the original Game Bar text-input
failure has not been established or claimed fixed on hardware.

**Exportar perfiles a JSON** writes the saved catalog in a versioned
`LegionTweaks.Profiles` envelope. **Importar perfiles como copias** validates the
whole file (64 KiB, catalog version 2, maximum 20 profiles in total), shows a preview,
and writes a local `profiles-antes-importar-*.json` backup before committing.
Existing profiles and IDs are preserved; imports receive new IDs and duplicate
names get an ` (importado N)` suffix. Invalid/cancelled imports do not replace the
catalog. Import, export, naming and saving do not apply any device settings.
Exports contain saved profiles, so save draft changes first. File selection must
still be checked in Game Bar; the standalone Start-menu app is the fallback.

Storage detects a catalog changed by another window and asks the user to reload
instead of overwriting it. **Recargar perfiles** explicitly discards the unsaved
editor draft. Local pre-import backups are retained in the app's LocalState folder;
export JSON somewhere outside the app before uninstalling to keep a portable copy.

The initial 0.1.10 development check ran 141 behavior tests, built the .NET Framework
4.8 helper and compiled widget C# against Windows/Game Bar metadata in a temporary
harness. That historical harness did not compile XAML or validate rendering. Current
candidates use Linux tests and a separate Windows CI job for the actual UWP package.
See [VALIDATION.md](VALIDATION.md) for current software checks and the pending device
checklist; a successful package build does not establish physical validation.

## 0.1.9 — Detect Legion buttons on recent controller firmware

The button reader now accepts both original-Go controller ID families:
17EF:6182–6185 and 17EF:61EB–61EE. Version 0.1.8 excluded the second family at
both path and attribute filtering, even though the physical XInput reader already
accepted 61EB. The machine gate remains restricted to the 8APU1/Z1 Extreme.

Selection still requires the vendor button collection (FFA0:0001, 64-byte input)
and exactly one matching interface. Only known 04:00:A1 / 04:3C:74 reports can
activate a shortcut. No initialization, firmware remapping or device reset is sent.

The Mando panel now shows Lenovo interface/match counts, metadata/access failures,
and the selected PID, usage and input size. If nothing matches, it lists up to
eight descriptors without device paths or serial numbers. This distinguishes a
missing interface from descriptor rejection and an inaccessible interface.

Three regression cases cover both filtering stages for all eight IDs, unrelated
collections and Windows HID structure layout. The IPC test also checks diagnosis
round-tripping. The helper and widget are versioned together at 0.1.9; actual button
activation on a physical Legion Go remains an on-device check.

## 0.1.8 — Game Bar access from Legion buttons

The Mando tab now offers an explicit, persistent Game Bar shortcut: disabled,
Legion L, Legion R, or L + R together. It starts disabled. After saving, the helper
reads the original Legion Go vendor HID collection independently of the virtual
controller and widget. A deliberate press sends one balanced Win+G sequence;
holding the buttons does not repeat. Reconnect, resume and activation require a
neutral report before accepting another press. The selected shortcut resumes at
helper startup; performance/controller profiles still require manual application.

The reader opens only Lenovo 17EF:6182–6185, usage page FFA0, 64-byte input reports.
Both 04:00:A1 and 04:3C:74 layouts are accepted. It writes no firmware commands,
does not clear existing mappings, and does not initialize vendor-exclusive mode.
Missing/silent devices produce a visible status and bounded retries. Existing
Legion Space or remapper actions may still run on the same buttons; configure
those separately. This feature does not claim exclusive ownership of the buttons.

The controller panel refreshes the ViGEm-reported player slot once per second,
instead of retaining an unavailable startup result. Unknown slots and query errors
remain explicit. No device reset or player-slot reassignment is attempted, and this
change does not establish compatibility with a particular game.

Eight additional automated cases cover vendor reports, command/status rejection,
held buttons, chords, gaps/reconnect, IPC validation, save errors and keyboard ABI.
Physical button detection and Game Bar activation still need on-device validation.
See [CONTROLLERS.md](CONTROLLERS.md) for setup and the compact test procedure.

## 0.1.7 — Combined performance and controller profiles

Profiles now contain optional Lenovo mode/custom limits, independent AC/DC Boost
and EPP, Windows power mode, and an optional controller layout. The editor supports
create/update/rename/delete and atomic file replacement. Legacy TDP profiles migrate
without adding CPU or controller changes. Applying remains explicit; startup,
profile selection and saving do not activate a profile.

A profile is validated and controller prerequisites are checked before power writes.
The engine captures a single CPU power-plan identity, persists original values,
checks the resulting power settings and then changes the controller layout. Failure
attempts to roll back both parts and reports incomplete recovery. Existing original
Boost modes are restored exactly, even when they are not the editor's 0/2 values.

The initial controller runtime reads only a uniquely identified original Legion
XInput device (17EF:6182 or 17EF:61EB) using SDL2 and feeds a ViGEm Xbox 360 target.
Layouts include standard button remapping, radial stick deadzones, Y inversion,
stick/trigger swapping and rumble strength. Active layout changes retain the target.
HidHide masks only the identified physical XUSB endpoint. A durable lease and an
independent watchdog support recovery on process exit; startup/update/uninstall
also clean up a pending lease. Existing third-party HidHide entries are preserved.
An inverted allow-list, an already-hidden source, or inactive foreign hide entries
prevent activation before any power write. No controller driver is installed automatically.

ViGEm is retired upstream; its official driver is an optional prerequisite for this
initial backend. Current Handheld Companion uses VIIPER. Its architecture was
reviewed, but none of its noncommercial-licensed code or binaries are included.
The virtual controller's XInput player slot is displayed, not forced. First-slot-only
games may still need compatibility work. Physical controller reads, duplicate-input
suppression, rumble, reconnection and Diablo IV compatibility remain unverified
until tested on the device. Rear-button HID support, gyro, RTSS and automatic
per-game/AC/DC profile selection remain future work.

See [CONTROLLERS.md](CONTROLLERS.md) for driver prerequisites, profile editing,
physical-controller recovery and the initial on-device checks.

## 0.1.6 — AMD recovery and power diagnostics

This release adds diagnostics for inconsistent AMD power-domain readings and
recovers read-only telemetry contexts after failures or sampling gaps.

The audit found matching AMD sensor IDs, ABI and integer units, not swapped GPU,
CPU or ASIC fields. All three powers come from one PMLog query. Its returned record
contains no sensor timestamp, so the helper's capture time cannot prove that the
driver updated each sensor together. This release keeps the original watt readings
and adds a short overlay notice and `power_review` CSV event when valid GPU power is
above valid ASIC power. It does not clamp values, sum domains, invent a scaling
factor, or claim to have fixed their physical accuracy.

A separate recovery bug is corrected: PMLog failure could persist indefinitely if
an OD5 fallback temperature still worked. The helper now retries a failed/empty
PMLog context at bounded intervals (at least 30 seconds between creations during
continuous failed polling), preserves available fallback readings and discards
failed samples. A driver without the PMLog entry point can continue using OD5
without repeated reopening. Initialization/read exceptions impose a 30-second
cooldown and never reuse a previous numeric sample.

After a gap of more than 10 seconds between AMD reads, the next read recreates the
read-only context and reselects the adapter before querying it. A significant
backward clock change also reopens it. This is recovery after an observed gap,
not a Windows suspend-event detector. It also applies when the widget has been
hidden and recording was off. No power or fan setting is automatically reapplied.
The existing recorder continues after a gap and does not invent samples during it.

New recordings use **CSV schema 2**. Existing CSVs are untouched. Alongside the
existing columns, each AMD reading adds `_sensor_id`, `_raw`, `_supported` and
`_diagnostic`; `power_note` describes the comparison above. Raw PMLog values retain
unsupported/invalid integers for debugging; only the normal value column contains
validated measurements. When an OD5/ODN or WMI fallback supplies a reading, its
source identifies that API and the PMLog raw fields are empty. A failed PMLog call
also leaves raw fields empty rather than exporting its untrusted output buffer.

Diagnostics include `pmlog_status=<ADL return code>` (`0` is success),
`pmlog_unavailable` when the entry point is missing, and `pmlog_empty` for a
successful query without usable PMLog readings. `amd_session` counts context
creations within that helper process; `amd_open` records the reason for the current
context (`initial`, `after_gap`, `clock_changed`, `after_pmlog_error`, `after_error`).
These fields persist across reads so another widget cannot consume a one-shot
recovery marker before it reaches the recorder. `amd_retry_pending` marks an open
or read exception during cooldown. Diagnostics are not a sensor-update timestamp.
The standalone `--diagnose` process has its own AMD session and counter.

### Device check for 0.1.6

1. Update with the normal installer, open Registro and start a new session.
2. Play for 30–60 seconds with Game Bar closed. Note the selected Lenovo mode.
3. Suspend Windows for at least 30 seconds, then resume and return to the game.
4. Wait another 30–60 seconds. Reopen Registro: check that its sample count advances
   and verify the fan, charge-limit and power-light controls still respond.
5. Stop recording and share the CSV manually, plus `--diagnose` output if a sensor
   stays N/D. Check `gap`, fresh post-gap rows, AMD context diagnostics and any
   settings changed by Windows/firmware. Simulated tests cannot establish these
   results on the physical Legion.

If the folder button fails or opens Explorer Home, use the displayed recording
path manually. RTSS remains deferred.

## 0.1.5 — Session recording and explicit Lenovo mode

Open **Registro → Iniciar registro**, play with Game Bar closed, then return to
**Detener registro → Abrir carpeta de registros**. The helper samples the complete
read-only status about every two seconds while recording, independently of both
widgets. Closing a widget does not stop the session. No recording or setting
reapplication starts automatically at logon or after restarting the helper.

Each start creates a unique CSV in
`%ProgramData%\LegionTweaks\<Windows-user-SID>\Recordings`. The folder button opens
that directory through the Windows launcher API. If that fails or opens Explorer
Home, use the displayed path manually.
The owner can read/copy and delete finished CSVs; output-directory creation and
writing remain restricted to the elevated helper. Requests cannot choose output
paths. Updating or uninstalling the app preserves these CSVs. Stop a session before
updating; a forced process stop may omit its final `end` row but flushed samples
remain in the file. Recording stops automatically at 8 hours or approximately
64 MiB per session. Disk/file failures stop recording and appear in its status.

CSV format: UTF-8 with BOM, comma delimiter, quoted text, CRLF rows and invariant
decimal points. Schema 1 records the app version, row type (`sample`, `error`,
`end`), UTC capture/write times, elapsed time, interval since the previous attempt,
observed events, AC/DC, Lenovo mode and whether Custom mode is selected. Every
control/sensor has value, unit, source and error columns. Voltages retain raw **mV**;
power domains remain separate. Missing or errored values are empty, not zero.
Driver/error text is escaped and protected against spreadsheet formula execution.

Events include `start`, `stop`, `helper_exit`, `limit_reached`, `ac_changed`,
`Mode_changed`, per-setting changes, `partial`, `read_error`, `clock_changed` and
`gap`. A gap greater than six seconds is reported without inventing intermediate
samples. It can reflect suspension, a slow read or another delay; it is not proof
of a particular Windows power event. Read failures keep their own empty-metric
rows and sampling retries on the next tick. Simultaneous hardware edits and reads
use the control engine's lock, so a sample cannot split a TDP write transaction.

Both the main panel and overlay now show **Modo Lenovo** alongside the custom
register values. Quiet/Balanced/Performance label those values as stored while
Custom is inactive. Missing/unknown modes remain unconfirmed. This distinguishes
inactive custom registers from enforced limits. A configured limit is not a
measurement of instantaneous power. The overlay also shows
whether CSV recording is active and its sample count. Recording-status refreshes
do not overwrite drafts in the control sliders.

### Device check for 0.1.5

1. Apply Custom 10/10/10 W and start recording. Close Game Bar and play for 1–2 minutes.
2. Reopen Registro: the sample count and last-sample time should have advanced.
3. Disconnect/reconnect AC, then suspend and resume. Observe the actual mode and
   limits after each transition; this version does not automatically reapply them.
4. Stop recording, open the folder and inspect/share the CSV. Check AC/mode/setting
   changes, gaps and any error rows. No readings should be fabricated during sleep.

These are device checks to run, not claims that Windows resume or folder launching
has been validated by the simulated tests. RTSS remains deferred.

## 0.1.4 — Fix nearly invisible overlay

The overlay renderer incorrectly divided Game Bar's already-normalized
`RequestedOpacity` by 100, turning full opacity (1) into 0.01. It now applies the
0–1 value directly, matching Microsoft's executable Game Bar sample, while
retaining the bounds check. Compact Game Bar may omit the pin control; RTSS
integration remains deferred.

## 0.1.3 — Pinned Game Bar overlay and AMD GPU telemetry

Historical implementation: the separate Game Bar telemetry overlay described
below was removed in 0.1.31. The main settings widget remains; use RTSS for OSD.

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

The initial parser could report `WMI no devolvió Value.` for fields returned
by `GetFeatureValue` when their property-name casing differed.

0.1.1 preserves WMI's case-insensitive property lookup when copying a method
response. A provider field named `value` now satisfies a request for `Value`,
as it does in GoTweaks. Missing/null fields still fail closed, nonzero return
codes still fail, and missing-field errors list the returned field names.
Regression tests exercise the same output parser used by the Windows helper.
GPU temperature zero is treated as unavailable from 0.1.3 onward, with AMD-driver
telemetry providing an independent read-only source.

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
| Manual profiles | Up to 20 combined performance/controller profiles, CRUD, legacy migration, explicit verified apply. |
| Legion L/R shortcut | Opt-in global Win+G shortcut, independent of physical/virtual mode, persistent choice and live button status. |
| CPU Boost | Enable (Windows mode 2) / disable (mode 0), independently for AC/DC. Exact previous mode is preserved for restoration. |
| CPU EPP | 0–100 for AC/DC in the active Windows power plan. |
| Windows power mode | Efficiency / Balanced / Performance using PowrProf overlay schemes. |
| Cooling | Firmware automatic mode or maximum fan speed. No custom fan curves / EC writes. |
| Battery | 80% charge limiter, if the Lenovo WMI capability is exposed. |
| Power LED | Lenovo lighting ID 4, if supported by the installed firmware. |
| Readings | CPU temperature/fan via Lenovo, GPU temperature/voltage/load/clock/power via AMD ADL, battery and power source. Missing readings are N/D. |
| In-game overlay | Explicit RTSS text publisher with per-game FPS/frame-time tags, appearance levels/custom layout, timestamps and stale-data handling. |
| Session CSV | Explicit start/stop in Registro; helper-owned 2 s sampling continues with widgets closed, captures controls and sensors, marks changes/errors/gaps, and preserves previous sessions. |
| Restore | Durable original-value journal; restore on demand or during uninstall. CPU entries retain their original plan and AC/DC identity. |

Not integrated in this version: Radeon/ADLX features, gyro, configurable keyboard output from controller buttons,
RGB stick lighting, RTSS custom layouts/graphs, independent FPS capture, Lossless Scaling, automatic
per-game switching, automatic AC/DC TDP profiles, Intel/MSI controls and upstream
self-updaters. The original ClawTweaks source remains available in the repository;
the Legion target does not compile or invoke its MSI-specific code.

See [PENDIENTES.md](PENDIENTES.md) for the prioritized controller work, including
configurable keyboard destinations such as Insert for the OptiScaler menu.

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

The helper does not reapply performance settings or controller profiles at logon,
reconnect or resume. An explicitly saved Legion-button shortcut resumes listening.
Firmware/Windows
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

The journal and helper errors are in `%ProgramData%\LegionTweaks\<Windows-user-SID>`;
CSVs are in its readable `Recordings` subfolder. `--diagnose` uses a separate
read-only journal, so its `SavedOriginals: 0` does not describe the running helper's
backup. It also does not query the running recording session; use Registro for that.
The helper uses a separate, versioned local pipe. It validates both the connecting
Windows user and the installed widget package family, bounds requests, and accepts
only enumerated operations (no command execution or arbitrary WMI IDs).

## Verification

`dotnet run --project LegionGo/Tests/LegionTweaks.Tests.csproj -c Release` runs 418
behavioral tests against the real control engine with a simulated device: hardware
gating, no writes at startup, range validation, Custom-mode confirmation, failed
reads/writes, coupled-register drift, rollback, durable originals, AC/DC and power-plan
isolation, concurrency and protocol serialization, plus WMI output parsing and
switch rendering under a disabled parent, read-only overlay polling, AMD ABI layout,
sensor support/range handling, GPU selection and voltage/unit serialization. New
coverage includes mode labeling, CSV escaping/units/cultures/errors, idempotent
start/stop, widget-independent timer sampling, AC/settings changes, gaps,
read-error recovery, failed file creation, preserved sessions, duration bounds,
and sampling concurrent with TDP edits. Eight additional cases exercise AMD
context recovery with working fallbacks, bounded retries, missing APIs,
initialization/read failures, recording across a simulated sleep gap, raw sensor
provenance through IPC/CSV and non-mutating power review. They cannot establish physical
WMI compatibility, cooling performance, Game Bar rendering or installation success.
The Windows CI job builds the actual helper and UWP package. Controller input,
HidHide visibility, rumble, suspension recovery and game compatibility must be
checked on the physical device. No automated test substitutes for those checks.

The 0.1.10 additions cover service originals, rollback, corrupt/failed journals,
independent restore paths and non-mutating polling, the Windows service ABI,
profile transfer validation/collisions/capacity, and text selection/Unicode edits.
Service tests use a simulated backend; they never start or stop Legion Space.
Player-1 cases cover the FPS state sequence, cancellation, deadlines, enumeration
transitions, occupied/ambiguous slots, no power changes, IPC compatibility, strict
source identity and the XInput ABI. They do not run the actual controller backend.

See [SOURCES.md](SOURCES.md) for exact upstream revisions, implementation mappings
and license attribution. The combined derivative remains AGPL-3.0; the GoTweaks MIT
notice is included in [LICENSE.GoTweaks](LICENSE.GoTweaks). The AMD interop subset
retains LibreHardwareMonitor's MPL-2.0 notice and [license](LICENSE.LibreHardwareMonitor).
