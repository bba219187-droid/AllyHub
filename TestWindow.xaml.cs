using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AllyHub;

/// Testes ao hardware do Ally: comando, vibracao, ecra, toque, colunas, bateria, stress, armazenamento.
public partial class TestWindow : Window
{
    enum View { List, Controller, Colors, Touch, Stress }

    View _view = View.List;
    readonly List<Button> _rows = new();
    readonly List<Action> _actions = new();
    int _idx;
    bool _inMenu;
    Gamepad? _pad;

    // comando
    readonly Dictionary<Btn, Border> _btnCells = new();
    Border? _l3, _r3;
    Ellipse? _lDot, _rDot;
    Rectangle? _ltBar, _rtBar;
    int _holdB;

    // cores
    static readonly (Color c, string n)[] Palette =
    {
        (System.Windows.Media.Colors.Red, "Vermelho"), (System.Windows.Media.Colors.Lime, "Verde"), (System.Windows.Media.Colors.Blue, "Azul"),
        (System.Windows.Media.Colors.White, "Branco"), (System.Windows.Media.Colors.Black, "Preto"), (Color.FromRgb(0x80, 0x80, 0x80), "Cinzento"),
    };
    int _color;

    // stress
    CancellationTokenSource? _stress;

    public TestWindow()
    {
        InitializeComponent();
        Paint.MouseMove += (_, e) => { if (_view == View.Touch && e.LeftButton == MouseButtonState.Pressed) Dot(e.GetPosition(Paint), 18); };
        Paint.MouseDown += (_, e) =>
        {
            if (_view == View.Touch) Dot(e.GetPosition(Paint), 18);
            else if (_view == View.Colors) NextColor();
        };
        Paint.TouchMove += (_, e) => { if (_view == View.Touch) Dot(e.GetTouchPoint(Paint).Position, 26); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Back(); e.Handled = true; } };
        Closed += (_, _) => { _stress?.Cancel(); _pad?.Rumble(0, 0); };
        ShowMenu();
    }

    // =====================================================================
    // entrada do comando (chamada a ~60 Hz pela MainWindow)
    // =====================================================================

    public void Tick(Btn pressed, Gamepad pad)
    {
        _pad = pad;
        switch (_view)
        {
            case View.Controller: ControllerTick(pad); return;
            case View.Colors:
                if (pressed.HasFlag(Btn.B)) Back();
                else if (pressed.HasFlag(Btn.A) || pressed.HasFlag(Btn.Right)) NextColor();
                return;
            case View.Touch:
                if (pressed.HasFlag(Btn.B)) Back();
                if (pressed.HasFlag(Btn.X)) Paint.Children.Clear();
                return;
            case View.Stress:
                if (pressed.HasFlag(Btn.B)) Back();
                return;
            default:
                if (pressed == 0) return;
                if (pressed.HasFlag(Btn.B)) { Back(); return; }
                if (pressed.HasFlag(Btn.Up)) Sel(_idx - 1);
                if (pressed.HasFlag(Btn.Down)) Sel(_idx + 1);
                if (pressed.HasFlag(Btn.A) && _idx < _actions.Count) { Sfx.Select(); _actions[_idx](); }
                return;
        }
    }

    void Back()
    {
        Sfx.Back();
        _stress?.Cancel();
        _pad?.Rumble(0, 0);
        Full.Visibility = Visibility.Collapsed;
        if (_view == View.List && _inMenu) { Close(); return; }
        ShowMenu();
    }

    // =====================================================================
    // listas (menu e testes simples)
    // =====================================================================

    void ShowList(string header, string sub, string? body, params (string label, Action act)[] rows)
    {
        _view = View.List;
        _inMenu = false;
        Header.Text = header;
        Sub.Text = sub;
        Hint.Text = "↕ escolher   A confirmar   B voltar";
        Area.Children.Clear();
        _rows.Clear();
        _actions.Clear();

        var panel = new StackPanel { Width = 620, HorizontalAlignment = HorizontalAlignment.Left };
        if (body != null)
            panel.Children.Add(new TextBlock { Text = body, FontSize = 20, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 22), LineHeight = 30 });
        foreach (var (label, act) in rows)
        {
            var b = new Button { Content = label, Style = (Style)FindResource("PanelBtn"), Focusable = false };
            var i = _rows.Count;
            b.Click += (_, _) => { Sel(i); act(); };
            _rows.Add(b);
            _actions.Add(act);
            panel.Children.Add(b);
        }
        Area.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        Sel(0, sound: false);
    }

    void Sel(int i, bool sound = true)
    {
        if (_rows.Count == 0) return;
        _idx = (i + _rows.Count) % _rows.Count;
        for (int k = 0; k < _rows.Count; k++) _rows[k].Tag = k == _idx ? "sel" : null;
        _rows[_idx].BringIntoView();
        if (sound) Sfx.Move();
    }

    void ShowMenu()
    {
        ShowList("Testa o teu Ally", "Escolhe um teste. Cada um mostra se está tudo bem.", null,
            ("🎮  Comando — botões, sticks e gatilhos", ShowController),
            ("📳  Vibração", ShowVibration),
            ("🖥  Ecrã — cores (píxeis mortos)", ShowColors),
            ("👆  Ecrã tátil", ShowTouch),
            ("🔊  Colunas — esquerda e direita", ShowSpeakers),
            ("🌀  Ventoinhas", ShowFans),
            ("🔥  Stress e temperaturas (60 s)", ShowStress),
            ("🔋  Saúde da bateria", ShowBattery),
            ("💾  Memória e armazenamento", ShowStorage),
            ("←  Sair do modo de testes", Close));
        _inMenu = true;
    }

    // =====================================================================
    // comando
    // =====================================================================

    void ShowController()
    {
        _view = View.Controller;
        _holdB = 0;
        Header.Text = "Comando";
        Sub.Text = "Carrega em todos os botões, mexe os sticks e os gatilhos. O que funcionar acende.";
        Hint.Text = "Mantém B durante 2 segundos para sair";
        Area.Children.Clear();
        _btnCells.Clear();

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var grid = new WrapPanel { VerticalAlignment = VerticalAlignment.Top };
        foreach (var (b, n) in new[] { (Btn.A, "A"), (Btn.B, "B"), (Btn.X, "X"), (Btn.Y, "Y"), (Btn.LB, "LB"), (Btn.RB, "RB"),
                                       (Btn.View, "VIEW"), (Btn.Menu, "MENU"), (Btn.Up, "↑"), (Btn.Down, "↓"), (Btn.Left, "←"), (Btn.Right, "→") })
        {
            var cell = Cell(n);
            _btnCells[b] = cell;
            grid.Children.Add(cell);
        }
        _l3 = Cell("L3"); _r3 = Cell("R3");
        grid.Children.Add(_l3); grid.Children.Add(_r3);
        root.Children.Add(grid);

        var sticks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        (Canvas c, Ellipse dot) Stick(string label)
        {
            var c = new Canvas { Width = 200, Height = 200, Margin = new Thickness(0, 0, 30, 0) };
            c.Children.Add(new Ellipse { Width = 200, Height = 200, Stroke = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)), StrokeThickness = 2 });
            var dot = new Ellipse { Width = 30, Height = 30, Fill = (Brush)FindResource("AccentBrush") };
            Canvas.SetLeft(dot, 85); Canvas.SetTop(dot, 85);
            c.Children.Add(dot);
            var t = new TextBlock { Text = label, FontSize = 14, Foreground = Brushes.Gray };
            Canvas.SetTop(t, 206); Canvas.SetLeft(t, 80);
            c.Children.Add(t);
            sticks.Children.Add(c);
            return (c, dot);
        }
        _lDot = Stick("Stick esq.").dot;
        _rDot = Stick("Stick dir.").dot;

        var triggers = new StackPanel { Margin = new Thickness(0, 50, 0, 0) };
        Rectangle Bar(string label)
        {
            triggers.Children.Add(new TextBlock { Text = label, FontSize = 14, Foreground = Brushes.Gray });
            var track = new Grid { Width = 430, Height = 16, Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 14) };
            var bar = new Rectangle { Fill = (Brush)FindResource("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            track.Children.Add(bar);
            triggers.Children.Add(track);
            return bar;
        }
        _ltBar = Bar("Gatilho LT");
        _rtBar = Bar("Gatilho RT");

        var right = new StackPanel();
        right.Children.Add(sticks);
        right.Children.Add(triggers);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Area.Children.Add(root);
    }

    static Border Cell(string text) => new()
    {
        Width = 96, Height = 70, Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255)),
        Child = new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    void ControllerTick(Gamepad pad)
    {
        var raw = pad.Raw;
        var held = (Btn)raw.Buttons;
        var on = (Brush)FindResource("AccentBrush");
        var off = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255));
        foreach (var (b, cell) in _btnCells) cell.Background = held.HasFlag(b) ? on : off;
        if (_l3 != null) _l3.Background = (raw.Buttons & 0x0040) != 0 ? on : off;
        if (_r3 != null) _r3.Background = (raw.Buttons & 0x0080) != 0 ? on : off;

        void Place(Ellipse? dot, short x, short y)
        {
            if (dot == null) return;
            Canvas.SetLeft(dot, 85 + x / 32768.0 * 85);
            Canvas.SetTop(dot, 85 - y / 32768.0 * 85);
        }
        Place(_lDot, raw.LX, raw.LY);
        Place(_rDot, raw.RX, raw.RY);
        if (_ltBar != null) _ltBar.Width = raw.LT / 255.0 * 430;
        if (_rtBar != null) _rtBar.Width = raw.RT / 255.0 * 430;

        if (pad.User < 0) Sub.Text = "⚠ Nenhum comando detetado.";
        _holdB = held.HasFlag(Btn.B) ? _holdB + 1 : 0;
        if (_holdB > 120) Back();
    }

    // =====================================================================
    // vibracao e colunas
    // =====================================================================

    void ShowVibration()
    {
        async void Buzz(double l, double r)
        {
            _pad?.Rumble(l, r);
            await Task.Delay(1200);
            _pad?.Rumble(0, 0);
        }
        ShowList("Vibração", "Cada opção vibra durante 1 segundo.", null,
            ("Motor esquerdo (forte)", () => Buzz(1, 0)),
            ("Motor direito (fino)", () => Buzz(0, 1)),
            ("Os dois", () => Buzz(1, 1)),
            ("←  Voltar", ShowMenu));
    }

    void ShowSpeakers()
    {
        void Tone(bool l, bool r)
        {
            var wav = WavGen.Wav(new[] { (440.0, 0.35), (660.0, 0.35), (880.0, 0.5) }, 0.5, l, r);
            try { new SoundPlayer(new MemoryStream(wav)).Play(); } catch { }
        }
        ShowList("Colunas", "Sobe o volume. Cada opção toca só numa coluna (ou nas duas).", null,
            ("🔈  Coluna esquerda", () => Tone(true, false)),
            ("🔈  Coluna direita", () => Tone(false, true)),
            ("🔊  As duas", () => Tone(true, true)),
            ("←  Voltar", ShowMenu));
    }

    // =====================================================================
    // ecra: cores e toque
    // =====================================================================

    void ShowColors()
    {
        _view = View.Colors;
        _color = -1;
        Paint.Children.Clear();
        Full.Visibility = Visibility.Visible;
        NextColor();
    }

    void NextColor()
    {
        _color++;
        if (_color >= Palette.Length) { Back(); return; }
        var (c, n) = Palette[_color];
        Full.Background = new SolidColorBrush(c);
        FullHint.Foreground = new SolidColorBrush(c.R + c.G + c.B > 380 ? Color.FromArgb(0x90, 0, 0, 0) : Color.FromArgb(0x90, 255, 255, 255));
        FullHint.Text = $"{n} ({_color + 1}/{Palette.Length}) — procura pontos de outra cor.   A / toque = seguinte   B = sair";
    }

    void ShowTouch()
    {
        _view = View.Touch;
        Paint.Children.Clear();
        Full.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0B, 0x0F));
        FullHint.Foreground = new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255));
        FullHint.Text = "Passa o dedo por todo o ecrã — se houver zonas que não desenham, o toque tem falhas.   X = limpar   B = sair";
        Full.Visibility = Visibility.Visible;
    }

    void Dot(Point p, double size)
    {
        var e = new Ellipse { Width = size, Height = size, Fill = (Brush)FindResource("AccentBrush"), IsHitTestVisible = false };
        Canvas.SetLeft(e, p.X - size / 2);
        Canvas.SetTop(e, p.Y - size / 2);
        Paint.Children.Add(e);
        if (Paint.Children.Count > 4000) Paint.Children.RemoveAt(0);
    }

    // =====================================================================
    // ventoinhas
    // =====================================================================

    async void ShowFans()
    {
        ShowList("Ventoinhas", "A ler…", null, ("←  Voltar", ShowMenu));
        if (!await Task.Run(() => Fans.Available)) { Sub.Text = "⚠ Driver ASUS (ATKACPI) não encontrado — instala o Armoury Crate / ASUS System Control Interface."; return; }
        var err = await Task.Run(() => Fans.Apply(Fans.Presets.Length - 1));   // maximo
        Sub.Text = err != null ? $"⚠ {err}" : "Ventoinhas no máximo durante 10 segundos — deves ouvi-las a acelerar.";
        for (int i = 0; i < 10 && _view == View.List && Header.Text == "Ventoinhas"; i++)
        {
            var (cpu, gpu) = await Task.Run(Fans.Rpm);
            Sub.Text = $"Ventoinhas no máximo ({10 - i} s)   ·   CPU: {cpu?.ToString() ?? "—"} RPM   ·   GPU: {gpu?.ToString() ?? "—"} RPM";
            await Task.Delay(1000);
        }
        var cfg = Config.Load();
        await Task.Run(() => Fans.Apply(cfg.FanPreset));
        if (Header.Text == "Ventoinhas") Sub.Text = $"Teste terminado. Voltou ao perfil «{Fans.Presets[cfg.FanPreset].Name}».";
    }

    // =====================================================================
    // stress
    // =====================================================================

    async void ShowStress()
    {
        _view = View.Stress;
        Header.Text = "Stress e temperaturas";
        Sub.Text = "O CPU vai a 100% durante 60 segundos. Liga o carregador para um teste completo.";
        Hint.Text = "B = parar";
        Area.Children.Clear();
        var lines = new TextBlock { FontSize = 24, LineHeight = 40 };
        Area.Children.Add(lines);

        _stress?.Cancel();
        var cts = _stress = new CancellationTokenSource();
        var token = cts.Token;
        for (int i = 0; i < Environment.ProcessorCount; i++)
            new Thread(() => { double x = 1; while (!token.IsCancellationRequested) x = Math.Sqrt(x + 1.0001); }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();

        double maxT = 0, sumP = 0; int n = 0;
        for (int s = 0; s < 60 && !token.IsCancellationRequested; s++)
        {
            var (t, p) = await Task.Run(Tdp.Info);
            var (cpu, _) = await Task.Run(Fans.Rpm);
            if (t is double tt) maxT = Math.Max(maxT, tt);
            if (p is double pp) { sumP += pp; n++; }
            lines.Text = $"Tempo: {s + 1} / 60 s\n" +
                         $"Temperatura CPU: {(t is double a ? $"{a:0} °C" : "— (precisa do RyzenAdj)")}\n" +
                         $"Potência: {(p is double b ? $"{b:0.0} W" : "—")}\n" +
                         $"Ventoinha: {(cpu is int r ? $"{r} RPM" : "—")}\n" +
                         $"Máxima: {(maxT > 0 ? $"{maxT:0} °C" : "—")}";
            await Task.Delay(1000);
        }
        cts.Cancel();
        if (_view != View.Stress) return;

        var verdict = maxT == 0 ? "Sem leitura de temperatura — põe o RyzenAdj na pasta da app."
            : maxT < 90 ? "✓ Excelente — temperaturas baixas para o Ally em carga."
            : maxT < 97 ? "✓ Normal — o Ally costuma trabalhar perto disto em carga máxima."
            : "⚠ Muito quente — verifica se as entradas de ar estão livres e se as ventoinhas aceleram.";
        lines.Text += $"\nPotência média: {(n > 0 ? $"{sumP / n:0.0} W" : "—")}\n\n{verdict}";
        Hint.Text = "B = voltar";
    }

    // =====================================================================
    // bateria e armazenamento
    // =====================================================================

    async void ShowBattery()
    {
        ShowList("Saúde da bateria", "A ler…", null, ("←  Voltar", ShowMenu));
        var (design, full, cycles) = await Task.Run(SysInfo.BatteryHealth);
        var (pct, charging) = Battery.Get();
        if (Header.Text != "Saúde da bateria") return;
        if (design <= 0 || full <= 0) { Sub.Text = "⚠ O Windows não devolveu dados da bateria."; return; }
        var health = Math.Min(100, full * 100.0 / design);
        var verdict = health >= 85 ? "✓ Bateria em bom estado." : health >= 70 ? "Desgaste normal com o uso." : "⚠ Bateria bastante gasta — dura bem menos que nova.";
        ShowList("Saúde da bateria", verdict,
            $"Saúde: {health:0} %\n" +
            $"Capacidade de fábrica: {design / 1000.0:0.0} Wh\n" +
            $"Capacidade atual: {full / 1000.0:0.0} Wh\n" +
            $"Ciclos de carga: {(cycles > 0 ? cycles.ToString() : "—")}\n" +
            $"Carga agora: {(pct >= 0 ? $"{pct} %" : "—")}{(charging ? " (a carregar)" : "")}",
            ("←  Voltar", ShowMenu));
    }

    void ShowStorage()
    {
        var mem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024;
        var lines = new List<string> { $"Memória RAM utilizável pelo Windows: {mem:0.0} GB" };
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            double total = d.TotalSize / 1e9, free = d.AvailableFreeSpace / 1e9;
            lines.Add($"{d.Name.TrimEnd('\\')}  {d.VolumeLabel}   —   {free:0} GB livres de {total:0} GB ({free / total * 100:0} % livre)");
        }
        ShowList("Memória e armazenamento", "Discos e cartão microSD encontrados.", string.Join("\n", lines), ("←  Voltar", ShowMenu));
    }
}
