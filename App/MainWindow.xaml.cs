using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FF14ModManager.Core;
using Microsoft.Win32;

namespace FF14ModManager;

public partial class MainWindow : Window
{
    // tiny one-way converters for grid cells
    sealed class Fn(Func<object?, object?> f) : IValueConverter
    {
        public object? Convert(object? v, Type t, object? p, CultureInfo c) => f(v);
        public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
    }
    public static readonly IValueConverter Tick = new Fn(v => v is true ? "✓" : "");
    public static readonly IValueConverter Not = new Fn(v => v is not true);
    public static readonly IValueConverter Star = new Fn(v => v is true ? "★" : "");
    public static readonly IValueConverter HasShot = new Fn(v => v is string ? "📷" : "");
    public static readonly IValueConverter FileName = new Fn(v => v is string s ? Path.GetFileNameWithoutExtension(s) : "");

    const string AllFolders = "(all folders)", RootFolder = "(root)";
    static readonly ThumbConverter Thumbs = new();

    readonly string pluginConfigs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs");
    readonly string appData = App.DataDir;
    readonly Shots shots;
    readonly DispatcherTimer bridgeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    PenumbraData? pen;
    List<Design> designs = [];
    string? lastAppliedDesign;   // the bridge cannot ask Glamourer "which design is on"; remember what we applied
    bool polling;
    Dictionary<Mod, Overlap>? overlapsAll, overlapsInCollection;   // cached; reset on reload, collection change, mod toggle
    Mod? overlapFocus;                                             // "Show only mods overlapping this"

    Dictionary<Mod, Overlap> Overlaps(bool inCollection)
    {
        if (pen == null) return [];
        if (inCollection)
            return overlapsInCollection ??= CollectionBox.SelectedItem is Collection c ? Analysis.Overlaps(pen, c).ToDictionary(o => o.Mod) : [];
        return overlapsAll ??= Analysis.Overlaps(pen, null).ToDictionary(o => o.Mod);
    }

