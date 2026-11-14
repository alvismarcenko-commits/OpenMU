param([Parameter(Mandatory=$true)][string]$EvidenceDirectory, [switch]$NoBuild)
$ErrorActionPreference='Stop'
$name='aether-v30-crash-'+[Guid]::NewGuid().ToString('N').Substring(0,10)
$keys=@('AETHER_V414_DISPOSABLE_DB','AETHER_MARKETPLACE_TEST_DB_PASSWORD','AETHER_MARKETPLACE_TEST_DB_PORT','AETHER_V414_TEST_DB_NAME','AETHER_V30_PROCESS_PROBE','AETHER_V30_CRASH_STAGE','AETHER_V30_PROBE_DIRECTORY')
$previous=@{}
foreach($key in $keys){$previous[$key]=[Environment]::GetEnvironmentVariable($key)}
$project=Join-Path $PSScriptRoot 'AetherMarketplaceGameServer.Tests.csproj'
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
$evidence=(Resolve-Path -LiteralPath $EvidenceDirectory).Path
try {
    if(-not $NoBuild){
        dotnet build $project -c Release -p:ci=true -m:1 -nr:false -p:UseSharedCompilation=false --nologo -v minimal
        if($LASTEXITCODE -ne 0){throw 'Build failed'}
    }
    $env:AETHER_MARKETPLACE_TEST_DB_PASSWORD=[Guid]::NewGuid().ToString('N')
    $env:AETHER_V414_DISPOSABLE_DB='1'
    $env:AETHER_V414_TEST_DB_NAME='aether_v29_native_disposable'
    $env:AETHER_V30_PROCESS_PROBE='1'
    docker run -d --rm --name $name -p 127.0.0.1::5432 -e POSTGRES_USER=aether_test -e POSTGRES_DB=aether_v29_native_disposable -e "POSTGRES_PASSWORD=$env:AETHER_MARKETPLACE_TEST_DB_PASSWORD" postgres:17
    if($LASTEXITCODE -ne 0){throw 'Isolated container failed'}
    for($i=0;$i -lt 40;$i++){
        docker exec $name pg_isready -h 127.0.0.1 -U aether_test -d aether_v29_native_disposable 2>$null | Out-Null
        if($LASTEXITCODE -eq 0){break}
        Start-Sleep -Milliseconds 500
    }
    $binding=docker port $name 5432/tcp
    if($binding -notmatch '^127\.0\.0\.1:(\d+)$'){throw 'Expected private test port'}
    $env:AETHER_MARKETPLACE_TEST_DB_PORT=$Matches[1]
    foreach($stage in @('before-custody','after-custody','before-payment','after-payment','before-delivery','during-persistence','after-persistence','during-recovery')){
        $folder=Join-Path $evidence ($stage+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
        New-Item -ItemType Directory -Path $folder | Out-Null
        $env:AETHER_V30_PROBE_DIRECTORY=$folder
        $env:AETHER_V30_CRASH_STAGE=$stage
        $worker=Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @('test',$project,'-c','Release','--no-build','--nologo','--filter','FullyQualifiedName~ProcessCrashWorkerAsync') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $folder 'worker.log') -RedirectStandardError (Join-Path $folder 'worker-errors.log')
        $readyPath=Join-Path $folder 'ready.json'
        try {
            $deadline=[DateTime]::UtcNow.AddSeconds(150)
            while(-not(Test-Path -LiteralPath $readyPath)){
                if($worker.HasExited){throw "Worker exited before $stage; inspect $folder"}
                if([DateTime]::UtcNow -gt $deadline){throw "Checkpoint timeout: $stage"}
                Start-Sleep -Milliseconds 200
            }
            $ready=Get-Content -Raw -LiteralPath $readyPath | ConvertFrom-Json
            if($ready.stage -ne $stage){throw 'Wrong checkpoint'}
            # Prove this testhost belongs to the process started above before terminating it.
            $target=Get-CimInstance Win32_Process -Filter "ProcessId=$($ready.pid)"
            if($target.CommandLine -notmatch 'testhost'){throw 'Unexpected target; refusing termination'}
            $ancestor=$target
            $owned=$false
            for($depth=0;$depth -lt 6;$depth++){
                if($ancestor.ParentProcessId -eq $worker.Id){$owned=$true;break}
                $ancestor=Get-CimInstance Win32_Process -Filter "ProcessId=$($ancestor.ParentProcessId)"
                if($null -eq $ancestor){break}
            }
            if(-not $owned){throw 'Target is not a child of this isolated worker'}
            Stop-Process -Id $ready.pid -Force
            if(-not $worker.WaitForExit(30000)){throw 'Test runner did not exit after process termination'}
            "TERMINATED $stage testhost=$($ready.pid)" | Tee-Object -FilePath (Join-Path $folder 'termination.txt')
        } finally {
            if(-not $worker.HasExited){Stop-Process -Id $worker.Id -Force}
        }
        # The killed transaction rolls back. Remove only this runner's deliberate delay trigger.
        docker exec $name psql -X -v ON_ERROR_STOP=1 -U aether_test -d aether_v29_native_disposable -c 'DROP TRIGGER IF EXISTS v30_crash_delay ON data."Item"; DROP FUNCTION IF EXISTS public.v30_crash_delay();'
        if($LASTEXITCODE -ne 0){throw 'Delay trigger cleanup failed'}
        dotnet test $project -c Release --no-build --nologo --filter 'FullyQualifiedName~ProcessCrashRecoveryAsync' --logger "trx;LogFileName=$stage-recovery.trx" *> (Join-Path $folder 'recovery.log')
        if($LASTEXITCODE -ne 0){throw "Recovery failed: $stage; inspect $folder"}
        "PASS $stage" | Tee-Object -FilePath (Join-Path $evidence 'summary.txt') -Append
    }
} finally {
    docker rm -f $name
    foreach($key in $keys){[Environment]::SetEnvironmentVariable($key,$previous[$key])}
}
