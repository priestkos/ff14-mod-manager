using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FF14ModManager;

public partial class App : Application
{
    // Show the error and keep running: every action is read-only or self-contained, so continuing is safe.
    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "FF14 Mod Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}

/// <summary>Path → small bitmap, loaded fully so the PNG is not locked (shots can still be moved/attached).</summary>
public sealed class ThumbConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not string path || !File.Exists(path)) return null;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        img.DecodePixelWidth = p is string w ? int.Parse(w, CultureInfo.InvariantCulture) : 320;
        img.UriSource = new Uri(path);
        img.EndInit();
        img.Freeze();
        return img;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
