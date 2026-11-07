using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AllyHub;

/// Atualizacoes automaticas a partir dos Releases do GitHub.
public static class Updater
{
    // >>> MUDA ISTO para o teu repositorio, ex.: "pedrooliveira/AllyHub" <<<
    public const string Repo = "bba219187-droid/AllyHub";

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);
    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    public static bool IsNewer(string tag, Version current) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out var latest) && latest > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    /// Se houver versao nova: descarrega, troca o .exe e devolve a versao. Senao devolve null.
    public static async Task<string?> CheckAndApply()
    {
        if (Repo.StartsWith("O-TEU")) return null;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AllyHub-Updater");

        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!IsNewer(tag, Current)) return null;

        var url = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Where(a => a.GetProperty("name").GetString() == "AllyHub.exe")
            .Select(a => a.GetProperty("browser_download_url").GetString())
            .FirstOrDefault();
        if (url == null) return null;

        var exe = Environment.ProcessPath!;
        var tmp = exe + ".new";
        await File.WriteAllBytesAsync(tmp, await http.GetByteArrayAsync(url));
        // O Windows deixa renomear um .exe em execucao; o antigo e apagado no proximo arranque
        File.Move(exe, exe + ".old", true);
        File.Move(tmp, exe);
        return tag.TrimStart('v', 'V');
    }

    public static void Cleanup()
    {
        try { File.Delete(Environment.ProcessPath + ".old"); } catch { }
    }
}

/// Arrancar com o Windows: tarefa agendada ao iniciar sessao, com privilegios elevados
/// (o atalho na pasta Arranque nao consegue pedir administrador sozinho).
public static class AutoStart
{
    const string Task = "AllyHub";

    public static bool Enabled => Run("schtasks", $"/Query /TN {Task}") == 0;

    public static void Set(bool on)
    {
        var exe = Environment.ProcessPath!.Replace("'", "''");
        var ps = on
            ? "$a=New-ScheduledTaskAction -Execute '" + exe + "';" +
              "$t=New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME; $t.Delay='PT3S';" +
              // importante num portatil: correr tambem na bateria
              "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero);" +
              "$p=New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive;" +
              $"Register-ScheduledTask -TaskName {Task} -Action $a -Trigger $t -Settings $s -Principal $p -Force"
            : $"Unregister-ScheduledTask -TaskName {Task} -Confirm:$false";
        Run("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"");
    }

    static int Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}

public static class Battery
{
    [StructLayout(LayoutKind.Sequential)]
    struct Status { public byte AC, Flag, Percent, Saver; public int Life, FullLife; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out Status s);

    /// (percentagem 0-100 ou -1, ligado a corrente)
    public static (int percent, bool charging) Get() =>
        GetSystemPowerStatus(out var s) ? (s.Percent == 255 ? -1 : s.Percent, s.AC == 1) : (-1, false);
}
