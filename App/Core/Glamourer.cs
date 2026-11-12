using System.Text.Json;

namespace FF14ModManager.Core;

public sealed record DesignMod(string Name, string Dir, bool Enabled);

public sealed class Design
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public string Folder { get; init; } = "";
    public DateTime? LastEdit { get; init; }
    public int FileVersion { get; init; }
    public List<DesignMod> Mods { get; init; } = [];
    public string FilePath { get; init; } = "";
    public string TagsText => string.Join(", ", Tags);
    public string? Thumbnail { get; set; }   // set by the UI from the screenshot store
}

public static class GlamourerData
{
    static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static List<Design> Load(string pluginConfigs)
    {
        var dir = Path.Combine(pluginConfigs, "Glamourer", "designs");
        if (!Directory.Exists(dir)) return [];
        var legacyFolders = LegacySortOrder(pluginConfigs);
        var list = new List<Design>();
        foreach (var f in Directory.GetFiles(dir, "*.json"))   // *.json does not match the *.json.bak backups
            if (Read(f, legacyFolders) is { } d) list.Add(d);
        return list.OrderBy(d => d.Folder).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Design? Read(string file, IReadOnlyDictionary<string, string>? legacyFolders = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(PenumbraData.ReadShared(file), Lenient);
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var id = S("Identifier");
            var folder = S("FileSystemFolder");
            if (folder == "" && legacyFolders?.TryGetValue(id, out var full) == true && full.LastIndexOf('/') is var cut and > 0)
                folder = full[..cut];
            return new Design
            {
                Id = id, Name = S("Name"), Description = S("Description"), Folder = folder, FilePath = file,
                FileVersion = r.TryGetProperty("FileVersion", out var fv) && fv.TryGetInt32(out var v) ? v : 0,
                LastEdit = ReadDate(r, "LastEdit"),
                Tags = r.TryGetProperty("Tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [],
                Mods = r.TryGetProperty("Mods", out var m) && m.ValueKind == JsonValueKind.Array
                    ? m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => new DesignMod(
                        x.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                        x.TryGetProperty("Directory", out var d) ? d.GetString() ?? "" : "",
                        !x.TryGetProperty("Enabled", out var e) || e.ValueKind != JsonValueKind.False)).ToList()
                    : [],
            };
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    // FileVersion 2 writes ISO-8601 strings; FileVersion 3 writes unix milliseconds.
    static DateTime? ReadDate(JsonElement r, string key)
    {
        if (!r.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms)) return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var dt)) return dt.LocalDateTime;
        return null;
    }

    // Older Glamourer kept design paths in sort_order.json { "Data": { id: "Folder/Name" } }.
    static Dictionary<string, string> LegacySortOrder(string pluginConfigs)
    {
        var f = Path.Combine(pluginConfigs, "Glamourer", "sort_order.json");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(f)) return map;
        try
        {
            using var doc = JsonDocument.Parse(PenumbraData.ReadShared(f), Lenient);
            if (doc.RootElement.TryGetProperty("Data", out var data))
                foreach (var p in data.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString()!;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException) { }   // legacy folders are optional
        return map;
    }
}
