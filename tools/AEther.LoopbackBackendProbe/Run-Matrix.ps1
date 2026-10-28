param(
    [int]$Seconds = 5,
    [string]$Tag = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [string[]]$Backends = @('naudio','cscore','bass','miniaudio')
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $root
try {
    & dotnet build tools/AEther.LoopbackBackendProbe/AEther.LoopbackBackendProbe.csproj -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed' }
    $output = Join-Path $root ('artifacts/capture-backends/' + $Tag)
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $failed = @()
    # Run sequentially: simultaneous clients would contaminate engine-period comparisons.
    foreach ($backend in $Backends) {
        foreach ($mode in @('baseline','before','after')) {
            $log = Join-Path $output "$backend-$mode-default.log"
            & dotnet tools/AEther.LoopbackBackendProbe/bin/Debug/net10.0-windows7.0/AEther.LoopbackBackendProbe.dll $backend $mode $Seconds default $Tag *> $log
            $code = $LASTEXITCODE
            Write-Output "$backend/$mode exit=$code; $log"
            if ($code -ne 0) { $failed += "$backend/$mode" }
        }
    }
    if ($failed.Count -gt 0) { throw ('Failed cases: ' + ($failed -join ', ')) }
}
finally { Pop-Location }
