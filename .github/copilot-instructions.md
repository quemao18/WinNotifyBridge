# Copilot Instructions

## Project Guidelines
- Always align with Sonar rules and do not leave warnings or errors in the code.
- Exclude YourPhone/Phone Link notifications from forwarding; keep native Windows toasts eligible, and later support tray-based app selection for Telegram forwarding.
- Implement a UI-based diagnostics interface to detect Teams toasts instead of relying only on raw logs.
- For release builds, disable diagnostics mode.
- Make the away-mode reliability recommendation configurable from Settings.
- Launch the tray in administrator mode at the end of installation.
- Use semantic versioning for feature releases; for example, version the keep-awake addition as v1.1.0 instead of v1.0.1.

## User Preferences
- Prefer a visual/UI-based approach for development tasks, specifically favoring modern WPF over WinForms.