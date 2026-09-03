param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('X64', 'X86')]
    [string]$Architecture,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$results = Join-Path $repo 'artifacts\test-results'
New-Item -ItemType Directory -Force -Path $results | Out-Null
$sdk = if ($env:REDBIRD_DOTNET_EXE) {
    (Resolve-Path -LiteralPath $env:REDBIRD_DOTNET_EXE).Path
}
else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

if ($Architecture -eq 'X64') {
    $project = Join-Path $repo 'src\RedBird.X64.Tests\RedBird.X64.Tests.csproj'
    $assembly = Join-Path $repo "bin\RedBird.X64.Tests\$Configuration\net10.0\RedBird.X64.Tests.dll"
    $runtime = $sdk
}
else {
    $project = Join-Path $repo 'src\RedBird.X86.Tests\RedBird.X86.Tests.csproj'
    $assembly = Join-Path $repo "bin\RedBird.X86.Tests\$Configuration\net10.0\RedBird.X86.Tests.dll"
    $runtimeRoot = Join-Path $repo '.tools\dotnet-x86'
    $runtime = Join-Path $runtimeRoot 'dotnet.exe'

    if (-not (Test-Path -LiteralPath $runtime)) {
        $tools = Join-Path $repo '.tools'
        New-Item -ItemType Directory -Force -Path $tools | Out-Null
        $installer = Join-Path $tools 'dotnet-install.ps1'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
        & $installer -Runtime dotnet -Channel '10.0' -Architecture x86 -InstallDir $runtimeRoot -NoPath
        $installerSucceeded = $?
        if (-not $installerSucceeded -or -not (Test-Path -LiteralPath $runtime)) {
            throw 'Installing the .NET 10 x86 runtime failed.'
        }
    }
}

& $sdk restore $project
if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }
& $sdk build $project -c $Configuration --no-restore -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$xunitReport = Join-Path $results "$($Architecture.ToLowerInvariant())-xunit.xml"
& $runtime $assembly -xml $xunitReport
$testExitCode = $LASTEXITCODE

$junitReport = Join-Path $results "$($Architecture.ToLowerInvariant())-junit.xml"
if (Test-Path -LiteralPath $xunitReport) {
    & (Join-Path $PSScriptRoot 'xunit-to-junit.ps1') -InputPath $xunitReport -OutputPath $junitReport
}

# Convert the report before failing the job so GitLab can display the individual failed tests.
if ($testExitCode -ne 0) { throw "$Architecture tests failed with exit code $testExitCode." }
