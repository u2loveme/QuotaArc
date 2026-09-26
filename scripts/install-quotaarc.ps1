[CmdletBinding()]
param(
    [string]$LocalAppDataRoot = $env:LOCALAPPDATA,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\QuotaArc'),
    [string]$LegacyInstallRoot,
    [string]$Desktop = [Environment]::GetFolderPath('Desktop'),
    [string]$PackageRoot = $PSScriptRoot,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$legacyCompatibilityFile = Join-Path $PSScriptRoot 'legacy-quotaarc-compat.ps1'
if (-not (Test-Path -LiteralPath $legacyCompatibilityFile -PathType Leaf)) { throw 'Legacy upgrade compatibility data is missing.' }
. $legacyCompatibilityFile
if (-not $LegacyInstallRoot) { $LegacyInstallRoot = Join-Path $env:LOCALAPPDATA (Join-Path 'Programs' $LegacyQuotaArc.InstallDirectory) }
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$LegacyInstallRoot = [IO.Path]::GetFullPath($LegacyInstallRoot)
$Desktop = [IO.Path]::GetFullPath($Desktop)
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$programsDirectory = Split-Path -Parent $InstallRoot
$packageApp = Join-Path $PackageRoot 'app'
$sourceExe = Join-Path $packageApp 'QuotaArc.exe'
$sourceIcon = Join-Path $PackageRoot 'QuotaArc.ico'
$sourceUninstaller = Join-Path $PackageRoot 'uninstall-quotaarc.ps1'
$newMarkerName = '.quotaarc-managed'
$newMarkerValue = 'QuotaArc managed install'
$legacyMarkerName = $LegacyQuotaArc.ManagedMarker
$legacyMarkerValue = $LegacyQuotaArc.ManagedMarkerValue
$legacyExeName = $LegacyQuotaArc.Executable
$exeName = 'QuotaArc.exe'
$shortcutPath = Join-Path $Desktop 'QuotaArc.lnk'
$legacyShortcutPath = Join-Path $Desktop $LegacyQuotaArc.Shortcut

function Test-ManagedDirectory([string]$Path, [string]$MarkerName, [string]$MarkerValue, [string]$ExecutableName) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $markerPath = Join-Path $Path $MarkerName
    $executablePath = Join-Path $Path $ExecutableName
    if ((-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) -or (-not (Test-Path -LiteralPath $executablePath -PathType Leaf))) { return $false }
    return (Get-Content -LiteralPath $markerPath -Raw).Trim() -ceq $MarkerValue
}

function Get-ShortcutTarget([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    if ([string]::IsNullOrWhiteSpace($shortcut.TargetPath)) { return $null }
    return [IO.Path]::GetFullPath($shortcut.TargetPath)
}

if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "Package executable not found: $sourceExe" }
if (-not (Test-Path -LiteralPath $sourceIcon -PathType Leaf)) { throw "Package icon not found: $sourceIcon" }
if (-not (Test-Path -LiteralPath $sourceUninstaller -PathType Leaf)) { throw "Package uninstaller not found: $sourceUninstaller" }

$targetExists = Test-Path -LiteralPath $InstallRoot
if ($targetExists -and -not (Test-ManagedDirectory $InstallRoot $newMarkerName $newMarkerValue $exeName)) {
    throw "Refusing to overwrite a QuotaArc destination whose ownership cannot be proven: $InstallRoot"
}
$legacyExists = Test-Path -LiteralPath $LegacyInstallRoot
$legacyManaged = Test-ManagedDirectory $LegacyInstallRoot $legacyMarkerName $legacyMarkerValue $legacyExeName
if ($legacyExists -and -not $legacyManaged) {
    throw "Legacy destination exists but is not a verified managed installation; leaving it untouched: $LegacyInstallRoot"
}
$legacyDatabasePath = Join-Path $LocalAppDataRoot (Join-Path $LegacyQuotaArc.DataDirectory 'telemetry.sqlite3')
$databasePath = Join-Path $LocalAppDataRoot 'QuotaArc\telemetry.sqlite3'
$profileMarkerPath = Join-Path (Split-Path -Parent $databasePath) '.quotaarc-profile.json'
$canonicalProfileValidated = $false
if ((Test-Path -LiteralPath $databasePath -PathType Leaf) -and (Test-Path -LiteralPath $profileMarkerPath -PathType Leaf)) {
    try {
        $profileMarker = Get-Content -LiteralPath $profileMarkerPath -Raw | ConvertFrom-Json
        $canonicalProfileValidated = $profileMarker.schema_version -eq 1 -and $profileMarker.profile_validated -eq $true
    }
    catch {
        $canonicalProfileValidated = $false
    }
}
if ($legacyManaged -and -not (Test-Path -LiteralPath $legacyDatabasePath -PathType Leaf) -and -not $canonicalProfileValidated) {
    throw "Verified legacy install exists but the established runtime database is missing; refusing a migration that could create a blank profile: $legacyDatabasePath"
}

