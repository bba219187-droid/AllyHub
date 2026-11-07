using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AllyHub;

/// Menu por cima do jogo: TDP, perfil, booster, temperatura, potencia e bateria.
public partial class QuickMenu : Window
{
    readonly MainWindow _main;
    readonly Button[] _rows;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    IntPtr _prevForeground;
    int _idx;
    bool _busy;

    public QuickMenu(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _rows = new[] { BtnTdp, BtnProf, BtnBoost, BtnFan, BtnHub, BtnClose };
        for (int i = 0; i < _rows.Length; i++)
        {
            var n = i;
            _rows[i].Click += (_, _) => { _idx = n; if (n <= 1 || n == 3) Adjust(+1); else Confirm(); };
        }
        _timer.Tick += (_, _) => RefreshStats();
        Deactivated += (_, _) => { if (IsVisible) CloseMenu(backToGame: false); };
    }

    public void Open()
    {
        _prevForeground = Native.GetForegroundWindow();
        Height = SystemParameters.PrimaryScreenHeight;
        Top = 0;
        Left = SystemParameters.PrimaryScreenWidth - Width;
        _idx = 0;
        Refresh();
        Show();
        Activate();
        SlideX.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(Width, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Sfx.Select();
        RefreshStats();
        _timer.Start();
    }

    public void CloseMenu(bool backToGame = true)
    {
        if (!IsVisible) return;
        _timer.Stop();
        Hide();
        Sfx.Back();
        if (backToGame && _prevForeground != IntPtr.Zero) Native.SetForegroundWindow(_prevForeground);
    }

    public void OnPad(Btn b)
    {
        if (b == 0) return;
        if (b.HasFlag(Btn.B)) { CloseMenu(); return; }
        if (b.HasFlag(Btn.Up)) { _idx = (_idx + _rows.Length - 1) % _rows.Length; Sfx.Move(); }
        if (b.HasFlag(Btn.Down)) { _idx = (_idx + 1) % _rows.Length; Sfx.Move(); }
        if (b.HasFlag(Btn.Left)) Adjust(-1);
        if (b.HasFlag(Btn.Right)) Adjust(+1);
        if (b.HasFlag(Btn.A)) { Confirm(); return; }
        Refresh();
    }

    void Adjust(int d)
    {
        if (_idx == 0) _main.AdjustWatts(d);
        else if (_idx == 1) _main.CycleProfile(d);
        else if (_idx == 3) _main.CycleFan(d);
        Refresh();
    }

    void Confirm()
    {
        switch (_idx)
        {
            case 2:
                if (!_main.Booster && !_main.BoosterAccepted) { CloseMenu(backToGame: false); _main.ToggleBooster(); return; }
                _main.ToggleBooster();
                break;
            case 3: _main.CycleFan(+1); break;
            case 4: CloseMenu(backToGame: false); _main.ShowHub(); return;
            case 5: CloseMenu(); return;
        }
        Refresh();
    }

    void Refresh()
    {
        BtnTdp.Content = _main.Booster ? $"⚡  TDP: {_main.EffectiveWatts} W (Booster)" : $"⚡  TDP: {_main.EffectiveWatts} W      ◀ ▶";
        BtnProf.Content = $"☰  Perfil: {_main.ProfileLabel}      ◀ ▶";
        BtnFan.Content = $"🌀  Ventoinhas: {_main.FanName}      ◀ ▶";
        BtnBoost.Content = _main.Booster ? "🔥  Booster: LIGADO" : "🔥  Booster: desligado";
        for (int i = 0; i < _rows.Length; i++) _rows[i].Tag = i == _idx ? "sel" : null;
        GameName.Text = _main.PlayingName?.ToUpperInvariant() ?? "NENHUM JOGO DETETADO";
        Session.Text = _main.PlayingName == null ? "" : $"Nesta sessão: {Fmt.Playtime(_main.SessionSeconds)}";
        Clock.Text = DateTime.Now.ToString("HH:mm");
    }

    async void RefreshStats()
    {
        var (pct, charging) = Battery.Get();
        Bat.Text = pct < 0 ? "—" : $"{pct}%{(charging ? " ⚡" : "")}";
        Refresh();
        if (_busy) return;
        _busy = true;
        var (temp, power) = await Task.Run(Tdp.Info);
        var (rpm, _) = await Task.Run(Fans.Rpm);
        _busy = false;
        FanRpm.Text = rpm is int r ? $"{r / 1000.0:0.0}k" : "—";
        CpuTemp.Text = temp is double t ? $"{t:0}°C" : "—";
        Power.Text = power is double p ? $"{p:0.0} W" : "—";
    }
}
