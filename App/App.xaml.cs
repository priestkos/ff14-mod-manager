using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FF14ModManager;

public partial class App : Application
{
    static readonly string ThemeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FF14ModManager", "theme.txt");

    /// <summary>"System" (follow Windows), "Dark" or "Light"; anything unreadable falls back to System.</summary>
    public static string SavedTheme
    {
        get
        {
            try { var t = File.ReadAllText(ThemeFile).Trim(); return t is "Dark" or "Light" ? t : "System"; }
            catch (IOException) { return "System"; }
        }
        set { Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile)!); File.WriteAllText(ThemeFile, value); }
    }

#pragma warning disable WPF0001   // ThemeMode (the built-in Fluent light/dark theme) is marked experimental in .NET 10
    public static void ApplyTheme(string name) => Current.ThemeMode = name switch
    {
        "Dark" => ThemeMode.Dark,
        "Light" => ThemeMode.Light,
        _ => ThemeMode.System,
    };
#pragma warning restore WPF0001

    // Before StartupUri creates the window, so its BasedOn styles resolve against the Fluent theme.
    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyTheme(SavedTheme);
        base.OnStartup(e);
    }

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
