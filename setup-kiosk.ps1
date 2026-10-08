param(
    [string]$BeUrl,
    [string]$Code,
    [switch]$Status
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

if ($Status) {
    $dir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $be = ""
    $urlFile = Join-Path $dir "be-url.txt"
    $secretFile = Join-Path $dir "kiosk-secret.txt"
    if (Test-Path $urlFile) { $be = (Get-Content -Raw $urlFile).Trim() }
    if ($be -notmatch '^https?://') {
        Write-Host "Backend: not configured"
        Write-Host "Backend status: not checked"
        Write-Host "Kiosk access: not set up"
        exit 2
    }
    Write-Host "Backend: $be"
    $alive = $false
    try {
        $health = Invoke-WebRequest -Uri "$($be.TrimEnd('/'))/health" -UseBasicParsing -TimeoutSec 15
        $alive = $health.StatusCode -eq 200
    } catch {
        $alive = $false
    }
    if (-not $alive) {
        Write-Host "Backend status: unreachable"
        Write-Host "Kiosk access: could not be verified"
        exit 1
    }
    Write-Host "Backend status: reachable"
    if (-not (Test-Path $secretFile)) {
        Write-Host "Kiosk access: not set up"
        exit 2
    }
    try {
        $secret = (Get-Content -Raw $secretFile).Trim()
        $me = Invoke-WebRequest -Uri "$($be.TrimEnd('/'))/api/kiosks/me" -Headers @{ "X-Kiosk-Key" = $secret } -UseBasicParsing -TimeoutSec 15
        $session = ($me.Content | ConvertFrom-Json).data
        Write-Host "Kiosk access: $($session.kioskCode) · $($session.status)"
    } catch {
        Write-Host "Kiosk access: revoked. Enter a new provisioning code."
        exit 2
    }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($BeUrl) -or [string]::IsNullOrWhiteSpace($Code)) {
    Write-Host "A backend URL and a provisioning code are required."
    exit 1
}
$be = $BeUrl.Trim().TrimEnd("/")
$lan = (
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.PrefixOrigin -ne "WellKnown" } |
    Select-Object -First 1 -ExpandProperty IPAddress
)

function Step($name, $ok, $detail) {
    $state = if ($ok) { "Succeeded" } else { "Failed" }
    Write-Host "$name`: $state. $detail"
}

Step "Backend" $true $be

try {
    $health = Invoke-WebRequest -Uri "$be/health" -UseBasicParsing -TimeoutSec 15
    Step "Connection" ($health.StatusCode -eq 200) "The server responded."
    if ($health.StatusCode -ne 200) { exit 1 }
} catch {
    Step "Connection" $false "The server could not be reached."
    exit 1
}

$body = @{ code = $Code.Trim(); lanIp = $lan } | ConvertTo-Json
try {
    $redeem = Invoke-WebRequest -Uri "$be/api/provision/kiosk" -Method POST -ContentType "application/json" -Body $body -UseBasicParsing -TimeoutSec 20
    $payload = $redeem.Content | ConvertFrom-Json
    $data = $payload.data
    if (-not $data.secret) { throw "The server rejected the provisioning code." }
    Set-Content -Path ".\kiosk-secret.txt" -Value $data.secret -NoNewline
    @(
        "kiosk=$($data.kioskCode)"
        "site=$($data.siteCode)"
        "id=$($data.kioskId)"
    ) | Set-Content -Path ".\kiosk-identity.txt"
    Step "Provisioning" $true "$($data.kioskCode) · site $($data.siteCode)"
} catch {
    Step "Provisioning" $false "The code is invalid, already used, or expired."
    exit 1
}

$secret = Get-Content -Raw ".\kiosk-secret.txt"
try {
    $me = Invoke-WebRequest -Uri "$be/api/kiosks/me" -Headers @{ "X-Kiosk-Key" = $secret.Trim() } -UseBasicParsing -TimeoutSec 15
    $session = ($me.Content | ConvertFrom-Json).data
    Step "Authentication" ($me.StatusCode -eq 200) "$($session.kioskCode) · $($session.status)"
} catch {
    Step "Authentication" $false "The server rejected the kiosk credential."
    exit 1
}

try {
    $local = Invoke-WebRequest -Uri "http://127.0.0.1:8090/api/health" -UseBasicParsing -TimeoutSec 5
    Step "Kiosk service" ($local.StatusCode -eq 200) "Port 8090 is open."
} catch {
    Step "Kiosk service" $false "Not running yet. The Kiosk will start next."
}

Write-Host "Kiosk setup complete."
Write-Host "Kiosk: $($session.kioskCode)"
Write-Host "Site: $($session.siteCode)"
