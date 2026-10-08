param([switch]$Launch)
$ErrorActionPreference = 'Stop'
$installPath = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\AgentLock'))
$sourcePath = [IO.Path]::GetFullPath($PSScriptRoot)
$exePath = Join-Path $installPath 'AgentLock.exe'
if (!(Test-Path -LiteralPath (Join-Path $sourcePath 'AgentLock.exe'))) { throw 'AgentLock.exe is missing from this package.' }
$running = @(Get-Process -Name AgentLock -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $exePath } catch { $false } })
if ($running.Count -gt 0 -and $sourcePath -ne $installPath) { throw 'Exit AgentLock from its tray menu before updating. Unlock active protection first.' }
New-Item -ItemType Directory -Path $installPath -Force | Out-Null
if ($sourcePath -ne $installPath) {
    Get-ChildItem -LiteralPath $sourcePath -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $installPath $_.Name) -Force }
}
$shell = New-Object -ComObject WScript.Shell
$startMenuPath = [Environment]::GetFolderPath('Programs')
$desktopPath = [Environment]::GetFolderPath('DesktopDirectory')
foreach ($shortcutPath in @((Join-Path $startMenuPath 'AgentLock.lnk'), (Join-Path $desktopPath 'AgentLock.lnk'))) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $installPath
    $shortcut.Description = 'AgentLock - local input protection while desktop agents work'
    $shortcut.Save()
}
Write-Output ('Installed: ' + $exePath)
Write-Output 'Use the AgentLock shortcut. Set your own protection password inside the app.'
if ($Launch) { Start-Process -FilePath $exePath -WindowStyle Normal }
