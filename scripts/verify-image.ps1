$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$name = 'vairas-gateway-check-' + [Guid]::NewGuid().ToString('N')
$container = ''
try {
    $container = (docker run --rm -d --name $name -p '127.0.0.1::8080' vairas-gateway:local).Trim()
    if ($LASTEXITCODE -ne 0 -or $container -notmatch '^[a-f0-9]{64}$') { throw 'Temporary Gateway container did not start.' }
    $endpoint = (docker port $container 8080/tcp).Trim()
    if ($LASTEXITCODE -ne 0 -or $endpoint -notmatch '^127\.0\.0\.1:\d+$') { throw 'No loopback container port.' }
    $base = 'http://' + $endpoint
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri ($base + '/health/ready') -Headers @{ Host = 'localhost' } -TimeoutSec 2
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { throw 'Container did not become ready.' }
    $user = (docker inspect --format '{{.Config.User}}' $container).Trim()
    if ($user -in @('', '0', 'root')) { throw 'Container must use the nonroot runtime user.' }
    try {
        Invoke-WebRequest -UseBasicParsing -Uri ($base + '/api/v1/users/me') -Headers @{ Host = 'localhost'; 'X-Forwarded-Proto' = 'https' } -TimeoutSec 3 | Out-Null
        throw 'Untrusted cleartext request was unexpectedly accepted.'
    } catch {
        if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 403) { throw }
    }
    $imageId = (docker inspect --format '{{.Image}}' $container).Trim()
    $evidence = @{ checkedAt = (Get-Date).ToUniversalTime().ToString('o'); image = 'vairas-gateway:local'; imageId = $imageId; nonRootUser = $user; readiness = 200; untrustedCleartextApi = 403; upstreamConnectivity = 'NOT RUN' }
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'evidence') | Out-Null
    $evidence | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $root 'evidence/image-smoke.json')
    $evidence | ConvertTo-Json
} finally {
    if ($container -match '^[a-f0-9]{64}$') { docker rm -f $container | Out-Null }
}
