param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sdk = if ($env:REDBIRD_DOTNET_EXE) {
    (Resolve-Path -LiteralPath $env:REDBIRD_DOTNET_EXE).Path
}
else {
    (Get-Command dotnet -ErrorAction Stop).Source
}
Push-Location $repo
try {
    $site = Join-Path $repo 'docs\_site'
    if (Test-Path -LiteralPath $site) {
        Remove-Item -LiteralPath $site -Recurse -Force
    }
    & $sdk restore RedBird.slnx
    if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }
    & $sdk tool restore
    if ($LASTEXITCODE -ne 0) { throw "Tool restore failed with exit code $LASTEXITCODE." }
    & $sdk tool run docfx docs/docfx.json --warningsAsErrors
    if ($LASTEXITCODE -ne 0) { throw "DocFX failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath (Join-Path $repo 'docs\_site\index.html'))) {
        throw 'DocFX completed without producing docs/_site/index.html.'
    }
}
finally {
    Pop-Location
}
