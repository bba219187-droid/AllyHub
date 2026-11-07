using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AllyHub;

public class GameStats
{
    public long Seconds { get; set; }
    public DateTime? LastPlayed { get; set; }
    public bool Favorite { get; set; }
}

public class ManualGame
{
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
}

public class Config
{
    // Perfis em watts. Edita config.json para afinar.
    public Dictionary<string, int> Profiles { get; set; } = new()
    {
        ["Silencioso"] = 10,
        ["Equilibrado"] = 15,
        ["Desempenho"] = 20,
        ["Turbo"] = 25,
    };
    public int DefaultWatts { get; set; } = 15;
    public int MinWatts { get; set; } = 7;
    public int MaxWatts { get; set; } = 30;   // 30 W so ligado a corrente
    public Dictionary<string, int> GameWatts { get; set; } = new();
    public Dictionary<string, GameStats> Stats { get; set; } = new();
    public List<ManualGame> Manual { get; set; } = new();
    public string Accent { get; set; } = "#FF1F3D";
    public bool Sounds { get; set; } = true;
    public int FanPreset { get; set; }          // indice em Fans.Presets (0 = automatico da ASUS)
    public bool BoosterAccepted { get; set; }
    public DateTime? BoosterAcceptedAt { get; set; }

    public GameStats StatsFor(string id) => Stats.TryGetValue(id, out var s) ? s : Stats[id] = new GameStats();

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllyHub");
    static readonly string FilePath = Path.Combine(Dir, "config.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Config Load()
    {
        try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* disco cheio / sem permissao: nao rebentar a app */ }
    }
}

public static class Tdp
{
    public static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "ryzenadj.exe");
    public static bool Available => File.Exists(Exe);

    /// Booster = limite maximo do fabricante (Turbo da ASUS): 30 W na corrente, 25 W na bateria.
    public static int BoosterWatts => Battery.Get().charging ? 30 : 25;

    public static string Args(int watts)
    {
        int mw = watts * 1000;
        // ponytail: stapm = fast = slow; separar fast-limit se quiseres boosts curtos
        return $"--stapm-limit={mw} --fast-limit={mw} --slow-limit={mw}";
    }

    /// Devolve null se correu bem, senao a mensagem de erro.
    public static string? Apply(int watts)
    {
        if (!Available) return "ryzenadj.exe nao encontrado na pasta da app";
        var (code, _, err) = Run(Args(watts));
        return code == 0 ? null : (err.Trim() is { Length: > 0 } e ? e : $"codigo {code}");
    }

    /// Temperatura do CPU (C) e potencia atual (W), lidas pelo RyzenAdj.
    public static (double? temp, double? power) Info()
    {
        if (!Available) return (null, null);
        var (code, output, _) = Run("-i");
        return code == 0 ? ParseInfo(output) : (null, null);
    }

    public static (double? temp, double? power) ParseInfo(string table)
    {
        double? Val(string name)
        {
            var m = Regex.Match(table, $@"\|\s*{name}\s*\|\s*([-\d.]+)", RegexOptions.IgnoreCase);
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        return (Val("THM VALUE CORE"), Val("SOCKET POWER") ?? Val("STAPM VALUE"));
    }

    static (int code, string output, string error) Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Exe, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardError = true, RedirectStandardOutput = true,
            })!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return (-1, "", "tempo esgotado"); }
            return (p.ExitCode, output.Result, error.Result);
        }
        catch (Exception e) { return (-1, "", e.Message); }
    }

    /// Plano de energia do Windows: "Melhor desempenho" (booster) ou "Equilibrado".
    public static void BestPerformance(bool on)
    {
        var guid = on ? "ded574b5-45a0-4f42-8737-46345c09c238" : "00000000-0000-0000-0000-000000000000";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg", $"/overlaysetactive {guid}") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(5000);
        }
        catch { }
    }
}

public static class Fmt
{
    public static string Playtime(long seconds) => seconds switch
    {
        < 60 => seconds == 0 ? "Nunca jogado" : "< 1 min",
        < 3600 => $"{seconds / 60} min",
        _ => $"{seconds / 3600} h {seconds % 3600 / 60:00} min",
    };

    public static string LastPlayed(DateTime? when, DateTime now)
    {
        if (when is not DateTime w) return "—";
        var days = (now.Date - w.Date).Days;
        return days switch
        {
            0 => "Hoje",
            1 => "Ontem",
            < 7 => $"Há {days} dias",
            _ => w.ToString("dd/MM/yyyy"),
        };
    }
}