$newShortcutTarget = Get-ShortcutTarget $shortcutPath
if ($newShortcutTarget -and $newShortcutTarget -ine (Join-Path $InstallRoot $exeName)) {
    throw "Refusing to replace an unrelated QuotaArc desktop shortcut: $shortcutPath -> $newShortcutTarget"
}

$processes = @(Get-CimInstance Win32_Process | Where-Object {
    ($_.Name -eq $exeName -and $_.ExecutablePath -eq (Join-Path $InstallRoot $exeName)) -or
    ($_.Name -eq $legacyExeName -and $_.ExecutablePath -eq (Join-Path $LegacyInstallRoot $legacyExeName))
})
if ($processes.Count -gt 0) {
    $details = ($processes | ForEach-Object { "$($_.Name) PID $($_.ProcessId): $($_.ExecutablePath)" }) -join [Environment]::NewLine
    throw "Close all running QuotaArc and legacy processes before installing. No process was terminated.`n$details"
}

New-Item -ItemType Directory -Path $programsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $Desktop -Force | Out-Null
$stageRoot = Join-Path $programsDirectory ".quotaarc-installing-$([Guid]::NewGuid().ToString('N'))"
$rollbackRoot = Join-Path $programsDirectory ".quotaarc-rollback-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stageRoot | Out-Null
$targetMovedToRollback = $false
$launchedProcessId = $null
$legacySupportStatePath = Join-Path $LocalAppDataRoot (Join-Path $LegacyQuotaArc.DataDirectory 'support-engagement-state.json')
$supportStatePath = Join-Path $LocalAppDataRoot 'QuotaArc\support-engagement-state.json'
$supportStateBefore = $null
try {
    if (Test-Path -LiteralPath $legacySupportStatePath -PathType Leaf) {
        $supportStateBefore = Get-Content -LiteralPath $legacySupportStatePath -Raw | ConvertFrom-Json
    }
    Copy-Item -Path (Join-Path $packageApp '*') -Destination $stageRoot -Recurse -Force
    Copy-Item -LiteralPath $sourceIcon -Destination (Join-Path $stageRoot 'QuotaArc.ico') -Force
    Copy-Item -LiteralPath $sourceUninstaller -Destination (Join-Path $stageRoot 'uninstall-quotaarc.ps1') -Force
    Copy-Item -LiteralPath $legacyCompatibilityFile -Destination (Join-Path $stageRoot 'legacy-quotaarc-compat.ps1') -Force
    Set-Content -LiteralPath (Join-Path $stageRoot $newMarkerName) -Value $newMarkerValue -Encoding ascii
    $stagedExe = Join-Path $stageRoot $exeName
    if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) { throw "Staged executable missing: $stagedExe" }
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($stagedExe)
    if ($versionInfo.ProductName -cne 'QuotaArc' -or $versionInfo.FileDescription -cne 'QuotaArc') {
        throw "Staged executable metadata is not QuotaArc (ProductName='$($versionInfo.ProductName)', FileDescription='$($versionInfo.FileDescription)')."
    }

    if ($targetExists) {
        Move-Item -LiteralPath $InstallRoot -Destination $rollbackRoot
        $targetMovedToRollback = $true
    }
    Move-Item -LiteralPath $stageRoot -Destination $InstallRoot
    $installedExe = Join-Path $InstallRoot $exeName
    if (-not (Test-ManagedDirectory $InstallRoot $newMarkerName $newMarkerValue $exeName)) {
        throw 'Installed QuotaArc ownership validation failed.'
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $installedExe
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.Description = 'QuotaArc'
    $shortcut.IconLocation = "$installedExe,0"
    $shortcut.Save()
    if ((Get-ShortcutTarget $shortcutPath) -ine [IO.Path]::GetFullPath($installedExe)) {
        throw 'QuotaArc shortcut target did not validate after creation.'
    }

    if (-not $NoLaunch) {
        $started = Start-Process -FilePath $installedExe -WorkingDirectory $InstallRoot -PassThru
        $launchedProcessId = $started.Id
        $deadline = [DateTime]::UtcNow.AddSeconds(25)
        $validatedProcess = $null
        do {
            Start-Sleep -Milliseconds 250
            $validatedProcess = Get-CimInstance Win32_Process -Filter "Name = '$exeName'" |
                Where-Object { $_.ExecutablePath -eq $installedExe } | Select-Object -First 1
            if ($validatedProcess) {
                $localProcess = Get-Process -Id $validatedProcess.ProcessId -ErrorAction SilentlyContinue
                if ($localProcess -and $localProcess.MainWindowHandle -ne [IntPtr]::Zero) { break }
            }
        } while ([DateTime]::UtcNow -lt $deadline)
        if (-not $validatedProcess -or -not $localProcess -or $localProcess.MainWindowHandle -eq [IntPtr]::Zero) {
            throw "QuotaArc did not present a main window after launch (started PID $($started.Id)); the legacy installation was retained."
        }
        if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) { throw "Shared runtime database is missing: $databasePath" }
        if (-not (Test-Path -LiteralPath $profileMarkerPath -PathType Leaf)) { throw 'QuotaArc profile migration marker was not created after launch.' }
        if ($supportStateBefore -and (Test-Path -LiteralPath $supportStatePath -PathType Leaf)) {
            $state = Get-Content -LiteralPath $supportStatePath -Raw | ConvertFrom-Json
            if ($state.SupportLinkOpened -ne $supportStateBefore.SupportLinkOpened) {
                throw 'SupportLinkOpened changed during branding migration.'
            }
            if ($state.SupportPromptShown -ne $supportStateBefore.SupportPromptShown) {
                throw 'SupportPromptShown changed during branding migration.'
            }
            if ($supportStateBefore.SupportLinkOpened -eq $true -and $state.SupportLinkOpened -ne $true) {
                throw 'SupportLinkOpened was not preserved as true after QuotaArc launch.'
            }
        }
    }

    # Keep the legacy install and shortcut when running in test/no-launch mode.
    # A real installation retires them only after the new executable owns a visible window.
    if (-not $NoLaunch -and $legacyManaged) {
        $legacyProcess = Get-CimInstance Win32_Process -Filter "Name = '$legacyExeName'" |
            Where-Object { $_.ExecutablePath -eq (Join-Path $LegacyInstallRoot $legacyExeName) }
        if ($legacyProcess) { throw 'Legacy process appeared during migration; the legacy installation was retained.' }
        $legacyShortcutTarget = Get-ShortcutTarget $legacyShortcutPath
        if ($legacyShortcutTarget -ieq [IO.Path]::GetFullPath((Join-Path $LegacyInstallRoot $legacyExeName))) {
            Remove-Item -LiteralPath $legacyShortcutPath
        }
        $retiredLegacyRoot = Join-Path $programsDirectory ".quotaarc-legacy-install-retired-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))"
        Move-Item -LiteralPath $LegacyInstallRoot -Destination $retiredLegacyRoot
        Write-Output "Legacy managed application retired for rollback at $retiredLegacyRoot"
    }

    if (-not $NoLaunch -and $targetMovedToRollback -and (Test-Path -LiteralPath $rollbackRoot)) {
        Remove-Item -LiteralPath $rollbackRoot -Recurse
    }
}
catch {
    if ($launchedProcessId) {
        $ownedProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $launchedProcessId" -ErrorAction SilentlyContinue
        if ($ownedProcess -and $ownedProcess.Name -eq $exeName -and $ownedProcess.ExecutablePath -eq (Join-Path $InstallRoot $exeName)) {
            Stop-Process -Id $launchedProcessId -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $launchedProcessId -Timeout 5 -ErrorAction SilentlyContinue
        }
    }
    if ($targetMovedToRollback -and (Test-Path -LiteralPath $rollbackRoot)) {
        if (Test-Path -LiteralPath $InstallRoot) {
            if (-not (Test-ManagedDirectory $InstallRoot $newMarkerName $newMarkerValue $exeName)) {
                throw 'Could not safely restore the previous QuotaArc install because the new destination no longer proves ownership.'
            }
            Remove-Item -LiteralPath $InstallRoot -Recurse
        }
        Move-Item -LiteralPath $rollbackRoot -Destination $InstallRoot
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
}

if ($NoLaunch) {
    Write-Output "QuotaArc installed at $InstallRoot; launch and smoke-test it before retiring the legacy installation."
    if ($targetMovedToRollback) { Write-Output "Previous QuotaArc install retained for rollback at $rollbackRoot" }
} else {
    Write-Output "QuotaArc installed, launched, and its main window validated: $InstallRoot"
    if ($legacyManaged) { Write-Output 'Verified managed legacy installation retired; shared runtime data was left in place.' }
}
