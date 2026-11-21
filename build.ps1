param([switch]$Test, [switch]$Staged)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    throw 'Windows .NET Framework 4.8 and its C# compiler are required to build this project.'
}

$source = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$refs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
    'System.Security.dll', 'System.Web.Extensions.dll', 'System.Net.Http.dll', 'System.IO.Compression.dll') |
    ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$options = @('/nologo', '/utf8output', '/codepage:65001', '/langversion:5', '/platform:x64', '/optimize+')
$assets = Join-Path $PSScriptRoot 'src\assets'
$options += @(Get-ChildItem -LiteralPath $assets -File | ForEach-Object { '/resource:' + $_.FullName + ',RogueCoreNexus.' + $_.Name })

$outputDirectory = if ($Staged) { 'build\release' } else { 'dist' }
$dist = Join-Path $PSScriptRoot $outputDirectory
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$exe = Join-Path $dist 'RogueCoreNexus.exe'
$manifest = Join-Path $PSScriptRoot 'src\app.manifest'
& $compiler @options @refs /target:winexe "/out:$exe" "/win32manifest:$manifest" "/win32icon:$(Join-Path $assets 'app.ico')" @source
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
Write-Output $exe

if ($Test) {
    $build = Join-Path $PSScriptRoot 'build'
    New-Item -ItemType Directory -Force -Path $build | Out-Null
    $testExe = Join-Path $build 'SmokeTests.exe'
    $testSource = Join-Path $PSScriptRoot 'tests\SmokeTests.cs'
    & $compiler @options @refs /target:exe /main:RogueCoreNexus.SmokeTests "/out:$testExe" @source $testSource
    if ($LASTEXITCODE -ne 0) { throw "Test build failed with exit code $LASTEXITCODE." }
    & $testExe $build
    if ($LASTEXITCODE -ne 0) { throw "Smoke tests failed with exit code $LASTEXITCODE." }
}
