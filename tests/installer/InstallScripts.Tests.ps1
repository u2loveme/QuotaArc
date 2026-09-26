param(
    [string]$PackageRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$installer = Join-Path $repoRoot 'scripts\install-quotaarc.ps1'
$uninstaller = Join-Path $repoRoot 'scripts\uninstall-quotaarc.ps1'
$packageRoot = if ($PackageRoot) {
    (Resolve-Path -LiteralPath $PackageRoot).Path
} else {
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'artifacts') -Directory -Filter 'QuotaArc-*-win-x64' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $packageRoot) { throw 'Build the QuotaArc package before running installer tests.' }
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "QuotaArcInstallerTests-$([Guid]::NewGuid().ToString('N'))"
$programs = Join-Path $tempRoot 'Programs'
$desktop = Join-Path $tempRoot 'Desktop'
$localAppData = Join-Path $tempRoot 'AppData'
$legacyRoot = Join-Path $programs 'Codex Usage Monitor'
$installRoot = Join-Path $programs 'QuotaArc'
$shell = New-Object -ComObject WScript.Shell
$freshInstallRoot = Join-Path $tempRoot 'FreshInstall'
$freshPrograms = Join-Path $freshInstallRoot 'Programs'
$freshLocalAppData = Join-Path $freshInstallRoot 'AppData'
$freshUserProfile = Join-Path $freshInstallRoot 'User'
$freshDesktop = Join-Path $freshInstallRoot 'Desktop'
$freshInstall = Join-Path $freshPrograms 'QuotaArc'
$freshExecutable = Join-Path $freshInstall 'QuotaArc.exe'
$freshDatabase = Join-Path $freshLocalAppData 'QuotaArc\telemetry.sqlite3'
$testPython = Join-Path $repoRoot '.venv\Scripts\python.exe'
$freshProcessId = $null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $threw = $false
    try { & $Action } catch { $threw = $true }
    Assert-True $threw $Message
}

