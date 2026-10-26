# Source provenance

## Desktop frame ownership and orderly widget exit (0.1.41)

Original AGPL-3.0-only port code reads the shell frame's documented
System.AppUserModel.ID property and verifies the system frame-host executable.
Only one exact matching frame is eligible for ShowWindowAsync(SW_HIDE/SW_RESTORE),
with readback of visibility and minimized state. This replaces the 0.1.40
assumption that a CoreWindow's ancestor necessarily is its taskbar frame. The
companion now performs these visibility writes only on its own verified desktop
frame; it never injects code, subclasses foreign windows or changes input routing.

Game Bar's documented widget Close method and closed event provide the handshake
before shared-process exit. The documented IsLaunchActivation contract controls
when a new SDK widget may be constructed. No third-party implementation is copied.
The private bounded lifecycle log uses only fixed event names and state flags.

- https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-id
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindowasync
- https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widget
- https://learn.microsoft.com/en-us/xbox/game-bar/guide/app-activation
- https://learn.microsoft.com/en-us/uwp/api/windows.ui.core.preview.systemnavigationmanagerpreview.closerequested

## Desktop tray companion (0.1.40)

Original AGPL-3.0-only port code uses the documented desktop extension, in-process
app service and per-view UWP consolidation APIs. A separate .NET Framework 4.8
WinForms process owns the notification icon at the user's normal privilege level;
it has no hardware/helper transport, elevated launcher or controller code. The
app service accepts only its own package family. The minimized-window probe uses
the CoreWindow explicitly registered by the desktop view and verifies its package
identity before reading the enclosing frame state; no window-title matching,
global hooks, arbitrary launches or cross-process window writes are added.

- https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.fulltrustprocesslauncher
- https://learn.microsoft.com/en-us/windows/uwp/launch-resume/convert-app-service-in-process
- https://learn.microsoft.com/en-us/uwp/api/windows.ui.core.preview.systemnavigationmanagerpreview.closerequested
- https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.applicationview.tryconsolidateasync
- https://learn.microsoft.com/en-us/windows/win32/api/corewindow/nn-corewindow-icorewindowinterop
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-isiconic
- https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon

The tray embeds only original port logic and reuses the package's existing logo.
Microsoft.Windows.SDK.Contracts 10.0.26100.1 supplies compile-time API references;
Windows implements those APIs. The helper's private Game Bar routing is unchanged.

## Readiness, early isolation and physical return (0.1.39)

Original AGPL-3.0-only port code coordinates the existing SDL source and passive
HID cache, verifies the selective HidHide lease before supervised physical return,
and reuses the existing independently supervised mode cycle for an explicit
physical-player-one return. No new protocol bytes, initializers, firmware mappings,
GameInput interfaces, exemption removal or third-party implementation are added.
The prior source and license attributions for those adapters remain applicable.
Simulated coordinator checks do not establish native Game Bar compatibility.

## Game Bar focus continuity (0.1.38)

Original AGPL-3.0-only port code extends the existing per-view observation with
CoreWindow activation and a read-only native foreground check. The native check
accepts only the Game Bar package family and its known host executable names on
the ordinary input desktop. Process handles use query-limited access and are
disposed after inspection. No host implementation or third-party code is copied.

- https://learn.microsoft.com/en-us/xbox/game-bar/api/enum-xgb-displaymode
- https://learn.microsoft.com/en-us/uwp/api/windows.ui.core.corewindow.activated
- https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getpackagefamilyname
- https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew

## Foreground Game Bar input (0.1.37)

The decoder, expiring cache, per-view foreground context and source-switch
neutral gate are original port code. Original-Go report-layout facts are from
the MIT GoTweaks reference already attributed and licensed below:

- https://github.com/corando98/GoTweaks/blob/99ed5f5f8c22bb78bfffee9b37f9a04094f30482/XboxGamingBarHelper/Labs/LegionButtonMonitor.cs

Only the standard gamepad payload is decoded. No initialization, IMU/touch
processing, firmware mapping or heartbeat writer is copied. The two formats
shift the standard payload by two bytes; the physical model is not inferred
from the product ID. Synthetic protocol fixtures are used in software checks;
private device recordings are not included.

- https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widget
- https://learn.microsoft.com/en-us/windows/win32/api/xinput/nf-xinput-xinputenable
- https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/interpreting-hid-reports

The existing owned read loop provides cached HID input. SDL remains the normal
gameplay backend and rumble transport. No GameInput, OpenXInput or Handheld
Companion runtime/code is added. Protected failure evidence uses framework
exception metadata and excludes raw exception messages and source filenames.

## View lifetime and game launch accesses (0.1.36)

These changes are original AGPL-3.0-only port code using the existing Game Bar
activation pattern attributed below. SDK/UWP references used for view lifetime
and context-specific URI launch:

