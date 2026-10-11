# Changelog

All notable changes to VR Mod Loader. Version numbers come from the `VERSION` file; `build.ps1` stamps them
into the exe (`-Bump patch|minor|major` increments it).

## 1.3.4 - 2026-10-10

### Fixed
- OLED guard now blacks out the whole screen. In a windowed (attach-and-watch) session it left the title bar and
  taskbar lit; it now goes borderless-fullscreen while the guard is on and restores the window afterwards.
- Live view sometimes stayed black after minimizing and restoring (or coming back from the tray). The DWM thumbnail
  is now detached and re-registered on restore so it always comes back.

### Internal
- Added the OpenVR frame-stats backend (not yet wired into the UI).

## 1.3.3 - 2026-10-10

### Fixed
- Settings window opened scrolled sideways with the whole left label column hidden (a long hint forced the grid wider
  than the dialog, and focusing the first field scrolled it right). The grid is now docked to the panel width so it
  never scrolls horizontally, long hints wrap, and the scroll resets to the top-left on open.

## 1.3.2 - 2026-10-10

### Fixed
- SteamVR Status box was black. The DWM thumbnail would not render into that panel inside the fullscreen launcher;
  switched it to a PrintWindow (`PW_RENDERFULLCONTENT`) capture refreshed ~once a second, which reads the DWM surface
  and works with the launcher on top. The game view keeps the live DWM thumbnail.

## 1.3.1 - 2026-10-10

### Fixed
- Minimized windows (the game's mirror, the SteamVR Status window) are now really restored behind the launcher so
  their live pictures render. `ShowWindow(SW_SHOWNOACTIVATE)` does not un-minimize; `SetWindowPlacement` does, with
  an activating restore as fallback after which the launcher takes the foreground back.

## 1.3.0 - 2026-10-10

### Added
- **SteamVR Status picture** in the LIVE VIEW tab: Valve's device icons and frame-time graph, live, next to the game
  view (same DWM technique; the window is kept open behind the launcher instead of minimized).
- **Link quality panel**: Meta's service logs a round trip every second while streaming (Air Link or cable). The
  dashboard shows the current round trip, session average / max / jitter, spike count, a 90-second sparkline, the
  headset battery and on/off state and SteamVR's state. The session record gets the averages ("Air Link good (avg 6
  ms, max 24)") and the Sessions tab shows them.

## 1.2.3 - 2026-10-10

### Fixed
- Drag-to-reorder no longer ping-pongs: the target slot is computed from where the dragged row would sit, so it only
  changes when you cross a half-row boundary.
- Header alerts clear themselves: SteamVR coming back to Ready clears a "lost the headset" alert, and any alert expires
  after two minutes. A SteamVR Standby while the headset is off (proximity sensor) is logged as normal, not as an alert.

## 1.2.2 - 2026-10-10

### Changed
- Reorder the launch sequence by **dragging a step row** (grab its name and move it; the other rows make room live).
  The ▲▼ buttons from 1.2.1 are gone. The launch row still stays last.
- `tools\publish.ps1`: tag, push and publish a release with the exe attached, using the stored git credential.

## 1.2.1 - 2026-10-10

### Added
- **Reorderable launch sequence**: ▲▼ on every step row; the order is saved. "Launch Titanfall 2 VR" stays last.
- **Air Link is first-class**: the Headset Link step waits for Link over Wi-Fi or cable (OculusDash / USB Link mode)
  and the coaching says exactly what to tap. Settings → Launch has a "Headset connects via" choice (auto / cable /
  Air Link) that only changes the coaching and whether a missing cable is a warning.

### Removed
- The automatic "nudge": asking Meta to raise a prompt only produced the USB-connection prompt, which does not start
  Link. Link is always started from the headset's Quick Settings. The USB re-plug stays as a manual option on the
  timeout dialog when a cable is present.

## 1.2.0 - 2026-10-10

### Added
- **Headset Link step** (between Meta Horizon and SteamVR). The PC can tell a Quest that is merely plugged in
  (USB `VID_2833&PID_5011`) from one that is streaming over Link (`PID_5010`, or `OculusDash.exe` running - also
  Air Link). The step waits for Link and coaches whoever holds the headset with a big on-screen banner: plug the
  cable in / wake the headset, then "tap Allow on the Link prompt, or Quick Settings → Link → <this PC>". It asks
  Meta to raise the Link prompt (any VR app registering with the service makes it call `launchLink()`; the mod's
  `xr_probe` does the job) and the timeout dialog has a **Nudge** button that asks again and re-plugs the USB
  device (`pnputil /restart-device`, one UAC click).
- Pre-flight shows the headset's USB state; the in-game watch logs "headset put on / taken off" and the switch into
  Link mode.

### Changed
- Paths are **auto-detected** instead of hard-coded: the game folder from CircuitLord's installer settings, the
  Respawn registry key, every Steam library, default EA/Origin folders or a drive scan; the Meta Horizon client from
  its registry base / Program Files; the EA app from the Origin `ClientPath` key; CircuitLord's exe from Downloads,
  Desktop, Documents and friends (or a running instance). A saved path that stops existing is re-detected on start,
  and Settings has an "Auto-detect the four paths again" link.

## 1.1.0 - 2026-10-10

### Added
- **Focus mode**: on start, every other window is minimized (Win+D) and the launcher goes borderless fullscreen with
  its own header buttons; F11 toggles. Launched apps' windows are minimized as they become ready. After the tear-down
  the desktop is restored. Skipped automatically when a game is already running. `--windowed` skips it for one run.
- **Attach & watch**: pressing LAUNCH while the game is already running offers to watch that session instead of
  running the steps.
- **In-game dashboard** with a **LIVE VIEW** tab: live DWM picture of the game's mirror window plus a compact log.
  A minimized game window is restored behind the launcher so the picture keeps running.
- **Headset link watch**: Meta's service log and SteamVR's monitor log are tailed in game; USB errors, Link
  terminations, SteamVR Standby and low headset battery are flagged red, raise a tray balloon and land in the
  session record.
- **OLED guard**: after 2 minutes without PC input the screen goes near-black with a dim drifting timer and a small
  drifting live view.
- **Sleep guard**: the PC is held awake while a session runs (never the display).
- Idle status line shows a game running outside the launcher.

### Fixed
- Meta Horizon step: `OculusClient.exe` is a 0-byte alias; readiness now checks the real `Client.exe` processes under
  `Meta Horizon\Support\oculus-client` (the step used to time out after 45 s every time).
- `xr_probe` crashing (0xC0000409) when no OpenXR runtime is up is treated as "not ready"; the pre-flight probe is
  skipped while SteamVR is down (saves 8 s and a Windows error report per run).
- 175 % DPI layout: every pixel size is scaled; custom checkboxes paint their own background.
- Pre-flight list no longer starts scrolled past its first rows.

### Changed
- The red button reads **KILL GAME + CLOSE ALL** while a game runs and its confirmation says what it does.

## 1.0.0 - 2026-10-10

First build: pre-flight checks, ordered launch with smart waits (Meta Horizon, SteamVR, EA app, CircuitLord
window), headset probe via the mod's `xr_probe.exe`, Discord kill + restart, save backups, direct launch
replicating CircuitLord's *Launch in VR*, game watch with Northstar / crash_monitor log mirroring, crash analysis
from the crash_monitor session folder, auto-close countdown and tear-down, session history, tray icon,
dark DPI-aware UI, in-code icon, one-command build with the compiler that ships with Windows.
