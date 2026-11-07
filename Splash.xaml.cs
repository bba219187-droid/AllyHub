using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AllyHub;

public partial class Splash : Window
{
    readonly Config _cfg;

    public Splash(Config cfg)
    {
        _cfg = cfg;
        InitializeComponent();
        Ver.Text = "v" + Updater.CurrentText;

        var custom = Path.Combine(AppContext.BaseDirectory, "splash.png");
        if (File.Exists(custom))
        {
            Custom.Source = new BitmapImage(new Uri(custom));
            Custom.Visibility = Visibility.Visible;
            Emblem.Visibility = Visibility.Collapsed;
        }
        Loaded += async (_, _) => await Run();
    }

    static DoubleAnimation Anim(double to, double ms, double delay = 0) =>
        new(to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

    void Animate()
    {
        BeginAnimation(OpacityProperty, Anim(1, 300));
        Hex.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, Anim(0, 900, 150));
        Letter.BeginAnimation(OpacityProperty, Anim(1, 400, 800));
        Brand.BeginAnimation(OpacityProperty, Anim(1, 500, 1000));

        var pulse = new DoubleAnimation(0.85, 1.1, TimeSpan.FromSeconds(1.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        BarX.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-120, 340, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    async Task Run()
    {
        Animate();
        var minTime = Task.Delay(2400);
        var scan = Task.Run(() => Library.Scan(_cfg));

        try
        {
            Msg.Text = "A procurar atualizações…";
            var updated = await Updater.CheckAndApply();
            if (updated != null)
            {
                Msg.Text = $"Atualizado para v{updated} — a reiniciar…";
                await Task.Delay(1200);
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
                Application.Current.Shutdown();
                return;
            }
        }
        catch { /* sem internet ou GitHub em baixo: segue com a versao atual */ }

        Msg.Text = "A carregar a biblioteca…";
        var games = await scan;
        await minTime;

        var main = new MainWindow(_cfg, games);
        main.Show();
        main.Activate();
        var fade = Anim(0, 450);
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