- https://github.com/microsoft/XboxGameBarSamples/blob/master/Samples/WidgetAdvSampleCS/App.xaml.cs
- https://learn.microsoft.com/en-us/windows/uwp/ui-input/show-multiple-views
- https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widget

SDL's documented per-process hint is used by user-generated Windows launch
accesses. No SDL implementation, game files or compatibility database is copied:

- https://wiki.libsdl.org/SDL2/SDL_HINT_JOYSTICK_RAWINPUT

The first Legion target was authored 2026-10-10 in MazTema-debug/ClawTweaks.

## Lossless Scaling access and startup history (0.1.35)

The read-only detector, its bounded status contract, protected startup-decision
history and UI are original AGPL-3.0-only port code. No Lossless Scaling binary,
profile schema, private ClawTweaks helper or upstream UI is copied. The fixed app
identity is the publisher's Steam store listing; Valve documents `steam://run/AppID`
as the launch protocol. Windows Launcher invokes that fixed URI from UWP, without
passing executable paths or launch arguments to the elevated helper.

- https://store.steampowered.com/app/993090/Lossless_Scaling/
- https://partner.steamgames.com/doc/api/steam_api#SteamAPI_RestartAppIfNecessary
- https://learn.microsoft.com/en-us/uwp/api/windows.system.launcher.launchuriasync

Detection uses Windows read-only uninstall registry keys for that fixed app,
file-version metadata and matching process image paths. It neither invokes the
Steam API nor edits Steam/Lossless Scaling configuration. Being installed or
running is not evidence of active scaling or a particular LSFG mode.

## RTSS publication and Game Bar status corrections (0.1.34)

The installed RTSS SDK's `RTSSSharedMemory.h` defines the legacy, extended and
additional text fields. `Samples/SharedMemory/RTSSSharedMemorySample/RTSSSharedMemorySampleDlg.cpp`
updates `szOSDEx` instead of `szOSD` for v2.7 and newer. The port follows that
choice, clearing only its own old text and duplicate exact-owner slots. Additional
text is touched only when both version and validated entry size include that field;
graphics, application records, reserved/foreign slots and global settings are kept.
The SDK sample documents `<P=10,10>` in zoomed pixel units; this port emits a fixed
`<P=8,8><A=0>` inset and restores alignment before the final newline. No arbitrary
markup or position payload is accepted from the widget.

Widget status refresh follows the SDK's visibility/display-mode events and the
official sample's dispatch of callbacks to the view's UI thread:

- https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widget
- https://github.com/microsoft/XboxGameBarSamples/blob/master/Samples/WidgetAdvSampleCS/Widget1.xaml.cs

Read-only status serialization, timestamp presentation, request-channel tests,
saved/draft appearance UI and historical-result labels are original port code.
These changes do not change the OptiScaler shortcut implementation.

## Display diagnostics and supervised dynamic modes (0.1.32)

Original AGPL-3.0-only port code; no third-party display manager is copied.
The native adapter follows the Unicode Windows SDK structures and APIs:

- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaydevicesw
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaysettingsexw
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changedisplaysettingsexw
- https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-devmodew

Enumeration flags are zero (no EDS_RAWMODE or EDS_ROTATEDMODE). Candidate data
comes from enumeration; CDS_TEST (2) precedes the dynamic change (0). There is no
CDS_UPDATEREGISTRY, unsafe-mode flag, primary-display change or arbitrary native
mode payload through IPC. Unicode DEVMODE is 220 bytes; DISPLAY_DEVICE is 840.
Integer GDI refresh rates are not a measurement of fractional scan timing or FPS.

The bounded trial/recovery worker, protected atomic journal, owner PID/creation
time checks and IPC/UI are original code. Readback and identity checks reduce
the risk of stale/foreign changes; Windows does not provide an atomic compare
and exchange for these modes, and software deadlines cannot unblock a hung
graphics driver. Simulated checks establish policy, not real panel recovery.

## Game Bar package registration and telemetry overlay retirement (0.1.31)

The package-level `windows.activatableClass.proxyStub` extension follows the exact
interface names/GUIDs in `readme.txt` from the pinned Microsoft.Gaming.XboxGameBar
7.3.2506120 NuGet package. It is separate from the application-level widget
extension. This SDK requirement is not generated by the existing package build.

- https://learn.microsoft.com/en-us/xbox/game-bar/guide/pkg-manifest
- https://learn.microsoft.com/en-us/xbox/game-bar/designguide/widgetui

The registration includes the SDK's navigation interface. Correct packaging does
not establish runtime controller navigation; that requires testing inside Game
Bar separately from the standalone app. No additional controller readers, input
hooks or forced focus restoration are added. The old telemetry-only Game Bar
widget/page is removed; the settings widget and RTSS publisher remain.

## RTSS appearance and fixed OptiScaler shortcut (0.1.30)

