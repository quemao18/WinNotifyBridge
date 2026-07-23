# Changelog

All notable changes to this project will be documented in this file.

## [v1.1.0] - 2026-07-06

### Added
- Keep-awake option in Settings to reduce missed notifications while the user is away but still signed in.
- Local `WNB_LISTENER_PATH` override so the tray can launch the listener more reliably in local development setups.
- Dedicated `CHANGELOG.md` for release tracking.

### Changed
- Diagnostics monitor is now generic and focused on Event Viewer and WPN Database inspection instead of Teams-only labels.
- Tray icon handling now uses the packaged application icon.
- MSI installer now offers a Finish dialog option to launch the tray through an elevated helper script.
- MSI/release packaging promoted to semantic version `v1.1.0`.

### Fixed
- Diagnostics mode is disabled automatically in Release builds.
- Release packaging and documentation now align with the latest feature set and MSI naming.

## [v1.0.0] - 2026-07-06

### Added
- Initial public release of the Windows service, tray application, and listener.
- Telegram forwarding for supported Windows notifications.
- Basic diagnostics monitor and MSI installer packaging.

## Tag comparison

- `v1.0.0...v1.1.0` once the new tag is pushed: `https://github.com/quemao18/WinNotifyBridge/compare/v1.0.0...v1.1.0`

## Release assets

- `v1.0.0` → `WinNotifyBridge-v1.0.0.msi`
- `v1.1.0` → `WinNotifyBridge-v1.1.0.msi`
