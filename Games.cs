using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AllyHub;

public record Game(string Id, string Name, string Store, string Launch,
    string? WorkDir = null, string? Cover = null, string? Hero = null, string? InstallDir = null);

public static class Library
{
    public static List<Game> Scan(Config cfg)
    {
        var all = new List<Game>();
        foreach (var scan in new Func<IEnumerable<Game>>[] { Steam, Epic, Gog, Ubisoft, () => Manual(cfg) })
        {
            try { all.AddRange(scan()); } catch { /* launcher nao instalado */ }
        }
        return all.GroupBy(g => g.Id).Select(g => g.First())
                  .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- Steam ----------
    static IEnumerable<Game> Steam()
    {
        var root = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (root == null) yield break;
        root = root.Replace('/', '\\');
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        var libs = File.Exists(vdf) ? ParseLibraryFolders(File.ReadAllText(vdf)) : new List<string> { root };

        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                var (id, name, dir) = ParseAppManifest(File.ReadAllText(acf));
                if (id == null || name == null || IgnoredSteam.Contains(id)) continue;
                yield return new Game("steam:" + id, name, "Steam", "steam://rungameid/" + id,
                    Cover: SteamArt(root, id, "library_600x900.jpg"),
                    Hero: SteamArt(root, id, "library_hero.jpg"),
                    InstallDir: dir == null ? null : Path.Combine(apps, "common", dir));
            }
        }
    }

    // Redistributables e ferramentas que nao sao jogos
    static readonly HashSet<string> IgnoredSteam = new() { "228980", "1070560", "1391110", "1628350", "1493710" };

    public static List<string> ParseLibraryFolders(string vdf) =>
        Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"")
             .Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))
             .ToList();

    public static (string? id, string? name, string? installdir) ParseAppManifest(string acf)
    {
        string? Get(string key) { var m = Regex.Match(acf, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase); return m.Success ? m.Groups[1].Value : null; }
        return (Get("appid"), Get("name"), Get("installdir"));
    }

    public static string? SteamArt(string root, string id, string file)
    {
        var cache = Path.Combine(root, "appcache", "librarycache");
        // Steam novo: librarycache\<id>\file ; Steam antigo: librarycache\<id>_file
        foreach (var p in new[] { Path.Combine(cache, id, file), Path.Combine(cache, id + "_" + file) })
            if (File.Exists(p)) return p;
        return null;
    }

    // ---------- Epic ----------
    static IEnumerable<Game> Epic()
    {
        var dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.GetFiles(dir, "*.item"))
        {
            var g = ParseEpicItem(File.ReadAllText(f));
            if (g != null) yield return g;
        }
    }

    public static Game? ParseEpicItem(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) return null;
        var app = S("AppName");
        if (app == "" || S("DisplayName") == "") return null;
        var url = $"com.epicgames.launcher://apps/{S("CatalogNamespace")}%3A{S("CatalogItemId")}%3A{app}?action=launch&silent=true";
        var loc = S("InstallLocation");
        return new Game("epic:" + app, S("DisplayName"), "Epic", url, InstallDir: loc == "" ? null : loc);
    }

    // ---------- GOG ----------
    static IEnumerable<Game> Gog()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (key == null) yield break;
        foreach (var sub in key.GetSubKeyNames())
        {
            using var g = key.OpenSubKey(sub);
            var name = g?.GetValue("gameName") as string;
            var exe = g?.GetValue("exe") as string;
            if (name == null || exe == null || !File.Exists(exe)) continue;
            yield return new Game("gog:" + sub, name, "GOG", exe,
                WorkDir: g!.GetValue("workingDir") as string ?? Path.GetDirectoryName(exe),
                InstallDir: g.GetValue("path") as string ?? Path.GetDirectoryName(exe));
        }
    }

    // ---------- Ubisoft Connect ----------
    static IEnumerable<Game> Ubisoft()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (key == null) yield break;
        foreach (var id in key.GetSubKeyNames())
        {
            using var g = key.OpenSubKey(id);
            var dir = (g?.GetValue("InstallDir") as string)?.Replace('/', '\\').TrimEnd('\\');
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            yield return new Game("ubi:" + id, Path.GetFileName(dir), "Ubisoft", $"uplay://launch/{id}/0", InstallDir: dir);
        }
    }

    // ---------- adicionados a mao (EA, Battle.net, emuladores...) ----------
    static IEnumerable<Game> Manual(Config cfg) =>
        cfg.Manual.Where(m => File.Exists(m.Exe)).Select(m =>
        {
            var dir = Path.GetDirectoryName(m.Exe);
            return new Game(ManualId(m.Exe), m.Name, "Manual", m.Exe, WorkDir: dir, InstallDir: dir);
        });

    public static string ManualId(string exe) => "manual:" + exe.ToLowerInvariant();
}
