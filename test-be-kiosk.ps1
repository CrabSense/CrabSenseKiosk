param(
    [Parameter(Mandatory = $true)]
    [string]$BeUrl
)

$ErrorActionPreference = "Stop"
$be = $BeUrl.TrimEnd("/")
$fail = 0

function Show-Result($name, $ok, $detail) {
    if ($ok) { Write-Host "OK  $name  $detail" }
    else { Write-Host "FAIL  $name  $detail" }
}

try {
    $kiosk = Invoke-WebRequest -Uri "http://127.0.0.1:8090/api/health" -UseBasicParsing -TimeoutSec 8
    Show-Result "Kiosk" ($kiosk.StatusCode -eq 200) $kiosk.StatusCode
    if ($kiosk.StatusCode -ne 200) { $fail = 1 }
} catch {
    Show-Result "Kiosk" $false $_.Exception.Message
    $fail = 1
}

try {
    $health = Invoke-WebRequest -Uri "$be/health" -UseBasicParsing -TimeoutSec 8
    Show-Result "BE" ($health.StatusCode -eq 200) $health.StatusCode
    if ($health.StatusCode -ne 200) { $fail = 1 }
} catch {
    Show-Result "BE" $false $_.Exception.Message
    $fail = 1
}

$cfg = Get-Content -Raw ".\appsettings.json" | ConvertFrom-Json
$key = [string]$cfg.EdgeGateway.ApiKey
try {
    $pending = Invoke-WebRequest -Uri "$be/api/edge/commands/pending?deviceCode=CrabSense-C115" -Headers @{ "X-Edge-Key" = $key } -UseBasicParsing -TimeoutSec 8
    Show-Result "Kiosk->BE pending" ($pending.StatusCode -eq 200) $pending.Content
    if ($pending.StatusCode -ne 200) { $fail = 1 }
} catch {
    $detail = $_.Exception.Message
    $resp = $_.Exception.Response
    if ($resp) {
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $body = $reader.ReadToEnd()
        if ($body) { $detail = "$($resp.StatusCode) $body" }
    }
    Show-Result "Kiosk->BE pending" $false $detail
    $fail = 1
}

try {
    $snap = Invoke-WebRequest -Uri "http://127.0.0.1:8090/api/telemetry/snapshots" -UseBasicParsing -TimeoutSec 8
    Show-Result "ESP->Kiosk snapshot" ($snap.StatusCode -eq 200) $snap.Content
    if ($snap.StatusCode -ne 200) { $fail = 1 }
} catch {
    Show-Result "ESP->Kiosk snapshot" $false $_.Exception.Message
    $fail = 1
}

if ($fail -ne 0) { exit 1 }
