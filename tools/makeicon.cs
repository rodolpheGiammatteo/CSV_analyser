#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property PublishTrimmed=false
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Renders the "Oscillo" icon (same drawing as Assets/app.svg) at several sizes and packs them into a PNG-based .ico.
var outDir = args[0];
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

var thread = new Thread(() =>
{
    var pngs = sizes.Select(s => (Size: s, Data: Render(s))).ToList();
    WriteIco(Path.Combine(outDir, "app.ico"), pngs);
    File.WriteAllBytes(Path.Combine(outDir, "app-64.png"), Render(64));
    File.WriteAllBytes(Path.Combine(outDir, "preview-256.png"), Render(256));
    Console.WriteLine($"OK: {string.Join(", ", sizes)} px");
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static SolidColorBrush Hex(string hex)
{
    var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    b.Freeze();
    return b;
}

static Pen RoundPen(string hex, double width, DashStyle? dash = null) => new(Hex(hex), width)
{
    StartLineCap = PenLineCap.Round,
    EndLineCap = PenLineCap.Round,
    LineJoin = PenLineJoin.Round,
    DashStyle = dash ?? DashStyles.Solid,
};

static byte[] Render(int size)
{
    bool showCursor = size >= 40;
    bool showGrid = size >= 64;
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.PushTransform(new ScaleTransform(size / 100.0, size / 100.0));
        dc.DrawRoundedRectangle(Hex("#0F172A"), null, new Rect(0, 0, 100, 100), 22, 22);

        if (showGrid)
        {
            var grid = new Pen(Hex("#1E293B"), 1.5);
            foreach (var y in new[] { 35.0, 50, 65 })
                dc.DrawLine(grid, new Point(10, y), new Point(90, y));
        }

        var wave = Geometry.Parse("M12 62 L26 62 L32 34 L40 70 L48 44 L56 56 L64 30 L72 60 L88 60");
        double waveWidth = size <= 24 ? 10 : size <= 32 ? 8 : size <= 48 ? 6.5 : 5;
        dc.DrawGeometry(null, RoundPen("#22D3EE", waveWidth), wave);

        if (showCursor)
        {
            // WPF dash lengths are expressed in multiples of the pen width (2.5): 4px on / 4px off.
            var cursor = new Pen(Hex("#F8FAFC"), 2.5) { DashStyle = new DashStyle([1.6, 1.6], 0) };
            dc.DrawLine(cursor, new Point(64, 16), new Point(64, 84));
            dc.DrawEllipse(Hex("#F8FAFC"), null, new Point(64, 30), 5, 5);
        }
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var ms = new MemoryStream();
    encoder.Save(ms);
    return ms.ToArray();
}

static void WriteIco(string path, List<(int Size, byte[] Data)> images)
{
    using var w = new BinaryWriter(File.Create(path));
    w.Write((short)0);
    w.Write((short)1);
    w.Write((short)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, data) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(data.Length);
        w.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in images)
        w.Write(data);
}