Appearance validation, fixed metric formatting, widget preferences/preview and
bounded shortcut coordination are original AGPL-3.0-only port code. The existing
ClawTweaks OptiScaler quick-settings tile closes Game Bar then requests Insert;
this port implements its own fixed, capability-gated helper operation instead of
depending on the private upstream helper. No OptiScaler binaries/code are bundled.
Keyboard ownership rejects concurrent shortcuts immediately so the Legion HID
reader cannot wait behind the close/focus operation; its owned close step can reenter.
The OptiScaler project documents Insert as its menu shortcut:

- https://github.com/optiscaler/OptiScaler/blob/master/Features.md

Relative size syntax follows the installed RTSS SDK's OverlayEditor samples:
`OverlayAttributesLibrary.cpp::FormatExplicitAttrTag` generates `<S=n>` and
`OverlayLayer.cpp::ApplyTag` restores the size with `<S>`; DEFAULT_SIZE is 100.
Only the four fixed numeric sizes are generated. This reference adds no SDK source
copy, external template, new memory ABI, global RTSS setting or polling interval.
Game Bar foreground/background state follows its documented widget API:

- https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widget

## Physical menu ownership correction (0.1.29)

The original split layout incorrectly treated Menu as a right-half control.
Menu and View are both left-half controls on the original 8APU1; the corrected
fixed mapping keeps both on player 1 and gives right M3 the player-2 Start role.
This changes only report routing, with no new firmware command or reader change.

## Two-player input partition (0.1.28)

The fixed half-controller transform, two-slot coordinator extension and UI are
original AGPL-3.0-only code. One combined SDL source is partitioned into two
independently submitted ViGEm Xbox reports. Existing dependencies and their
licenses are unchanged; no external split-controller implementation or game
configuration is copied. The supervised receiver mode protocol is unchanged.
Software checks cannot establish game-specific controls, wireless auxiliary
provenance or hardware hot-plug behavior.

## Transient input recovery (0.1.27)

The bounded reconnect path and separate diagnostic counter are original
AGPL-3.0-only code, extending the existing source recovery coordinator. No new
HID protocol or receiver command is introduced. The original 8APU1 user guide
describes attached XInput, single-detached Dual D-input and both-detached XInput;
that description does not establish an application's live hot-plug compatibility.
Steam documents its desktop configuration separately from in-game mappings.

- https://download.lenovo.com/pccbbs/pubs/legion_go_8apu1/user_guide/en/index.html
- https://partner.steamgames.com/doc/features/steam_controller/getting_started_for_players
- https://partner.steamgames.com/doc/features/steam_controller/legacy_mode

## Suspend/resume lifetime (0.1.26)

The controller power snapshot, bounded source recovery coordinator and native
callback adapter are original AGPL-3.0-only code. Notification ABI and Modern
Standby opt-in follow Microsoft documentation; no third-party implementation is
copied. SDL/ViGEm APIs and licenses are unchanged. Windows QPC includes standby
time; a long sampling gap is a recovery trigger, not evidence of a power event.

- https://learn.microsoft.com/en-us/windows/win32/api/powerbase/nf-powerbase-powerregistersuspendresumenotification
- https://learn.microsoft.com/en-us/windows/win32/api/powrprof/ns-powrprof-device_notify_subscribe_parameters
- https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nc-powrprof-device_notify_callback_routine
- https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/powrprof.h
- https://learn.microsoft.com/en-us/windows/win32/api/powerbase/nf-powerbase-powerunregistersuspendresumenotification
- https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/w8cookbook/desktop-activity-moderator.md
- https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps

## Steam-hosted utility classification (0.1.25)

The application guard recognizes the Lossless Scaling process only when its
executable name and `steamapps/common/Lossless Scaling/LosslessScaling.exe` suffix
both match. This original classification code uses no third-party SDK or code.
It does not change or inspect the utility's settings and does not assert that
all Steam-hosted applications or games can be classified.

## Steam client during mode-based preparation (0.1.24)

`Helper/PlayerOneApplications.cs` and its tests are original AGPL-3.0-only port
code. Known executable names and `steamapps` path segments provide a bounded-use,
limited game check during preparation. This is not a Steam API, comprehensive
game detector or Steam Input integration. It neither modifies Steam nor closes
processes. The existing mode supervisor and recovery protocol are unchanged;
physical restoration never depends on this application check. Older PnP/FPS
application guards retain their previous behavior.

## Integrated Desktop and startup policy (0.1.23)

`Core/DesktopControllerProfile.cs`, `Core/ControllerStartup.cs`, the protected
helper store and their tests are original AGPL-3.0-only port code. Desktop is
manufactured in the helper, not loaded from editable profile/power data. Startup
uses the existing mode-based P1 route after recovery, with explicit boolean-only
configuration, durable attempt state, one attempt per helper and no retry loop.
Installer suppression prevents a software update starting input unexpectedly.
Status queries use cached state and the existing polling path; no extra input,
telemetry or mouse thread is added. The mode protocol facts below are unchanged.

