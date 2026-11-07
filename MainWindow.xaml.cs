using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AllyHub;

public partial class MainWindow : Window
{
    readonly Config _cfg = Config.Load();
    readonly Gamepad _pad = new();
    Button? _selected;
    int _watts;
    int? _activeWatts;   // TDP do ultimo jogo lancado (reaplicado periodicamente)

    public MainWindow(List<Game> games)
    {
        InitializeComponent();
        WattSlider.Minimum = _cfg.MinWatts;
        WattSlider.Maximum = _cfg.MaxWatts;
        GaugeTrack.Data = Arc(1);

        foreach (var (name, w) in _cfg.Profiles)
        {
            var b = new Button { Content = name, ToolTip = $"{w} W", Style = (Style)FindResource("Chip"), Tag = null };
            b.Click += (_, _) => SetWatts(w);
            ProfileBar.Children.Add(b);
        }

        SetWatts(_cfg.DefaultWatts);
        TdpState.Text = Tdp.Available ? "Aplicado ao abrir o jogo" : "⚠ ryzenadj.exe em falta — TDP desligado";
        Status.Text = "v" + Updater.CurrentText;
        UpdateAutoBtn(AutoStart.Enabled);
        ShowGames(games);

        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        poll.Tick += (_, _) => { if (IsActive) OnPad(_pad.Poll()); };
        poll.Start();

        // ponytail: o Armoury Crate repoe o TDP dele ao mudar de modo; reaplicamos a cada 15 s
        var reapply = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        reapply.Tick += (_, _) => { if (_activeWatts is int w) Tdp.Apply(w); };
        reapply.Start();

        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        clock.Tick += (_, _) => UpdateClock();
        clock.Start();
        UpdateClock();

        PreviewKeyDown += OnKey;
        Activated += (_, _) => _selected?.Focus();
    }

    // ---------- biblioteca ----------

    void ShowGames(List<Game> games)
    {
        Tiles.Children.Clear();
        _selected = null;
        foreach (var g in games) Tiles.Children.Add(MakeTile(g));
        if (games.Count == 0)
        {
            GameTitle.Text = "Nenhum jogo encontrado";
            GameInfo.Text = "Instala jogos na Steam, Epic ou GOG e carrega em B para atualizar.";
            StoreText.Text = "ALLYHUB";
            return;
        }
        Dispatcher.InvokeAsync(() => Select((Button)Tiles.Children[0]), DispatcherPriority.Loaded);
    }

    async void Reload()
    {
        Status.Text = "A atualizar…";
        ShowGames(await Task.Run(Library.Scan));
        Status.Text = "v" + Updater.CurrentText;
    }

