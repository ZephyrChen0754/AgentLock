param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'src\AgentLock\AgentLock.csproj'
$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw)
$packageVersion = [string]$projectXml.Project.PropertyGroup.Version
if ($packageVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'The package version must contain three numeric parts.' }
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
if (!$SkipTests) {
    & $dotnetPath run --project (Join-Path $projectRoot 'tests\AgentLock.Tests\AgentLock.Tests.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
$publishPath = Join-Path $projectRoot 'release\AgentLock'
& $dotnetPath publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $publishPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination (Join-Path $publishPath 'install.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination (Join-Path $publishPath 'uninstall.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.cmd') -Destination (Join-Path $publishPath '安装到本机.cmd') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.cmd') -Destination (Join-Path $publishPath '卸载.cmd') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\安装与使用.md') -Destination (Join-Path $publishPath '使用说明.md') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\兼容性记录.md') -Destination (Join-Path $publishPath '兼容性记录.md') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\验收清单.md') -Destination (Join-Path $publishPath '验收清单.md') -Force
$releaseNotes = Join-Path $projectRoot ('docs\发布说明-' + $packageVersion + '.md')
if (Test-Path -LiteralPath $releaseNotes) { Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $publishPath '发布说明.md') -Force }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\Agent实测页面.html') -Destination (Join-Path $publishPath 'Agent实测页面.html') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\给Agent的实测任务.txt') -Destination (Join-Path $publishPath '给Agent的实测任务.txt') -Force
$exeHash = Get-FileHash -LiteralPath (Join-Path $publishPath 'AgentLock.exe') -Algorithm SHA256
$qaRecord = Join-Path $projectRoot 'artifacts\qa\交付验收记录.md'
$publishedQaRecord = Join-Path $publishPath '交付验收记录.md'
if ((Test-Path -LiteralPath $qaRecord) -and (Get-Content -LiteralPath $qaRecord -Raw).Contains($exeHash.Hash)) { Copy-Item -LiteralPath $qaRecord -Destination $publishedQaRecord -Force }
elseif (Test-Path -LiteralPath $publishedQaRecord) { Remove-Item -LiteralPath $publishedQaRecord -Force }
[IO.File]::WriteAllText((Join-Path $publishPath 'SHA256.txt'), $exeHash.Hash + '  AgentLock.exe' + [Environment]::NewLine)
$packagePath = Join-Path $projectRoot ('release\AgentLock-' + $packageVersion + '-win-x64.zip')
Compress-Archive -LiteralPath $publishPath -DestinationPath $packagePath -Force
$exeHash | Format-List
Write-Output $packagePath
