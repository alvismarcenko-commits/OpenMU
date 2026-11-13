$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$containerName = 'aether-v51028-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$password = [Guid]::NewGuid().ToString('N')
$previousConnection = $env:AETHER_V51028_TEST_CONNECTION
try {
    # No published ports, existing servers, production credentials, or player data.
    docker run --detach --rm --name $containerName --publish 127.0.0.1::5432 --env POSTGRES_DB=aether_v51028_disposable --env POSTGRES_USER=aether_test --env "POSTGRES_PASSWORD=$password" postgres:17
    if ($LASTEXITCODE -ne 0) { throw 'Disposable PostgreSQL start failed.' }
    $ready = $false
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        # TCP excludes the image's temporary socket-only initialization server.
        docker exec $containerName pg_isready -h 127.0.0.1 -U aether_test -d aether_v51028_disposable 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready=$true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Disposable PostgreSQL did not become ready.' }
    $scripts = @(
        'marketplace-transaction-v415.sql', 'marketplace-reserve-purchase-v415.sql',
        'marketplace-account-binding-fix3.sql', 'marketplace-identity-hardening-fix3.sql',
        'marketplace-settlement-ledger-fix3.sql', 'marketplace-settlement-integration-fix3.sql',
        'marketplace-ac-wallet-fix3.sql', 'marketplace-ac-atomic-settlement-v3-staging.sql',
        'marketplace-payment-proof-v1-staging.sql', 'marketplace-ac-lifecycle-v51028-staging.sql'
    )
    foreach ($script in $scripts) {
        Get-Content -Raw -LiteralPath (Join-Path $repository "src\AetherMarketplaceGameServer\$script") |
            docker exec -i $containerName psql -X -v ON_ERROR_STOP=1 -U aether_test -d aether_v51028_disposable
        if ($LASTEXITCODE -ne 0) { throw "Migration failed: $script" }
    }
    Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'ac-lifecycle-v51028.sql') |
        docker exec -i $containerName psql -X -v ON_ERROR_STOP=1 -U aether_test -d aether_v51028_disposable
    if ($LASTEXITCODE -ne 0) { throw 'AC lifecycle assertions failed.' }
    $binding = docker port $containerName 5432/tcp
    if ($LASTEXITCODE -ne 0 -or $binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'Cannot resolve disposable port.' }
    $env:AETHER_V51028_TEST_CONNECTION = "Host=127.0.0.1;Port=$($Matches[1]);Database=aether_v51028_disposable;Username=aether_test;Password=$password;Timeout=10;Command Timeout=15"
    dotnet build (Join-Path $PSScriptRoot 'AetherMarketplaceGameServer.Tests.csproj') -c Release -p:ci=true --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test (Join-Path $PSScriptRoot 'AetherMarketplaceGameServer.Tests.csproj') -c Release --no-build --nologo --filter 'FullyQualifiedName~AuthorizedAcSettlementIntegrationTests|FullyQualifiedName~GameServerPurchaseAuthorizationTests|FullyQualifiedName~MarketplacePurchaseAuthorizationTests' --logger 'trx;LogFileName=ac-lifecycle-v51028.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Server authorization/settlement tests failed.' }
} finally {
    docker rm --force $containerName
    $env:AETHER_V51028_TEST_CONNECTION = $previousConnection
}
