param(
	[string]$Endpoint = "http://127.0.0.1:45877/notify/",
	[string]$App = "TestApp",
	[string]$Title = "Test notification",
	[string]$Body = "This is a bridge test",
	[int]$Count = 1,
	[int]$DelaySeconds = 1
)

if ($Count -lt 1)
{
	Write-Error "Count must be >= 1."
	exit 1
}

if ($DelaySeconds -lt 0)
{
	Write-Error "DelaySeconds must be >= 0."
	exit 1
}

for ($i = 1; $i -le $Count; $i++)
{
	try
	{
		$payload = @{
			app = $App
			title = if ($Count -gt 1) { "$Title #$i" } else { $Title }
			body = if ($Count -gt 1) { "$Body #$i" } else { $Body }
		}

		$response = Invoke-WebRequest -Uri $Endpoint -Method Post -Body $payload -ContentType "application/x-www-form-urlencoded" -UseBasicParsing
		Write-Host "[$i/$Count] Sent. HTTP $($response.StatusCode)"
	}
	catch
	{
		Write-Error "[$i/$Count] Failed to send test notification: $($_.Exception.Message)"
		exit 1
	}

	if ($i -lt $Count -and $DelaySeconds -gt 0)
	{
		Start-Sleep -Seconds $DelaySeconds
	}
}

Write-Host "Done."
