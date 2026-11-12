$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$containerName = 'aether-v414-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$previousEnvironment = @{}
foreach ($name in @('AETHER_MARKETPLACE_TEST_DB_PASSWORD', 'AETHER_V414_DISPOSABLE_DB', 'AETHER_MARKETPLACE_TEST_DB_PORT')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$env:AETHER_MARKETPLACE_TEST_DB_PASSWORD = [Guid]::NewGuid().ToString('N')
$env:AETHER_V414_DISPOSABLE_DB = '1'
$env:AETHER_MARKETPLACE_TEST_DB_PORT = '55439'
Push-Location -LiteralPath $repository
try {
    docker run --detach --rm --name $containerName --publish 127.0.0.1:55439:5432 --env POSTGRES_DB=aether_marketplace_test --env POSTGRES_USER=aether_test --env "POSTGRES_PASSWORD=$env:AETHER_MARKETPLACE_TEST_DB_PASSWORD" postgres:17
    if ($LASTEXITCODE -ne 0) { throw 'Disposable PostgreSQL start failed.' }
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        docker exec $containerName pg_isready -U aether_test -d aether_marketplace_test 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Disposable PostgreSQL did not become ready.' }
    dotnet build tests/AetherMarketplaceGameServer.Tests/AetherMarketplaceGameServer.Tests.csproj -c Release -p:ci=true --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
    dotnet build tests/AetherMarketplaceDelivery.Tests/AetherMarketplaceDelivery.Tests.csproj -c Release -p:ci=true --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Regression build failed.' }
    dotnet test tests/AetherMarketplaceGameServer.Tests/AetherMarketplaceGameServer.Tests.csproj -c Release --no-build --nologo --logger 'trx;LogFileName=native-integration.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Native integration tests failed.' }
    dotnet test tests/AetherMarketplaceDelivery.Tests/AetherMarketplaceDelivery.Tests.csproj -c Release --no-build --nologo --logger 'trx;LogFileName=delivery-regression.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Delivery regression tests failed.' }
} finally {
    docker rm --force $containerName
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    Pop-Location
}
