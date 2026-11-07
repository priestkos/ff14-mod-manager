using System.Text.Json;
using LiteDB;

namespace FF14ModManager.Core;

public sealed class Mod
{
    public string Dir { get; init; } = "";            // folder name under ModDirectory; Penumbra's key everywhere
    public string FullPath { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string Website { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public string Folder { get; set; } = "";          // Penumbra folder path, "" = root
    public bool Favorite { get; set; }
    public string[] LocalTags { get; set; } = [];
    public string Note { get; set; } = "";
    public DateTime? ImportDate { get; set; }
    public long Size { get; set; } = -1;              // -1 = not measured yet
    public string? MetaError { get; init; }
    public bool Enabled { get; set; }                 // in the collection picked in the UI; set by the UI
    public string[] DefaultPaths { get; init; } = [];   // game paths the mod always changes (normalised: lower case, '/')
    public ModGroup[] Groups { get; init; } = [];
    public string[] Outfits { get; init; } = [];        // distinct Analysis.Outfit keys over AllPaths, for search
    public IEnumerable<string> AllPaths => DefaultPaths.Concat(Groups.SelectMany(g => g.Options.SelectMany(o => o))).Distinct();

    /// <summary>Game paths changed with these group settings (group name -> Penumbra setting value).</summary>
    public IEnumerable<string> ActivePaths(IReadOnlyDictionary<string, ulong> settings)
    {
        var paths = new HashSet<string>(DefaultPaths);
        foreach (var g in Groups)
        {
            var s = settings.TryGetValue(g.Name, out var v) ? v : g.DefaultSettings;
            IEnumerable<int> chosen = g.Type switch
            {
                "Single" or "Combining" => s < (ulong)g.Options.Length ? [(int)s] : [],   // Combining: the bitmask IS the container index
                "Multi" => Enumerable.Range(0, Math.Min(64, g.Options.Length)).Where(i => ((s >> i) & 1) == 1),
                _ => [],
            };
            foreach (var i in chosen) paths.UnionWith(g.Options[i]);
        }
        return paths;
    }

    public string AllTags => string.Join(", ", Tags.Concat(LocalTags).Distinct());
    public string SizeText => Size < 0 ? "…" : Size >= 1 << 30 ? $"{Size / (double)(1 << 30):0.0} GB" : $"{Size / (double)(1 << 20):0.0} MB";
}

/// <summary>An option group; Options[i] = game paths option i changes (Combining groups: container i).</summary>
public sealed record ModGroup(string Name, string Type, ulong DefaultSettings, string[][] Options);

public sealed record ModSetting(bool Enabled, int Priority, string OptionsJson);

public sealed class Collection
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string[] Inheritance { get; init; } = [];
    public Dictionary<string, ModSetting> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public override string ToString() => Name;
}

public sealed class PenumbraData
{
    public string ModDirectory { get; init; } = "";
    public List<Mod> Mods { get; init; } = [];
    public List<Collection> Collections { get; init; } = [];
    public Dictionary<string, string> Active { get; init; } = [];   // role ("Current", "Yourself"...) -> collection id
    public string? LocalDataError { get; init; }

