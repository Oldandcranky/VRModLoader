# Changelog

All notable changes to VR Mod Loader. Version numbers come from the `VERSION` file; `build.ps1` stamps them
into the exe (`-Bump patch|minor|major` increments it).

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
