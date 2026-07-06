# WinNotifyBridge

WinNotifyBridge is a Windows desktop bridge that captures supported Windows notifications and forwards them to Telegram.

## What is included in the installer

The MSI installer is intended to install and configure all required parts:

- **Service**: receives and forwards notifications
- **Tray app**: user interface to manage the service and open diagnostics
- **Listener**: captures notification data and sends it to the service

## Requirements

- Windows 10/11
- .NET Framework 4.8
- Administrator rights for installation and service configuration
- Telegram bot token and chat ID

## Download

Download the latest MSI from the repository **Releases** page.

- **Latest stable release**: v1.0.0
- **Installer**: `WinNotifyBridge-v1.0.0.msi`

## Installation

1. Download the MSI from the Releases page.
2. Run the installer as Administrator.
3. Complete the installation wizard.
4. Sign in to Windows and open **WinNotifyBridge Tray** if it does not start automatically.
5. Open **Settings** from the tray icon and configure:
   - Telegram Bot Token
   - Telegram Chat ID
   - Optional URL prefix and app filter settings

## First-time setup

1. Create a Telegram bot with [BotFather](https://t.me/BotFather).
2. Get the bot token.
3. Send a message to your target chat.
4. Get the chat ID for that conversation.
5. Open the tray settings and save the values.

## How it works

- The **service** runs in the background and exposes a local bridge endpoint.
- The **tray app** manages the service and starts the listener.
- The **listener** reads supported Windows notification data and forwards eligible notifications.

## Supported notifications

WinNotifyBridge is designed to forward native Windows notifications that appear in the Windows notification pipeline.

Examples include:

- Microsoft Teams (PWA) toast notifications
- Snipping Tool notifications
- Other eligible Windows app toasts

Phone Link / Your Phone notifications are excluded.

## Diagnostics

The tray includes a diagnostics monitor for troubleshooting:

- **Event Viewer** view
- **WPN Database** view

This helps verify whether a notification is visible to Windows and whether it reaches the notification database.

## Troubleshooting

### I installed the MSI but notifications do not arrive

Check the following:

- The service is running
- The tray app is open
- The listener process is running
- Telegram bot token and chat ID are correct
- The app is allowed by your filter settings

### Teams notifications are not forwarded

Verify that Teams is generating a real toast notification in Windows.
If Teams is using the PWA/browser notification path, it should still appear in the diagnostics monitor and listener logs when enabled in development builds.

### Nothing starts after login

Make sure the installer added the tray startup entry or scheduled task correctly.

## Release history

### v1.0.0

Initial public release.

- MSI installer for service, tray, and listener
- Windows tray UI for service control and settings
- Notification forwarding to Telegram
- Diagnostics monitor for Windows notification troubleshooting

## Build from source

Open `WinNotifyBridge.sln` in Visual Studio and build the solution in `Release`.
The release output can be packaged into an MSI installer.
