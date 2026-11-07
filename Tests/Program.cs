// Self-check for the WPF-free core. Builds a fake pluginConfigs + mod directory, never touches the live game files.
// Run: dotnet run --project Tests   (exit code = number of failed checks)
using System.IO.Compression;
using System.Text.Json.Nodes;
using FF14ModManager.Core;
using LiteDB;

if (args.Contains("--live"))   // read-only smoke run against this machine's real Penumbra/Glamourer data
{
    var cfgLive = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs");
    var sw0 = System.Diagnostics.Stopwatch.StartNew();
    var live = PenumbraData.Load(cfgLive, Path.Combine(Path.GetTempPath(), "ff14mm-live-cache"));
    var liveDesigns = GlamourerData.Load(cfgLive);
    Console.WriteLine($"load {sw0.ElapsedMilliseconds} ms: {live.Mods.Count} mods in {live.ModDirectory}, {live.Mods.Count(m => m.Folder != "")} filed, " +
        $"{live.Mods.Count(m => m.MetaError != null)} broken meta, {live.Collections.Count} collections ({string.Join(", ", live.Collections)}), " +
        $"current={live.ById(live.Active.GetValueOrDefault("Current", ""))?.Name}, localData={live.LocalDataError ?? "ok"}");
    Console.WriteLine($"{liveDesigns.Count} designs, folders: {string.Join(", ", liveDesigns.GroupBy(d => d.Folder).Select(g => $"{Analysis.Display(g.Key)}={g.Count()}"))}, " +
        $"no-date={liveDesigns.Count(d => d.LastEdit == null)}, linked mods={liveDesigns.Sum(d => d.Mods.Count)}");
    sw0.Restart();
    Parallel.ForEach(live.Mods, m => m.Size = PenumbraData.DirSize(m.FullPath));
    Console.WriteLine($"sizes {sw0.ElapsedMilliseconds} ms, total {live.Mods.Sum(m => m.Size) / (double)(1L << 30):0.0} GB");
    var cl = Analysis.Cleanup(live, liveDesigns);
    Console.WriteLine($"cleanup: {string.Join(", ", cl.GroupBy(r => r.Kind).Select(g => $"{g.Key}={g.Count()}"))}; duplicates={Analysis.Duplicates(live.Mods).Count}");
    foreach (var r in Analysis.FolderSizes(live.Mods).Take(4)) Console.WriteLine($"  {r.Item}: {r.Detail}");
    sw0.Restart();
    var ovAll = Analysis.Overlaps(live, null);
    var tAll = sw0.ElapsedMilliseconds; sw0.Restart();
    var cur = live.ById(live.Active.GetValueOrDefault("Current", ""))!;
    var ovCur = Analysis.Overlaps(live, cur);
    Console.WriteLine($"paths: {live.Mods.Sum(m => m.AllPaths.Count())} game paths, {live.Mods.Count(m => m.Outfits.Length > 0)} mods touch gear; " +
        $"overlaps all installed: {ovAll.Count} mods ({tAll} ms); in '{cur.Name}' with selected options: {ovCur.Count} mods ({sw0.ElapsedMilliseconds} ms)");
    foreach (var o in ovCur.Take(12))
        Console.WriteLine($"  {o.Mod.Name} [prio {o.Priority}] ~ {string.Join(" | ", o.With.Select(w => w.Name).Take(3))}{(o.With.Count > 3 ? $" +{o.With.Count - 3}" : "")} :: {string.Join(", ", o.Outfits.Take(4))} · {o.Paths.Count} files");
    if (args.Contains("--dups")) foreach (var r in Analysis.Duplicates(live.Mods)) Console.WriteLine($"  [{r.Kind}] {r.Detail}");
    return 0;
}

var failed = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failed++; }

