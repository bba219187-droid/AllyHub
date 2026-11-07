using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AllyHub;

public partial class MainWindow : Window
{
    enum Mode { Library, Details, System, Terms, Launching }

    static readonly string[] ThemeNames = { "Vermelho", "Ciano", "Roxo", "Verde", "Laranja" };
    const double TileSlot = 190 + 32;   // largura do tile + margens

    readonly Config _cfg;
    readonly Gamepad _pad = new();
    readonly Dictionary<string, Button> _tiles = new();
    readonly DispatcherTimer _heroDelay = new() { Interval = TimeSpan.FromMilliseconds(160) };
    List<Game> _all;
    string _filter = "Todos";
    Mode _mode = Mode.Library;
    Button? _selected;
    int _watts;
    bool _booster, _heroFlip, _launchCancel, _checking, _sysLoaded, _updBusy;
    QuickMenu? _quick;

    // jogo a correr
    Game? _playing;
    DateTime _playStart;
    bool _seen;
    int _missing;
    long _session;

    public MainWindow(Config cfg, List<Game> games)
    {
        _cfg = cfg;
        _all = games;
        InitializeComponent();
        WattSlider.Minimum = _cfg.MinWatts;
        WattSlider.Maximum = _cfg.MaxWatts;
        GaugeTrack.Data = Arc(1);

        foreach (var (name, w) in _cfg.Profiles)
        {
            var b = new Button { Content = name, ToolTip = $"{w} W", Style = (Style)FindResource("Chip") };
            b.Click += (_, _) => { SetWatts(w); SaveSelectedWatts(); };
            ProfileBar.Children.Add(b);
        }

        // o carrossel desliza com animacao; impedir o "salto" automatico do ScrollViewer
        Tiles.RequestBringIntoView += (_, e) => e.Handled = true;
        _heroDelay.Tick += (_, _) => { _heroDelay.Stop(); if (_selected?.Tag is Game g) SetHero(Load(g.Hero ?? g.Cover, 1280)); };

        SetWatts(_cfg.DefaultWatts);
        TdpState.Text = Tdp.Available ? "Aplicado ao abrir o jogo" : "⚠ ryzenadj.exe em falta — TDP desligado";
        Status.Text = "v" + Updater.CurrentText;
        SetFilter("Todos");

        Every(16, PollPad);                       // comando ~60 Hz
        Every(15000, () => { if (_playing != null) ApplyNow(); });  // o Armoury Crate repoe o TDP dele
        Every(10000, TrackTick);                  // tempo de jogo
        Every(5000, UpdateClock);
        UpdateClock();

        var pulse = new DoubleAnimation(0.55, 1, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        BoosterBadge.BeginAnimation(OpacityProperty, pulse);

        PreviewKeyDown += OnKey;
        Activated += (_, _) => { if (_mode == Mode.Library) _selected?.Focus(); };
        Closed += (_, _) => { if (_booster) Tdp.BestPerformance(false); Application.Current.Shutdown(); };
    }

    void Every(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => a();
        t.Start();
    }

    // ---------- atalho global Ctrl+Shift+A para o menu rapido ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        Native.RegisterHotKey(h, 1, 0x0002 | 0x0004 | 0x4000, 0x41);   // Ctrl+Shift (sem repeticao) + A
        HwndSource.FromHwnd(h)?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == 0x0312 && wp.ToInt32() == 1) { ToggleQuick(); handled = true; }
            return IntPtr.Zero;
        });
    }

    // =====================================================================
    // biblioteca e filtros
    // =====================================================================

    List<string> Filters()
    {
        var f = new List<string> { "Todos" };
        if (_all.Any(g => _cfg.StatsFor(g.Id).Favorite)) f.Add("★ Favoritos");
        if (_all.Any(g => _cfg.StatsFor(g.Id).LastPlayed != null)) f.Add("Recentes");
        foreach (var s in new[] { "Steam", "Epic", "GOG", "Ubisoft", "Manual" })
            if (_all.Any(g => g.Store == s)) f.Add(s);
        return f;
    }

    IEnumerable<Game> Filtered() => _filter switch
    {
        "★ Favoritos" => _all.Where(g => _cfg.StatsFor(g.Id).Favorite),
        "Recentes" => _all.Where(g => _cfg.StatsFor(g.Id).LastPlayed != null)
                          .OrderByDescending(g => _cfg.StatsFor(g.Id).LastPlayed).Take(20),
        "Todos" => _all,
        _ => _all.Where(g => g.Store == _filter),
    };

    void RenderFilters()
    {
        FilterBar.Children.Clear();
        foreach (var f in Filters())
        {
            var b = new Button { Content = f, Style = (Style)FindResource("Chip"), Tag = f == _filter ? "on" : null };
            b.Click += (_, _) => SetFilter(f);
            FilterBar.Children.Add(b);
        }
    }

    void SetFilter(string filter, string? keepId = null)
    {
        if (!Filters().Contains(filter)) filter = "Todos";
        _filter = filter;
        RenderFilters();
        Tiles.Children.Clear();
        _selected = null;

        var games = Filtered().ToList();
        foreach (var g in games) Tiles.Children.Add(TileFor(g));

        if (games.Count == 0)
        {
            GameTitle.Text = _all.Count == 0 ? "NENHUM JOGO ENCONTRADO" : "NADA AQUI AINDA";
            GameInfo.Text = _all.Count == 0
                ? "Instala jogos na Steam, Epic, GOG ou Ubisoft, ou adiciona um .exe em Sistema."
                : "Muda de filtro com LT / RT.";
            StoreText.Text = "ALLYHUB";
            FavStar.Visibility = Visibility.Collapsed;
            return;
        }
        var keep = keepId != null && _tiles.TryGetValue(keepId, out var k) && Tiles.Children.Contains(k) ? k : (Button)Tiles.Children[0];
        Dispatcher.InvokeAsync(() => Select(keep, sound: false), DispatcherPriority.Loaded);
    }

    void CycleFilter(int step)
    {
        var f = Filters();
        var i = (f.IndexOf(_filter) + step + f.Count) % f.Count;
        Sfx.Move();
        SetFilter(f[i]);
    }

    async void Reload(string? keepId = null)
    {
        Status.Text = "A atualizar a biblioteca…";
        _all = await Task.Run(() => Library.Scan(_cfg));
        _tiles.Clear();
        SetFilter(_filter, keepId ?? (_selected?.Tag as Game)?.Id);
        Status.Text = "v" + Updater.CurrentText;
    }

    static BitmapImage? Load(string? path, int width)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit(); b.UriSource = new Uri(path); b.DecodePixelWidth = width; b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    Button TileFor(Game g)
    {
        if (_tiles.TryGetValue(g.Id, out var cached)) return cached;

        var grid = new Grid();
        var cover = Load(g.Cover, 380);
        if (cover != null)
            grid.Children.Add(new Image { Source = cover, Stretch = Stretch.UniformToFill });
        else
        {
            grid.Background = new LinearGradientBrush(Color.FromRgb(0x2B, 0x10, 0x18), Color.FromRgb(0x14, 0x15, 0x1C), 60);
            grid.Children.Add(new TextBlock
            {
                Text = g.Name.ToUpperInvariant(), FontSize = 20, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14),
            });
        }
        grid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0A, 0x0B, 0x0F)), Padding = new Thickness(7, 2, 7, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8),
            Child = new TextBlock { Text = g.Store.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Bold },
        });

        var tile = new Button { Content = grid, Tag = g, Style = (Style)FindResource("Tile") };
        // toque/rato: 1.o toque escolhe, 2.o toque joga
        tile.PreviewMouseDown += (_, e) => { if (_selected != tile) { Select(tile); e.Handled = true; } };
        tile.Click += (_, _) => Launch(g);
        return _tiles[g.Id] = tile;
    }

    void Select(Button tile, bool sound = true)
    {
        if (_selected != null) Panel.SetZIndex(_selected, 0);
        _selected = tile;
        Panel.SetZIndex(tile, 1);
        tile.Focus();
        if (sound) Sfx.Move();
        CenterTile(tile);

        var g = (Game)tile.Tag;
        SetWatts(_cfg.GameWatts.TryGetValue(g.Id, out var w) ? w : _cfg.DefaultWatts);
        RefreshInfo();

        // fundo so troca quando paras num jogo (evita pesar ao percorrer depressa)
        _heroDelay.Stop();
        _heroDelay.Start();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Info.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        InfoX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-24, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    void RefreshInfo()
    {
        if (_selected?.Tag is not Game g) return;
        var s = _cfg.StatsFor(g.Id);
        GameTitle.Text = g.Name.ToUpperInvariant();
        StoreText.Text = g.Store.ToUpperInvariant();
        FavStar.Visibility = s.Favorite ? Visibility.Visible : Visibility.Collapsed;
        var profile = _cfg.GameWatts.TryGetValue(g.Id, out var w) ? $"Perfil {w} W" : "Ainda sem perfil";
        GameInfo.Text = $"⏱ {Fmt.Playtime(s.Seconds)}     ·     Última vez: {Fmt.LastPlayed(s.LastPlayed, DateTime.Now)}     ·     {profile}";
    }

    // ---------- carrossel suave ----------

    public static readonly DependencyProperty HOffsetProperty = DependencyProperty.Register(
        nameof(HOffset), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, e) => ((MainWindow)d).Scroll.ScrollToHorizontalOffset((double)e.NewValue)));

    public double HOffset { get => (double)GetValue(HOffsetProperty); set => SetValue(HOffsetProperty, value); }

    void CenterTile(Button tile)
    {
        var i = Tiles.Children.IndexOf(tile);
        if (i < 0) return;
        Scroll.UpdateLayout();
        var target = Math.Clamp(i * TileSlot + TileSlot / 2 - Scroll.ViewportWidth / 2, 0, Math.Max(0, Scroll.ExtentWidth - Scroll.ViewportWidth));
        BeginAnimation(HOffsetProperty, new DoubleAnimation(Scroll.HorizontalOffset, target, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    void SetHero(ImageSource? src)
    {
        var (show, hide) = _heroFlip ? (HeroA, HeroB) : (HeroB, HeroA);
        _heroFlip = !_heroFlip;
        show.Source = src;
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(src == null ? 0 : 0.55, TimeSpan.FromMilliseconds(450)));
        hide.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)));
    }

    void Move(FocusNavigationDirection dir)
    {
        if (_selected == null) { if (Tiles.Children.Count > 0 && Tiles.Children[0] is Button f) Select(f); return; }
        if (_selected.PredictFocus(dir) is Button next && next.Tag is Game) Select(next);
    }

    void ToggleFavorite()
    {
        if (_selected?.Tag is not Game g) return;
        var s = _cfg.StatsFor(g.Id);
        s.Favorite = !s.Favorite;
        _cfg.Save();
        Sfx.Select();
        if (_filter == "★ Favoritos") SetFilter(_filter); else RenderFilters();
        RefreshInfo();
    }

    // =====================================================================
    // desempenho e booster
    // =====================================================================

    public bool Booster => _booster;
    public bool BoosterAccepted => _cfg.BoosterAccepted;
    public int EffectiveWatts => _booster ? Tdp.BoosterWatts : _watts;
    public string ProfileLabel => _booster ? "BOOSTER" : _cfg.Profiles.FirstOrDefault(p => p.Value == _watts).Key ?? "Personalizado";
    public string? PlayingName => _playing?.Name;
    public long SessionSeconds => _session;

    void SetWatts(int w)
    {
        _watts = Math.Clamp(w, _cfg.MinWatts, _cfg.MaxWatts);
        WattSlider.Value = _watts;
        RefreshPerf();
    }

    void RefreshPerf()
    {
        var eff = EffectiveWatts;
        WattsBig.Text = eff.ToString();
        ProfileName.Text = _booster ? "⚡ BOOSTER" : ProfileLabel.ToUpperInvariant();
        var name = _booster ? null : _cfg.Profiles.FirstOrDefault(p => p.Value == _watts).Key;
        foreach (Button b in ProfileBar.Children)
            b.Tag = name != null && (string)b.Content == name ? "on" : null;
        GaugeArc.Data = Arc((double)(eff - _cfg.MinWatts) / (_cfg.MaxWatts - _cfg.MinWatts));
        WattSlider.IsEnabled = !_booster;
        BoosterBadge.Visibility = _booster ? Visibility.Visible : Visibility.Collapsed;
        BoosterBtn.Content = _booster ? "⚡ Desligar Booster" : "⚡ Ativar Booster";
        BoosterBtn.Tag = _booster ? "on" : null;
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
        if ((int)e.NewValue != _watts) { SetWatts((int)e.NewValue); SaveSelectedWatts(); }
    }

    void SaveSelectedWatts()
    {
        var g = _playing ?? _selected?.Tag as Game;
        if (g == null) return;
        _cfg.GameWatts[g.Id] = _watts;
        _cfg.Save();
        RefreshInfo();
    }

    /// Aplica ja o TDP efetivo (quando ha jogo a correr ou o booster mudou).
    void ApplyNow()
    {
        if (!Tdp.Available) return;
        var w = EffectiveWatts;
        Task.Run(() => Tdp.Apply(w));
    }

    public void CycleProfile(int step)
    {
        if (_booster) return;
        var ws = _cfg.Profiles.Values.OrderBy(v => v).ToList();
        if (ws.Count == 0) return;
        SetWatts(step > 0 ? ws.FirstOrDefault(v => v > _watts, ws.First()) : ws.LastOrDefault(v => v < _watts, ws.Last()));
        Sfx.Move();
        SaveSelectedWatts();
        if (_playing != null) ApplyNow();
    }

    public void AdjustWatts(int delta)
    {
        if (_booster) return;
        SetWatts(_watts + delta);
        Sfx.Move();
        SaveSelectedWatts();
        if (_playing != null) ApplyNow();
    }

    public void ToggleBooster()
    {
        if (!_booster && !_cfg.BoosterAccepted)
        {
            ShowHub();
            OpenPanel(Mode.Terms);
            return;
        }
        _booster = !_booster;
        var on = _booster;
        Task.Run(() => Tdp.BestPerformance(on));
        ApplyNow();
        if (on) Sfx.Boost(); else Sfx.Back();
        RefreshPerf();
        if (_mode == Mode.Details) FillDetails();
    }

    void Booster_Click(object s, RoutedEventArgs e) => ToggleBooster();

    void TermsAccept_Click(object s, RoutedEventArgs e)
    {
        _cfg.BoosterAccepted = true;
        _cfg.BoosterAcceptedAt = DateTime.Now;
        _cfg.Save();
        ClosePanel();
        ToggleBooster();
    }

    // =====================================================================
    // abrir jogo: ecra de carregamento ate a janela do jogo aparecer
    // =====================================================================

    async void Launch(Game g)
    {
        if (_mode == Mode.Launching) return;
        if (_mode != Mode.Library) ClosePanel(sound: false);

        _cfg.GameWatts[g.Id] = _watts;
        _cfg.StatsFor(g.Id).LastPlayed = DateTime.Now;
        _cfg.Save();
        Sfx.Select();

        string? tdpErr = null;
        if (Tdp.Available) { var w = EffectiveWatts; tdpErr = await Task.Run(() => Tdp.Apply(w)); }

        // ecra "A abrir"
        _mode = Mode.Launching;
        _launchCancel = false;
        LaunchHero.Source = Load(g.Hero ?? g.Cover, 1600);
        LaunchTitle.Text = g.Name.ToUpperInvariant();
        LaunchStep.Text = "A ABRIR";
        LaunchInfo.Text = tdpErr != null ? $"⚠ TDP: {tdpErr}" : $"⚡ {EffectiveWatts} W  ·  {ProfileLabel}";
        LaunchPanel.Opacity = 0;
        LaunchPanel.Visibility = Visibility.Visible;
        LaunchPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
        LaunchBarX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-140, 420, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        Topmost = true;   // fica por cima da Steam e dos pop-ups do launcher
        Activate();

        try
        {
            Process.Start(new ProcessStartInfo(g.Launch) { UseShellExecute = true, WorkingDirectory = g.WorkDir ?? "" });
        }
        catch (Exception e)
        {
            LaunchStep.Text = "ERRO";
            LaunchInfo.Text = $"⚠ Não foi possível abrir: {e.Message}";
            await Task.Delay(3500);
            EndLaunch(minimize: false);
            return;
        }

        // esperar pela janela do jogo (max. 2 min)
        var start = DateTime.Now;
        if (g.InstallDir != null)
        {
            while (!_launchCancel && DateTime.Now - start < TimeSpan.FromMinutes(2))
            {
                var dir = g.InstallDir;
                var state = await Task.Run(() => Running.Check(dir));
                if (state == 2) { LaunchStep.Text = "A INICIAR"; await Task.Delay(1500); break; }
                if (state == 1) LaunchStep.Text = "A CARREGAR";
                await Task.Delay(700);
            }
        }
        else
        {
            for (int i = 0; i < 12 && !_launchCancel; i++) await Task.Delay(500);
        }

        if (_launchCancel) return;
        _playing = g;
        _playStart = DateTime.Now;
        _seen = false;
        _missing = 0;
        _session = 0;
        EndLaunch(minimize: true);
    }

    void EndLaunch(bool minimize)
    {
        Topmost = false;
        _mode = Mode.Library;
        LaunchBarX.BeginAnimation(TranslateTransform.XProperty, null);
        LaunchPanel.Visibility = Visibility.Collapsed;
        MainContent.IsEnabled = true;
        if (minimize) WindowState = WindowState.Minimized;
        else _selected?.Focus();
    }

    async void TrackTick()
    {
        var g = _playing;
        if (g == null || _checking || _mode == Mode.Launching) return;

        bool running;
        if (g.InstallDir == null) running = WindowState == WindowState.Minimized;   // sem pasta: conta enquanto estamos em segundo plano
        else
        {
            _checking = true;
            var dir = g.InstallDir;
            running = await Task.Run(() => Running.Check(dir)) > 0;
            _checking = false;
        }
        if (_playing != g) return;

        if (running)
        {
            _seen = true;
            _missing = 0;
            var s = _cfg.StatsFor(g.Id);
            s.Seconds += 10;
            s.LastPlayed = DateTime.Now;
            _session += 10;
            if (_session % 60 == 0) _cfg.Save();
        }
        else if (_seen ? ++_missing >= 2 : DateTime.Now - _playStart > TimeSpan.FromMinutes(3))
        {
            // o jogo fechou: volta a AllyHub
            var restore = _seen;
            _playing = null;
            _cfg.Save();
            _quick?.Hide();
            if (restore) ShowHub();
            RefreshInfo();
        }
    }

    public void ShowHub()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
        Show();
        Topmost = true; Activate(); Topmost = false;
        if (_mode == Mode.Library) _selected?.Focus();
        RefreshInfo();
    }

    // =====================================================================
    // paineis (detalhes, sistema, termos)
    // =====================================================================

    FrameworkElement? PanelFor(Mode m) => m switch
    {
        Mode.Details => DetailsPanel,
        Mode.System => SystemPanel,
        Mode.Terms => TermsPanel,
        _ => null,
    };

    void OpenPanel(Mode m)
    {
        if (_mode == Mode.Launching) return;
        foreach (var p in new[] { DetailsPanel, SystemPanel, TermsPanel }) p.Visibility = Visibility.Collapsed;
        _mode = m;
        MainContent.IsEnabled = false;
        var panel = PanelFor(m)!;
        panel.Opacity = 0;
        panel.Visibility = Visibility.Visible;
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        Sfx.Select();

        Button first = m switch { Mode.Details => BtnPlay, Mode.System => BtnCheckUpdates, _ => BtnTermsNo };
        if (m == Mode.Details) FillDetails();
        if (m == Mode.System) FillSystem();
        Dispatcher.InvokeAsync(() => first.Focus(), DispatcherPriority.Loaded);
    }

    void ClosePanel(bool sound = true)
    {
        if (PanelFor(_mode) is { } p) p.Visibility = Visibility.Collapsed;
        _mode = Mode.Library;
        MainContent.IsEnabled = true;
        if (sound) Sfx.Back();
        Dispatcher.InvokeAsync(() => _selected?.Focus(), DispatcherPriority.Loaded);
        RefreshInfo();
    }

    void ClosePanel_Click(object s, RoutedEventArgs e) => ClosePanel();

    // ---------- detalhes ----------

    void FillDetails()
    {
        if (_selected?.Tag is not Game g) return;
        var s = _cfg.StatsFor(g.Id);
        DetailsTitle.Text = g.Name.ToUpperInvariant();
        DetailsStore.Text = g.Store.ToUpperInvariant();
        DetailsCover.Source = Load(g.Cover, 600);
        DetailsCoverText.Text = DetailsCover.Source == null ? g.Name.ToUpperInvariant() : "";
        StatTime.Text = Fmt.Playtime(s.Seconds);
        StatLast.Text = Fmt.LastPlayed(s.LastPlayed, DateTime.Now);
        StatProfile.Text = _booster ? "BOOSTER" : $"{_watts} W";
        BtnProfile.Content = _booster ? "⚙  Perfil: Booster ativo" : $"⚙  Perfil: {_watts} W · {ProfileLabel}      ◀ ▶";
        BtnFav.Content = s.Favorite ? "★  Remover dos favoritos" : "☆  Adicionar aos favoritos";
        BtnRemove.Visibility = g.Store == "Manual" ? Visibility.Visible : Visibility.Collapsed;
    }

    void DetailsPlay_Click(object s, RoutedEventArgs e) { if (_selected?.Tag is Game g) Launch(g); }
    void DetailsProfile_Click(object s, RoutedEventArgs e) { CycleProfile(+1); FillDetails(); }

    void DetailsFav_Click(object s, RoutedEventArgs e)
    {
        ToggleFavorite();
        FillDetails();
    }

    void DetailsRemove_Click(object s, RoutedEventArgs e)
    {
        if (_selected?.Tag is not Game g || g.Store != "Manual") return;
        _cfg.Manual.RemoveAll(m => Library.ManualId(m.Exe) == g.Id);
        _cfg.Save();
        ClosePanel();
        Reload();
    }

    // ---------- sistema ----------

    async void FillSystem()
    {
        SysVersion.Text = $"AllyHub v{Updater.CurrentText}";
        SysTdp.Text = Tdp.Available ? "RyzenAdj: instalado ✓" : "RyzenAdj: em falta (TDP desligado)";
        BtnTheme.Content = $"🎨  Cor: {ThemeNames[Math.Max(0, Array.IndexOf(App.Themes, _cfg.Accent))]}";
        BtnSounds.Content = _cfg.Sounds ? "♪  Sons: SIM" : "♪  Sons: NÃO";
        BtnAuto.Content = "▶  Arranca com o Windows: …";
        var auto = await Task.Run(() => AutoStart.Enabled);
        BtnAuto.Content = auto ? "▶  Arranca com o Windows: SIM" : "▶  Arranca com o Windows: NÃO";

        if (_sysLoaded) return;
        _sysLoaded = true;
        SysBios.Text = "BIOS: …";
        var (bios, gpu, win) = await Task.Run(SysInfo.Basic);
        SysBios.Text = $"BIOS: {bios}";
        SysGpu.Text = $"GPU: {gpu}";
        SysWin.Text = win;
    }

    async void CheckUpdates_Click(object s, RoutedEventArgs e)
    {
        if (_updBusy) return;
        _updBusy = true;
        UpdState.Text = "A procurar no Windows Update… (pode demorar 1–2 minutos)";
        UpdList.ItemsSource = null;
        var list = await Task.Run(SysInfo.PendingUpdates);
        _updBusy = false;
        var drivers = list.Count(u => u.StartsWith("[Driver]"));
        UpdState.Text = list.Count == 0
            ? "✓ Está tudo atualizado (ou sem ligação à internet)."
            : $"{list.Count} atualizações pendentes, {drivers} de drivers. Instala em «Abrir Windows Update».";
        UpdList.ItemsSource = list.Take(12).Select(u => "•  " + u).ToList();
    }

    void OpenWU_Click(object s, RoutedEventArgs e) => SysInfo.OpenWindowsUpdate();

    async void OpenAC_Click(object s, RoutedEventArgs e)
    {
        UpdState.Text = "A abrir o Armoury Crate…";
        var ok = await Task.Run(SysInfo.OpenArmouryCrate);
        UpdState.Text = ok ? "No Armoury Crate: Settings → Update Center → Check for Updates." : "⚠ Armoury Crate não encontrado.";
    }

    void Theme_Click(object s, RoutedEventArgs e)
    {
        var i = (Array.IndexOf(App.Themes, _cfg.Accent) + 1) % App.Themes.Length;
        _cfg.Accent = App.Themes[i];
        _cfg.Save();
        App.SetAccent(_cfg.Accent);
        Sfx.Select();
        FillSystem();
    }

    void Sounds_Click(object s, RoutedEventArgs e)
    {
        _cfg.Sounds = !_cfg.Sounds;
        Sfx.On = _cfg.Sounds;
        _cfg.Save();
        Sfx.Select();
        FillSystem();
    }

    async void AutoStart_Click(object s, RoutedEventArgs e)
    {
        BtnAuto.Content = "▶  Arranca com o Windows: …";
        await Task.Run(() => AutoStart.Set(!AutoStart.Enabled));
        FillSystem();
    }

    void AddGame_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Jogos (*.exe)|*.exe", Title = "Escolhe o executável do jogo" };
        if (dlg.ShowDialog(this) != true) return;
        var info = FileVersionInfo.GetVersionInfo(dlg.FileName);
        var name = new[] { info.ProductName, info.FileDescription, Path.GetFileNameWithoutExtension(dlg.FileName) }
            .First(n => !string.IsNullOrWhiteSpace(n))!.Trim();
        if (!_cfg.Manual.Any(m => m.Exe.Equals(dlg.FileName, StringComparison.OrdinalIgnoreCase)))
            _cfg.Manual.Add(new ManualGame { Name = name, Exe = dlg.FileName });
        _cfg.Save();
        ClosePanel();
        _filter = "Todos";
        Reload(Library.ManualId(dlg.FileName));
    }

    void System_Click(object s, RoutedEventArgs e) => OpenPanel(Mode.System);

    // =====================================================================
    // menu rapido
    // =====================================================================

    void ToggleQuick()
    {
        _quick ??= new QuickMenu(this);
        if (_quick.IsVisible) _quick.CloseMenu();
        else _quick.Open();
    }

    // =====================================================================
    // barra de cima
    // =====================================================================

    void UpdateClock()
    {
        Clock.Text = DateTime.Now.ToString("HH:mm");
        var (pct, charging) = Battery.Get();
        BatteryText.Text = pct < 0 ? "" : $"{(charging ? "⚡" : "🔋")} {pct}%";
    }

    void Exit_Click(object s, RoutedEventArgs e) => Close();

    // =====================================================================
    // comando e teclado
    // =====================================================================

    void PollPad()
    {
        var b = _pad.Poll();
        if (_quick?.IsVisible == true) { _quick.OnPad(b); return; }
        if (b != 0 && _pad.Held.HasFlag(Btn.View) && _pad.Held.HasFlag(Btn.Menu) && !IsActive) { ToggleQuick(); return; }
        if (IsActive && b != 0) OnPad(b);
    }

    void OnPad(Btn b)
    {
        switch (_mode)
        {
            case Mode.Launching:
                if (b.HasFlag(Btn.B)) { _launchCancel = true; Sfx.Back(); EndLaunch(minimize: false); }
                return;

            case Mode.Library:
                if (b.HasFlag(Btn.Left)) Move(FocusNavigationDirection.Left);
                if (b.HasFlag(Btn.Right)) Move(FocusNavigationDirection.Right);
                if (b.HasFlag(Btn.Up)) AdjustWatts(+1);
                if (b.HasFlag(Btn.Down)) AdjustWatts(-1);
                if (b.HasFlag(Btn.LB)) CycleProfile(-1);
                if (b.HasFlag(Btn.RB)) CycleProfile(+1);
                if (b.HasFlag(Btn.LT)) CycleFilter(-1);
                if (b.HasFlag(Btn.RT)) CycleFilter(+1);
                if (b.HasFlag(Btn.X)) ToggleFavorite();
                if (b.HasFlag(Btn.Y)) OpenPanel(Mode.System);
                if (b.HasFlag(Btn.View)) ToggleBooster();
                if (b.HasFlag(Btn.Menu) && _selected != null) OpenPanel(Mode.Details);
                if (b.HasFlag(Btn.B)) Reload();
                if (b.HasFlag(Btn.A) && _selected?.Tag is Game g) Launch(g);
                return;

            default:
                PanelPad(b);
                return;
        }
    }

    void PanelPad(Btn b)
    {
        if (b.HasFlag(Btn.B)) { ClosePanel(); return; }
        var panel = PanelFor(_mode);
        if (panel == null) return;

        var cur = Keyboard.FocusedElement as Button;
        if (cur == null || !panel.IsAncestorOf(cur))
        {
            Button first = _mode switch { Mode.Details => BtnPlay, Mode.System => BtnCheckUpdates, _ => BtnTermsNo };
            first.Focus();
            return;
        }

        // no detalhe, esquerda/direita no perfil muda os watts
        if (cur == BtnProfile && (b.HasFlag(Btn.Left) || b.HasFlag(Btn.Right)))
        {
            AdjustWatts(b.HasFlag(Btn.Right) ? +1 : -1);
            FillDetails();
            return;
        }

        foreach (var (btn, dir) in new[] { (Btn.Up, FocusNavigationDirection.Up), (Btn.Down, FocusNavigationDirection.Down),
                                            (Btn.Left, FocusNavigationDirection.Left), (Btn.Right, FocusNavigationDirection.Right) })
        {
            if (b.HasFlag(btn) && cur.PredictFocus(dir) is Button next && panel.IsAncestorOf(next))
            {
                next.Focus();
                Sfx.Move();
                return;
            }
        }

        if (b.HasFlag(Btn.A)) cur.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    void OnKey(object s, KeyEventArgs e)
    {
        if (_mode == Mode.Launching)
        {
            if (e.Key == Key.Escape) { _launchCancel = true; EndLaunch(minimize: false); }
            e.Handled = true;
            return;
        }
        if (_mode != Mode.Library)
        {
            if (e.Key == Key.Escape) { ClosePanel(); e.Handled = true; }
            return;   // setas/Enter nativos do WPF nos botoes dos paineis
        }

        e.Handled = true;
        switch (e.Key)
        {
            case Key.Left: Move(FocusNavigationDirection.Left); break;
            case Key.Right: Move(FocusNavigationDirection.Right); break;
            case Key.Up: case Key.Add: case Key.OemPlus: AdjustWatts(+1); break;
            case Key.Down: case Key.Subtract: case Key.OemMinus: AdjustWatts(-1); break;
            case Key.Enter: if (_selected?.Tag is Game g) Launch(g); break;
            case Key.PageUp: CycleProfile(+1); break;
            case Key.PageDown: CycleProfile(-1); break;
            case Key.Tab: CycleFilter(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : +1); break;
            case Key.F: ToggleFavorite(); break;
            case Key.D: if (_selected != null) OpenPanel(Mode.Details); break;
            case Key.S: OpenPanel(Mode.System); break;
            case Key.B: ToggleBooster(); break;
            case Key.F5: Reload(); break;
            case Key.Escape: Close(); break;
            default: e.Handled = false; break;
        }
    }
}
