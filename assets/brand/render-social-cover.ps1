param([string] $DotNetPath)
$ErrorActionPreference = 'Stop'
$coverWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $DotNetPath) {
    $coverBundledSdk = Join-Path $coverWorkspace '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $coverBundledSdk -PathType Leaf) { $DotNetPath = $coverBundledSdk }
    else {
        $coverGlobalSdk = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($coverGlobalSdk) { $DotNetPath = $coverGlobalSdk.Source }
    }
}
if (-not $DotNetPath -or -not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) { throw 'Install a .NET 10 SDK or provide -DotNetPath.' }
$coverRenderRoot = Join-Path $env:TEMP ('AgentLock-social-render-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $coverRenderRoot | Out-Null
$coverBrandSource = [Security.SecurityElement]::Escape((Join-Path $coverWorkspace 'src\AgentLock\Ui\BrandMark.cs'))
$coverThemeSource = [Security.SecurityElement]::Escape((Join-Path $coverWorkspace 'src\AgentLock\Ui\VisualTheme.cs'))
$coverRenderProject = Join-Path $coverRenderRoot 'SocialRender.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWindowsForms>true</UseWindowsForms><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <ItemGroup><Compile Include="Program.cs"/><Compile Include="$coverBrandSource" Link="BrandMark.cs"/><Compile Include="$coverThemeSource" Link="VisualTheme.cs"/></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $coverRenderProject -Encoding utf8
@'
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using AgentLock.Ui;

string output = Path.GetFullPath(args[0]);
using var bitmap = new Bitmap(1200, 630, PixelFormat.Format32bppArgb);
using var graphics = Graphics.FromImage(bitmap);
graphics.Clear(VisualTheme.Canvas);
graphics.SmoothingMode = SmoothingMode.AntiAlias;
graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
using var accent = new SolidBrush(VisualTheme.Accent);
using var footer = new SolidBrush(VisualTheme.AccentSoft);
using var edge = new Pen(VisualTheme.Border);
using (var rule = VisualTheme.RoundedRectangle(new RectangleF(74, 151, 48, 5), 2.5F)) graphics.FillPath(accent, rule);
graphics.DrawLine(edge, 74, 490, 704, 490);
graphics.FillRectangle(footer, 0, 615, 1200, 15);
BrandMark.Draw(graphics, new RectangleF(832, 184, 288, 288));
DrawText("AgentLock", "Segoe UI Semibold", 32, FontStyle.Regular, 72, 109, VisualTheme.Ink);
DrawText("人离开，", "Microsoft YaHei UI", 62, FontStyle.Bold, 72, 246, VisualTheme.Ink);
DrawText("Agent 接着工作", "Microsoft YaHei UI", 62, FontStyle.Bold, 72, 329, VisualTheme.Ink);
DrawText("Windows 同桌面守护", "Microsoft YaHei UI", 25, FontStyle.Regular, 74, 411, VisualTheme.Muted);
DrawText("F12 密码解锁 · 本机运行 · 无需虚拟机", "Microsoft YaHei UI", 20, FontStyle.Regular, 74, 548, VisualTheme.Muted);
bitmap.Save(output, ImageFormat.Png);
Console.WriteLine($"Rendered social cover: {output} (1200x630)");

void DrawText(string text, string family, float size, FontStyle style, float x, float baseline, Color color)
{
    using var font = new Font(family, size, style, GraphicsUnit.Pixel);
    float ascent = size * font.FontFamily.GetCellAscent(style) / font.FontFamily.GetEmHeight(style);
    using var outline = new GraphicsPath();
    outline.AddString(text, font.FontFamily, (int)style, size,
        new PointF(x, baseline - ascent), StringFormat.GenericTypographic);
    using var ink = new SolidBrush(color);
    graphics.FillPath(ink, outline);
}
'@ | Set-Content -LiteralPath (Join-Path $coverRenderRoot 'Program.cs') -Encoding utf8
& $DotNetPath run --project $coverRenderProject --configuration Release -- (Join-Path $PSScriptRoot 'agentlock-social-cover.png')
if ($LASTEXITCODE -ne 0) { throw 'Social cover rendering failed.' }
Write-Output ('Renderer project retained for reproducibility: ' + $coverRenderRoot)