    public Collection? ById(string id) => Collections.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Own setting first, then inherited collections in order (Penumbra's resolution order).</summary>
    public ModSetting? Effective(Collection c, string dir, HashSet<string>? seen = null)
    {
        if (c.Settings.TryGetValue(dir, out var s)) return s;
        seen ??= new(StringComparer.OrdinalIgnoreCase);
        if (!seen.Add(c.Id)) return null;             // inheritance cycle guard
        foreach (var parent in c.Inheritance.Select(ById).OfType<Collection>())
            if (Effective(parent, dir, seen) is { } inherited) return inherited;
        return null;
    }

    static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <param name="pluginConfigs">%APPDATA%\XIVLauncher\pluginConfigs</param>
    /// <param name="cacheDir">where mod_data.db is copied before reading, so Penumbra's lock is never touched</param>
    public static PenumbraData Load(string pluginConfigs, string cacheDir)
    {
        var root = Path.Combine(pluginConfigs, "Penumbra");
        var modDir = FindModDirectory(pluginConfigs)
            ?? throw new InvalidOperationException($"Penumbra ModDirectory not found under {root}");
        var mods = Directory.Exists(modDir)
            ? Directory.GetDirectories(modDir).Select(ReadMod).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : throw new DirectoryNotFoundException($"Penumbra ModDirectory does not exist: {modDir}");

        string? localErr = null;
        try { ApplyLocalData(mods, Path.Combine(root, "mod_data.db"), cacheDir); }
        catch (Exception e) { localErr = $"mod_data.db unreadable ({e.Message}); folders/favourites/local tags not shown"; }

        var collections = new List<Collection>();
        var colDir = Path.Combine(root, "collections");
        if (Directory.Exists(colDir))
            foreach (var f in Directory.GetFiles(colDir, "*.json"))
                if (ReadCollection(f) is { } c) collections.Add(c);

        var active = new Dictionary<string, string>();
        var activeFile = Path.Combine(root, "active_collections.json");
        if (File.Exists(activeFile))
        {
            using var doc = JsonDocument.Parse(ReadShared(activeFile), Lenient);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) active[p.Name] = p.Value.GetString()!;
        }

        return new PenumbraData
        {
            ModDirectory = modDir, Mods = mods, LocalDataError = localErr, Active = active,
            Collections = collections.Where(c => c.Id != Guid.Empty.ToString()).OrderBy(c => c.Name).ToList(),
        };
    }

    static string? FindModDirectory(string pluginConfigs)
    {
        var root = Path.Combine(pluginConfigs, "Penumbra");
        // Penumbra 1.x splits its config into Penumbra/config/*.json; older builds used pluginConfigs/Penumbra.json.
        var candidates = (Directory.Exists(Path.Combine(root, "config")) ? Directory.GetFiles(Path.Combine(root, "config"), "*.json") : [])
            .Append(Path.Combine(pluginConfigs, "Penumbra.json"));
        foreach (var f in candidates.Where(File.Exists))
        {
            try
            {
                using var doc = JsonDocument.Parse(ReadShared(f), Lenient);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("ModDirectory", out var v) && v.GetString() is { Length: > 0 } dir)
                    return dir;
            }
            catch (JsonException) { /* a half-written config file: try the next one */ }
        }
        return null;
    }

    static Mod ReadMod(string path)
    {
        var dir = Path.GetFileName(path);
        var meta = Path.Combine(path, "meta.json");
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(meta), Lenient);
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            string[] defaults = [];
            ModGroup[] groups = [];
            string? filesError = null;
            try { (defaults, groups) = ReadChangedFiles(r, path); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { filesError = $"option files unreadable: {e.Message}"; }
            return new Mod
            {
                DefaultPaths = defaults, Groups = groups, MetaError = filesError,
                Outfits = defaults.Concat(groups.SelectMany(g => g.Options.SelectMany(o => o))).Select(Analysis.Outfit).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray(),
                Dir = dir, FullPath = path, Name = S("Name") is { Length: > 0 } n ? n : dir,
                Author = S("Author"), Version = S("Version"), Description = S("Description"), Website = S("Website"),
                Tags = r.TryGetProperty("ModTags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [],
            };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Mod { Dir = dir, FullPath = path, Name = dir, MetaError = $"meta.json unreadable: {e.Message}" };
        }
    }

    // Penumbra 1.7+ (meta FileVersion 4) keeps DefaultData and Groups inside meta.json; earlier versions wrote
    // default_mod.json + group_NNN_name.json beside it. Read whichever this mod has.
    static (string[] Defaults, ModGroup[] Groups) ReadChangedFiles(JsonElement meta, string modPath)
    {
        if (meta.TryGetProperty("DefaultData", out var dd) | meta.TryGetProperty("Groups", out var gs))
            return (Changed(dd), gs.ValueKind == JsonValueKind.Array ? gs.EnumerateArray().Select(ReadGroup).ToArray() : []);
        var defaultFile = Path.Combine(modPath, "default_mod.json");
        if (!File.Exists(defaultFile)) return ([], []);
        using var def = JsonDocument.Parse(ReadShared(defaultFile), Lenient);
        var groups = Directory.GetFiles(modPath, "group_*.json").Order(StringComparer.Ordinal).Select(f =>
        {
            using var g = JsonDocument.Parse(ReadShared(f), Lenient);
            return ReadGroup(g.RootElement);
        }).ToArray();
        return (Changed(def.RootElement), groups);
    }

