using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AllyHub;

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
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
    }
}

public static class Tdp
{
    public static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "ryzenadj.exe");
    public static bool Available => File.Exists(Exe);

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
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Exe, Args(watts))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? null : p.StandardError.ReadToEnd().Trim();
        }
        catch (Exception e) { return e.Message; }
    }
}
