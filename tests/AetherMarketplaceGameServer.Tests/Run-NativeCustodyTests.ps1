param([switch]$IncludeRegression, [switch]$NoBuild, [string]$Filter='FullyQualifiedName~OriginalCustodyEntityAndReceiptSurviveFreshContextAsync')
$ErrorActionPreference='Stop'
$name='aether-v29-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
$names=@('AETHER_V414_DISPOSABLE_DB','AETHER_MARKETPLACE_TEST_DB_PASSWORD','AETHER_MARKETPLACE_TEST_DB_PORT','AETHER_V414_TEST_DB_NAME','AETHER_V415_TRANSACTION_TEST','AETHER_V415_TEST_DB_NAME')
$previous=@{}
foreach($key in $names){$previous[$key]=[Environment]::GetEnvironmentVariable($key)}
try {
    $env:AETHER_MARKETPLACE_TEST_DB_PASSWORD=[Guid]::NewGuid().ToString('N')
    $env:AETHER_V414_DISPOSABLE_DB='1'
    $env:AETHER_V414_TEST_DB_NAME='aether_v29_native_disposable'
    docker run -d --rm --name $name -p 127.0.0.1::5432 -e POSTGRES_USER=aether_test -e POSTGRES_DB=aether_v29_native_disposable -e "POSTGRES_PASSWORD=$env:AETHER_MARKETPLACE_TEST_DB_PASSWORD" postgres:17
    if($LASTEXITCODE -ne 0){throw 'Container failed'}
    for($i=0;$i -lt 40;$i++) {
        docker exec $name pg_isready -h 127.0.0.1 -U aether_test -d aether_v29_native_disposable 2>$null | Out-Null
        if($LASTEXITCODE -eq 0){break}
        Start-Sleep -Milliseconds 500
    }
    $binding=docker port $name 5432/tcp
    if($binding -notmatch '^127\.0\.0\.1:(\d+)$'){throw 'Missing isolated port'}
    $env:AETHER_MARKETPLACE_TEST_DB_PORT=$Matches[1]
    if(-not $NoBuild) {
        dotnet build "$PSScriptRoot/AetherMarketplaceGameServer.Tests.csproj" -c Release -p:ci=true -m:1 -nr:false -p:UseSharedCompilation=false --nologo -v minimal
        if($LASTEXITCODE -ne 0){throw 'Build failed'}
    }
    dotnet test "$PSScriptRoot/AetherMarketplaceGameServer.Tests.csproj" -c Release --no-build --nologo --filter $Filter --logger 'trx;LogFileName=v29-native.trx'
    if($LASTEXITCODE -ne 0){throw 'Native integration failed'}
    if($IncludeRegression) {
        docker exec $name createdb -U aether_test aether_marketplace_test
        if($LASTEXITCODE -ne 0){throw 'Regression database creation failed'}
        $env:AETHER_V414_TEST_DB_NAME='aether_marketplace_test'
        dotnet test "$PSScriptRoot/AetherMarketplaceGameServer.Tests.csproj" -c Release --no-build --nologo --filter 'FullyQualifiedName~NativeIntegrationTests|FullyQualifiedName~PostgreSqlReservationTests' --logger 'trx;LogFileName=v29-native-regression.trx'
        if($LASTEXITCODE -ne 0){throw 'Native regressions failed'}
        foreach($migration in @('marketplace-transaction-v415.sql','marketplace-reserve-purchase-v415.sql')) {
            Get-Content -Raw -LiteralPath "$PSScriptRoot/../../src/AetherMarketplaceGameServer/$migration" |
                docker exec -i $name psql -X -v ON_ERROR_STOP=1 -U aether_test -d aether_marketplace_test
            if($LASTEXITCODE -ne 0){throw 'Reservation regression schema failed'}
        }
        $env:AETHER_V415_TRANSACTION_TEST='1'
        $env:AETHER_V415_TEST_DB_NAME='aether_marketplace_test'
        dotnet build "$PSScriptRoot/../AetherMarketplaceDelivery.Tests/AetherMarketplaceDelivery.Tests.csproj" -c Release -p:ci=true -m:1 -nr:false -p:UseSharedCompilation=false --nologo -v minimal
        if($LASTEXITCODE -ne 0){throw 'Delivery regression build failed'}
        dotnet test "$PSScriptRoot/../AetherMarketplaceDelivery.Tests/AetherMarketplaceDelivery.Tests.csproj" -c Release --no-build --nologo --logger 'trx;LogFileName=v29-delivery-regression.trx'
        if($LASTEXITCODE -ne 0){throw 'Delivery regressions failed'}
    }
} finally {
    docker rm -f $name
    foreach($key in $names){[Environment]::SetEnvironmentVariable($key,$previous[$key])}
}
