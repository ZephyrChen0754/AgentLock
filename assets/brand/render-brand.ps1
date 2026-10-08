param([string] $DotNetPath)
$ErrorActionPreference = 'Stop'
$brandWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $DotNetPath) {
    $brandBundledSdk = Join-Path $brandWorkspace '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $brandBundledSdk -PathType Leaf) { $DotNetPath = $brandBundledSdk }
    else {
        $brandGlobalSdk = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($brandGlobalSdk) { $DotNetPath = $brandGlobalSdk.Source }
    }
}
if (-not $DotNetPath -or -not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) { throw 'Install a .NET 10 SDK or provide -DotNetPath.' }
$brandRenderRoot = Join-Path $env:TEMP ('AgentLock-brand-render-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $brandRenderRoot | Out-Null
$brandSourcePath = [Security.SecurityElement]::Escape((Join-Path $brandWorkspace 'src\AgentLock\Ui\BrandMark.cs'))
$themeSourcePath = [Security.SecurityElement]::Escape((Join-Path $brandWorkspace 'src\AgentLock\Ui\VisualTheme.cs'))
$brandRenderProject = Join-Path $brandRenderRoot 'BrandRender.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWindowsForms>true</UseWindowsForms><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <ItemGroup><Compile Include="Program.cs"/><Compile Include="$brandSourcePath" Link="BrandMark.cs"/><Compile Include="$themeSourcePath" Link="VisualTheme.cs"/></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $brandRenderProject -Encoding utf8
@'
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AgentLock.Ui;

string output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
using (var mark = BrandMark.CreateBitmap(512)) mark.Save(Path.Combine(output, "agentlock-512.png"), ImageFormat.Png);
using (var mark = BrandMark.CreateBitmap(512, tile: false)) mark.Save(Path.Combine(output, "agentlock-symbol.png"), ImageFormat.Png);

int[] sizes = [16, 24, 32, 48, 64, 128, 256];
var entries = new List<byte[]>();
foreach (int size in sizes)
{
    using var mark = BrandMark.CreateBitmap(size);
    entries.Add(ToIconDib(mark));
}
string iconPath = Path.Combine(output, "agentlock.ico");
using (var writer = new BinaryWriter(File.Create(iconPath)))
{
    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
    uint offset = (uint)(6 + 16 * sizes.Length);
    for (int index = 0; index < sizes.Length; index++)
    {
        writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
        writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write((uint)entries[index].Length); writer.Write(offset);
        offset += (uint)entries[index].Length;
    }
    foreach (byte[] entry in entries) writer.Write(entry);
}
for (int index = 0; index < sizes.Length; index++)
{
    int size = sizes[index];
    // Icon.Initialize compares byte directory dimensions literally, so a bundled
    // 256px entry (specified as zero per ICO) can lose to 128px. Validate each
    // real frame in a single-entry container instead of misdiagnosing its pixels.
    using var frame = new MemoryStream();
    using (var writer = new BinaryWriter(frame, Encoding.UTF8, leaveOpen: true))
    {
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write((uint)entries[index].Length); writer.Write((uint)22); writer.Write(entries[index]);
    }
    frame.Position = 0;
    using var icon = new Icon(frame, new Size(size, size));
    using var decoded = icon.ToBitmap();
    if (icon.Width != size || icon.Height != size || decoded.Width != size || decoded.Height != size)
        throw new InvalidDataException($"ICO frame {size} could not be loaded.");
    if (size < 256)
    {
        using var selected = new Icon(iconPath, new Size(size, size));
        if (selected.Width != size) throw new InvalidDataException($"Bundled ICO size {size} could not be selected.");
    }
}

// Wordmark PNGs remain transparent; SVG masters use the same palette and geometry.
foreach (bool dark in new[] { false, true })
{
    using var wordmark = new Bitmap(1280, 320, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(wordmark);
    graphics.Clear(Color.Transparent);
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    if (dark)
    {
        using var mark = BrandMark.CreateBitmap(300, tile: false);
        RecolorInk(mark, Color.White);
        graphics.DrawImageUnscaled(mark, 0, 10);
    }
    else BrandMark.Draw(graphics, new RectangleF(0, 10, 300, 300), tile: false);
    using var font = new Font("Segoe UI Semibold", 142, FontStyle.Regular, GraphicsUnit.Pixel);
    using var outline = new GraphicsPath();
    outline.AddString("AgentLock", font.FontFamily, (int)font.Style, 142,
        new PointF(288, 77), StringFormat.GenericTypographic);
    using var ink = new SolidBrush(dark ? Color.White : VisualTheme.Ink);
    graphics.FillPath(ink, outline);
    wordmark.Save(Path.Combine(output, dark ? "agentlock-wordmark-dark.png" : "agentlock-wordmark-light.png"), ImageFormat.Png);
    string foreground = dark ? "#FFFFFF" : "#173C36";
    string outlineData = SvgPathData(outline);
    string svg = $"""
    <svg xmlns="http://www.w3.org/2000/svg" width="1280" height="320" viewBox="0 0 1280 320" role="img" aria-labelledby="title desc">
      <title id="title">AgentLock</title>
      <desc id="desc">Outlined AgentLock wordmark, transparent background for {(dark ? "dark" : "light")} surfaces. No external fonts required.</desc>
      <g transform="translate(0 10) scale(.5859375)">
        <path d="M128 376 198 190C210 152 232 130 256 130S302 152 314 190L384 376" fill="none" stroke="{foreground}" stroke-width="44" stroke-linecap="round" stroke-linejoin="round"/>
        <path d="M236 238 320 288 236 338Z" fill="#67D8B4"/>
      </g>
      <path d="{outlineData}" fill="{foreground}"/>
    </svg>
    """;
    File.WriteAllText(Path.Combine(output, dark ? "agentlock-wordmark-dark.svg" : "agentlock-wordmark-light.svg"), svg);
}

using (var preview = new Bitmap(1200, 720, PixelFormat.Format32bppArgb))
using (var graphics = Graphics.FromImage(preview))
{
    graphics.Clear(VisualTheme.Canvas);
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    BrandMark.Draw(graphics, new RectangleF(60, 55, 250, 250));
    using var title = VisualTheme.UiFont(31, FontStyle.Bold);
    using var detail = VisualTheme.UiFont(12);
    using var ink = new SolidBrush(VisualTheme.Ink);
    using var muted = new SolidBrush(VisualTheme.Muted);
    graphics.DrawString("AgentLock", title, ink, 350, 112);
    graphics.DrawString("人离开，Agent 接着工作", detail, muted, 354, 180);
    graphics.DrawString("Windows app icon · SVG master · Light / dark wordmarks", detail, muted, 60, 330);
    int position = 68;
    foreach (int size in sizes)
    {
        using var mark = BrandMark.CreateBitmap(size);
        graphics.DrawImageUnscaled(mark, position, 400);
        graphics.DrawString($"{size}px", detail, muted, position, 420 + size);
        position += Math.Max(86, size + 38);
    }
    preview.Save(Path.Combine(output, "brand-preview.png"), ImageFormat.Png);
}
Console.WriteLine($"Rendered PNGs and seven-entry ICO to {output}");
Console.WriteLine("Icon entries loaded successfully: " + string.Join(", ", sizes));

static byte[] ToIconDib(Bitmap bitmap)
{
    int size = bitmap.Width, maskStride = ((size + 31) / 32) * 4;
    var rectangle = new Rectangle(0, 0, size, size);
    var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    byte[] pixels = new byte[Math.Abs(data.Stride) * size];
    try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
    finally { bitmap.UnlockBits(data); }
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
    writer.Write((uint)40); writer.Write(size); writer.Write(size * 2);
    writer.Write((ushort)1); writer.Write((ushort)32); writer.Write((uint)0);
    writer.Write((uint)(size * size * 4));
    writer.Write(0); writer.Write(0); writer.Write((uint)0); writer.Write((uint)0);
    for (int y = size - 1; y >= 0; y--) writer.Write(pixels, y * data.Stride, size * 4);
    for (int y = size - 1; y >= 0; y--)
    {
        byte[] mask = new byte[maskStride];
        for (int x = 0; x < size; x++)
            if (pixels[y * data.Stride + x * 4 + 3] == 0) mask[x / 8] |= (byte)(0x80 >> (x % 8));
        writer.Write(mask);
    }
    writer.Flush(); return stream.ToArray();
}

static void RecolorInk(Bitmap bitmap, Color replacement)
{
    // Rendered symbol uses two solid colors; alpha is preserved at antialiased edges.
    for (int y = 0; y < bitmap.Height; y++)
    for (int x = 0; x < bitmap.Width; x++)
    {
        var color = bitmap.GetPixel(x, y);
        if (color.A != 0 && color.G < 150) bitmap.SetPixel(x, y, Color.FromArgb(color.A, replacement));
    }
}

static string SvgPathData(GraphicsPath path)
{
    var result = new StringBuilder();
    PointF[] points = path.PathPoints;
    byte[] types = path.PathTypes;
    string Point(int index) => points[index].X.ToString("0.###", CultureInfo.InvariantCulture)
        + " " + points[index].Y.ToString("0.###", CultureInfo.InvariantCulture);
    for (int index = 0; index < points.Length; index++)
    {
        var kind = (PathPointType)(types[index] & (byte)PathPointType.PathTypeMask);
        if (kind == PathPointType.Start) result.Append('M').Append(Point(index));
        else if (kind == PathPointType.Line) result.Append('L').Append(Point(index));
        else if (kind == PathPointType.Bezier3)
        {
            result.Append('C').Append(Point(index)).Append(' ').Append(Point(index + 1)).Append(' ').Append(Point(index + 2));
            index += 2;
        }
        else throw new InvalidDataException("Unsupported outline path point.");
        if ((types[index] & (byte)PathPointType.CloseSubpath) != 0) result.Append('Z');
    }
    return result.ToString();
}
'@ | Set-Content -LiteralPath (Join-Path $brandRenderRoot 'Program.cs') -Encoding utf8
& $DotNetPath run --project $brandRenderProject --configuration Release -- $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'Brand rendering or ICO verification failed.' }
Write-Output ('Renderer project retained for reproducibility: ' + $brandRenderRoot)
