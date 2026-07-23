@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "Start-Process -FilePath '%~dp0WinNotifyBridge.Tray.exe' -Verb RunAs"