    public MainWindow()
    {
        InitializeComponent();
        shots = new Shots(Path.Combine(appData, "shots"));
        ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Content == App.SavedTheme);
        Loaded += async (_, _) => { await LoadAsync(); bridgeTimer.Tick += async (_, _) => await PollBridge(); bridgeTimer.Start(); await PollBridge(); };
    }

    // ---------------------------------------------------------------- loading
    async Task LoadAsync()
    {
        StatusText.Text = "Loading Penumbra and Glamourer data…";
        try
        {
            var (p, d) = await Task.Run(() => (PenumbraData.Load(pluginConfigs, Path.Combine(appData, "cache")), GlamourerData.Load(pluginConfigs)));
            pen = p;
            designs = d;
            overlapsAll = overlapsInCollection = null;
            overlapFocus = null;
        }
        catch (Exception e)
        {
            StatusText.Text = $"Could not load: {e.Message}";
            return;
        }
        foreach (var d in designs) d.Thumbnail = shots.Latest("design", d.Id);

        var current = pen.ById(pen.Active.GetValueOrDefault("Current", "")) ?? pen.Collections.FirstOrDefault();
        CollectionBox.ItemsSource = pen.Collections;
        CompareA.ItemsSource = CompareB.ItemsSource = pen.Collections;
        CompareA.SelectedItem = current;
        CompareB.SelectedItem = pen.Collections.FirstOrDefault(c => c != current);

        ModGrid.ItemsSource = pen.Mods;
        CollectionViewSource.GetDefaultView(pen.Mods).Filter = ModMatches;
        ModFolder.ItemsSource = FolderChoices(pen.Mods.Select(m => m.Folder));
        ModFolder.SelectedIndex = 0;
        LoadTagChoices(pen.Mods);
        ModFilter_Changed(ModSearch, new RoutedEventArgs());   // a reload keeps the search text: re-apply its relevance order to the new list
        CollectionBox.SelectedItem = current;   // fires Collection_Changed → sets Enabled flags

        DesignGrid.ItemsSource = designs;
        CollectionViewSource.GetDefaultView(designs).Filter = DesignMatches;
        DesignFolder.ItemsSource = FolderChoices(designs.Select(d => d.Folder));
        DesignFolder.SelectedIndex = 0;

        SummaryText.Text = $"{pen.Mods.Count} mods in {pen.ModDirectory} · {pen.Mods.Count(m => m.Folder != "")} in folders · {designs.Count} Glamourer designs";
        StatusText.Text = pen.LocalDataError ?? "Loaded. Measuring mod sizes in the background…";
        Gallery_Refresh(null, null);
        RefreshUnsorted();
        _ = MeasureSizes(pen);
    }

    async Task MeasureSizes(PenumbraData p)
    {
        await Task.Run(() => Parallel.ForEach(p.Mods, new ParallelOptions { MaxDegreeOfParallelism = 4 }, m => m.Size = PenumbraData.DirSize(m.FullPath)));
        if (p != pen) return;   // a reload replaced the data meanwhile
        ModGrid.Items.Refresh();
        StatusText.Text = $"Ready. {p.Mods.Sum(m => m.Size) / (double)(1L << 30):0.0} GB of mods.";
    }

    static List<string> FolderChoices(IEnumerable<string> folders)
    {
        var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in folders.Where(f => f != ""))
        {
            var parts = f.Split('/');
            for (var i = 1; i <= parts.Length; i++) all.Add(string.Join('/', parts[..i]));   // parents too, so a parent selects its subtree
        }
        return [AllFolders, RootFolder, .. all];
    }

    static bool InFolder(string folder, object? choice) => choice switch
    {
        null or AllFolders => true,
        RootFolder => folder == "",
        string f => folder.Equals(f, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    static bool Has(string s, string q) => s.Contains(q, StringComparison.OrdinalIgnoreCase);

    async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is not ComboBoxItem { Content: string theme } || theme == App.SavedTheme) return;
        App.ApplyTheme(theme);
        App.SavedTheme = theme;
    }

    void Collection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (pen == null || CollectionBox.SelectedItem is not Collection c) return;
        foreach (var m in pen.Mods) m.Enabled = pen.Effective(c, m.Dir)?.Enabled == true;
        overlapsInCollection = null;
        RefreshOverlaps();
        CollectionViewSource.GetDefaultView(pen.Mods).Refresh();
        ModGrid.Items.Refresh();
        StatusText.Text = $"Collection “{c.Name}”: {pen.Mods.Count(m => m.Enabled)} of {pen.Mods.Count} mods enabled.";
    }

    // ---------------------------------------------------------------- mods
    readonly Dictionary<Mod, int> searchRank = [];   // filled by ModMatches, read by the sort that follows it in the same Refresh

    bool ModMatches(object o)
    {
        var m = (Mod)o;
        if (overlapFocus != null)   // focus mode ignores the other filters: show the mod and everything it overlaps
            return m == overlapFocus || Overlaps(ModEnabledOnly.IsChecked == true).TryGetValue(overlapFocus, out var f) && f.With.Contains(m);
        if (Analysis.SearchRank(m, ModSearch.Text.Trim()) is not { } rank) return false;
        searchRank[m] = rank;
        return InFolder(m.Folder, ModFolder.SelectedItem)
            && Analysis.TagsMatch(m, tagHas, tagLacks)
            && (ModEnabledOnly.IsChecked != true || m.Enabled)
            && (ModFavOnly.IsChecked != true || m.Favorite)
            && (ModOverlapOnly.IsChecked != true || Overlaps(ModEnabledOnly.IsChecked == true).ContainsKey(m));
    }

    // While searching, name matches sort first and description-only matches last; a column click re-sorts as usual.
    sealed class ByRank(Dictionary<Mod, int> rank) : System.Collections.IComparer
    {
        public int Compare(object? a, object? b) =>
            rank.GetValueOrDefault((Mod)a!).CompareTo(rank.GetValueOrDefault((Mod)b!)) is var c and not 0 ? c
                : StringComparer.OrdinalIgnoreCase.Compare(((Mod)a!).Name, ((Mod)b!).Name);
    }

    // ---------------------------------------------------------------- tag filter
    sealed class TagChoice(string name, int count)
    {
        public string Name => name;
        public int State;   // 0 ignored, 1 must have, 2 must not have
        public string Label => $"{State switch { 1 => "✓", 2 => "✗", _ => "   " }}  {name}  ({count})";
    }

    List<TagChoice> tagChoices = [];
    readonly HashSet<string> tagHas = new(StringComparer.OrdinalIgnoreCase), tagLacks = new(StringComparer.OrdinalIgnoreCase);

    void LoadTagChoices(List<Mod> mods)
    {
        var counts = mods.SelectMany(m => m.Tags.Concat(m.LocalTags).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        tagChoices = [new TagChoice(Analysis.Untagged, mods.Count(m => m.Tags.Length + m.LocalTags.Length == 0)),
            .. counts.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => new TagChoice(kv.Key, kv.Value))];
        // keep the user's choices across a reload when the tag still exists
        foreach (var c in tagChoices) c.State = tagHas.Contains(c.Name) ? 1 : tagLacks.Contains(c.Name) ? 2 : 0;
        ApplyTagChoices();
        TagSearch_Changed(null, null);
    }

    void ApplyTagChoices()
    {
        tagHas.Clear(); tagLacks.Clear();
        foreach (var c in tagChoices.Where(c => c.State == 1)) tagHas.Add(c.Name);
        foreach (var c in tagChoices.Where(c => c.State == 2)) tagLacks.Add(c.Name);
        TagButton.Content = tagHas.Count + tagLacks.Count == 0 ? "Tags: any"
            : "Tags: " + string.Join(" ", tagHas.Select(t => "✓" + t).Concat(tagLacks.Select(t => "✗" + t)));
        if (TagButton.Content is string s && s.Length > 40) TagButton.Content = s[..39] + "…";
        ModFilter_Changed(this, new RoutedEventArgs());
    }

    void TagChoice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TagChoice c) return;
        c.State = (c.State + 1) % 3;
        TagList.Items.Refresh();
        ApplyTagChoices();
    }

    void TagClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in tagChoices) c.State = 0;
        TagList.Items.Refresh();
        ApplyTagChoices();
    }

    void TagSearch_Changed(object? sender, RoutedEventArgs? e)
    {
        var q = TagSearch.Text.Trim();
        TagList.ItemsSource = q == "" ? tagChoices : tagChoices.Where(c => Has(c.Name, q)).ToList();
    }

    void ModFocus_Click(object sender, RoutedEventArgs e) => SetOverlapFocus(overlapFocus == null ? SelectedMod : null);

    void SetOverlapFocus(Mod? m)
    {
        overlapFocus = m;
        if (pen != null) CollectionViewSource.GetDefaultView(pen.Mods).Refresh();
        ModFocusBtn.Content = overlapFocus == null ? "Show only mods overlapping this" : "Show all mods";
        StatusText.Text = overlapFocus == null ? "Showing all mods (filters apply again)."
            : $"Showing “{overlapFocus.Name}” and the mods it overlaps{(ModEnabledOnly.IsChecked == true ? " in game in this collection" : "")}. Click “Show all mods” to go back.";
    }

    void ModFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (pen == null) return;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(pen.Mods);
        searchRank.Clear();
        if (sender == ModSearch)   // typing re-sorts by relevance; clearing the box goes back to name order
            view.CustomSort = ModSearch.Text.Trim() == "" || overlapFocus != null ? null : new ByRank(searchRank);
        view.Refresh();
    }

    Mod? SelectedMod => ModGrid.SelectedItem as Mod;

    void ModGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowMod(SelectedMod);

    void ShowMod(Mod? m)
    {
        if (m == null) return;
        ModShot.Source = Img(shots.Latest("mod", m.Dir), 760);
        ModTitle.Text = m.Name;
        var lines = new List<string>
        {
            $"{(m.Author == "" ? "unknown author" : m.Author)} · v{m.Version} · {m.SizeText}",
            $"Folder: {Analysis.Display(m.Folder)}{(m.Favorite ? " · ★ favourite" : "")}",
            m.FullPath,
        };
        if (m.ImportDate is { } d) lines.Add($"Imported {d:yyyy-MM-dd}");
        if (m.AllTags != "") lines.Add($"Tags: {m.AllTags}");
        if (m.Note != "") lines.Add($"Note: {m.Note}");
        if (m.Website != "") lines.Add(m.Website);
        if (m.MetaError != null) lines.Add(m.MetaError);
        if (pen != null)
        {
            var on = pen.Collections.Where(c => pen.Effective(c, m.Dir)?.Enabled == true).Select(c => c.Name).ToList();
            lines.Add($"Enabled in: {(on.Count == 0 ? "no collection" : string.Join(", ", on))}");
        }
        ModInfo.Text = string.Join("\n", lines);
        var used = Analysis.UsedBy(designs, m);
        ModUsedBy.Text = used.Count == 0 ? "none" : string.Join("\n", used.Select(d => $"• {d.Name}"));
        var others = Overlaps(false).TryGetValue(m, out var ov) ? ov.With : [];
        ModOverlaps.Text = others.Count == 0 ? "none" : string.Join("\n", others.Take(25).Select(o =>
        {
            var (gear, files) = Analysis.Shared(m, o);
            return $"{(o.Enabled ? "✓" : "•")} {o.Name}: {(gear.Count > 0 ? string.Join(", ", gear.Take(4)) + (gear.Count > 4 ? "…" : "") + " · " : "")}{files} shared file{(files == 1 ? "" : "s")}";
        })) + (others.Count > 25 ? $"\n…and {others.Count - 25} more" : "");
        ModDesc.Text = m.Description;
        ModToggle.Content = m.Enabled ? "Disable in collection (in game)" : "Enable in collection (in game)";
    }

    void ModOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMod is { } m) Explorer($"\"{m.FullPath}\"");
    }

    void ModAttach_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMod is { } m) AttachShot(NewestUnsorted(), "mod", m.Dir, m.Name);
    }

    async void ModToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMod is not { } m || pen == null || CollectionBox.SelectedItem is not Collection c) return;
        var want = !m.Enabled;
        var reply = await Bridge.Ask(new JsonObject { ["cmd"] = "setMod", ["collection"] = c.Id, ["dir"] = m.Dir, ["enabled"] = want });
        if (!BridgeOk(reply)) return;
        var old = pen.Effective(c, m.Dir);
        c.Settings[m.Dir] = new ModSetting(want, old?.Priority ?? 0, old?.OptionsJson ?? "{}");   // mirror what Penumbra now has
        m.Enabled = want;
        overlapsInCollection = null;
        RefreshOverlaps();
        ModGrid.Items.Refresh();
        ShowMod(m);
        StatusText.Text = $"{(want ? "Enabled" : "Disabled")} “{m.Name}” in collection “{c.Name}”.";
    }

    // ---------------------------------------------------------------- overlaps tab
    void RefreshOverlaps()
    {
        if (pen == null) return;
        var inGame = OverlapEnabledOnly.IsChecked == true;
        var list = Overlaps(inGame).Values.OrderBy(o => o.Mod.Name, StringComparer.OrdinalIgnoreCase).ToList();
        OverlapGrid.ItemsSource = list;
        CollectionViewSource.GetDefaultView(list).Filter = OverlapMatches;
        var gear = list.SelectMany(o => o.Outfits).Distinct().Count();
        var files = list.SelectMany(o => o.Paths).Distinct().Count();
        var c = CollectionBox.SelectedItem as Collection;
        OverlapText.Text = list.Count == 0 ? "No overlaps." :
            $"{list.Count} mods change a game file or gear piece another mod also changes: {gear} gear pieces and {files} game files are contested. " +
            (inGame ? $"In game in “{c?.Name}”, the mod with the higher priority wins each contested file (set priorities in Penumbra)."
                    : "All installed mods and every option, enabled or not: a list of what could clash, not what does.") +
            " Double-click a row to see it and its overlaps in the Mods tab.";
    }

    bool OverlapMatches(object o)
    {
        var ov = (Overlap)o;
        var q = OverlapSearch.Text.Trim();
        return q == "" || Has(ov.Mod.Name, q) || Has(ov.WithText, q) || Has(ov.OutfitsText, q) || q.Length >= 5 && ov.Paths.Any(x => Has(x, q));
    }

    void OverlapFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (OverlapGrid.ItemsSource != null) CollectionViewSource.GetDefaultView(OverlapGrid.ItemsSource).Refresh();
    }

    void OverlapMode_Changed(object sender, RoutedEventArgs e) => RefreshOverlaps();

    void OverlapGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (OverlapGrid.SelectedItem is not Overlap ov || pen == null) return;
        ModEnabledOnly.IsChecked = OverlapEnabledOnly.IsChecked;   // focus uses the same mode as this list
        SetOverlapFocus(ov.Mod);   // focus mode bypasses the Mods-tab filters, so the row is always there to select
        ModGrid.SelectedItem = ov.Mod;
        ModGrid.ScrollIntoView(ov.Mod);
        Tabs.SelectedIndex = 0;
    }

    // ---------------------------------------------------------------- designs
    bool DesignMatches(object o)
    {
        var d = (Design)o;
        var q = DesignSearch.Text.Trim();
        return (q == "" || Has(d.Name, q) || Has(d.TagsText, q) || Has(d.Description, q) || Has(d.Folder, q))
            && InFolder(d.Folder, DesignFolder.SelectedItem);
    }

    void DesignFilter_Changed(object sender, RoutedEventArgs e) => CollectionViewSource.GetDefaultView(designs).Refresh();

    Design? SelectedDesign => DesignGrid.SelectedItem as Design;

    void DesignGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDesign(SelectedDesign);

    void ShowDesign(Design? d)
    {
        if (d == null) return;
        DesignShot.Source = Img(d.Thumbnail, 760);
        DesignTitle.Text = d.Name;
        var shotCount = shots.For("design", d.Id).Count;
        DesignInfo.Text = $"Folder: {Analysis.Display(d.Folder)}\nLast edit: {d.LastEdit:yyyy-MM-dd HH:mm}\n" +
                          (d.TagsText != "" ? $"Tags: {d.TagsText}\n" : "") + $"{shotCount} screenshot(s) · file format v{d.FileVersion}";
        DesignMods.Text = d.Mods.Count == 0 ? "none" : string.Join("\n", d.Mods.Select(dm =>
            pen?.Mods.Any(m => m.Dir.Equals(dm.Dir, StringComparison.OrdinalIgnoreCase)) == true
                ? $"✓ {dm.Name}{(dm.Enabled ? "" : " (set to off)")}" : $"✗ {dm.Name} — not installed"));
        DesignDesc.Text = d.Description;
    }

    async void DesignApply_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDesign is not { } d) return;
        var reply = await Bridge.Ask(new JsonObject { ["cmd"] = "applyDesign", ["id"] = d.Id });
        if (!BridgeOk(reply)) return;
        lastAppliedDesign = d.Name;
        StatusText.Text = $"Applied “{d.Name}” to your character.";
    }

    void DesignAttach_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDesign is { } d) AttachShot(NewestUnsorted(), "design", d.Id, d.Name);
    }

    void DesignOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDesign is { } d) Explorer($"/select,\"{d.FilePath}\"");
    }

    // ---------------------------------------------------------------- gallery
    void Gallery_Refresh(object? sender, RoutedEventArgs? e)
    {
        if (GalleryList == null) return;   // Checked can fire while the window is still being built
        GalleryList.ItemsSource = designs.Where(d => GalleryShotsOnly.IsChecked != true || d.Thumbnail != null).ToList();
    }

    void Gallery_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GalleryList.SelectedItem is not Design d) return;
        DesignSearch.Text = "";
        DesignFolder.SelectedIndex = 0;
        DesignGrid.SelectedItem = d;
        DesignGrid.ScrollIntoView(d);
        Tabs.SelectedIndex = 1;
    }

    // ---------------------------------------------------------------- screenshots
    async void Capture_Click(object sender, RoutedEventArgs e) => await CaptureAsync();

    async void CaptureDelayed_Click(object sender, RoutedEventArgs e)
    {
        for (var i = 3; i > 0; i--) { StatusText.Text = $"Capturing in {i}…"; await Task.Delay(1000); }
        await CaptureAsync();
    }

    async Task CaptureAsync()
    {
        var hwnd = Capture.FindGameWindow();
        if (hwnd == IntPtr.Zero) { StatusText.Text = "FFXIV is not running (no ffxiv_dx11.exe window)."; return; }
        try
        {
            var bmp = await Capture.Window(hwnd);
            var path = shots.NewPath();
            Capture.SavePng(bmp, path);
            var st = await Bridge.Ask(new JsonObject { ["cmd"] = "status" }, 800);
            shots.WriteInfo(path, new Dictionary<string, string?>
            {
                ["capturedAt"] = DateTime.Now.ToString("s"),
                ["character"] = (string?)st?["character"],
                ["world"] = (string?)st?["world"],
                ["collection"] = (string?)st?["collection"],
                ["design"] = st != null ? lastAppliedDesign : null,
            });
            RefreshUnsorted();
            UnsortedList.SelectedItem = path;
            ShotPreview.Source = bmp;
            StatusText.Text = $"Captured {bmp.PixelWidth}×{bmp.PixelHeight} → {path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Capture failed: {ex.Message}";
        }
    }

    void RefreshUnsorted() => UnsortedList.ItemsSource = shots.UnsortedShots();

    string? NewestUnsorted() => UnsortedList.SelectedItem as string ?? shots.UnsortedShots().FirstOrDefault();

    void Unsorted_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UnsortedList.SelectedItem is not string path) return;
        ShotPreview.Source = Img(path, 1600);
        var info = shots.ReadInfo(path);
        ShotInfo.Text = info == null ? Path.GetFileName(path)
            : string.Join(" · ", info.Where(kv => kv.Value != null).Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    void ShotToDesign_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDesign is { } d) AttachShot(NewestUnsorted(), "design", d.Id, d.Name);
        else StatusText.Text = "Select a design in the Designs tab first.";
    }

    void ShotToMod_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMod is { } m) AttachShot(NewestUnsorted(), "mod", m.Dir, m.Name);
        else StatusText.Text = "Select a mod in the Mods tab first.";
    }

    void AttachShot(string? png, string kind, string key, string name)
    {
        if (png == null) { StatusText.Text = "No unsorted captures. Take one in the Screenshot tab."; return; }
        var dest = shots.Attach(png, kind, key);
        if (kind == "design" && designs.FirstOrDefault(d => d.Id == key) is { } design)
        {
            design.Thumbnail = dest;
            DesignGrid.Items.Refresh();
            Gallery_Refresh(null, null);
            ShowDesign(SelectedDesign);
        }
        else ShowMod(SelectedMod);
        RefreshUnsorted();
        ShotPreview.Source = null;
        ShotInfo.Text = "";
        StatusText.Text = $"Screenshot attached to “{name}”.";
    }

    void ShotsOpen_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(shots.Root);
        Explorer($"\"{shots.Root}\"");
    }

    // global hotkey: Ctrl+Shift+F12 captures from inside the game
    const int HotkeyId = 0xF14, WmHotkey = 0x0312;
    const uint ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000, VkF12 = 0x7B;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        if (!RegisterHotKey(hwnd, HotkeyId, ModControl | ModShift | ModNoRepeat, VkF12))
            StatusText.Text = "Ctrl+Shift+F12 is taken by another program; use the Capture button.";
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
        base.OnClosed(e);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == HotkeyId) { _ = CaptureAsync(); handled = true; }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    // ---------------------------------------------------------------- bridge
    async Task PollBridge()
    {
        if (polling) return;
        polling = true;
        try
        {
            var st = await Bridge.Ask(new JsonObject { ["cmd"] = "status" }, 500);
            BridgeText.Text = st?["ok"]?.GetValue<bool>() == true
                ? $"In game: {(string?)st["character"] ?? "not logged in"}{((string?)st["world"] is { } w ? " @ " + w : "")} · on you: {(string?)st["collection"] ?? "?"}"
                : "In-game bridge: not connected";
        }
        finally { polling = false; }
    }

    bool BridgeOk(JsonObject? reply)
    {
        if (reply == null) { StatusText.Text = "In-game bridge not connected: start the game and enable the FF14 Mod Manager Bridge plugin."; return false; }
        if (reply["ok"]?.GetValue<bool>() == true) return true;
        StatusText.Text = $"In game: {(string?)reply["error"] ?? "failed"}";
        return false;
    }

    // ---------------------------------------------------------------- tools
    void ShowReport(List<ReportRow> rows, string summary)
    {
        ReportGrid.ItemsSource = rows;
        ReportText.Text = summary;
    }

    bool SizesReady => pen != null && pen.Mods.All(m => m.Size >= 0);

    void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (pen == null) return;
        var rows = Analysis.Cleanup(pen, designs);
        var unused = rows.Where(r => r.Kind == "Unused mod").Select(r => pen.Mods.First(m => m.Name == r.Item)).ToList();
        ShowReport(rows, $"Report only, nothing is deleted. {unused.Count} mods are enabled in no collection and linked by no design" +
            (SizesReady ? $" ({unused.Sum(m => m.Size) / (double)(1L << 30):0.0} GB)." : " (sizes still being measured).") +
            " Penumbra may still apply some through temporary settings or other plugins.");
    }

    void Duplicates_Click(object sender, RoutedEventArgs e)
    {
        if (pen == null) return;
        var rows = Analysis.Duplicates(pen.Mods);
        ShowReport(rows, $"{rows.Count} possible duplicate groups. Check them in Penumbra before removing anything.");
    }

    void Sizes_Click(object sender, RoutedEventArgs e)
    {
        if (pen == null) return;
        ShowReport(Analysis.FolderSizes(pen.Mods), SizesReady ? "Disk use per Penumbra folder (including subfolders)." : "Still measuring; totals are partial. Run again in a moment.");
    }

    void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (pen == null || CompareA.SelectedItem is not Collection a || CompareB.SelectedItem is not Collection b) return;
        var rows = Analysis.Compare(pen, a, b);
        ShowReport(rows, $"{rows.Count} differences between “{a.Name}” and “{b.Name}” (installed mods only, inheritance applied).");
    }

    void ExportCsv_Click(object sender, RoutedEventArgs e) =>
        Export("CSV file|*.csv", "csv", () => Analysis.ToCsv(pen!, CollectionBox.SelectedItem as Collection));

    void ExportHtml_Click(object sender, RoutedEventArgs e) =>
        Export("HTML file|*.html", "html", () => Analysis.ToHtml(pen!, designs, CollectionBox.SelectedItem as Collection));

    void Export(string filter, string ext, Func<string> content)
    {
        if (pen == null) return;
        var dlg = new SaveFileDialog { Filter = filter, FileName = $"penumbra-inventory-{DateTime.Now:yyyyMMdd}.{ext}" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, content(), new UTF8Encoding(true));   // BOM so Excel reads names with accents correctly
        StatusText.Text = $"Exported {pen.Mods.Count} mods → {dlg.FileName}";
    }

    async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var zip = Path.Combine(appData, "backups", $"configs-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        StatusText.Text = "Backing up…";
        try
        {
            var (files, skipped) = await Task.Run(() => Analysis.Backup(pluginConfigs, zip));
            StatusText.Text = $"Backed up {files} files → {zip}" + (skipped.Count > 0 ? $" · {skipped.Count} skipped (in use): {skipped[0]}" : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)   // e.g. same-second zip name already exists
        {
            StatusText.Text = $"Backup failed: {ex.Message}";
        }
    }

    void BackupsOpen_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(appData, "backups");
        Directory.CreateDirectory(dir);
        Explorer($"\"{dir}\"");
    }

    // ---------------------------------------------------------------- helpers
    static ImageSource? Img(string? path, int width) => Thumbs.Convert(path, typeof(ImageSource), width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) as ImageSource;

    static void Explorer(string args) => Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
}
