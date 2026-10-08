$ErrorActionPreference = 'Stop'
$installPath = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\AgentLock'))
$expectedPath = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\AgentLock'))
if ($installPath -ne $expectedPath -or [IO.Path]::GetFileName($installPath) -ne 'AgentLock') { throw 'Unexpected installation path; refusing removal.' }
$exePath = Join-Path $installPath 'AgentLock.exe'
$running = @(Get-Process -Name AgentLock -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $exePath } catch { $false } })
if ($running.Count -gt 0) { throw 'Unlock protection and exit AgentLock from the tray menu before uninstalling.' }
if (Test-Path -LiteralPath $installPath) {
    $installFolder = Get-Item -LiteralPath $installPath -Force
    if (($installFolder.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Refusing to remove a redirected installation directory.' }
    if (@(Get-ChildItem -LiteralPath $installPath -Force -Recurse | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0) { throw 'Refusing removal because the installation includes redirected paths.' }
}
$shell = New-Object -ComObject WScript.Shell
foreach ($shortcutPath in @((Join-Path ([Environment]::GetFolderPath('Programs')) 'AgentLock.lnk'), (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'AgentLock.lnk'))) {
    if (Test-Path -LiteralPath $shortcutPath) {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        if ($shortcut.TargetPath -eq $exePath) { Remove-Item -LiteralPath $shortcutPath -Force }
    }
}
if (Test-Path -LiteralPath $installPath) { Remove-Item -LiteralPath $installPath -Recurse -Force }
Write-Output 'Uninstalled. Your protection password and logs remain in LocalAppData\AgentLock.'
