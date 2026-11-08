# Requires PowerShell 5.1+, .NET SDK 10 and Docker Desktop (Linux containers).
[CmdletBinding()]
param([string]$SourceRoot = (Join-Path $PSScriptRoot '..\..\..'), [string]$Image = 'postgres:16.15')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$SourceRoot = (Resolve-Path $SourceRoot).Path
$token = [Guid]::NewGuid().ToString('N')
$database = 'aether_gr_test_' + $token
$name = 'aether-gr-' + $token
$run = Join-Path $SourceRoot ('runs\gr-' + $token)
New-Item -ItemType Directory -Path $run -Force | Out-Null
$envFile = Join-Path $run 'container.env'
$container = $null
$previousConnection = [Environment]::GetEnvironmentVariable('AETHER_GR_TEST_CONNECTION')
$previousToken = [Environment]::GetEnvironmentVariable('AETHER_GR_TEST_TOKEN')
$previousPhase = [Environment]::GetEnvironmentVariable('AETHER_GR_RESTART_PHASE')
$previousManifest = [Environment]::GetEnvironmentVariable('AETHER_GR_RESTART_MANIFEST')
function Get-OwnedTestContainerLabel {
    $raw = & docker inspect $container
    if ($LASTEXITCODE -ne 0) { throw 'Container inspection failed.' }
    $objects = @(($raw -join "`n") | ConvertFrom-Json)
    if ($objects.Count -ne 1 -or $objects[0].Name -cne ('/' + $name)) { throw 'Container identity mismatch.' }
    return $objects[0].Config.Labels.'aether.gr.test'
}
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed (exit $LASTEXITCODE)." }
}
try {
    if ($database -notmatch '^aether_gr_test_[a-f0-9]{32}$') { throw 'Unsafe database name.' }
    Invoke-Checked 'docker' @('info', '--format', '{{.OSType}}')
    $password = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
    @("POSTGRES_USER=aether_test", "POSTGRES_PASSWORD=$password", "POSTGRES_DB=$database") | Set-Content -Encoding ASCII $envFile
    $output = & docker run --detach --name $name --label "aether.gr.test=$token" --publish '127.0.0.1::5432' --mount 'type=volume,target=/var/lib/postgresql/data' --env-file $envFile $Image
    if ($LASTEXITCODE -ne 0) { throw 'Fresh PostgreSQL container creation failed.' }
    $container = ($output | Select-Object -Last 1).Trim()
    if ($container -notmatch '^[a-f0-9]{64}$') { throw 'Unexpected container identity.' }
    Remove-Item $envFile -Force
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        & docker exec $container pg_isready -h 127.0.0.1 -U aether_test -d $database *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'PostgreSQL readiness timeout.' }
    $actual = & docker exec $container psql -X -U aether_test -d $database -Atc 'SELECT current_database()'
    if ($LASTEXITCODE -ne 0 -or ($actual -join '').Trim() -cne $database) { throw 'Database identity mismatch before write.' }
    Invoke-Checked 'docker' @('exec', $container, 'psql', '-X', '-v', 'ON_ERROR_STOP=1', '-U', 'aether_test', '-d', $database, '-c', "COMMENT ON DATABASE $database IS 'aether-gr-disposable:$token'")
    $binding = (& docker port $container '5432/tcp' | Select-Object -First 1).Trim()
    if ($LASTEXITCODE -ne 0 -or $binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'Expected loopback-only ephemeral port.' }
    $port = $Matches[1]
    $env:AETHER_GR_TEST_CONNECTION = "Host=127.0.0.1;Port=$port;Database=$database;Username=aether_test;Password=$password;Include Error Detail=false;Timeout=15;Command Timeout=60"
    $env:AETHER_GR_TEST_TOKEN = $token
    [Environment]::SetEnvironmentVariable('AETHER_GR_RESTART_PHASE', $null)
    [Environment]::SetEnvironmentVariable('AETHER_GR_RESTART_MANIFEST', $null)
    $project = Join-Path $SourceRoot 'tests\MUnique.OpenMU.Tests\MUnique.OpenMU.Tests.csproj'
    Invoke-Checked 'dotnet' @('build', $project, '-c', 'Release', '-p:ci=true', '-p:GrandResetOnly=true', '-p:UseSharedCompilation=false', '-m:1')
    Invoke-Checked 'dotnet' @('test', $project, '-c', 'Release', '--no-build', '-p:GrandResetOnly=true', '--filter', 'FullyQualifiedName~GrandReset&Name!~RestartDurability', '--logger', 'trx;LogFileName=grand-reset.trx', '--results-directory', $run)
    [xml]$trx = Get-Content (Join-Path $run 'grand-reset.trx') -Raw
    $results = @($trx.TestRun.Results.UnitTestResult)
    $pg = @($results | Where-Object { $_.testName -match 'ExchangeSurvivesIndependentContextAsync|ConcurrentContextsCommitOnceAsync|RejectedSaveRollsBackAllProgressAsync|LostCommitAcknowledgementRequiresReloadAsync' })
    if ($pg.Count -ne 7 -or @($pg | Where-Object { $_.outcome -ne 'Passed' }).Count -gt 0) { throw 'All 7 real PostgreSQL cases must PASS; missing/skipped tests are not success.' }
    # The anonymous volume is new for this container and survives restart. Never mount existing data.
    $env:AETHER_GR_RESTART_MANIFEST = Join-Path $run 'restart-manifest.json'
    $env:AETHER_GR_RESTART_PHASE = 'write'
    Invoke-Checked 'dotnet' @('test', $project, '-c', 'Release', '--no-build', '-p:GrandResetOnly=true', '--filter', 'Name=RestartDurabilityAsync', '--logger', 'trx;LogFileName=restart-write.trx', '--results-directory', $run)
    $before = & docker exec $container psql -X -U aether_test -d $database -Atc 'SELECT pg_postmaster_start_time()'
    if ($LASTEXITCODE -ne 0) { throw 'Could not read pre-restart PostgreSQL identity.' }
    $label = Get-OwnedTestContainerLabel
    if ($LASTEXITCODE -ne 0 -or ($label -join '').Trim() -cne $token) { throw 'Container ownership mismatch before restart.' }
    Invoke-Checked 'docker' @('restart', '--time', '10', $container)
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        & docker exec $container pg_isready -h 127.0.0.1 -U aether_test -d $database *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'PostgreSQL restart readiness timeout.' }
    $after = & docker exec $container psql -X -U aether_test -d $database -Atc 'SELECT pg_postmaster_start_time()'
    if ($LASTEXITCODE -ne 0 -or ($before -join '').Trim() -ceq ($after -join '').Trim()) { throw 'PostgreSQL process restart was not proven.' }
    $binding = (& docker port $container '5432/tcp' | Select-Object -First 1).Trim()
    if ($LASTEXITCODE -ne 0 -or $binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'Expected loopback-only port after restart.' }
    $port = $Matches[1]
    $env:AETHER_GR_TEST_CONNECTION = "Host=127.0.0.1;Port=$port;Database=$database;Username=aether_test;Password=$password;Include Error Detail=false;Timeout=15;Command Timeout=60"
    $env:AETHER_GR_RESTART_PHASE = 'verify'
    Invoke-Checked 'dotnet' @('test', $project, '-c', 'Release', '--no-build', '-p:GrandResetOnly=true', '--filter', 'Name=RestartDurabilityAsync', '--logger', 'trx;LogFileName=restart-verify.trx', '--results-directory', $run)
    foreach ($phase in @('write', 'verify')) {
        [xml]$phaseTrx = Get-Content (Join-Path $run "restart-$phase.trx") -Raw
        $phaseResults = @($phaseTrx.TestRun.Results.UnitTestResult)
        if ($phaseResults.Count -ne 1 -or $phaseResults[0].outcome -ne 'Passed') { throw "Restart $phase phase must PASS without skipping." }
    }
    @{ Before = ($before -join '').Trim(); After = ($after -join '').Trim(); ContainerId = $container } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $run 'restart-proof.json')
    Write-Host "PASS: 7/7 PostgreSQL cases and physical restart write/verify. Results: $run"

}
finally {
    [Environment]::SetEnvironmentVariable('AETHER_GR_TEST_CONNECTION', $previousConnection)
    [Environment]::SetEnvironmentVariable('AETHER_GR_TEST_TOKEN', $previousToken)
    [Environment]::SetEnvironmentVariable('AETHER_GR_RESTART_PHASE', $previousPhase)
    [Environment]::SetEnvironmentVariable('AETHER_GR_RESTART_MANIFEST', $previousManifest)
    if (Test-Path $envFile) { Remove-Item $envFile -Force }
    if ($container -and $container -match '^[a-f0-9]{64}$') {
        $label = Get-OwnedTestContainerLabel
        if ($LASTEXITCODE -eq 0 -and ($label -join '').Trim() -ceq $token) {
            & docker rm --force --volumes $container | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Warning "Could not remove test container $container" }
        } else { Write-Warning 'Container ownership mismatch: cleanup refused.' }
    }
}
