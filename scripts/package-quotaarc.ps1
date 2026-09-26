param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'src\QuotaArc.Desktop\QuotaArc.Desktop.csproj'
$version = (dotnet msbuild $project -getProperty:Version | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) { throw 'Could not read the QuotaArc package version from the desktop project.' }
$packageName = "QuotaArc-$version-win-x64"
$packageDirectory = Join-Path $OutputDirectory $packageName
$zipPath = "$packageDirectory.zip"
if (Test-Path -LiteralPath $packageDirectory) { throw "Package directory already exists: $packageDirectory" }
if (Test-Path -LiteralPath $zipPath) { throw "Package archive already exists: $zipPath" }

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
$appDirectory = Join-Path $packageDirectory 'app'
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:WindowsAppSDKSelfContained=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    "-p:InformationalVersion=$version" `
    "-p:PathMap=$repoRoot=/_/" `
    -o $appDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$publishedExe = Join-Path $appDirectory 'QuotaArc.exe'
if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) { throw "Publish did not produce QuotaArc.exe: $publishedExe" }
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExe)
if ($metadata.ProductName -cne 'QuotaArc' -or $metadata.FileDescription -cne 'QuotaArc') {
    throw "Published executable metadata is incorrect (ProductName='$($metadata.ProductName)', FileDescription='$($metadata.FileDescription)')."
}

Copy-Item (Join-Path $repoRoot 'scripts\install-quotaarc.ps1') $packageDirectory
Copy-Item (Join-Path $repoRoot 'scripts\uninstall-quotaarc.ps1') $packageDirectory
Copy-Item (Join-Path $repoRoot 'scripts\legacy-quotaarc-compat.ps1') $packageDirectory
Copy-Item (Join-Path $repoRoot 'assets\QuotaArc.ico') (Join-Path $packageDirectory 'QuotaArc.ico')
Copy-Item (Join-Path $repoRoot 'LICENSE') $packageDirectory
Copy-Item (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') $packageDirectory
Copy-Item (Join-Path $repoRoot 'licenses') (Join-Path $packageDirectory 'licenses') -Recurse
@'
QuotaArc — portable Windows x64 package

Install or update:
  Run install-quotaarc.ps1 from this extracted folder.
  The app is installed to %LOCALAPPDATA%\Programs\QuotaArc and a QuotaArc
  desktop shortcut is created. A verified legacy managed installation is
  upgraded after the QuotaArc app launches successfully.

Uninstall:
  Run uninstall-quotaarc.ps1 from the installed folder.
  This removes only the managed QuotaArc application and shortcut. Runtime
  history and preferences under %LOCALAPPDATA%\QuotaArc are retained.

The package is self-contained for Windows x64. Windows App SDK dependencies
are included.

This public beta is unsigned. Windows may show a SmartScreen or publisher
warning. Verify the download against SHA256SUMS.txt and review the source at
https://github.com/u2loveme/QuotaArc. Do not disable Windows security.

License: MPL-2.0. Third-party license texts are in licenses/.
'@ | Set-Content -LiteralPath (Join-Path $packageDirectory 'README.txt') -Encoding utf8

Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw 'QuotaArc package archive was not created.' }
Get-Item -LiteralPath $zipPath | Select-Object FullName,Length,LastWriteTime
