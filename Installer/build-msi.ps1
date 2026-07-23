param(
	[string]$Configuration = "Release",
	[string]$Platform = "Any CPU",
	[string]$OutputName = "WinNotifyBridge-v1.1.0.msi",
	[string]$WixBinPath = ""
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$solutionPath = Join-Path $repoRoot "WinNotifyBridge.sln"

function Stop-WorkspaceProcess
{
	param(
		[string]$exeName,
		[string]$workspacePath
	)

	$normalizedWorkspacePath = [System.IO.Path]::GetFullPath($workspacePath)
	$processes = Get-CimInstance Win32_Process -Filter "Name = '$exeName'" -ErrorAction SilentlyContinue
	if (-not $processes)
	{
		return
	}

	foreach ($processInfo in $processes)
	{
		$processPath = $processInfo.ExecutablePath
		if ([string]::IsNullOrWhiteSpace($processPath))
		{
			continue
		}

		$normalizedProcessPath = [System.IO.Path]::GetFullPath($processPath)
		if ($normalizedProcessPath.StartsWith($normalizedWorkspacePath, [System.StringComparison]::OrdinalIgnoreCase))
		{
			try
			{
				Stop-Process -Id $processInfo.ProcessId -Force -ErrorAction Stop
				Write-Host "Stopped running workspace process: $exeName (PID $($processInfo.ProcessId))"
			}
			catch
			{
				Write-Warning "Could not stop process $exeName (PID $($processInfo.ProcessId)): $($_.Exception.Message)"
			}
		}
	}
}

function Stop-ServiceIfExists
{
	param([string]$serviceName)

	$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
	if (-not $service)
	{
		return
	}

	if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped -or
		$service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::StopPending)
	{
		return
	}

	try
	{
		Write-Host "Stopping service '$serviceName' to release locked binaries..."
		Stop-Service -Name $serviceName -Force -ErrorAction Stop
		$service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
	}
	catch
	{
		Write-Warning "Could not stop service '$serviceName': $($_.Exception.Message)"
	}
}

function Stop-ProcessByImageName
{
	param([string]$imageName)

	try
	{
		$null = taskkill /F /IM $imageName 2>$null
	}
	catch
	{
		# Ignore: process may not exist.
	}
}

if (-not (Test-Path $solutionPath))
{
	throw "Solution file not found at $solutionPath"
}

$candleExe = $null
$lightExe = $null

$wixCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($WixBinPath))
{
	$wixCandidates += $WixBinPath
}

if ($env:WIX)
{
	$wixCandidates += $env:WIX
}

$wixCandidates += "${env:ProgramFiles(x86)}\WiX Toolset v3.11\bin"
$wixCandidates += "$env:ProgramFiles\WiX Toolset v3.11\bin"
$wixCandidates += "${env:ProgramFiles(x86)}\WiX Toolset v3.14\bin"
$wixCandidates += "$env:ProgramFiles\WiX Toolset v3.14\bin"

$wixCandidates = $wixCandidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

foreach ($candidate in $wixCandidates)
{
	$candidateCandle = Join-Path $candidate "candle.exe"
	$candidateLight = Join-Path $candidate "light.exe"
	if ((Test-Path $candidateCandle) -and (Test-Path $candidateLight))
	{
		$WixBinPath = $candidate
		$candleExe = $candidateCandle
		$lightExe = $candidateLight
		break
	}
}

if (-not $candleExe -or -not $lightExe)
{
	$candleCommand = Get-Command "candle.exe" -ErrorAction SilentlyContinue
	$lightCommand = Get-Command "light.exe" -ErrorAction SilentlyContinue
	if ($candleCommand -and $lightCommand)
	{
		$candleExe = $candleCommand.Source
		$lightExe = $lightCommand.Source
		$WixBinPath = Split-Path -Parent $candleExe
	}
}

if (-not $candleExe -or -not $lightExe)
{
	$searched = ($wixCandidates -join ", ")
	throw "WiX v3 binaries not found. Install WiX Toolset v3 (candle.exe/light.exe) or pass -WixBinPath. Searched: $searched"
}

Write-Host "Using WiX binaries from: $WixBinPath"

Stop-ServiceIfExists -serviceName "Service1"
Stop-WorkspaceProcess -exeName "WinNotifyBridge.exe" -workspacePath $repoRoot
Stop-WorkspaceProcess -exeName "WinNotifyBridge.Tray.exe" -workspacePath $repoRoot
Stop-WorkspaceProcess -exeName "WinNotifyBridge.Listener.exe" -workspacePath $repoRoot
Stop-ProcessByImageName -imageName "WinNotifyBridge.exe"
Stop-ProcessByImageName -imageName "WinNotifyBridge.Tray.exe"
Stop-ProcessByImageName -imageName "WinNotifyBridge.Listener.exe"

Write-Host "Building solution ($Configuration|$Platform)..."
& msbuild $solutionPath /t:Build /p:Configuration=$Configuration "/p:Platform=$Platform" /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0)
{
	throw "Solution build failed."
}

$serviceBinDir = Join-Path $repoRoot "WinNotifyBridge\bin\$Configuration"
$trayBinDir = Join-Path $repoRoot "WinNotifyBridge.Tray\bin\$Configuration"
$listenerBinDir = Join-Path $repoRoot "WinNotifyBridge.Listener\bin\$Configuration"

foreach ($binDir in @($serviceBinDir, $trayBinDir, $listenerBinDir))
{
	if (-not (Test-Path $binDir))
	{
		throw "Build output directory not found: $binDir"
	}
}

$productWxsPath = Join-Path $scriptRoot "Product.wxs"
if (-not (Test-Path $productWxsPath))
{
	throw "Product.wxs not found at $productWxsPath"
}

$objDir = Join-Path $scriptRoot "obj"
$outDir = Join-Path $scriptRoot "bin"
New-Item -ItemType Directory -Path $objDir -Force | Out-Null
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$wixObjPath = Join-Path $objDir "Product.wixobj"
$msiPath = Join-Path $outDir $OutputName

Write-Host "Compiling WiX sources..."
$candleArgs = @(
	"-dServiceBinDir=$serviceBinDir",
	"-dTrayBinDir=$trayBinDir",
	"-dListenerBinDir=$listenerBinDir",
	"-out", $wixObjPath,
	$productWxsPath
)
& $candleExe @candleArgs
if ($LASTEXITCODE -ne 0)
{
	throw "candle.exe failed."
}

Write-Host "Linking MSI..."
$lightArgs = @(
	"-ext", "WixUIExtension",
	"-ext", "WixUtilExtension",
	"-out", $msiPath,
	$wixObjPath
)
& $lightExe @lightArgs
if ($LASTEXITCODE -ne 0)
{
	throw "light.exe failed."
}

Write-Host "MSI created at: $msiPath"
