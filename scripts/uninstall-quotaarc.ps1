param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\QuotaArc'),
    [string]$Desktop = [Environment]::GetFolderPath('Desktop')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $InstallRoot -PathType Container)) { throw "QuotaArc install directory was not found: $InstallRoot" }
$resolvedRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $InstallRoot).Path)
$markerPath = Join-Path $resolvedRoot '.quotaarc-managed'
$exe = Join-Path $resolvedRoot 'QuotaArc.exe'
if ((-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) -or ((Get-Content -LiteralPath $markerPath -Raw).Trim() -cne 'QuotaArc managed install') -or (-not (Test-Path -LiteralPath $exe -PathType Leaf))) {
    throw "Refusing to remove a folder without the QuotaArc ownership marker and executable: $resolvedRoot"
}

$legacyCompatibilityFile = Join-Path $PSScriptRoot 'legacy-quotaarc-compat.ps1'
if (-not (Test-Path -LiteralPath $legacyCompatibilityFile -PathType Leaf)) { throw 'Legacy compatibility data is missing.' }
. $legacyCompatibilityFile
$legacyRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA (Join-Path 'Programs' $LegacyQuotaArc.InstallDirectory)))
$legacyExe = Join-Path $legacyRoot $LegacyQuotaArc.Executable
$processes = @(Get-CimInstance Win32_Process | Where-Object {
    ($_.Name -eq 'QuotaArc.exe' -and $_.ExecutablePath -eq $exe) -or ($_.Name -eq $LegacyQuotaArc.Executable -and $_.ExecutablePath -eq $legacyExe)
})
if ($processes.Count -gt 0) {
    $details = ($processes | ForEach-Object { "$($_.Name) PID $($_.ProcessId): $($_.ExecutablePath)" }) -join [Environment]::NewLine
    throw "Close running QuotaArc and legacy processes before uninstalling. No process was terminated.`n$details"
}

$shortcutPath = Join-Path ([IO.Path]::GetFullPath($Desktop)) 'QuotaArc.lnk'
if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if (-not [string]::IsNullOrWhiteSpace($shortcut.TargetPath) -and [IO.Path]::GetFullPath($shortcut.TargetPath) -ieq [IO.Path]::GetFullPath($exe)) {
        Remove-Item -LiteralPath $shortcutPath
    }
}

Remove-Item -LiteralPath $resolvedRoot -Recurse
Write-Output 'QuotaArc application files removed. Runtime data under %LOCALAPPDATA%\QuotaArc was kept.'
