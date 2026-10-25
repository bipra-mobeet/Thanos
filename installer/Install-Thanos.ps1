param([string]$SourcePath)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Thanos.App.exe')) {
        $PSScriptRoot
    } else {
        Join-Path $PSScriptRoot '..\src\Thanos.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish'
    }
}
$source = (Resolve-Path -LiteralPath $SourcePath).Path
$programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$installPath = [IO.Path]::GetFullPath((Join-Path $programsRoot 'Thanos'))
if (-not $installPath.StartsWith($programsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unsafe installation path.'
}

if (-not [string]::Equals($source, $installPath, [StringComparison]::OrdinalIgnoreCase)) {
    Get-Process -Name 'Thanos.App' -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path -LiteralPath $installPath) {
        Remove-Item -LiteralPath $installPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $installPath -Force | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $installPath -Recurse -Force
}

$uninstallerPath = Join-Path $installPath 'Uninstall-Thanos.ps1'
if (-not (Test-Path -LiteralPath $uninstallerPath)) {
    throw 'The release package is missing Uninstall-Thanos.ps1.'
}

$oldShortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Thanos.lnk'
$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Thanos by Bipra.lnk'
Remove-Item -LiteralPath $oldShortcutPath -Force -ErrorAction SilentlyContinue
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installPath 'Thanos.App.exe'
$shortcut.WorkingDirectory = $installPath
$shortcut.Description = 'Thanos by Bipra - find and remove similar-looking photos'
$shortcut.Save()

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Thanos'
$estimatedSize = [int][Math]::Ceiling(
    ((Get-ChildItem -LiteralPath $installPath -Recurse -File | Measure-Object Length -Sum).Sum) / 1KB)

New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'Thanos by Bipra' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '1.0.0' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'Bipra' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installPath -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value (Join-Path $installPath 'Thanos.App.exe') -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallerPath`"" -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name QuietUninstallString -Value "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$uninstallerPath`" -Quiet" -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name EstimatedSize -Value $estimatedSize -PropertyType DWord -Force | Out-Null

Write-Output "Thanos by Bipra installed at $installPath"
