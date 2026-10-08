param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$sourceWorkspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectXml = [xml](Get-Content -LiteralPath (Join-Path $sourceWorkspace 'src\AgentLock\AgentLock.csproj') -Raw)
$sourceVersion = [string]$projectXml.Project.PropertyGroup.Version
if ($sourceVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid source package version.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $sourceWorkspace 'release' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$packageName = 'AgentLock-' + $sourceVersion + '-source'
$stageParent = Join-Path $sourceWorkspace ('artifacts\source-export\' + [guid]::NewGuid().ToString('N'))
$stagePath = Join-Path $stageParent $packageName
New-Item -ItemType Directory -Path $stagePath -Force | Out-Null

# Explicit public inputs: never copy the workspace, user profile, or QA tree.
$publicInputs = @(
    'README.md', 'CONTRIBUTING.md', '.gitignore', 'NuGet.Config',
    'src', 'tests', 'scripts', 'assets',
    'docs\安装与使用.md', 'docs\兼容性记录.md', 'docs\验收清单.md',
    'docs\视觉设计.md', ('docs\发布说明-' + $sourceVersion + '.md'),
    'docs\Agent实测页面.html', 'docs\给Agent的实测任务.txt',
    'docs\images\main-window.png', 'docs\images\unlock-window.png',
    'docs\images\password-recovery-window.png'
)
foreach ($optionalLicenseFile in @('LICENSE', 'LICENSE.md', 'LICENSE.txt', 'NOTICE')) {
    if (Test-Path -LiteralPath (Join-Path $sourceWorkspace $optionalLicenseFile) -PathType Leaf) { $publicInputs += $optionalLicenseFile }
}
$sourceFiles = New-Object 'System.Collections.Generic.List[string]'
foreach ($inputPath in $publicInputs) {
    $publicPath = Join-Path $sourceWorkspace $inputPath
    if (!(Test-Path -LiteralPath $publicPath)) { throw ('Missing public input: ' + $inputPath) }
    $inputItem = Get-Item -LiteralPath $publicPath
    if (($inputItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Public source inputs cannot be reparse points.' }
    $items = if ($inputItem.PSIsContainer) { @(Get-ChildItem -LiteralPath $publicPath -File -Recurse) } else { @($inputItem) }
    foreach ($item in $items) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Source files cannot be reparse points.' }
        $relativePath = $item.FullName.Substring($sourceWorkspace.Length + 1)
        if ($relativePath -match '(^|[\\/])(bin|obj|\.tools|artifacts|release|\.git)([\\/]|$)') { continue }
        if ($item.Name -match '^(credential\.json|\.env($|\.))' -or $item.Extension -match '^\.(log|tmp|ready|user|suo)$') { continue }
        $targetPath = Join-Path $stagePath $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
        Copy-Item -LiteralPath $item.FullName -Destination $targetPath
        $sourceFiles.Add($relativePath)
    }
}
$manifest = foreach ($relativePath in ($sourceFiles | Sort-Object)) {
    (Get-FileHash -LiteralPath (Join-Path $stagePath $relativePath) -Algorithm SHA256).Hash + '  ' + $relativePath.Replace('\', '/')
}
[IO.File]::WriteAllLines((Join-Path $stagePath 'SOURCE-SHA256.txt'), [string[]]$manifest)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $OutputDirectory ($packageName + '.zip')
$temporaryZip = Join-Path $stageParent ($packageName + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($stagePath, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $true)
Copy-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
$exportReport = [PSCustomObject]@{
    version = $sourceVersion
    packagePath = $zipPath
    stagePath = $stagePath
    publicFileCount = $sourceFiles.Count
    packageSHA256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    licenseSelected = @(Get-ChildItem -LiteralPath $stagePath -File | Where-Object { $_.Name -in @('LICENSE', 'LICENSE.md', 'LICENSE.txt') }).Count -gt 0
}
$exportReport | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stageParent 'source-export-report.json') -Encoding utf8
$exportReport | ConvertTo-Json
