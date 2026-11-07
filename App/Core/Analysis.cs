using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FF14ModManager.Core;

public sealed record ReportRow(string Kind, string Item, string Detail);

/// <summary>A mod that changes a game file or outfit another mod also changes.</summary>
public sealed record Overlap(Mod Mod, List<Mod> With, List<string> Outfits, List<string> Paths, int? Priority)
{
    public string WithText => string.Join(", ", With.Select(m => m.Name));
    public string OutfitsText => string.Join(", ", Outfits);
}

public static partial class Analysis
{
    /// <summary>Designs that link a mod (by directory, falling back to name for designs saved without one).</summary>
    public static List<Design> UsedBy(IEnumerable<Design> designs, Mod mod) =>
        designs.Where(d => d.Mods.Any(m => Links(m, mod))).ToList();

    static bool Links(DesignMod dm, Mod mod) =>
        dm.Dir != "" ? dm.Dir.Equals(mod.Dir, StringComparison.OrdinalIgnoreCase) : dm.Name.Equals(mod.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>B: cleanup candidates. Report only; nothing here deletes.</summary>
    public static List<ReportRow> Cleanup(PenumbraData p, List<Design> designs)
    {
        var rows = new List<ReportRow>();
        foreach (var m in p.Mods)
        {
            var on = p.Collections.Where(c => c.Settings.TryGetValue(m.Dir, out var s) && s.Enabled).Select(c => c.Name).ToList();
            if (on.Count == 0 && UsedBy(designs, m).Count == 0)
                rows.Add(new("Unused mod", m.Name, $"enabled in no collection, linked by no design · {m.SizeText} · {Display(m.Folder)}"));
        }
        foreach (var d in designs)
            foreach (var dm in d.Mods.Where(dm => !p.Mods.Any(m => Links(dm, m))))
                rows.Add(new("Design links missing mod", d.Name, $"{dm.Name} ({dm.Dir})"));
        foreach (var m in p.Mods.Where(m => m.MetaError != null))
            rows.Add(new("Broken meta.json", m.Dir, m.MetaError!));
        // Penumbra keeps settings for deleted mods (so a reinstall restores them); worth knowing, harmless.
        var onDisk = p.Mods.Select(m => m.Dir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in p.Collections)
            if (c.Settings.Keys.Count(k => !onDisk.Contains(k)) is var n and > 0)
                rows.Add(new("Stale collection settings", c.Name, $"{n} settings ({c.Settings.Count(kv => kv.Value.Enabled && !onDisk.Contains(kv.Key))} enabled) for mods no longer on disk"));
        return rows;
    }

    /// <summary>F: same name+author, folder names that differ only by a "(2)"-style reimport suffix, and similar names.</summary>
    public static List<ReportRow> Duplicates(List<Mod> mods)
    {
        var rows = new List<ReportRow>();
        var group = new Dictionary<Mod, int>();   // mod -> exact group it was reported in, so the similar-name pass skips that pair
        foreach (var g in mods.GroupBy(m => (Norm(m.Name), Norm(m.Author))).Where(g => g.Count() > 1))
        {
            foreach (var m in g) group[m] = rows.Count;
            rows.Add(new("Same name + author", g.First().Name, string.Join("  |  ", g.Select(m => m.Dir))));
        }
        foreach (var g in mods.GroupBy(m => Norm(ReimportSuffix().Replace(m.Dir, ""))).Where(g => g.Count() > 1 && !g.All(group.ContainsKey)))
        {
            foreach (var m in g) group[m] = rows.Count;
            rows.Add(new("Reimported folder", g.First().Dir, string.Join("  |  ", g.Select(m => m.Dir))));
        }
        rows.AddRange(SimilarNames(mods, group));
        return rows;
    }

    [GeneratedRegex(@"\s*\(\d+\)\s*$")]
    private static partial Regex ReimportSuffix();

    /// <summary>
    /// Word-based, because mod sets differ by one short word ("(Front)"/"(Behind)", "AST"/"BLM") and a
    /// letter-similarity score calls those duplicates. Two names are similar when, ignoring case, punctuation,
    /// word order and version tags, they have the same words (a typo in a long word allowed), or one is the
    /// other plus at most two words. When each has words the other lacks, they are a set, not a duplicate.
    /// Groups chain: A~B and B~C are reported together.
    /// </summary>
    static List<ReportRow> SimilarNames(List<Mod> mods, Dictionary<Mod, int> exactGroup)
    {
        var words = mods.Select(m => Words(m.Name)).ToArray();
        var parent = Enumerable.Range(0, mods.Count).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
        for (var a = 0; a < mods.Count; a++)
            for (var b = a + 1; b < mods.Count; b++)
            {
                if (exactGroup.TryGetValue(mods[a], out var ga) && exactGroup.TryGetValue(mods[b], out var gb) && ga == gb) continue;
                if (SimilarWords(words[a], words[b])) parent[Root(a)] = Root(b);
            }
        return Enumerable.Range(0, mods.Count).GroupBy(Root).Where(g => g.Count() > 1)
            .Select(g => g.Select(i => mods[i]).ToList())
            .Select(ms => new ReportRow("Similar name", ms[0].Name, string.Join("  |  ", ms.Select(m => m.Name))))
            .ToList();
    }

    [GeneratedRegex(@"^(v\d+(\.\d+)*[a-z]?|\d+(\.\d+)+)$")]
    private static partial Regex VersionTag();

    // A list, not a set: "Body+ (Body)" vs "Body+ (Muscle)" differ only once "body" counts twice.
    static List<string> Words(string name) =>
        Regex.Split(StripAccents(name.ToLowerInvariant()), @"[^\p{L}\p{N}.]+").Select(w => w.Trim('.'))
            .Where(w => w != "" && !VersionTag().IsMatch(w)).ToList();

    static string StripAccents(string s) =>
        new string(s.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(System.Text.NormalizationForm.FormC);

    static bool SimilarWords(List<string> a, List<string> b)
    {
        var onlyA = new List<string>(a);
        var onlyB = new List<string>();
        foreach (var w in b) if (!onlyA.Remove(w)) onlyB.Add(w);
        foreach (var w in onlyA.ToList())
            if (onlyB.FirstOrDefault(v => Typo(w, v)) is { } match) { onlyA.Remove(w); onlyB.Remove(match); }
        if (onlyA.Count > 0 && onlyB.Count > 0) return false;   // each has its own words: a set (Front/Behind)
        if (onlyA.Count + onlyB.Count > 2) return false;
        var shorter = onlyA.Count == 0 ? a : b;
        // one word ("Chained", "Bastet") or under 6 letters ("Idle 2") is too little name to call a match
        return shorter.Count >= 2 && shorter.Sum(w => w.Count(char.IsLetter)) >= 6;
    }

    // A dropped or doubled letter in a word of 5+ ("Outfit"/"Outfitt"); a changed letter only in 8+ ("Basket"/"Bastet" are different words).
    static bool Typo(string a, string b)
    {
        if (Math.Min(a.Length, b.Length) < 5 || Math.Abs(a.Length - b.Length) > 2) return false;
        var d = Levenshtein(a, b);
        return a.Length != b.Length ? d == Math.Abs(a.Length - b.Length) && d == 1 : Math.Min(a.Length, b.Length) >= 8 && d <= (a.Length >= 9 ? 2 : 1);
    }

    static int Levenshtein(string a, string b)
    {
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1], prev[j]) + 1, prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = cur;
        }
        return prev[b.Length];
    }

    static string Norm(string s) => s.Trim().ToLowerInvariant();

    /// <summary>E: total size per Penumbra folder (mods directly in it plus its subfolders), largest first.</summary>
    public static List<ReportRow> FolderSizes(List<Mod> mods)
    {
        var totals = new Dictionary<string, (long Size, int Count)>();
        foreach (var m in mods.Where(m => m.Size >= 0))
        {
            var parts = m.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i <= parts.Length; i++)
            {
                var key = i == 0 ? "(all mods)" : string.Join('/', parts[..i]);
                totals.TryGetValue(key, out var t);
                totals[key] = (t.Size + m.Size, t.Count + 1);
            }
        }
        return totals.OrderByDescending(kv => kv.Value.Size)
            .Select(kv => new ReportRow("Folder size", kv.Key, $"{kv.Value.Size / (double)(1 << 20):#,0} MB · {kv.Value.Count} mods")).ToList();
    }

    /// <summary>G: mods whose effective state differs between two collections.</summary>
    public static List<ReportRow> Compare(PenumbraData p, Collection a, Collection b)
    {
        var rows = new List<ReportRow>();
        foreach (var m in p.Mods)
        {
            var sa = p.Effective(a, m.Dir);
            var sb = p.Effective(b, m.Dir);
            bool ea = sa?.Enabled == true, eb = sb?.Enabled == true;
            if (ea && !eb) rows.Add(new($"Only in {a.Name}", m.Name, Display(m.Folder)));
            else if (eb && !ea) rows.Add(new($"Only in {b.Name}", m.Name, Display(m.Folder)));
            else if (ea && eb && (sa!.Priority != sb!.Priority || sa.OptionsJson != sb.OptionsJson))
                rows.Add(new("Options differ", m.Name, $"priority {sa.Priority} vs {sb.Priority}; {Compact(sa.OptionsJson)} vs {Compact(sb.OptionsJson)}"));
        }
        return rows;
    }

    static string Compact(string json) => Regex.Replace(json, @"\s+", "");

    [GeneratedRegex(@"^chara/equipment/(e\d{4})/.*\1_(met|top|glv|dwn|sho)[_.]")]
    private static partial Regex EquipPath();
    [GeneratedRegex(@"^chara/accessory/(a\d{4})/.*\1_(ear|nek|wrs|rir|ril)[_.]")]
    private static partial Regex AccessoryPath();
    [GeneratedRegex(@"^chara/weapon/(w\d{4})/obj/body/(b\d{4})/")]
    private static partial Regex WeaponPath();

    static readonly Dictionary<string, string> Slots = new()
    {
        ["met"] = "Head", ["top"] = "Body", ["glv"] = "Hands", ["dwn"] = "Legs", ["sho"] = "Feet",
        ["ear"] = "Earrings", ["nek"] = "Necklace", ["wrs"] = "Bracelets", ["rir"] = "Ring (R)", ["ril"] = "Ring (L)",
    };

    /// <summary>
    /// The gear piece a game path belongs to: model set + slot, e.g. "e6015 Body", for every race and file type of it.
    /// shortcut: model ids, not item names. Names live in the game's own data sheets (one model can be several
    /// dyed/renamed items); upgrade by reading them in game through the Bridge (Penumbra GetChangedItems).
    /// Null for anything that is not gear (bodies, faces, hair, animations, UI): those overlap by file only.
    /// </summary>
    public static string? Outfit(string gamePath)
    {
        // e0000 is no gear at all: the bare body body mods replace, so name it.
        if (EquipPath().Match(gamePath) is { Success: true } e)
            return $"{e.Groups[1].Value} {Slots[e.Groups[2].Value]}{(e.Groups[1].Value == "e0000" ? " (smallclothes)" : "")}";
        if (AccessoryPath().Match(gamePath) is { Success: true } a) return $"{a.Groups[1].Value} {Slots[a.Groups[2].Value]}";
        if (WeaponPath().Match(gamePath) is { Success: true } w) return $"{w.Groups[1].Value}-{w.Groups[2].Value} Weapon";
        return null;
    }

    /// <summary>
    /// Every mod that changes a game file or gear piece another mod also changes.
    /// enabledIn = null: all installed mods, every option. A collection: only mods enabled in it (inheritance applied),
    /// with only the options selected there, which is what the game actually loads; Priority then says who wins.
    /// </summary>
    public static List<Overlap> Overlaps(PenumbraData p, Collection? enabledIn)
    {
        var paths = new Dictionary<Mod, HashSet<string>>();
        var priority = new Dictionary<Mod, int>();
        foreach (var m in p.Mods)
        {
            if (enabledIn == null) { paths[m] = m.AllPaths.ToHashSet(); continue; }
            if (p.Effective(enabledIn, m.Dir) is not { Enabled: true } s) continue;
            paths[m] = m.ActivePaths(OptionSettings(s.OptionsJson)).ToHashSet();
            priority[m] = s.Priority;
        }
        var outfits = paths.ToDictionary(kv => kv.Key, kv => kv.Value.Select(Outfit).OfType<string>().ToHashSet());
        var byPath = new Dictionary<string, List<Mod>>();
        var byOutfit = new Dictionary<string, List<Mod>>();
        foreach (var (m, ps) in paths)
        {
            foreach (var x in ps) (byPath.TryGetValue(x, out var l) ? l : byPath[x] = []).Add(m);
            foreach (var o in outfits[m]) (byOutfit.TryGetValue(o, out var l) ? l : byOutfit[o] = []).Add(m);
        }
        var result = new List<Overlap>();
        foreach (var (m, ps) in paths)
        {
            var sharedPaths = ps.Where(x => byPath[x].Count > 1).Order(StringComparer.Ordinal).ToList();
            var sharedOutfits = outfits[m].Where(o => byOutfit[o].Count > 1).Order(StringComparer.Ordinal).ToList();
            var with = sharedPaths.SelectMany(x => byPath[x]).Concat(sharedOutfits.SelectMany(o => byOutfit[o])).Where(x => x != m).Distinct()
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (with.Count > 0) result.Add(new Overlap(m, with, sharedOutfits, sharedPaths, enabledIn == null ? null : priority[m]));
        }
        return result.OrderBy(o => o.Mod.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>What two mods share, across all their options.</summary>
    public static (List<string> Outfits, int Files) Shared(Mod a, Mod b)
    {
        var pa = a.AllPaths.ToHashSet();
        return (a.Outfits.Intersect(b.Outfits).Order(StringComparer.Ordinal).ToList(), b.AllPaths.Count(pa.Contains));
    }

    /// <summary>A collection's per-mod "Settings" object: option group name -> Single index / Multi bitmask.</summary>
    static Dictionary<string, ulong> OptionSettings(string json)
    {
        var d = new Dictionary<string, ulong>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.TryGetUInt64(out var v)) d[p.Name] = v;
        }
        catch (System.Text.Json.JsonException) { }   // unreadable settings: fall back to each group's defaults
        return d;
    }

    public static string Display(string folder) => folder == "" ? "(root)" : folder;

    // D: inventory export. Columns match the earlier Penumbra Inventory CSV, plus state and size.
    public static string ToCsv(PenumbraData p, Collection? c)
    {
        var sb = new StringBuilder("Penumbra folder,Mod,Directory name,Author,Version,Tags,Favorite,Enabled,Size MB\r\n");
        foreach (var m in p.Mods.OrderBy(m => m.Folder).ThenBy(m => m.Name))
            sb.AppendJoin(',', new[] { Display(m.Folder), m.Name, m.Dir, m.Author, m.Version, m.AllTags, m.Favorite ? "yes" : "",
                c != null && p.Effective(c, m.Dir)?.Enabled == true ? "yes" : "", m.Size < 0 ? "" : (m.Size / (double)(1 << 20)).ToString("0.0") }
                .Select(Csv)).Append("\r\n");
        return sb.ToString();
    }

    static string Csv(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    public static string ToHtml(PenumbraData p, List<Design> designs, Collection? c)
    {
        static string H(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder($"<!doctype html><meta charset=utf-8><title>Penumbra inventory</title><style>body{{font:14px system-ui;margin:2em}}table{{border-collapse:collapse}}td,th{{border:1px solid #ccc;padding:3px 6px;text-align:left}}</style>");
        sb.Append($"<h1>Penumbra inventory: {H(p.ModDirectory)}</h1><p>{p.Mods.Count} mods · {designs.Count} Glamourer designs · exported {DateTime.Now:yyyy-MM-dd HH:mm}{(c != null ? $" · enabled = collection “{H(c.Name)}”" : "")}</p>");
        sb.Append("<h2>Mods</h2><table><tr><th>Folder<th>Mod<th>Author<th>Version<th>Tags<th>Enabled<th>Size</tr>");
        foreach (var m in p.Mods.OrderBy(m => m.Folder).ThenBy(m => m.Name))
            sb.Append($"<tr><td>{H(Display(m.Folder))}<td>{H(m.Name)}<td>{H(m.Author)}<td>{H(m.Version)}<td>{H(m.AllTags)}<td>{(c != null && p.Effective(c, m.Dir)?.Enabled == true ? "yes" : "")}<td>{H(m.SizeText)}</tr>");
        sb.Append("</table><h2>Glamourer designs</h2><table><tr><th>Folder<th>Design<th>Tags<th>Last edit<th>Linked mods</tr>");
        foreach (var d in designs)
            sb.Append($"<tr><td>{H(Display(d.Folder))}<td>{H(d.Name)}<td>{H(d.TagsText)}<td>{d.LastEdit:yyyy-MM-dd}<td>{H(string.Join(", ", d.Mods.Select(x => x.Name)))}</tr>");
        return sb.Append("</table>").ToString();
    }

    /// <summary>H: zip Penumbra + Glamourer config folders. Files the game holds open are read shared; unreadable ones are listed.</summary>
    public static (int Files, List<string> Skipped) Backup(string pluginConfigs, string zipPath)
    {
        var skipped = new List<string>();
        var count = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var plugin in new[] { "Penumbra", "Glamourer" })
        {
            var root = Path.Combine(pluginConfigs, plugin);
            if (!Directory.Exists(root)) continue;
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dst = zip.CreateEntry(Path.GetRelativePath(pluginConfigs, f).Replace('\\', '/')).Open();
                    src.CopyTo(dst);
                    count++;
                }
                catch (IOException e) { skipped.Add($"{f}: {e.Message}"); }
            }
        }
        return (count, skipped);
    }
}
