param(
    [Parameter(Mandatory = $true)]
    [string]$BeUrl,
    [Parameter(Mandatory = $true)]
    [string]$Code
)

$ErrorActionPreference = "Stop"
$be = $BeUrl.Trim().TrimEnd("/")
$lan = (
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.PrefixOrigin -ne "WellKnown" } |
    Select-Object -First 1 -ExpandProperty IPAddress
)

function Step($name, $ok, $detail) {
    if ($ok) { Write-Host "OK   $name  $detail" }
    else { Write-Host "FAIL $name  $detail" }
}

Step "BE" $true $be

try {
    $health = Invoke-WebRequest -Uri "$be/health" -UseBasicParsing -TimeoutSec 15
    Step "VPS" ($health.StatusCode -eq 200) $health.StatusCode
    if ($health.StatusCode -ne 200) { exit 1 }
} catch {
    Step "VPS" $false $_.Exception.Message
    exit 1
}

$body = @{ code = $Code.Trim(); lanIp = $lan } | ConvertTo-Json
try {
    $redeem = Invoke-WebRequest -Uri "$be/api/provision/kiosk" -Method POST -ContentType "application/json" -Body $body -UseBasicParsing -TimeoutSec 20
    $payload = $redeem.Content | ConvertFrom-Json
    $data = $payload.data
    if (-not $data.secret) { throw "Provisioning rejected" }
    Set-Content -Path ".\kiosk-secret.txt" -Value $data.secret -NoNewline
    @(
        "kiosk=$($data.kioskCode)"
        "site=$($data.siteCode)"
        "id=$($data.kioskId)"
    ) | Set-Content -Path ".\kiosk-identity.txt"
    Step "Provisioning" $true "$($data.kioskCode) site $($data.siteCode)"
} catch {
    $detail = $_.Exception.Message
    $resp = $_.Exception.Response
    if ($resp) {
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $text = $reader.ReadToEnd()
        if ($text) { $detail = $text }
    }
    Step "Provisioning" $false $detail
    exit 1
}

$secret = Get-Content -Raw ".\kiosk-secret.txt"
try {
    $me = Invoke-WebRequest -Uri "$be/api/kiosks/me" -Headers @{ "X-Kiosk-Key" = $secret.Trim() } -UseBasicParsing -TimeoutSec 15
    $session = ($me.Content | ConvertFrom-Json).data
    Step "Authentication" ($me.StatusCode -eq 200) "$($session.kioskCode) $($session.status)"
} catch {
    Step "Authentication" $false $_.Exception.Message
    exit 1
}

try {
    $local = Invoke-WebRequest -Uri "http://127.0.0.1:8090/api/health" -UseBasicParsing -TimeoutSec 5
    Step "Kiosk API" ($local.StatusCode -eq 200) "8090"
} catch {
    Step "Kiosk API" $false "Chua chay. Mo start-kiosk.bat"
}

Write-Host "KIOSK READY"
Get-Content ".\kiosk-identity.txt"