var root = Path.Combine(Path.GetTempPath(), "ff14mm-test-" + Guid.NewGuid().ToString("N")[..8]);
var cfg = Path.Combine(root, "pluginConfigs");
var modsDir = Path.Combine(root, "mods");
var cache = Path.Combine(root, "cache");
void W(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
string J(string s) => s.Replace("\\", "\\\\");

const string Main = "11111111-1111-1111-1111-111111111111", Child = "22222222-2222-2222-2222-222222222222";
try
{
    // --- fixture ---------------------------------------------------------
    W(Path.Combine(cfg, "Penumbra", "config", "behavior.json"), "{ \"SomethingElse\": 1 }");
    W(Path.Combine(cfg, "Penumbra", "config", "advanced.json"), $"{{ \"ModDirectory\": \"{J(modsDir)}\", }}");   // trailing comma: lenient parse
    // Penumbra 1.7 layout (FileVersion 4): files live in meta.json under DefaultData and Groups[].Options[].
    W(Path.Combine(modsDir, "ModA", "meta.json"), """
        {"FileVersion":4,"Name":"Mod A","Author":"Kos","Version":"1.0","ModTags":["outfit"],
         "DefaultData":{"Files":{"chara/equipment/e6015/model/c0201e6015_top.mdl":"a\\top.mdl"}},
         "Groups":[{"Type":"Single","Name":"Opt","DefaultSettings":0,"Options":[
            {"Name":"zero","Files":{"chara/equipment/e0100/material/v0001/mt_c0201e0100_dwn_a.mtrl":"a\\x.mtrl"}},
            {"Name":"one","Files":{"Chara\\Equipment\\e0200\\model\\c0201e0200_sho.mdl":"a\\sho.mdl"}}]}]}
        """);   // option "one" is written with backslashes and capitals: paths compare normalised
    // pre-1.7 layout: default_mod.json + group_*.json beside a plain meta.json
    W(Path.Combine(modsDir, "ModA (2)", "meta.json"), "{\"Name\":\"mod a \",\"Author\":\"kos\"}");
    W(Path.Combine(modsDir, "ModA (2)", "default_mod.json"), """{"Files":{"chara/equipment/e0200/model/c0201e0200_sho.mdl":"sho.mdl"},"FileSwaps":{},"Manipulations":[]}""");
    W(Path.Combine(modsDir, "ModA (2)", "group_001_extra.json"), """{"Name":"Extra","Type":"Multi","DefaultSettings":0,"Options":[{"Name":"o","Files":{"chara/accessory/a0012/model/c0201a0012_ear.mdl":"ear.mdl"}}]}""");
    W(Path.Combine(modsDir, "Big", "meta.json"), """
        {"FileVersion":4,"Name":"Big, Comma \"Quoted\"","Author":"Y",
         "DefaultData":{"Files":{"chara/equipment/e6015/texture/v01_c0201e6015_top_n.tex":"b\\n.tex"},
                        "FileSwaps":{"chara/equipment/e0100/material/v0001/mt_c0201e0100_dwn_a.mtrl":"chara/x.mtrl"}},
         "Groups":[{"Type":"Multi","Name":"Extras","DefaultSettings":1,"Options":[
            {"Name":"o0","Files":{"chara/equipment/e6015/model/c0201e6015_top.mdl":"b\top.mdl"}},
            {"Name":"o1","Files":{"chara/equipment/e0200/model/c0201e0200_sho.mdl":"b\\sho.mdl"}}]},
           {"Type":"Imc","Name":"Variant","Identifier":{},"DefaultEntry":{}}]}
        """);
    Directory.CreateDirectory(Path.Combine(modsDir, "Big", "chara"));
    File.WriteAllBytes(Path.Combine(modsDir, "Big", "chara", "x.tex"), new byte[3 << 20]);
    W(Path.Combine(modsDir, "Broken", "meta.json"), "{ not json");
    W(Path.Combine(cfg, "Penumbra", "collections", Main + ".json"),
        $"{{\"Version\":2,\"Id\":\"{Main}\",\"Name\":\"Main\",\"Inheritance\":[],\"Settings\":{{\"ModA\":{{\"Settings\":{{\"Opt\":1}},\"Priority\":0,\"Enabled\":true}}}}}}");
    W(Path.Combine(cfg, "Penumbra", "collections", Child + ".json"),
        $"{{\"Version\":2,\"Id\":\"{Child}\",\"Name\":\"Child\",\"Inheritance\":[\"{Main}\"],\"Settings\":{{\"Big\":{{\"Settings\":{{\"Extras\":2}},\"Priority\":3,\"Enabled\":true}},\"Gone\":{{\"Settings\":{{}},\"Priority\":0,\"Enabled\":true}}}}}}");
    W(Path.Combine(cfg, "Penumbra", "collections", Child + ".json.bak"), "garbage");
    W(Path.Combine(cfg, "Penumbra", "active_collections.json"), $"{{\"Version\":2,\"Current\":\"{Child}\",\"Yourself\":\"{Main}\"}}");
    using (var db = new LiteDatabase(Path.Combine(cfg, "Penumbra", "mod_data.db")))
    {
        var c = db.GetCollection("LocalModData");
        c.Insert(new BsonDocument { ["_id"] = "ModA", ["Folder"] = "Outfits/Tops", ["Favorite"] = true, ["Note"] = "n",
            ["LocalTags"] = new BsonArray(new BsonValue[] { "mine" }), ["ImportDate"] = 1788433060879L });
        c.Insert(new BsonDocument { ["_id"] = "Big", ["Folder"] = "Outfits", ["Favorite"] = false, ["Note"] = "", ["LocalTags"] = new BsonArray() });
        c.Insert(new BsonDocument { ["_id"] = "Gone", ["Folder"] = "Deleted" });
    }
    var designsDir = Path.Combine(cfg, "Glamourer", "designs");
    W(Path.Combine(designsDir, "d1.json"), """
        {"FileVersion":2,"Identifier":"aaaaaaaa-0000-0000-0000-000000000001","LastEdit":"2026-04-17T20:30:30.98+05:30",
         "Name":"Gym Day","Description":"desc","Tags":["gym"],"FileSystemFolder":"Body",
         "Mods":[{"Name":"Big","Directory":"Big","Enabled":true},{"Name":"Missing One","Directory":"Missing","Enabled":true}]}
        """);
    W(Path.Combine(designsDir, "d2.json"), """{"FileVersion":3,"Identifier":"aaaaaaaa-0000-0000-0000-000000000002","LastEdit":1790658610041,"Name":"Bunny","FileSystemFolder":"Outfits"}""");
    W(Path.Combine(designsDir, "d2.json.bak"), "{}");

    // --- Penumbra --------------------------------------------------------
    var p = PenumbraData.Load(cfg, cache);
    Mod M(string dir) => p.Mods.Single(m => m.Dir == dir);
    Check(p.ModDirectory == modsDir, "ModDirectory read from Penumbra/config/*.json");
    Check(p.Mods.Count == 4, $"4 mods on disk (got {p.Mods.Count}); LiteDB record for deleted 'Gone' ignored");
    Check(p.LocalDataError == null, $"mod_data.db read ({p.LocalDataError})");
    Check(M("ModA").Folder == "Outfits/Tops" && M("ModA").Favorite && M("ModA").LocalTags.SequenceEqual(["mine"]), "folder/favourite/local tags from mod_data.db");
    Check(M("ModA").ImportDate?.Year == 2026, "import date from unix ms");
    Check(M("ModA (2)").Folder == "", "mod without a record sits at root");
    Check(M("Broken").MetaError != null && M("Broken").Name == "Broken", "broken meta.json reported, not fatal");
    Check(p.Collections.Count == 2 && p.Active["Current"] == Child, "collections (+ .bak ignored) and active roles");
    var main = p.ById(Main)!; var child = p.ById(Child)!;
    Check(p.Effective(child, "ModA")?.Enabled == true, "inherited setting resolves through Inheritance");
    Check(p.Effective(main, "Big") == null, "no setting = null");

    // --- Glamourer -------------------------------------------------------
    var designs = GlamourerData.Load(cfg);
    var d1 = designs.Single(d => d.Name == "Gym Day"); var d2 = designs.Single(d => d.Name == "Bunny");
    Check(designs.Count == 2, "2 designs, *.json.bak ignored");
    Check(d1.Folder == "Body" && d1.LastEdit?.Year == 2026 && d1.Mods.Count == 2 && d1.Tags.SequenceEqual(["gym"]), "v2 design: ISO date, mods, tags");
    Check(d2.FileVersion == 3 && d2.LastEdit?.Year == 2026 && d2.Mods.Count == 0, "v3 design: unix-ms date, absent Mods");

    // --- analysis --------------------------------------------------------
    foreach (var m in p.Mods) m.Size = PenumbraData.DirSize(m.FullPath);
    Check(M("Big").Size > 3_000_000, "DirSize counts nested files");
    Check(Analysis.UsedBy(designs, M("Big")).SequenceEqual([d1]) && Analysis.UsedBy(designs, M("ModA")).Count == 0, "C: which designs use a mod");
    var cleanup = Analysis.Cleanup(p, designs);
    Check(cleanup.Any(r => r.Kind == "Unused mod" && r.Item == "mod a "), "B: never-enabled, unlinked mod flagged");
    Check(!cleanup.Any(r => r.Kind == "Unused mod" && (r.Item == "Mod A" || r.Item.StartsWith("Big"))), "B: enabled or design-linked mods not flagged");
    Check(cleanup.Any(r => r.Kind == "Design links missing mod" && r.Detail.Contains("Missing")), "B: design linking a missing mod flagged");
    Check(cleanup.Any(r => r.Kind == "Broken meta.json"), "B: broken meta flagged");
    Check(cleanup.Count(r => r.Kind == "Stale collection settings") == 1 && cleanup.Any(r => r.Item == "Child" && r.Detail.StartsWith("1 settings (1 enabled)")), "B: settings kept for deleted mods counted per collection");
    var dups = Analysis.Duplicates(p.Mods);
    Check(dups.Count == 1 && dups[0].Detail.Contains("ModA (2)"), "F: same name+author (case/space-insensitive), reported once");
    Mod N(string name, string author = "x") => new() { Name = name, Dir = name, Author = author };
    var similar = Analysis.Duplicates([
        N("[Holo] Wanderer Outfit", "Holo"), N("Wanderer Outfitt (Holo)", "someone"),   // creator tag moved + typo
        N("Outfit Wanderer"),                                                          // word order
        N("Bunny Suit"), N("Bunny Ears"),                                              // control: shared word only
        N("Idle"), N("Idle 2"),                                                        // control: too short to judge
        N("Gym Day"),
        N("Japanese Alley"), N("Japanese Alley V2"),                                   // version / extra word: same mod
        // controls taken from a real mod list: parts of a set, not duplicates
        N("[Amborella] Hooked (Behind)"), N("[Amborella] Hooked (Front)"),
        N("[Eulogia] Summon Pack"), N("[Hydaelyn] Summon Pack"),
        N("The Primal AST V1.3.2 FFXIV VFX Modpack"), N("The Primal BLM V1.0.3 FFXIV VFX Modpack"),
        N("[IT] TEA BGM Revisited"), N("[IT] TOP BGM Revisited"),
        N("[HS] Yet Another Body+ (Body)"), N("[HS] Yet Another Body+ (Muscle)"),     // repeated word must count twice
        N("Basket 2.0"), N("Bastet"),                                                  // one-letter swap in a short word
        N("Chained"), N("Chained To Perfection"),                                      // one-word name inside a longer one
        N("Soiree Doree"), N("[Anno] Soirée Dorée")]);                                 // accents: same mod
    var sim = similar.Where(r => r.Kind.StartsWith("Similar name")).ToList();
    Check(sim.Any(r => r.Detail.Contains("Soirée")), "F: accents ignored");
    Check(sim.Count == 3 && new[] { "[Holo] Wanderer Outfit", "Wanderer Outfitt (Holo)", "Outfit Wanderer" }.All(sim.SelectMany(r => new[] { r.Detail }).First(d => d.Contains("Wanderer")).Contains)
        && sim.Any(r => r.Detail.Contains("Japanese Alley V2")),
        $"F: similar names grouped across tags, typos, word order and versions ({string.Join(" / ", sim.Select(r => r.Detail))})");
    Check(!similar.Any(r => new[] { "Bunny", "Idle", "Gym", "Hooked", "Summon", "Primal", "BGM", "Body+", "Bastet", "Chained" }.Any(r.Detail.Contains)),
        "F: sets (Front/Behind, AST/BLM, TEA/TOP, Body/Muscle), Basket/Bastet, one-word names and short names not matched");
    Check(!dups.Any(r => r.Kind.StartsWith("Similar name")), "F: pairs already reported as exact duplicates are not repeated");
    // --- overlaps: same game file or same outfit ------------------------
    Check(Analysis.Outfit("chara/equipment/e6015/model/c0201e6015_top.mdl") == "e6015 Body"
        && Analysis.Outfit("chara/equipment/e0100/material/v0001/mt_c0201e0100_dwn_a.mtrl") == "e0100 Legs"
        && Analysis.Outfit("chara/equipment/e6015/texture/v01_c0201e6015_top_n.tex") == "e6015 Body"
        && Analysis.Outfit("chara/accessory/a0012/model/c0201a0012_ear.mdl") == "a0012 Earrings"
        && Analysis.Outfit("chara/weapon/w3101/obj/body/b0018/model/w3101b0018.mdl") == "w3101-b0018 Weapon"
        && Analysis.Outfit("chara/equipment/e0000/texture/v01_c0201e0000_top_d.tex") == "e0000 Body (smallclothes)"
        && Analysis.Outfit("chara/equipment/e6015/texture/custom_glow.tex") == null
        && Analysis.Outfit("chara/human/c0201/obj/body/b0001/texture/c0201b0001_base.tex") == null,
        "outfit from game path: equipment slot, accessory, weapon; unknown slot and body files are not outfits");
    Check(M("ModA").AllPaths.Count() == 3 && M("ModA").AllPaths.Contains("chara/equipment/e0200/model/c0201e0200_sho.mdl"),
        "1.7 layout: default + every option, paths normalised (backslashes, case)");
    Check(M("ModA (2)").AllPaths.Count() == 2 && M("Big").AllPaths.Count() == 4,
        "old default_mod.json/group_*.json layout read; FileSwaps count as changed files; Imc group has no files");
    var all = Analysis.Overlaps(p, null);
    Overlap O(List<Overlap> list, string dir) => list.Single(o => o.Mod.Dir == dir);
    Check(all.Count == 3 && O(all, "ModA").With.Select(m => m.Dir).Order().SequenceEqual(["Big", "ModA (2)"])
        && O(all, "ModA (2)").With.Select(m => m.Dir).Order().SequenceEqual(["Big", "ModA"]),
        "all installed: every mod sharing a file or outfit, any option");
    Check(O(all, "ModA").Outfits.SequenceEqual(["e0100 Legs", "e0200 Feet", "e6015 Body"]) && O(all, "ModA").Paths.Count == 3,
        $"ModA: shared outfits incl. different files of e6015 Body; 3 shared files (got {string.Join(", ", O(all, "ModA").Outfits)}; {O(all, "ModA").Paths.Count})");
    var inChild = Analysis.Overlaps(p, child);
    Check(inChild.Count == 2 && O(inChild, "ModA").With.Single().Dir == "Big",
        "in a collection: enabled mods only (ModA inherited, ModA (2) not enabled)");
    Check(O(inChild, "ModA").Outfits.SequenceEqual(["e0200 Feet", "e6015 Body"]) && O(inChild, "ModA").Paths.SequenceEqual(["chara/equipment/e0200/model/c0201e0200_sho.mdl"]),
        "in a collection: only selected options (Single index 1, Multi bitmask 2), so unselected e0100 Legs is not a conflict");
    Check(O(inChild, "Big").Priority == 3 && O(inChild, "ModA").Priority == 0, "priority shown, it decides which mod wins in game");
    var (pairOutfits, pairFiles) = Analysis.Shared(M("ModA"), M("Big"));
    Check(pairOutfits.SequenceEqual(["e0100 Legs", "e0200 Feet", "e6015 Body"]) && pairFiles == 3, "pairwise detail for the details pane");
    var cmp = Analysis.Compare(p, main, child);
    Check(cmp.Count == 1 && cmp[0].Kind == "Only in Child", "G: compare uses effective (inherited) state");
    var sizes = Analysis.FolderSizes(p.Mods);
    Check(sizes[0].Item == "(all mods)" && sizes.Any(r => r.Item == "Outfits" && r.Detail.Contains("2 mods")), "E: folder totals roll up subfolders");
    var csv = Analysis.ToCsv(p, child);
    Check(csv.Contains("\"Big, Comma \"\"Quoted\"\"\"") && csv.Split("\r\n").Length == 6, "D: CSV quoting, header + 4 rows");
    var html = Analysis.ToHtml(p, designs, child);
    Check(html.Contains("Big, Comma &quot;Quoted&quot;") && html.Contains("Gym Day"), "D: HTML is encoded");

    // H: backup while the game holds a file open for writing
    var held = Path.Combine(cfg, "Penumbra", "collections", Main + ".json");
    var zipPath = Path.Combine(root, "backup.zip");
    using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
    {
        var (files, skipped) = Analysis.Backup(cfg, zipPath);
        Check(files >= 8 && skipped.Count == 0, $"H: backup zipped {files} files, skipped {skipped.Count}");
    }
    using (var z = ZipFile.OpenRead(zipPath))
        Check(z.Entries.Any(e => e.FullName == $"Penumbra/collections/{Main}.json") && z.Entries.Any(e => e.FullName == "Glamourer/designs/d1.json"), "H: zip paths relative to pluginConfigs");

    // mod_data.db locked exclusively: the app still lists mods and says why folders are missing
    using (new FileStream(Path.Combine(cfg, "Penumbra", "mod_data.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var locked = PenumbraData.Load(cfg, Path.Combine(root, "cache2"));
        Check(locked.Mods.Count == 4 && locked.LocalDataError != null, "locked mod_data.db degrades, does not fail");
    }

    // --- screenshots -----------------------------------------------------
    var shots = new Shots(Path.Combine(root, "shots"));
    var png = shots.NewPath();
    File.WriteAllBytes(png, [1, 2, 3]);
    shots.WriteInfo(png, new Dictionary<string, string?> { ["character"] = "Test Character" });
    var attached = shots.Attach(png, "design", d1.Id);
    Check(shots.Latest("design", d1.Id) == attached && shots.UnsortedShots().Count == 0, "shot attach moves into item folder");
    Check(shots.ReadInfo(attached)?["character"] == "Test Character", "sidecar moved with the shot");

    // --- bridge over a real named pipe ----------------------------------
    var pipe = "FF14ModManager.Test." + Guid.NewGuid().ToString("N")[..8];
    using var cts = new CancellationTokenSource();
    var server = Bridge.Serve(req => (string?)req["cmd"] == "boom"
        ? throw new InvalidOperationException("kaboom")
        : Task.FromResult(new JsonObject { ["ok"] = true, ["echo"] = (string?)req["cmd"] }), cts.Token, pipeName: pipe);
    var r1 = await Bridge.Ask(new JsonObject { ["cmd"] = "status" }, pipeName: pipe);
    var r2 = await Bridge.Ask(new JsonObject { ["cmd"] = "boom" }, pipeName: pipe);
    var r3 = await Bridge.Ask(new JsonObject { ["cmd"] = "again" }, pipeName: pipe);
    Check((string?)r1?["echo"] == "status", "bridge round trip");
    Check(r2?["ok"]?.GetValue<bool>() == false && ((string?)r2?["error"])!.Contains("kaboom"), "handler exception becomes ok:false");
    Check((string?)r3?["echo"] == "again", "server keeps serving after an error");
    cts.Cancel();
    await server.WaitAsync(TimeSpan.FromSeconds(5));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var none = await Bridge.Ask(new JsonObject { ["cmd"] = "status" }, timeoutMs: 500, pipeName: pipe);
    Check(none == null && sw.ElapsedMilliseconds < 3000, $"no bridge = null in {sw.ElapsedMilliseconds} ms");
}
catch (Exception e)
{
    Console.WriteLine($"FAIL  unhandled: {e}");
    failed++;
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { }
}

Console.WriteLine(failed == 0 ? "ALL PASS" : $"{failed} FAILED");
return failed;