    static ModGroup ReadGroup(JsonElement g)
    {
        string S(string k) => g.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var type = S("Type");
        // Combining groups keep their files in Containers, one per combination of options.
        var list = type == "Combining" && g.TryGetProperty("Containers", out var c) ? c : g.TryGetProperty("Options", out var o) ? o : default;
        return new ModGroup(S("Name"), type,
            g.TryGetProperty("DefaultSettings", out var d) && d.TryGetUInt64(out var dv) ? dv : 0,
            list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(Changed).ToArray() : []);
    }

    /// <summary>Game paths a container replaces (Files) or redirects (FileSwaps). Meta manipulations are not file paths.</summary>
    static string[] Changed(JsonElement container) => container.ValueKind != JsonValueKind.Object ? [] :
        new[] { "Files", "FileSwaps" }
            .Where(k => container.TryGetProperty(k, out var f) && f.ValueKind == JsonValueKind.Object)
            .SelectMany(k => container.GetProperty(k).EnumerateObject().Select(p => p.Name.Replace('\\', '/').ToLowerInvariant()))
            .Distinct().ToArray();

    static void ApplyLocalData(List<Mod> mods, string dbFile, string cacheDir)
    {
        if (!File.Exists(dbFile)) return;
        Directory.CreateDirectory(cacheDir);
        var copy = Path.Combine(cacheDir, "mod_data.db");
        CopyShared(dbFile, copy);
        var log = Path.Combine(Path.GetDirectoryName(dbFile)!, "mod_data-log.db");   // uncommitted pages, if Penumbra has any
        var logCopy = Path.Combine(cacheDir, "mod_data-log.db");
        if (File.Exists(log)) CopyShared(log, logCopy); else File.Delete(logCopy);

        using var db = new LiteDatabase($"Filename={copy};ReadOnly=true");
        var byDir = mods.ToDictionary(m => m.Dir, StringComparer.OrdinalIgnoreCase);
        foreach (var d in db.GetCollection("LocalModData").FindAll())
        {
            if (!byDir.TryGetValue(d["_id"].AsString ?? "", out var m)) continue;   // records outlive deleted mods
            m.Folder = d["Folder"].IsString ? d["Folder"].AsString : "";
            m.Favorite = d["Favorite"].IsBoolean && d["Favorite"].AsBoolean;
            m.Note = d["Note"].IsString ? d["Note"].AsString : "";
            m.LocalTags = d["LocalTags"].IsArray ? d["LocalTags"].AsArray.Where(x => x.IsString).Select(x => x.AsString).ToArray() : [];
            if (d["ImportDate"].IsNumber) m.ImportDate = DateTimeOffset.FromUnixTimeMilliseconds(d["ImportDate"].AsInt64).LocalDateTime;
        }
    }

    static Collection? ReadCollection(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(file), Lenient);
            var r = doc.RootElement;
            var c = new Collection
            {
                Id = r.GetProperty("Id").GetString() ?? "",
                Name = r.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                Inheritance = r.TryGetProperty("Inheritance", out var inh) && inh.ValueKind == JsonValueKind.Array
                    ? inh.EnumerateArray().Select(x => x.ToString()).ToArray() : [],
            };
            if (r.TryGetProperty("Settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
                foreach (var p in settings.EnumerateObject())
                    c.Settings[p.Name] = new ModSetting(
                        p.Value.TryGetProperty("Enabled", out var en) && en.ValueKind == JsonValueKind.True,
                        p.Value.TryGetProperty("Priority", out var pr) && pr.TryGetInt32(out var pri) ? pri : 0,
                        p.Value.TryGetProperty("Settings", out var o) ? o.GetRawText() : "{}");
            return c;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return null;   // a collection mid-write: skip it rather than fail the whole load
        }
    }

    /// <summary>Read without blocking or being blocked by the game writing the same file.</summary>
    public static string ReadShared(string file)
    {
        using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static void CopyShared(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(to, FileMode.Create, FileAccess.Write);
        src.CopyTo(dst);
    }

    public static long DirSize(string path)
    {
        try { return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}
