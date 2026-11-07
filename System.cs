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

    /// Se houver versao nova: descarrega (com progresso 0-100), troca o .exe e devolve a versao.
    /// Senao devolve null. Lanca excecao se o download falhar.
    public static async Task<string?> CheckAndApply(IProgress<int>? progress = null)
    {
        if (Repo.StartsWith("O-TEU")) return null;
        // sem timeout global: o .exe tem ~70 MB; cada fase tem o seu limite
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AllyHub-Updater");

        string json;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", cts.Token);

        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!IsNewer(tag, Current)) return null;

        var url = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Where(a => a.GetProperty("name").GetString() == "AllyHub.exe")
            .Select(a => a.GetProperty("browser_download_url").GetString())
            .FirstOrDefault();
        if (url == null) return null;

        var exe = Environment.ProcessPath!;
        var tmp = exe + ".new";
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
            await using var dst = File.Create(tmp);
            var buf = new byte[1 << 16];
            long done = 0;
            int n, last = -1;
            while ((n = await src.ReadAsync(buf, cts.Token)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
                done += n;
                var pct = total > 0 ? (int)(done * 100 / total) : 0;
                if (pct != last) { progress?.Report(pct); last = pct; }
            }
            if (total > 0 && done != total) throw new IOException("download incompleto");
        }

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

/// Informacao do sistema e atualizacoes (Windows Update inclui drivers da ASUS/AMD).
public static class SysInfo
{
    public static string Ps(string script, int timeoutMs = 30000)
    {
        try
        {
            var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            using var p = Process.Start(new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {enc}")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            })!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
            return output.Result.Trim();
        }
        catch { return ""; }
    }

    /// (BIOS, GPU + versao do driver, versao do Windows)
    public static (string bios, string gpu, string windows) Basic()
    {
        var r = Ps("[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                   "$b=(Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion;" +
                   "$g=Get-CimInstance Win32_VideoController | Select-Object -First 1;" +
                   "$o=Get-CimInstance Win32_OperatingSystem;" +
                   "\"$b|$($g.Name) — driver $($g.DriverVersion)|$($o.Caption) ($($o.BuildNumber))\"").Split('|');
        string At(int i) => r.Length > i && r[i].Trim() != "" ? r[i].Trim() : "—";
        return (At(0), At(1), At(2));
    }

    /// Atualizacoes pendentes no Windows Update (drivers marcados com [Driver]).
    public static List<string> PendingUpdates() =>
        Ps("[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
           "$s=New-Object -ComObject Microsoft.Update.Session;" +
           "$r=$s.CreateUpdateSearcher().Search('IsInstalled=0 and IsHidden=0');" +
           "foreach($u in $r.Updates){ if($u.Type -eq 2){'[Driver] '+$u.Title}else{$u.Title} }", 180000)
          .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// Saude da bateria: (capacidade de fabrica mWh, capacidade atual mWh, ciclos)
    public static (long design, long full, int cycles) BatteryHealth()
    {
        var r = Ps("$d=(Get-CimInstance -Namespace root\\wmi -ClassName BatteryStaticData -ErrorAction SilentlyContinue | Select-Object -First 1).DesignedCapacity;" +
                   "$f=(Get-CimInstance -Namespace root\\wmi -ClassName BatteryFullChargedCapacity -ErrorAction SilentlyContinue | Select-Object -First 1).FullChargedCapacity;" +
                   "$c=(Get-CimInstance -Namespace root\\wmi -ClassName BatteryCycleCount -ErrorAction SilentlyContinue | Select-Object -First 1).CycleCount;" +
                   "\"$d|$f|$c\"").Split('|');
        long L(int i) => r.Length > i && long.TryParse(r[i].Trim(), out var v) ? v : 0;
        return (L(0), L(1), (int)L(2));
    }

    public static void OpenWindowsUpdate() => Open("ms-settings:windowsupdate");

    /// Abre o Armoury Crate SE (onde esta o Update Center da ASUS). false se nao encontrado.
    public static bool OpenArmouryCrate()
    {
        var id = Ps("(Get-StartApps | Where-Object { $_.Name -like '*Armoury Crate*' } | Select-Object -First 1).AppID");
        if (id == "") return false;
        Open("shell:AppsFolder\\" + id);
        return true;
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}

public static class WavGen
{
    /// WAV PCM 16-bit mono 44.1 kHz com envelope de decaimento em cada nota.
    public static byte[] Wav((double freq, double secs)[] notes, double volume) => Wav(notes, volume, true, true, mono: true);

    /// Mesma coisa em estereo, com cada canal ligado ou desligado (teste de colunas).
    public static byte[] Wav((double freq, double secs)[] notes, double volume, bool left, bool right, bool mono = false)
    {
        const int rate = 44100;
        var samples = new List<short>();
        foreach (var (f, d) in notes)
        {
            int n = (int)(rate * d);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate, env = Math.Exp(-5.0 * i / n) * Math.Min(1, i / 60.0);
                var v = (short)(Math.Sin(2 * Math.PI * f * t) * env * volume * short.MaxValue);
                if (mono) samples.Add(v);
                else { samples.Add(left ? v : (short)0); samples.Add(right ? v : (short)0); }
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int bytes = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        short ch = (short)(mono ? 1 : 2);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(ch); w.Write(rate); w.Write(rate * 2 * ch); w.Write((short)(2 * ch)); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}

public static class Native
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}

/// Descobre se um jogo esta a correr (processo com executavel dentro da pasta do jogo).
public static class Running
{
    /// 0 = nao esta a correr, 1 = processo existe, 2 = processo com janela visivel
    public static int Check(string installDir)
    {
        var dir = installDir.TrimEnd('\\', '/') + "\\";
        int best = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var f = p.MainModule?.FileName;
                if (f != null && f.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                {
                    best = Math.Max(best, p.MainWindowHandle != IntPtr.Zero ? 2 : 1);
                    if (best == 2) return 2;
                }
            }
            catch { /* processos do sistema: sem acesso */ }
            finally { p.Dispose(); }
        }
        return best;
    }

    /// Pasta do jogo que esta em primeiro plano? (para saber se a janela ja esta a frente)
    public static bool IsForeground(string installDir)
    {
        try
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
            using var p = Process.GetProcessById((int)pid);
            var f = p.MainModule?.FileName;
            return f != null && f.StartsWith(installDir.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
