using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MesControlAgv.Wpf.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class Capture
{
    // Same native-surface composition as the existing WpfSmoke/TwinPreview capture.
    public static async Task Save(DigitalTwinView view,string path)
    {
        view.UpdateLayout();
        var host=(Grid)view.FindName("BrowserHost");
        var core=host.Children.OfType<WebView2>().Single().CoreWebView2;
        using var stream=new MemoryStream();await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,stream);stream.Position=0;
        var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();
        var visual=new DrawingVisual();
        using(var drawing=visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White,null,new Rect(view.RenderSize));
            drawing.DrawRectangle(new VisualBrush(view),null,new Rect(view.RenderSize));
            drawing.DrawImage(bitmap,new Rect(host.TranslatePoint(new Point(),view),host.RenderSize));
        }
        var dpi=VisualTreeHelper.GetDpi(view);
        var output=new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(view.ActualHeight*dpi.DpiScaleY),
            dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        output.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(output));
        using var file=File.Create(path);encoder.Save(file);
    }
}