## Mode-based player one integration (0.1.22)

`Helper/ModePlayerOnePort.cs` and its fake-device tests are original AGPL-3.0-only
port code. They adapt the existing preparation coordinator to the separately
supervised mode transport below, bind the inspected receiver and USB location
before arming, and retain recovery independent of cancellation. A new capability
and explicit IPC operation select this path; existing PnP commands keep their
semantics. No external controller initialization/remapping code was copied.
The passive button reader remains unchanged. That version did not include startup
activation. Software simulations and a mode-only cycle do not establish
integrated input/rumble, game compatibility or driver-failure recovery.

## Supervised protocol mode-cycle diagnostic (0.1.21)

`Core/ControllerModeRecovery.cs`, `Core/ControllerModeCycle.cs`, their helper
adapters and tests are original AGPL-3.0-only port code. They define strict
mode-response parsing, protected storage, identity/deadline rules and separate
supervision for an explicit installed-admin diagnostic. Startup/update/uninstall
recover pending mode journals. That version introduced no mode-based virtual
activation or widget command. The passive button reader is unchanged.

Protocol facts: the MIT GoTweaks monitor at commit
`99ed5f5f8c22bb78bfffee9b37f9a04094f30482` queries gamepad mode with
`05 00 03 0E 03` in a 64-byte output report. The matching input report begins
`04 00 03 0E 03`; byte 5 is 1 for XInput or 2 for DirectInput. The mode setter is
`05 00 04 0E 03 mode`, also padded to 64 bytes. Transport is independently
implemented with Windows overlapped I/O and fresh re-enumeration/readback. No
initialization or remapping implementation is copied.
https://github.com/corando98/GoTweaks/blob/99ed5f5f8c22bb78bfffee9b37f9a04094f30482/XboxGamingBarHelper/Labs/LegionButtonMonitor.cs

The original-Go `LegionGoTablet` class in the already reviewed Handheld Companion
commit `a250b19a4b7e29dac3b383f2d1ac517d1af43e4e` independently identifies the
61EB/61EC XInput/DirectInput pair. Only identity/protocol facts were consulted;
no CC-BY-NC-SA implementation or VIIPER component is copied or linked. This first
policy limits the proposed transition to that PID pair and separately requires
the original-Go/Z1 Extreme machine gate. Fake-device tests establish ordering and
failure handling only; they do not establish that a firmware transition frees an
XInput slot or that its recovery works on a physical machine.

Readonly USB ancestry/health uses Microsoft Configuration Manager metadata;
no Configuration Manager disable, enable, remove or reset function is imported
by the mode transport. All configuration writes are limited to the gamepad-mode
feature and require the protected operation's live worker authorization.
https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_registry_propertyw

`DEVPKEY_Device_LocationPaths` binds the USB-port location across PID/instance-ID
changes. The original XInput instance is also retained for final recovery checks;
the temporary instance ID is observed, not constructed by substituting its PID.
Alternative ACPI descriptions do not replace the explicit USB-port path.
https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-locationpaths

## Optional RTSS OSD publisher (0.1.18)

The memory adapter, service, formatter, UI and tests are original AGPL-3.0-only port
code. They reimplement the documented byte layout; no SDK implementation, RTSS
executable, hook DLL, renderer, installer or third-party wrapper is redistributed.
RTSS is installed/configured separately by its user. Official distribution:
https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/

Primary ABI reference: RTSS 7.3.5 SDK, `SDK/Include/RTSSSharedMemory.h`, SHA256
`615D6E295FDEFDE1A166A5FDCC6F84BD848E61DA5E31BBA6DCA05A3AC9390EC8`.
The corresponding `SDK/Samples/SharedMemory/RTSSSharedMemorySample/RTSSSharedMemorySampleDlg.cpp`
documents nonprimary OSD slots, exact owner IDs, extended 4096-byte text and the
`<FR>`/`<FT>` game-specific tags (lines 771–773 of this SDK). Lock bit 0 of `dwBusy`
is defined for ABI 2.14 onward. Version 0.1.19 accepts the reviewed ABI through 2.21.
The 2.21 additions in `RTSS_SHARED_MEMORY_APP_ENTRY` are timing statistics; they do
not change the 96-byte header or the OSD owner/text offsets used here. Those added
application fields are not read by the port. Later versions require another SDK
review. The adapter uses the legacy extended
text field, as the SDK SharedMemory sample does; it does not use the v2.20 32 KiB
text extension or embedded graphs.

The byte bounds, owner checks and concurrency rules are tested with simulated
memory. A successful write does not establish that a game hooks, renders the text
or reports accurate frame statistics. No game-application table or process names
are read by this integration, and no telemetry driver is added.

