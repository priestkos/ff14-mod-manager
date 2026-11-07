using System.Text.Json;

namespace FF14ModManager.Core;

/// <summary>
/// Screenshots on disk: unsorted/ for fresh captures, design/&lt;id&gt;/ and mod/&lt;dir&gt;/ once attached.
/// Keys are used as folder names as-is: design ids are GUIDs and mod keys are already folder names.
/// An optional sidecar &lt;same name&gt;.json carries who/what was on screen (from the in-game bridge).
/// </summary>
public sealed class Shots(string root)
{
    public string Root { get; } = root;
    string Unsorted => Path.Combine(Root, "unsorted");

    public string NewPath()
    {
        Directory.CreateDirectory(Unsorted);
        return Path.Combine(Unsorted, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
    }

    public void WriteInfo(string png, IDictionary<string, string?> info) =>
        File.WriteAllText(Path.ChangeExtension(png, ".json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));

    public Dictionary<string, string?>? ReadInfo(string png)
    {
        var f = Path.ChangeExtension(png, ".json");
        try { return File.Exists(f) ? JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(f)) : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Moves a capture (and its sidecar) under an item. Returns the new path.</summary>
    public string Attach(string png, string kind, string key)
    {
        var dir = Path.Combine(Root, kind, key);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(png));
        File.Move(png, dest);
        var info = Path.ChangeExtension(png, ".json");
        if (File.Exists(info)) File.Move(info, Path.ChangeExtension(dest, ".json"));
        return dest;
    }

    public List<string> For(string kind, string key) => Newest(Path.Combine(Root, kind, key));
    public string? Latest(string kind, string key) => For(kind, key).FirstOrDefault();
    public List<string> UnsortedShots() => Newest(Unsorted);

    static List<string> Newest(string dir) => Directory.Exists(dir)
        ? Directory.GetFiles(dir, "*.png").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList()
        : [];
}
