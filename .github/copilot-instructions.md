# Copilot Instructions

## Project Guidelines
- Always align with Sonar rules and do not leave warnings or errors in the code.
- Exclude YourPhone/Phone Link notifications from forwarding; keep native Windows toasts eligible, and later support tray-based app selection for Telegram forwarding.
- Implement a UI-based diagnostics interface to detect Teams toasts instead of relying only on raw logs.
- For release builds, disable diagnostics mode.

## User Preferences
- Prefer a visual/UI-based approach for development tasks, specifically favoring modern WPF over WinForms.