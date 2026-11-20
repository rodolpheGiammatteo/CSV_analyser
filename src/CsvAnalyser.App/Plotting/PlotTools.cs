using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ScottPlot;
using ScottPlot.Interactivity.UserActionResponses;
using ScottPlot.WPF;
using SkiaSharp;

namespace CsvAnalyser.App.Plotting;

/// <summary>What the main plot and the spectrum window share: mouse buttons and image export.</summary>
public static class PlotTools
{
    /// <summary>Left drag zooms on a rectangle and wheel-button drag pans (or the reverse).</summary>
    public static void ApplyMouseButtons(WpfPlot view, bool leftZooms)
    {
        var responses = view.UserInputProcessor.UserActionResponses;
        responses.RemoveAll(r => r is MouseDragPan or MouseDragZoomRectangle);
        var left = ScottPlot.Interactivity.StandardMouseButtons.Left;
        var middle = ScottPlot.Interactivity.StandardMouseButtons.Middle;
        responses.Add(new MouseDragZoomRectangle(leftZooms ? left : middle));
        responses.Add(new MouseDragPan(leftZooms ? middle : left));
    }

    /// <summary>
    /// One plot as an image, <paramref name="scale"/> times its size on screen. The drawing is scaled as a whole,
    /// so it looks the same as on screen, only sharper.
    /// </summary>
    public static byte[] RenderPlot(Plot plot, PixelSize screenSize, double scale, SKEncodedImageFormat format)
    {
        int w = Math.Max(1, (int)screenSize.Width), h = Math.Max(1, (int)screenSize.Height);
        return RenderScaled(w, h, scale, format, canvas => plot.Render(canvas, w, h));
    }

    /// <summary>All the stacked graphs as one image, <paramref name="scale"/> times their size on screen.</summary>
    public static byte[] RenderMultiplot(IMultiplot multiplot, PixelSize screenSize, double scale, SKEncodedImageFormat format)
    {
        int w = Math.Max(1, (int)screenSize.Width), h = Math.Max(1, (int)screenSize.Height);
        return RenderScaled(w, h, scale, format, canvas => multiplot.Render(canvas, new PixelRect(0, w, h, 0)));
    }

    /// <summary>
    /// Draws at the on-screen size on a canvas scaled by <paramref name="scale"/> (Plot.ScaleFactor would also
    /// scale the position of each stacked graph and push the lower ones out of the image).
    /// </summary>
    private static byte[] RenderScaled(int width, int height, double scale, SKEncodedImageFormat format, Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(
            Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale))));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.Scale((float)scale);
        draw(surface.Canvas);
        return Encode(surface, format);
    }
    private static byte[] Encode(SKSurface surface, SKEncodedImageFormat format)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    public static void CopyToClipboard(byte[] png)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(png);
        bitmap.EndInit();
        bitmap.Freeze();
        Clipboard.SetImage(bitmap);
    }

    /// <summary>Asks for a file (PNG or JPEG) and writes the image rendered in that format.</summary>
    public static void SaveWithDialog(Func<SKEncodedImageFormat, byte[]> render, string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = L.T("Enregistrer l'image"),
            Filter = L.T("Image PNG (*.png)|*.png|Image JPEG (*.jpg)|*.jpg"),
            FileName = suggestedName,
        };
        if (dialog.ShowDialog() != true)
            return;
        var format = dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || dialog.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? SKEncodedImageFormat.Jpeg
            : SKEncodedImageFormat.Png;
        try
        {
            File.WriteAllBytes(dialog.FileName, render(format));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, L.T("Enregistrement impossible"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
