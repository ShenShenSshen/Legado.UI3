$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $projectRoot 'upstream'
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'ui\engine.js'))) {
    throw 'Missing upstream checkout. See README.md.'
}
$assetsRoot = Join-Path $projectRoot 'Assets'
# The desktop engine carries local discovery changes. Merge engine updates manually.
if (-not (Test-Path -LiteralPath (Join-Path $assetsRoot 'engine.js'))) { throw 'Restore the WinUI engine.js from the project source.' }
Copy-Item -LiteralPath (Join-Path $sourceRoot 'ui\detect.js') -Destination $assetsRoot
$parserText = Get-Content -LiteralPath (Join-Path $sourceRoot 'src\parse.js') -Raw
$parserOutput = "((module, require) => {`n" + $parserText + "`n})(ParserModule, parserRequire);`n"
[IO.File]::WriteAllText((Join-Path $assetsRoot 'parse.js'), $parserOutput, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Destination (Join-Path $assetsRoot 'UPSTREAM-LICENSE.txt')