## Saved-profile comparison, CSV review and freshness (0.1.17)

`Core/ProfileComparison.cs`, `Core/SessionSummary.cs`, `Core/TelemetryFreshness.cs`
and their widget controls/tests are original AGPL-3.0-only port code. No external
CSV, plotting or telemetry package is added. The session reader consumes only the
existing port CSV schemas through a manually selected UWP file. It never executes
text cells, uploads records or queries hardware. File picker APIs are linked below.
Snapshot UTC and local monotonic receipt clocks describe data freshness, not sensor
accuracy, input latency or measured performance gains.

## Legion Space, profiles and assisted player 1 (0.1.10, in development)

`Core/LegionSpace.cs`, `Helper/LegionSpaceBackend.cs`, `Core/ProfileTransfer.cs`
and their widget controls/tests are new AGPL-3.0-only code for this port. No
GoTweaks service-control implementation is copied. The backend uses Windows SCM
APIs for the fixed `DAService` name and validates its Lenovo installation path.
Only startup mode, delayed-start flag and running state are changed; dependent
services are not recursively stopped. No service-restoration action is tied to
profile application, startup, power restoration, shutdown or uninstall.

Microsoft API references:

- [QueryServiceConfigW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceconfigw),
  [QueryServiceConfig2W](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceconfig2w)
  and [QueryServiceStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryservicestatusex).
- [ChangeServiceConfigW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfigw),
  [ChangeServiceConfig2W](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfig2w)
  and [SERVICE_DELAYED_AUTO_START_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_delayed_auto_start_info).
- [StartServiceW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicew)
  and [ControlService](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-controlservice).
