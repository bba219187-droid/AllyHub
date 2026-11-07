using System.Windows;
using System.Windows.Media;

namespace AllyHub;

public partial class App : Application
{
    public static readonly string[] Themes = { "#FF1F3D", "#00D1FF", "#A855F7", "#22E07A", "#FF8A1F" };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Updater.Cleanup();
        var cfg = Config.Load();
        SetAccent(cfg.Accent);
        Sfx.On = cfg.Sounds;
        new Splash(cfg).Show();
    }

    /// Muda a cor de destaque em toda a app (os estilos usam DynamicResource).
    public static void SetAccent(string hex)
    {
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { c = Color.FromRgb(0xFF, 0x1F, 0x3D); }
        Current.Resources["Accent"] = c;
        Current.Resources["AccentBrush"] = new SolidColorBrush(c);
    }
}