try {
    New-Item -ItemType Directory -Path $legacyRoot, $desktop, (Join-Path $localAppData 'CodexUsageMonitor') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $localAppData 'CodexUsageMonitor\telemetry.sqlite3') -Value 'migration fixture' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $legacyRoot '.token-monitor-managed') -Value 'Codex Usage Monitor managed install' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $legacyRoot 'TokenMonitor.Desktop.exe') -Value 'legacy executable fixture' -Encoding ascii
    $oldShortcut = $shell.CreateShortcut((Join-Path $desktop 'Codex Usage Monitor.lnk'))
    $oldShortcut.TargetPath = Join-Path $legacyRoot 'TokenMonitor.Desktop.exe'
    $oldShortcut.Save()

    & $installer -PackageRoot $packageRoot -LocalAppDataRoot $localAppData -InstallRoot $installRoot -LegacyInstallRoot $legacyRoot -Desktop $desktop -NoLaunch
    Assert-True (Test-Path -LiteralPath (Join-Path $installRoot 'QuotaArc.exe')) 'QuotaArc executable was not installed.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $installRoot '.quotaarc-managed') -Raw).Trim() -ceq 'QuotaArc managed install') 'QuotaArc marker is incorrect.'
    Assert-True (Test-Path -LiteralPath $legacyRoot) 'NoLaunch must retain the legacy installation until launch validation.'
    $newShortcut = $shell.CreateShortcut((Join-Path $desktop 'QuotaArc.lnk'))
    Assert-True ([IO.Path]::GetFullPath($newShortcut.TargetPath) -ieq [IO.Path]::GetFullPath((Join-Path $installRoot 'QuotaArc.exe'))) 'QuotaArc shortcut target is incorrect.'

    & $installer -PackageRoot $packageRoot -LocalAppDataRoot $localAppData -InstallRoot $installRoot -LegacyInstallRoot $legacyRoot -Desktop $desktop -NoLaunch
    Assert-True (@(Get-ChildItem -LiteralPath $programs -Directory -Filter '.quotaarc-rollback-*').Count -gt 0) 'A no-launch update must retain its rollback directory.'

    $unownedTarget = Join-Path $programs 'Unowned'
    New-Item -ItemType Directory -Path $unownedTarget -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $unownedTarget 'keep.txt') -Value 'preserve' -Encoding utf8
    Assert-Throws { & $installer -PackageRoot $packageRoot -LocalAppDataRoot $localAppData -InstallRoot $unownedTarget -LegacyInstallRoot (Join-Path $programs 'MissingLegacy') -Desktop $desktop -NoLaunch } 'Installer accepted an unowned destination.'
    Assert-True (Test-Path -LiteralPath (Join-Path $unownedTarget 'keep.txt')) 'Installer changed an unowned destination.'

    $unownedLegacy = Join-Path $programs 'UnownedLegacy'
    New-Item -ItemType Directory -Path $unownedLegacy -Force | Out-Null
    Assert-Throws { & $installer -PackageRoot $packageRoot -LocalAppDataRoot $localAppData -InstallRoot (Join-Path $programs 'OtherQuotaArc') -LegacyInstallRoot $unownedLegacy -Desktop $desktop -NoLaunch } 'Installer accepted an unowned legacy directory.'
    Assert-True (Test-Path -LiteralPath $unownedLegacy) 'Installer changed an unowned legacy directory.'

    Remove-Item -LiteralPath (Join-Path $localAppData 'CodexUsageMonitor\telemetry.sqlite3')
    $noProfileLegacy = Join-Path $programs 'NoProfileLegacy'
    New-Item -ItemType Directory -Path $noProfileLegacy -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $noProfileLegacy '.token-monitor-managed') -Value 'Codex Usage Monitor managed install' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $noProfileLegacy 'TokenMonitor.Desktop.exe') -Value 'legacy executable fixture' -Encoding ascii
    $noProfileDesktop = Join-Path $tempRoot 'NoProfileDesktop'
    New-Item -ItemType Directory -Path $noProfileDesktop -Force | Out-Null
    Assert-Throws { & $installer -PackageRoot $packageRoot -LocalAppDataRoot $localAppData -InstallRoot (Join-Path $programs 'NoProfileQuotaArc') -LegacyInstallRoot $noProfileLegacy -Desktop $noProfileDesktop -NoLaunch } 'Installer accepted a legacy install with no old or validated canonical database.'

    $canonicalProfile = Join-Path $localAppData 'QuotaArc'
    New-Item -ItemType Directory -Path $canonicalProfile -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $canonicalProfile '.quotaarc-profile.json') -Value '{"schema_version":1,"profile_validated":true}' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $canonicalProfile 'telemetry.sqlite3') -Value 'validated profile fixture' -Encoding ascii
    & $installer -PackageRoot $packageRoot -LocalAppDataRoot $localAppData -InstallRoot (Join-Path $programs 'PostMigrationQuotaArc') -LegacyInstallRoot $noProfileLegacy -Desktop $noProfileDesktop -NoLaunch
    Assert-True (Test-Path -LiteralPath (Join-Path $programs 'PostMigrationQuotaArc\QuotaArc.exe')) 'Installer refused an update with a validated canonical profile after legacy data retirement.'

    $unownedUninstall = Join-Path $programs 'UnownedUninstall'
    New-Item -ItemType Directory -Path $unownedUninstall -Force | Out-Null
    Assert-Throws { & $uninstaller -InstallRoot $unownedUninstall -Desktop $desktop } 'Uninstaller accepted a directory without the QuotaArc ownership marker.'
    Assert-True (Test-Path -LiteralPath $unownedUninstall) 'Uninstaller changed an unowned directory.'

    $disposableInstall = Join-Path $programs 'DisposableQuotaArc'
    New-Item -ItemType Directory -Path $disposableInstall -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $disposableInstall '.quotaarc-managed') -Value 'QuotaArc managed install' -Encoding ascii
    Copy-Item -LiteralPath (Join-Path $packageRoot 'app\QuotaArc.exe') -Destination (Join-Path $disposableInstall 'QuotaArc.exe')
    & $uninstaller -InstallRoot $disposableInstall -Desktop $desktop
    Assert-True (-not (Test-Path -LiteralPath $disposableInstall)) 'Uninstaller did not remove the verified disposable install.'

    New-Item -ItemType Directory -Path $freshPrograms, $freshLocalAppData, $freshUserProfile, $freshDesktop | Out-Null
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $freshLocalAppData 'QuotaArc'))) 'Fresh installer profile was not empty.'
    $previousEnvironment = @{
        LOCALAPPDATA = $env:LOCALAPPDATA
        USERPROFILE = $env:USERPROFILE
        PATH = $env:PATH
        CODEX_CLI_PATH = $env:CODEX_CLI_PATH
    }
    try {
        $env:LOCALAPPDATA = $freshLocalAppData
        $env:USERPROFILE = $freshUserProfile
        $env:PATH = Join-Path $env:SystemRoot 'System32'
        $env:CODEX_CLI_PATH = Join-Path $freshInstallRoot 'codex-not-installed.exe'
        & $installer -PackageRoot $packageRoot -LocalAppDataRoot $freshLocalAppData -InstallRoot $freshInstall `
            -LegacyInstallRoot (Join-Path $freshPrograms 'NoLegacyInstall') -Desktop $freshDesktop
        $installedProcess = Get-CimInstance Win32_Process -Filter "Name = 'QuotaArc.exe'" |
            Where-Object { $_.ExecutablePath -eq $freshExecutable } | Select-Object -First 1
        Assert-True ($null -ne $installedProcess) 'Fresh no-Codex installer did not leave the application running.'
        $freshProcessId = [int]$installedProcess.ProcessId
        Assert-True (Test-Path -LiteralPath (Join-Path $freshLocalAppData 'QuotaArc\telemetry.sqlite3')) `
            'Fresh no-Codex installer did not create telemetry.sqlite3.'
        Assert-True (Test-Path -LiteralPath (Join-Path $freshLocalAppData 'QuotaArc\.quotaarc-profile.json')) `
            'Fresh no-Codex installer did not create the canonical profile marker.'
        Assert-True (Test-Path -LiteralPath $testPython -PathType Leaf) 'The canonical Python test runtime is missing for SQLite smoke validation.'
        $integrity = & $testPython -c "import sqlite3,sys; print(sqlite3.connect(sys.argv[1]).execute('PRAGMA integrity_check').fetchone()[0])" $freshDatabase
        Assert-True ($LASTEXITCODE -eq 0 -and $integrity.Trim() -ceq 'ok') 'Fresh profile SQLite integrity_check failed.'
        $tablesScript = 'import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); print(",".join(sorted(r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type=''table'' AND name NOT LIKE ''sqlite_%''"))))'
        $tables = & $testPython -c $tablesScript $freshDatabase
        Assert-True ($LASTEXITCODE -eq 0 -and $tables.Trim() -ceq 'alert_events,app_settings,import_state,quota_samples,usage_samples') `
            'Fresh profile SQLite schema is incomplete.'
        $rowCounts = & $testPython -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); print(sum(c.execute('SELECT COUNT(*) FROM '+t).fetchone()[0] for t in ('alert_events','app_settings','import_state','quota_samples','usage_samples')))" $freshDatabase
        Assert-True ($LASTEXITCODE -eq 0 -and $rowCounts.Trim() -ceq '0') 'Fresh profile SQLite database contains unexpected seeded data.'
    }
    finally {
        $env:LOCALAPPDATA = $previousEnvironment.LOCALAPPDATA
        $env:USERPROFILE = $previousEnvironment.USERPROFILE
        $env:PATH = $previousEnvironment.PATH
        $env:CODEX_CLI_PATH = $previousEnvironment.CODEX_CLI_PATH
    }

    Write-Output 'PASS: package install, legacy ownership checks, rollback retention, scoped uninstall, and isolated fresh no-Codex install.'
}
finally {
    if ($freshProcessId) {
        $ownedProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $freshProcessId" -ErrorAction SilentlyContinue
        if ($ownedProcess -and $ownedProcess.Name -eq 'QuotaArc.exe' -and $ownedProcess.ExecutablePath -eq $freshExecutable) {
            Stop-Process -Id $freshProcessId -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $freshProcessId -Timeout 5 -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $resolvedTempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedTempRoot.StartsWith($resolvedTempBase, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove installer test data outside the temporary directory: $resolvedTempRoot"
        }
        Remove-Item -LiteralPath $resolvedTempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) -Recurse -Force
    }
}
