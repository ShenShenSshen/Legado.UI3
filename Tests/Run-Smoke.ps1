param(
    [switch] $GitHub,
    [switch] $WebDav,
    [switch] $SkipBuild,
    [ValidateSet('Debug','Release')]
    [string] $Configuration = 'Debug',
    [string] $Executable
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) {
    dotnet build (Join-Path $projectRoot 'Legado.UI3.csproj') -c $Configuration -p:Platform=x64 -p:LegadoUI3SelfTest=true
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not $Executable) {
    $Executable = Join-Path $projectRoot "bin\x64\$Configuration\net10.0-windows10.0.19041.0\win-x64\Legado.UI3.exe"
}
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$scratch = Join-Path $projectRoot ('test-artifacts\smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
New-Item -ItemType File -Path (Join-Path $scratch '.smoke-test') | Out-Null

$env:LEGADO_UI3_DATA_DIR = $scratch
$env:LEGADO_UI3_CHECK_GITHUB = if ($GitHub) { '1' } else { '0' }
$env:LEGADO_UI3_MOCK_DAV = ''
$epub = Join-Path $projectRoot 'upstream\test\shanju.epub'
$env:LEGADO_UI3_EPUB_FIXTURE = if (Test-Path -LiteralPath $epub) { $epub } else { '' }
$env:NO_PROXY = @($env:NO_PROXY, '127.0.0.1', 'localhost') -join ','

$davProcess = $null
$testProcess = $null
try {
    if ($WebDav) {
        $mock = (Resolve-Path -LiteralPath (Join-Path $projectRoot 'upstream\test\mock-dav.js')).Path
        $node = (Get-Command node -ErrorAction Stop).Source
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        $davLog = Join-Path $scratch 'mock-dav.log'
        $davProcess = Start-Process -FilePath $node -ArgumentList ('"{0}" {1}' -f $mock, $port) -WindowStyle Hidden -PassThru -RedirectStandardOutput $davLog
        for ($attempt = 0; $attempt -lt 40 -and -not $davProcess.HasExited; $attempt++) {
            if ((Test-Path -LiteralPath $davLog) -and (Select-String -LiteralPath $davLog -Pattern 'mock dav listening' -Quiet)) { break }
            Start-Sleep -Milliseconds 125
        }
        if ($davProcess.HasExited -or -not (Select-String -LiteralPath $davLog -Pattern 'mock dav listening' -Quiet)) { throw 'WebDAV mock failed to start.' }
        $env:LEGADO_UI3_MOCK_DAV = "http://127.0.0.1:$port/dav/"
    }
    $testProcess = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path -Parent $Executable) -WindowStyle Hidden -PassThru
    Write-Output $scratch
    $result = Join-Path $scratch 'smoke-result.txt'
    if (-not $testProcess.WaitForExit(125000)) { throw 'WinUI smoke test exceeded 125 seconds.' }
    foreach ($name in @('smoke-result.txt', 'errors.log', 'startup-error.txt')) {
        $path = Join-Path $scratch $name
        if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path }
    }
    if ($testProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result) -or (Select-String -LiteralPath $result -Pattern '^FAIL' -Quiet) -or
        (Test-Path -LiteralPath (Join-Path $scratch 'errors.log')) -or (Test-Path -LiteralPath (Join-Path $scratch 'startup-error.txt'))) {
        throw "WinUI smoke test failed ($($testProcess.ExitCode))."
    }
}
finally {
    if ($testProcess -and -not $testProcess.HasExited) { Stop-Process -Id $testProcess.Id }
    if ($davProcess -and -not $davProcess.HasExited) { Stop-Process -Id $davProcess.Id }
}
