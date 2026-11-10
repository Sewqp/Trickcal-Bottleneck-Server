param([switch]$Integration, [switch]$CrashRecovery)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build tests/GameServer.Checks/GameServer.Checks.csproj -c Release --nologo -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $checkArgs = @('tests/GameServer.Checks/bin/Release/net10.0/GameServer.Checks.dll')
    if ($Integration) { $checkArgs += '--integration' }
    dotnet @checkArgs
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
    if ($CrashRecovery) {
        if (-not $env:TRICKCAL_TEST_CRASH_REDIS) { throw 'Set TRICKCAL_TEST_CRASH_REDIS to a separate empty Redis database.' }
        $previousRedis = $env:TRICKCAL_TEST_REDIS
        try {
            $env:TRICKCAL_TEST_REDIS = $env:TRICKCAL_TEST_CRASH_REDIS
            python tools/check_crash_recovery.py
            if ($LASTEXITCODE -ne 0) { throw 'Crash recovery verification failed.' }
        } finally { $env:TRICKCAL_TEST_REDIS = $previousRedis }
    }
} finally { Pop-Location }