    static BitmapImage? Load(string? path, int width)
    {
        if (path == null) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit(); b.UriSource = new Uri(path); b.DecodePixelWidth = width; b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    Button MakeTile(Game g)
    {
        var grid = new Grid();
        var cover = Load(g.Cover, 380);
        if (cover != null)
            grid.Children.Add(new Image { Source = cover, Stretch = Stretch.UniformToFill });
        else
        {
            // sem capa: cartao com gradiente e o nome
            grid.Background = new LinearGradientBrush(Color.FromRgb(0x2B, 0x10, 0x18), Color.FromRgb(0x14, 0x15, 0x1C), 60);
            grid.Children.Add(new TextBlock
            {
                Text = g.Name.ToUpperInvariant(), FontSize = 20, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14),
            });
        }
        // etiqueta da loja
        grid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0A, 0x0B, 0x0F)), Padding = new Thickness(7, 2, 7, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8),
            Child = new TextBlock { Text = g.Store.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Bold },
        });

        var tile = new Button { Content = grid, Tag = g, Style = (Style)FindResource("Tile") };
        // toque/rato: 1.o toque escolhe, 2.o toque joga
        tile.PreviewMouseDown += (_, e) => { if (_selected != tile) { Select(tile); e.Handled = true; } };
        tile.Click += (_, _) => Launch(tile);
        return tile;
    }

    void Select(Button tile)
    {
        if (_selected != null) Panel.SetZIndex(_selected, 0);
        _selected = tile;
        Panel.SetZIndex(tile, 1);
        tile.Focus();
        tile.BringIntoView(new Rect(-260, 0, tile.ActualWidth + 520, tile.ActualHeight));

        var g = (Game)tile.Tag;
        GameTitle.Text = g.Name.ToUpperInvariant();
        StoreText.Text = g.Store.ToUpperInvariant();
        var saved = _cfg.GameWatts.TryGetValue(g.Id, out var w);
        GameInfo.Text = saved ? $"Perfil guardado: {w} W" : "Ainda sem perfil — escolhe um e carrega em A";
        SetWatts(saved ? w : _cfg.DefaultWatts);

        // troca do fundo com fade
        HeroBg.Source = Load(g.Hero ?? g.Cover, 1280);
        HeroBg.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.55, TimeSpan.FromMilliseconds(350)));
        Info.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(250)));
    }

    void Launch(Button tile)
    {
        var g = (Game)tile.Tag;
        _cfg.GameWatts[g.Id] = _watts;
        _cfg.Save();

        if (Tdp.Available)
        {
            var err = Tdp.Apply(_watts);
            _activeWatts = err == null ? _watts : null;
            TdpState.Text = err == null ? $"● {g.Name} a {_watts} W" : $"⚠ {err}";
        }

        try
        {
            Process.Start(new ProcessStartInfo(g.Launch) { UseShellExecute = true, WorkingDirectory = g.WorkDir ?? "" });
            WindowState = WindowState.Minimized;
        }
        catch (Exception e) { Status.Text = $"⚠ Não foi possível abrir: {e.Message}"; }
    }

    // ---------- desempenho ----------

    void SetWatts(int w)
    {
        _watts = Math.Clamp(w, _cfg.MinWatts, _cfg.MaxWatts);
        WattSlider.Value = _watts;
        WattsBig.Text = _watts.ToString();
        var name = _cfg.Profiles.FirstOrDefault(p => p.Value == _watts).Key;
        ProfileName.Text = (name ?? "Personalizado").ToUpperInvariant();
        foreach (Button b in ProfileBar.Children)
            b.Tag = (string)b.Content == name ? "on" : null;
        GaugeArc.Data = Arc((double)(_watts - _cfg.MinWatts) / (_cfg.MaxWatts - _cfg.MinWatts));
    }

    /// Arco de 270 graus do medidor (f = 0..1)
    static Geometry Arc(double f)
    {
        const double r = 98, c = 110, start = 135, sweep = 270;
        f = Math.Clamp(f, 0.004, 0.9999);
        Point P(double deg) => new(c + r * Math.Cos(deg * Math.PI / 180), c + r * Math.Sin(deg * Math.PI / 180));
        var fig = new PathFigure { StartPoint = P(start) };
        fig.Segments.Add(new ArcSegment(P(start + sweep * f), new Size(r, r), 0, sweep * f > 180, SweepDirection.Clockwise, true));
        var geo = new PathGeometry(new[] { fig });
        geo.Freeze();
        return geo;
    }

    void Slider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if ((int)e.NewValue != _watts) SetWatts((int)e.NewValue);
    }

    void CycleProfile(int step)
    {
        var ws = _cfg.Profiles.Values.OrderBy(v => v).ToList();
        if (ws.Count == 0) return;
        SetWatts(step > 0 ? ws.FirstOrDefault(v => v > _watts, ws.First()) : ws.LastOrDefault(v => v < _watts, ws.Last()));
    }

    // ---------- barra de cima ----------

    void UpdateClock()
    {
        Clock.Text = DateTime.Now.ToString("HH:mm");
        var (pct, charging) = Battery.Get();
        BatteryText.Text = pct < 0 ? "" : $"{(charging ? "⚡" : "🔋")} {pct}%";
    }

    void UpdateAutoBtn(bool on)
    {
        AutoBtn.Content = on ? "▶ Arranca com o Windows: SIM" : "▶ Arranca com o Windows: NÃO";
        AutoBtn.Tag = on ? "on" : null;
    }

    async void AutoStart_Click(object s, RoutedEventArgs e)
    {
        AutoBtn.Content = "…";
        var target = !AutoStart.Enabled;
        var now = await Task.Run(() => { AutoStart.Set(target); return AutoStart.Enabled; });
        UpdateAutoBtn(now);
    }

    void Exit_Click(object s, RoutedEventArgs e) => Close();

    // ---------- comando e teclado ----------

    void Move(FocusNavigationDirection dir)
    {
        if (_selected == null) { if (Tiles.Children.Count > 0 && Tiles.Children[0] is Button f) Select(f); return; }
        if (_selected.PredictFocus(dir) is Button next && next.Tag is Game) Select(next);
    }

    void OnPad(Btn b)
    {
        if (b == 0) return;
        if (b.HasFlag(Btn.Left)) Move(FocusNavigationDirection.Left);
        if (b.HasFlag(Btn.Right)) Move(FocusNavigationDirection.Right);
        if (b.HasFlag(Btn.Up)) SetWatts(_watts + 1);
        if (b.HasFlag(Btn.Down)) SetWatts(_watts - 1);
        if (b.HasFlag(Btn.A) && _selected != null) Launch(_selected);
        if (b.HasFlag(Btn.LB)) CycleProfile(-1);
        if (b.HasFlag(Btn.RB)) CycleProfile(+1);
        if (b.HasFlag(Btn.X)) SetWatts(_watts - 1);
        if (b.HasFlag(Btn.Y)) SetWatts(_watts + 1);
        if (b.HasFlag(Btn.B)) Reload();
    }

    void OnKey(object s, KeyEventArgs e)
    {
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Left: Move(FocusNavigationDirection.Left); break;
            case Key.Right: Move(FocusNavigationDirection.Right); break;
            case Key.Up: case Key.Add: case Key.OemPlus: SetWatts(_watts + 1); break;
            case Key.Down: case Key.Subtract: case Key.OemMinus: SetWatts(_watts - 1); break;
            case Key.Enter: if (_selected != null) Launch(_selected); break;
            case Key.PageUp: CycleProfile(+1); break;
            case Key.PageDown: CycleProfile(-1); break;
            case Key.F5: Reload(); break;
            case Key.Escape: Close(); break;
            default: e.Handled = false; break;
        }
    }
}
