param([switch]$IncludeServices, [switch]$BuildImage)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    $run = Join-Path $root ('evidence/runs/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $run | Out-Null
    node scripts/check-contracts.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Owner contract/catalogue drift detected.' }
    dotnet restore Vairas.Gateway.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Restore or NuGet audit failed.' }
    dotnet build Vairas.Gateway.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test Vairas.Gateway.slnx -c Release --no-build --no-restore --settings coverage.runsettings --collect:'XPlat Code Coverage' --logger 'trx;LogFileName=gateway.trx' --results-directory (Join-Path $run 'gateway')
    if ($LASTEXITCODE -ne 0) { throw 'Gateway tests failed.' }
    $file = Get-ChildItem (Join-Path $run 'gateway') -Filter coverage.cobertura.xml -Recurse | Select-Object -First 1
    if (-not $file) { throw 'Fresh coverage report missing.' }
    [xml]$report = Get-Content -LiteralPath $file.FullName
    $percent = [Math]::Round(100 * [double]$report.coverage.'line-rate', 2)
    if ($percent -lt 70) { throw "Gateway coverage below 70%: $percent" }
    $services = 'NOT RUN'; $image = 'NOT RUN'
    if ($IncludeServices) {
        dotnet restore Vairas.Gateway.ServiceTests/Vairas.Gateway.ServiceTests.csproj
        if ($LASTEXITCODE -ne 0) { throw 'Service test restore failed.' }
        dotnet test Vairas.Gateway.ServiceTests/Vairas.Gateway.ServiceTests.csproj -c Release --no-restore --logger 'trx;LogFileName=services.trx' --results-directory (Join-Path $run 'services')
        if ($LASTEXITCODE -ne 0) { throw 'Real owner service smoke test failed.' }
        $services = 'PASSED'
    }
    if ($BuildImage) {
        docker build -t vairas-gateway:local .
        if ($LASTEXITCODE -ne 0) { throw 'Gateway image build failed.' }
        & (Join-Path $PSScriptRoot 'verify-image.ps1')
        $image = 'PASSED'
    }
    $audit = dotnet list Vairas.Gateway.slnx package --vulnerable --include-transitive --format json
    if ($LASTEXITCODE -ne 0) { throw 'NuGet vulnerability query failed.' }
    $audit | Set-Content -Encoding UTF8 (Join-Path $run 'dependency-audit.json')
    $data = ($audit -join "`n") | ConvertFrom-Json
    foreach ($project in $data.projects) { foreach ($framework in $project.frameworks) { foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) { if ($package.vulnerabilities) { throw 'Vulnerable dependency detected.' } } } }
    @{ verifiedAt = (Get-Date).ToUniversalTime().ToString('o'); gatewayLineCoverage = $percent; services = $services; image = $image; distributedRuntime = 'NOT RUN' } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $run 'summary.json')
    node scripts/summarize.cjs $run
    if ($LASTEXITCODE -ne 0) { throw 'Could not summarize fresh evidence.' }
    node scripts/check-links.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Broken documentation link.' }
    Write-Output "Gateway verification passed: $run"
} finally { Pop-Location }
