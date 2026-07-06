#Requires -RunAsAdministrator
<#
.SYNOPSIS
	Stops the WinNotifyBridge service, copies the latest build binaries
	to the installed location, and restarts the service and tray app.

.DESCRIPTION
	Run this script as Administrator after building the solution.
	It automatically picks the most recently compiled output between
	Debug and Release configurations for each project.
#>

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

function Stop-ProcessesUsingPath {
	param([string]$TargetPath)

	$normalizedTarget = [IO.Path]::GetFullPath($TargetPath)
	$matches = @()
	Get-Process | ForEach-Object {
		try {
			foreach ($module in $_.Modules) {
				if ([string]::Equals($module.FileName, $normalizedTarget, [StringComparison]::OrdinalIgnoreCase)) {
					$matches += $_
					break
				}
			}
		} catch {
			# Access denied for some system processes; ignore.
		}
	}

	if ($matches.Count -eq 0) {
		return
	}

	foreach ($process in $matches | Sort-Object Id -Unique) {
		if ($process.Id -eq $PID) {
			continue
		}

		try {
			Write-Host ("Stopping process locking deployment file: {0} ({1})" -f $process.ProcessName, $process.Id) -ForegroundColor Yellow
			Stop-Process -Id $process.Id -Force -ErrorAction Stop
		} catch {
			Write-Warning ("Could not stop process {0} ({1}): {2}" -f $process.ProcessName, $process.Id, $_.Exception.Message)
		}
	}
}

function Get-NewestBinDir {
	param([string]$ProjectDir, [string]$ExeName)
	$debug   = Join-Path $ProjectDir "bin\Debug\$ExeName"
	$release = Join-Path $ProjectDir "bin\Release\$ExeName"
	$dItem   = Get-Item $debug   -ErrorAction SilentlyContinue
	$rItem   = Get-Item $release -ErrorAction SilentlyContinue
	if ($dItem -and $rItem) {
		if ($dItem.LastWriteTime -ge $rItem.LastWriteTime) {
			return Split-Path $dItem
		} else {
			return Split-Path $rItem
		}
	}
	if ($dItem) { return Split-Path $dItem }
	if ($rItem) { return Split-Path $rItem }
	return $null
}

$releaseDir  = Get-NewestBinDir (Join-Path $repoRoot "WinNotifyBridge")          "WinNotifyBridge.exe"
$releaseTray = Get-NewestBinDir (Join-Path $repoRoot "WinNotifyBridge.Tray")     "WinNotifyBridge.Tray.exe"
$releaseList = Get-NewestBinDir (Join-Path $repoRoot "WinNotifyBridge.Listener") "WinNotifyBridge.Listener.exe"

Write-Host "Source dirs selected:" -ForegroundColor DarkCyan
Write-Host "  Service : $releaseDir"
Write-Host "  Tray    : $releaseTray"
Write-Host "  Listener: $releaseList"

$installRoot    = "C:\Program Files (x86)\WinNotifyBridge"
$installSvc     = Join-Path $installRoot "Service"
$installTray    = Join-Path $installRoot "Tray"
$installList    = Join-Path $installRoot "Listener"

$serviceName = "Service1"

Write-Host "=== WinNotifyBridge binary update ===" -ForegroundColor Cyan

# --- stop tray app if running -------------------------------------------------
Write-Host "Stopping tray app..." -ForegroundColor Yellow
Get-Process -Name "WinNotifyBridge.Tray" -ErrorAction SilentlyContinue | Stop-Process -Force

# --- stop listener if running -------------------------------------------------
Write-Host "Stopping listener..." -ForegroundColor Yellow
Get-Process -Name "WinNotifyBridge.Listener" -ErrorAction SilentlyContinue | Stop-Process -Force

# --- stop service -------------------------------------------------------------
Write-Host "Stopping service '$serviceName'..." -ForegroundColor Yellow
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne "Stopped") {
	Stop-Service -Name $serviceName -Force
	$svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
	Write-Host "  Service stopped." -ForegroundColor Green
} else {
	Write-Host "  Service already stopped." -ForegroundColor Gray
}

Start-Sleep -Seconds 1

# --- stop any process holding deployed SQLite native dependency ---------------
$installedSqliteInterop = Join-Path $installList "x64\SQLite.Interop.dll"
if (Test-Path $installedSqliteInterop) {
	Stop-ProcessesUsingPath -TargetPath $installedSqliteInterop
}

Start-Sleep -Seconds 1

# --- copy binaries ------------------------------------------------------------
Write-Host "Copying service files..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $installSvc -Force | Out-Null
Copy-Item (Join-Path $releaseDir "*") $installSvc -Recurse -Force

Write-Host "Copying tray files..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $installTray -Force | Out-Null
Copy-Item (Join-Path $releaseTray "*") $installTray -Recurse -Force

Write-Host "Copying listener files..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $installList -Force | Out-Null
Copy-Item (Join-Path $releaseList "*") $installList -Recurse -Force

Write-Host "All files copied." -ForegroundColor Green

# --- restart service ----------------------------------------------------------
Write-Host "Starting service '$serviceName'..." -ForegroundColor Yellow
Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(20))
Write-Host "  Service running." -ForegroundColor Green

# --- launch tray app ----------------------------------------------------------
Write-Host "Launching tray app..." -ForegroundColor Yellow
$trayExe = Join-Path $installTray "WinNotifyBridge.Tray.exe"
if (Test-Path $trayExe) {
	Start-Process -FilePath $trayExe
	Write-Host "  Tray app launched (it will start the listener automatically)." -ForegroundColor Green
} else {
	Write-Host "  Tray exe not found at: $trayExe" -ForegroundColor Red
}

Write-Host "`n=== Done. WinNotifyBridge is running with updated binaries. ===" -ForegroundColor Cyan
Write-Host "NOTE: The tray app now manages the Listener process." -ForegroundColor White
Write-Host "      Use the tray icon > Start/Restart service to also start the Listener." -ForegroundColor White