- [UWP FileOpenPicker](https://learn.microsoft.com/en-us/uwp/api/windows.storage.pickers.fileopenpicker),
  [FileSavePicker](https://learn.microsoft.com/en-us/uwp/api/windows.storage.pickers.filesavepicker)
  and [CachedFileManager](https://learn.microsoft.com/en-us/uwp/api/windows.storage.cachedfilemanager).

The name editor uses ordinary UWP controls and direct draft edits from its own
touch buttons. It neither hooks keyboard input globally nor invokes an external
keyboard process. No Game Bar input or picker compatibility is inferred from
the core tests or C# metadata compilation.

`Core/PlayerOnePreparation.cs`, `Helper/PlayerOnePort.cs` and
`Widget/MainPage.PlayerOne.cs` are original AGPL-3.0-only code. The assistant uses
the existing ViGEm API for its final target, readonly SetupAPI enumeration and
System32 `XInputGetState`. The user operates the physical FPS switch; there are
no PNP disable/enable/reset calls or controller firmware commands in this path.
The existing selective HidHide journal and supervisor remain responsible for
physical visibility. No Handheld Companion or GoTweaks slot-management code is copied.

- [XInputGetState](https://learn.microsoft.com/en-us/windows/win32/api/xinput/nf-xinput-xinputgetstate).
- [SetupDiEnumDeviceInterfaces](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdienumdeviceinterfaces).

Windows documents controller indexes 0–3 and distinguishes disconnected devices
from other errors. The adapter preserves that distinction, while reporting player
numbers 1–4 in the widget. These API contracts do not establish a game's selection
policy or compatibility with the virtual controller.

## ClawTweaks

- Repository: https://github.com/enterTheVoidCode/ClawTweaks
- Fork base: https://github.com/MazTema-debug/ClawTweaks/commit/8e34189693fa2d8dd31de11d7637d773f9aaa634
- Branch: `release/v0.3.98.0`
- License: AGPLv3 (root `LICENSE`); pre-existing MIT portions retain root `LICENSE.MIT`.
- `XboxGamingBar/App.xaml.cs`: Game Bar activation lifecycle / retained widget object.
- `XboxGamingBar/IPC/NamedPipeClient.cs`: UWP CreateFileW + async FileStream transport technique.
- `XboxGamingBar/GamingWidget.xaml.cs`, `Next Gen Claw`: blue/violet palette and card styling.
- `BUILDING.md`: documents the absence of the private hardware helper.

`LegionGo/Widget` adapts these pieces into a separate UWP target. It does not claim to
port the unavailable helper or all original UI features. The new geometric icon is
not the upstream ClawTweaks/MSI logo.

## GoTweaks

- Repository: https://github.com/corando98/GoTweaks
- Source commit: https://github.com/corando98/GoTweaks/commit/99ed5f5f8c22bb78bfffee9b37f9a04094f30482
- Branch: `release/v0.3.98.0`
- License: MIT, preserved verbatim in `LICENSE.GoTweaks`.
- Attribution: corando98 and GoTweaks contributors; original license credits Microsoft Corporation.

| Adapted backend | GoTweaks source |
| --- | --- |
| Original Go identity | `XboxGamingBarHelper/Devices/LegionGo/LegionGoConfig.cs` |
| Capability IDs | `XboxGamingBarHelper/Devices/Libraries/Legion/LegionGoLibrary.cs` |
| WMI classes, methods and argument names | `XboxGamingBarHelper/Devices/Libraries/Legion/LenovoWMIService.cs` |
| SPL/SPPT/FPPT mapping and Custom transition | `XboxGamingBarHelper/Devices/Libraries/Legion/LegionManager.cs`, `SetCustomTDP` / `ApplyTDPValues` |
| Windows CPU/overlay controls | `XboxGamingBarHelper/Power/PowerManager.cs`, `XboxGamingBarHelper/Windows/PowerGuids.cs` |
| Original Legion front-button report layouts (0.1.8) | `XboxGamingBarHelper/Labs/LegionButtonMonitor.cs`: 64-byte reports, 04:00:A1 offset 16 / 04:3C:74 offset 18, L/R masks 80/40 |
| Vendor HID selection (0.1.8) | `XboxGamingBarHelper/Devices/Libraries/Legion/LegionGoController.cs`: Lenovo usage page FFA0; 0.1.8 initially restricted PIDs to 6182–6185; corrected in 0.1.9 |

The 0.1.8 reader and shortcut state machine are new, minimal implementations.
They do not copy GoTweaks' initialization, firmware remapping, gyro, service
disabling or controller-emulation paths. They issue no HID output/feature writes.
Win+G uses Microsoft's documented `SendInput` keyboard sequence pattern, with
x64 INPUT layout and modifier/desktop guards:
https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput

The 0.1.9 device-ID correction uses the two tablet PID families already listed in
GoTweaks' `LegionButtonMonitor.cs` (`LEGION_TABLET_PIDS`). Handheld Companion's
`HandheldCompanion/Devices/Lenovo/LegionGoTablet.cs` at commit
`a250b19a4b7e29dac3b383f2d1ac517d1af43e4e` independently identifies 61EB–61EE as
2025-firmware IDs for the original Go and specifies FFA0:0001. Only this hardware
identity information was consulted; no Handheld Companion implementation is copied.
The port still gates the machine model and CPU separately.

HID capability sizes and collection usage follow Microsoft's HIDP_CAPS contract:
https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/ns-hidpi-_hidp_caps

The ID mapping intentionally follows `LegionManager.ApplyTDPValues`: SPL →
`0x0101FF00`, SPPT → `0x0102FF00`, FPPT → `0x0103FF00`. Some upstream XML comments
call these short/long in an inconsistent order; the actual manager call sites are
the reference. Lenovo mode bytes are 1/2/3/255. Charge limit uses `0x03010001`; fan
maximum uses `0x04020000`. These reverse-engineered capabilities are not claimed as
an official Lenovo SDK contract; firmware may reject them and the port reports that.

The new engine adds stricter Z1 Extreme gating, conservative 5–30 W limits,
confirmed mode transitions, readback, rollback, persistent originals, and tests.
It never interprets a cached/default value as a successful hardware read.

## LibreHardwareMonitor — read-only AMD interop

- Repository: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- Source commit: `eb4f29f77afc58be3a2037f88b9353b4982305dc`.
- `LibreHardwareMonitorLib/Interop/AtiAdlxx.cs`: ADL2 P/Invoke signatures, ANSI
  adapter records, PMLog records, ODN EDGE temperature and OD5 temperature/activity layouts.
- `LibreHardwareMonitorLib/Hardware/Gpu/AmdGpu.cs`: sensor IDs/units, EDGE/GFX
  temperature fallback, millivolt conversion and GPU/ASIC/CPU power domains.
- License: MPL-2.0, retained in `LICENSE.LibreHardwareMonitor` and `Helper/AmdNative.cs`.

The adapted `AmdNative.cs` keeps only enumeration, telemetry and context-lifetime
entry points, renamed for this target. It adds no tuning calls. `AmdTelemetry.cs`
adds per-reading support/range validation, unique physical-GPU selection, fresh
sample errors and OD5/ODN fallbacks. The original complete hardware-monitor library,
kernel driver and AMD SDK are not redistributed. Source for the adapted file is
included in this public repository at the workflow commit producing each package.

## AMD power audit and recovery (0.1.6)

- Official ADL reference commit: `84c881cb2136f04287eb3af12de00a9e2867aa9e`.
- [ADLSensorType definitions](https://github.com/GPUOpen-LibrariesAndSDKs/display-library/blob/84c881cb2136f04287eb3af12de00a9e2867aa9e/include/adl_defines.h).
- [ADLPMLogDataOutput layout](https://github.com/GPUOpen-LibrariesAndSDKs/display-library/blob/84c881cb2136f04287eb3af12de00a9e2867aa9e/include/adl_structures.h).
- [API release notes](https://github.com/GPUOpen-LibrariesAndSDKs/display-library/blob/84c881cb2136f04287eb3af12de00a9e2867aa9e/Public-Documents/README.md).
- [Overdrive8 API documentation](https://github.com/GPUOpen-LibrariesAndSDKs/display-library/blob/84c881cb2136f04287eb3af12de00a9e2867aa9e/Public-Documents/html/group__OVERDRIVE8API.html).

| PMLog reading | Sensor ID | Integer unit |
| --- | --- | --- |
| GFX clock | 1 | MHz |
| EDGE temperature | 8 | °C |
| SoC voltage | 16 | mV |
| GFX activity | 19 | % |
| GFX voltage | 21 | mV |
| ASIC power | 23 | W |
| GFX temperature fallback | 28 | °C |
| GFX power | 30 | W |
| CPU power | 33 | W |

The named IDs preserve the earlier LibreHardwareMonitor mapping. Each record is
`int supported; int value`, and the query output is an integer size followed by
256 records (2052 bytes). It has no timestamp. The audit did not find a swapped
power field or justify a scaling correction. The three domains are read from the
same output buffer; non-additivity and driver freshness remain unverified.

AMD deprecates `ADL2_New_QueryPMLogData_Get` for efficiency and documents an
Overdrive8 shared-memory replacement with support/start/read/stop lifecycle.
That deprecation alone does not establish incorrect watt readings.
Version 0.1.6 retains the known-working reader while adding raw provenance and
context recovery; it does not silently introduce another backend or redistribute
the SDK. `AmdSession` contains the existing native context/enumeration/fallback
code; `AmdTelemetry` owns the testable recovery policy. Only context management
and sensor reads are invoked. No GPU tuning or Windows setting writes are added.

## Former Game Bar telemetry overlay (retired in 0.1.31)

- Microsoft widget API: https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widget
- Widget-to-widget activation: https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widgetcontrol
- Pinning/click-through: https://learn.microsoft.com/en-us/xbox/game-bar/guide/click-through
- Opacity implementation: https://github.com/microsoft/XboxGameBarSamples/blob/master/Samples/WidgetAdvSampleCS/Widget1.xaml.cs

For opacity, the executable Microsoft sample's `SetBackgroundOpacity` uses
`widget.RequestedOpacity` directly as a XAML 0–1 value. This corrects the extra
percentage conversion in 0.1.4. Some prose
API documentation describes a 0–100 scale; the runtime/sample behavior is used here.

The former `OverlayPage` used a separate `LegionOverlay` app extension, Game Bar visibility and
opacity events, and read-only polling. It did not inject code into games or
capture frames. Version 0.1.4 corrected near-transparent rendering. Version 0.1.5
also labelled the live Lenovo mode and distinguished stored custom registers from
an active Custom mode.

## CSV recording

`TelemetryCsv`, `TelemetryRecorder` and `HelperSession` are original code for this
port, under its AGPL-3.0-only license. They use the existing read-only status path;
no additional telemetry driver, SDK, RTSS component or hardware writer is added.
The CSV format, error handling and gap markers are documented in README.md.

- Explorer launch API: https://learn.microsoft.com/en-us/uwp/api/windows.system.launcher.launchfolderpathasync?view=winrt-26100

Microsoft documents that this API must run on the UI thread and requires the user
to have folder access, but not the calling UWP app. The helper grants the owner
read access to its CSV subfolder while keeping the journal and output-path creation
protected. Sampling gaps use observed UTC intervals, not unverified suspend events.

## Combined profiles and controller backend (0.1.7)

- Handheld Companion `a250b19a4b7e29dac3b383f2d1ac517d1af43e4e` was reviewed for
  the separation of physical input, per-profile layouts, virtual output and HidHide.
  Files reviewed include ControllerManager.XInput, LegionControllerXInput,
  Xbox360Target/VIIPERTarget, OpenXInput and Misc/HidHide. Its current license is
  CC-BY-NC-SA-4.0 and its current output backend is VIIPER. No HC code, assets or
  binaries are copied or linked into this AGPL port. Our implementation uses the
  public APIs below. It does not claim HC feature or compatibility parity.
- SDL 2.32.10, commit `5d249570393f7a37e037abf22cd6012a4cc56a71`, zlib license.
  Public SDL_gamecontroller.h/SDL_joystick.h APIs supply standard input. The Windows
  x64 runtime release ZIP is pinned by SHA-256 in Build-LegionGo.ps1.
  https://github.com/libsdl-org/SDL/tree/release-2.32.10
- Nefarius.ViGEm.Client 1.21.256 (NuGet), MIT; Xbox 360 output and feedback API.
  https://github.com/nefarius/ViGEm.NET
  Driver retirement: https://docs.nefarius.at/projects/ViGEm/End-of-Life/
  ViGEmBus is an external optional prerequisite, not distributed by this package.
- HidHide public IOCTL interface: https://docs.nefarius.at/projects/HidHide/API-Documentation/
  Constants and buffer ABI checked against Nefarius.Drivers.HidHide (MIT); the
  thin local bridge holds one handle across each read/modify/write transaction.
  The driver and its .NET package are not bundled. Attribution included.
- Windows SetupAPI enumerates GUID_DEVINTERFACE_XUSB_DEVICE to identify the single
  physical Lenovo endpoint. No arbitrary device IDs can be sent through IPC.
  Library source: https://github.com/nefarius/Nefarius.Drivers.HidHide

New core tests exercise legacy migration, bounded CRUD/storage, whole-profile
validation/preflight/rollback, AC/DC identity, original boost-mode restoration,
button-map composition and release, axis bounds/deadzones, selective HidHide
cleanup and preservation of foreign entries. Windows builds compile the native
interop and actual UWP editor; physical gaming/input behavior is not simulated
into a claim of device validation.
# Configuration Manager device transitions (0.1.11)

The optional automatic player-1 operation uses Microsoft's documented
Configuration Manager APIs for the exact original Lenovo XUSB MI_00 endpoint.
Its implementation is original to this port. It does not use Handheld Companion
or VIIPER code and does not write vendor HID/firmware commands.

- https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode
- https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_enable_devnode
- https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_status
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/porting-from-setupapi-to-cfgmgr32

Disable flags exclude CM_DISABLE_PERSIST. This documents the API contract, not
successful hardware validation or guaranteed cancellation of a blocked driver.

Windows SDK numeric definitions: `CM_DISABLE_UI_NOT_OK = 0x4`,
`CM_DISABLE_HARDWARE = 0x2`, `CM_DISABLE_PERSIST = 0x8`:
https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/cfgmgr32.h

## Explicit busy-reader reconnection and cursor output (0.1.13)

Original port code; no upstream controller/firmware implementation is copied.
The opt-in busy-reader operation uses the Windows SDK header's ABSOLUTE (1) and
UI_NOT_OK (4) flags, without HARDWARE (2) or PERSIST (8). Learn's function page
documents UI_NOT_OK/PERSIST; the header definitions do not establish successful
reconnection on any particular driver. The default operation remains polite.

- https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/cfgmgr32.h
- https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_disable_devnode
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-input
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openinputdesktop
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow

The x64 INPUT record is 40 bytes, with its MOUSEINPUT union at offset 8. Cursor
output uses relative movement only. Desktop/focus observations narrow the allowed
output window; they are not a guarantee that focus cannot change immediately
before Windows processes input. No keyboard hooks or firmware mappings are added.

## Driver-reported slot state

ViGEm.NET v1.21.256, commit 50422c0f9703059405b133ef61f27b516339f82b,
Xbox360Controller.UserIndex reads a cached field updated by an LED notification.
The port timestamp records a property read, not a new independent XInput query.
https://github.com/nefarius/ViGEm.NET/blob/50422c0f9703059405b133ef61f27b516339f82b/ViGEmClient/Targets/Xbox360Controller.cs

## Passive button sharing and PnP evidence (0.1.16)

The shared reader, input gates, candidate-bit observer, profile utilities and
protected mouse preferences are original port code. L/R use the already attributed
passive report formats; Y/M candidate masks are observations, not physical mappings.
No Handheld Companion/VIIPER implementation or firmware setter is included.
The candidate byte is offset 18 for `04 00 A1` or 20 for `04 3C 74`, masked with
FC to exclude Back/Start, separate from stick/trigger fields in the pinned monitor:
https://github.com/corando98/GoTweaks/blob/99ed5f5f8c22bb78bfffee9b37f9a04094f30482/XboxGamingBarHelper/Labs/LegionButtonMonitor.cs

Version 0.1.20 extends the original decoder and layout/gate/UI code to the
identified Y1/Y2/Y3 (80/40/20) and M2/M3 (04/08) digital masks in that candidate
byte. These protocol constants are reimplemented; no monitor implementation,
firmware mode switch, latched register setter or HID initializer is copied.
The original Go's physical labels are documented by Lenovo:
https://download.lenovo.com/pccbbs/pubs/legion_go_8apu1/user_guide/es/index.html
RB/M1 is one control with a mode-specific name, not another independently exposed
rear input. The decoder does not assign the remaining 10/03 bits as auxiliaries.
Synthetic reports exercise both supported layouts; private captures are not
distributed and tests do not establish compatibility with every firmware.

PnP diagnostics read existing Windows events through the .NET Eventing reader.
An explicit XPath uses UTC SystemTime; no log/channel is enabled or cleared.
Missing records remain unknown rather than proof of absent vetoes.

- https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader.eventlogquery
- https://learn.microsoft.com/en-us/windows/win32/wes/consuming-events
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/cfg/ne-cfg-pnp_veto_type
