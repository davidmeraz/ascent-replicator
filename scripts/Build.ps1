param([string]$NinjaTraderBin = 'C:\Program Files\NinjaTrader 8\bin')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDir 'csc.exe'
$outputDir = Join-Path $projectRoot 'build'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$coreSource = Join-Path $projectRoot 'src\CopierCore.cs'
$addonSource = Join-Path $projectRoot 'src\AscentReplicator.cs'
$versionSource = Join-Path $projectRoot 'src\ProductInfo.cs'
$assemblyInfo = Join-Path $projectRoot 'Properties\AssemblyInfo.cs'
$testSource = Join-Path $projectRoot 'tests\CoreTests.cs'
$testExecutable = Join-Path $outputDir 'CoreTests.exe'
& $compiler /nologo /warnaserror+ /target:exe "/out:$testExecutable" $coreSource $testSource
if ($LASTEXITCODE -ne 0) { throw 'Could not compile the core tests.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'The core tests failed.' }
$references = @(
    (Join-Path $NinjaTraderBin 'NinjaTrader.Core.dll'),
    (Join-Path $NinjaTraderBin 'NinjaTrader.Gui.dll'),
    (Join-Path $frameworkDir 'WPF\PresentationCore.dll'),
    (Join-Path $frameworkDir 'WPF\PresentationFramework.dll'),
    (Join-Path $frameworkDir 'WPF\WindowsBase.dll'),
    (Join-Path $frameworkDir 'System.Xaml.dll'),
    (Join-Path $frameworkDir 'System.Xml.Linq.dll'),
    (Join-Path $frameworkDir 'System.ComponentModel.DataAnnotations.dll')
)
$referenceArguments = $references | ForEach-Object { "/reference:$_" }
$assemblyPath = Join-Path $outputDir 'AscentReplicator.compile-check.dll'
& $compiler /nologo /warnaserror+ /target:library "/out:$assemblyPath" $referenceArguments $coreSource $addonSource $versionSource $assemblyInfo
if ($LASTEXITCODE -ne 0) { throw 'Compilation against NinjaTrader failed.' }
$assemblyMetadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath)
$bundle = Join-Path $outputDir "ascent-replicator-$($assemblyMetadata.ProductVersion)-source.zip"
Compress-Archive -LiteralPath $coreSource,$addonSource,$versionSource,(Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'CHANGELOG.md') -DestinationPath $bundle -Force
Write-Output "Ascent Replicator version: $($assemblyMetadata.ProductVersion)"
Write-Output "Compiled against NinjaTrader $((Get-Item -LiteralPath (Join-Path $NinjaTraderBin 'NinjaTrader.exe')).VersionInfo.FileVersion)"
Write-Output "Source bundle: $bundle"
