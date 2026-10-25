param([switch]$Quiet)

$ErrorActionPreference = 'Stop'
$programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$installPath = [IO.Path]::GetFullPath((Join-Path $programsRoot 'Thanos'))
if (-not $installPath.StartsWith($programsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unsafe installation path.'
}

Get-Process -Name 'Thanos.App' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -LiteralPath (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Thanos.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Thanos by Bipra.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Thanos' -Recurse -Force -ErrorAction SilentlyContinue

Set-Location -LiteralPath $env:TEMP
if (Test-Path -LiteralPath $installPath) {
    Remove-Item -LiteralPath $installPath -Recurse -Force
}

if (-not $Quiet) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show('Thanos by Bipra was uninstalled. Your local scan database was left in place.', 'Thanos by Bipra') | Out-Null
}
