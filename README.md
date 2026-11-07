# FF14 Mod Manager

A Windows app that lists your **Penumbra mods** and **Glamourer designs**, takes screenshots of the game window, and attaches them to designs and mods as thumbnails. It also includes cleanup and inventory tools. An optional in-game companion plugin, the **Bridge**, lets the app see which character you're on and apply designs or toggle mods.

The app **never writes to Penumbra or Glamourer files.** Penumbra rewrites its configs while the game runs, so a second writer could corrupt a collection. Every change goes through the Bridge plugin, which asks Penumbra and Glamourer to make it through their own IPC.

## What it does

| Tab | |
|---|---|
| **Mods** | Every mod in your Penumbra mod directory, with its Penumbra folder, ★ favourite, name, author, version, tags (meta + local) and size on disk. ✓ marks mods enabled in the collection picked at the top; inherited settings count. Filter by search, folder (a parent folder includes its subfolders), enabled only, or favourites. The details pane lists which collections enable the mod, which designs use it, and its description. Buttons: open the mod folder, attach a screenshot, enable/disable it in the game (Bridge). **Overlapping only** shows mods that share a file or gear piece with another mod; tick **Enabled only** as well to see what clashes in game in the selected collection. The details pane lists every mod this one overlaps and what they share, and **Show only mods overlapping this** filters the list to them. Search also matches gear pieces and game paths. |
| **Overlaps** | Every mod that changes a **game file** or a **gear piece** another mod also changes, with the mods it overlaps, the shared gear and the number of shared files. Default: **in game**, meaning only mods enabled in the selected collection, using only the options selected there; the Priority column shows who wins (the higher priority wins each contested file). Untick it to see every installed mod and every option, enabled or not. Filter by mod name, gear piece (`e6015`, `Body`, `smallclothes`) or part of a game path. Double-click a row to open that mod in the Mods tab, filtered to it and its overlaps. |
| **Designs** | Every Glamourer design with its folder, tags, last edit and linked mods (✗ = linked mod not installed). Buttons: apply to your character (Bridge), attach a screenshot, show the design file. |
| **Gallery** | Designs as a thumbnail grid. Double-click a design to open it. |
| **Screenshot** | Captures the FFXIV window with Windows.Graphics.Capture. The game can be behind other windows, but not minimised. Capture now, after 3 seconds, or press **Ctrl+Shift+F12** from inside the game. Captures land in *Unsorted*, where you attach them to the selected design or mod. When the Bridge is running, each capture also records the character, world, collection and last design applied. |
| **Tools** | **Cleanup report:** mods enabled in no collection and linked to no design, designs that link missing mods, broken `meta.json` files, and settings kept for deleted mods. It's a report only. **Duplicates:** same name + author, `(2)`-style reimports, and **similar names**. Two names count as similar when, ignoring case, punctuation, accents, word order and version tags (`V2`, `1.3.2`), they have the same words (a dropped or doubled letter allowed), or when one is the other plus up to two words (`Japanese Alley` / `Japanese Alley V2`, `Fae Fit` / `Fae Fit YAB`). Names where each side has its own words are treated as a set, not a duplicate (`Hooked (Front)` / `(Behind)`, `The Primal AST` / `BLM`). **Disk usage by folder.** **Compare two collections:** effective state, inheritance applied. **Export CSV / HTML.** **Back up configs:** zips `pluginConfigs/Penumbra` and `pluginConfigs/Glamourer`. |

**Theme** (top right): System follows the Windows light/dark setting; Dark and Light force one. It uses WPF's built-in Fluent theme, and your choice is remembered in `%LOCALAPPDATA%\FF14ModManager\theme.txt`. Selected rows use your Windows accent colour.

Hide the game HUD (Scroll Lock by default) before you capture if you want a clean shot. The capture is the game window exactly as it looks on screen.

## Where things live

| | |
|---|---|
| Penumbra mod directory | read from `%APPDATA%\XIVLauncher\pluginConfigs\Penumbra\config\*.json` → `ModDirectory` |
| Penumbra folders, favourites, local tags, notes | `Penumbra\mod_data.db` (LiteDB). The app reads a **copy** in its cache, so it never holds Penumbra's file open |
| Files each mod changes | Penumbra 1.7+: `meta.json` → `DefaultData` and `Groups[].Options[]` (`Containers` for combining groups), `Files` and `FileSwaps` keys. Older mods: `default_mod.json` + `group_*.json`. A **gear piece** is the model set + slot taken from the game path (`chara/equipment/e6015/…_top…` → `e6015 Body`; `e0000` is the bare body, "smallclothes"). These are model IDs, not item names: one model can be several items, and names live in the game's own data. Meta manipulations (EQP/IMC edits) are not counted |
| Collections | `Penumbra\collections\*.json`, `active_collections.json` |
| Glamourer designs | `Glamourer\designs\*.json` (file versions 2 and 3) |
| Screenshots | `%LOCALAPPDATA%\FF14ModManager\shots\{unsorted, design\<id>, mod\<dir>}` |
| Config backups | `%LOCALAPPDATA%\FF14ModManager\backups\configs-<timestamp>.zip` |

## Run it

`artifacts\app\FF14ModManager.exe` is a single self-contained exe, so no .NET install is needed. To rebuild it:

```bash
dotnet publish App/FF14ModManager.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/app
```

## The Bridge plugin (optional)

It enables *Apply to my character*, *Enable/Disable in collection*, the "In game:" status at the top right, and the character info saved with each screenshot.

It lives in its own repo, **[priestkos/FF14ModManagerBridge](https://github.com/priestkos/FF14ModManagerBridge)**, where CI installs the Dalamud distribution, builds it and uploads `latest.zip`. Install steps and the pipe protocol are in that README.

The app talks to it over the named pipe `FF14ModManager.Bridge` (current Windows user only). The protocol file `App/Core/Bridge.cs` is duplicated in the plugin repo as `FF14ModManagerBridge/Core/Bridge.cs`: **change both together.**

## Tests

```bash
dotnet run --project Tests            # self-check of the readers/analysis/bridge pipe on a generated fixture; exit code = failures
dotnet run --project Tests -- --live  # read-only summary of this machine's real Penumbra/Glamourer data
```

The fixture builds a fake `pluginConfigs` with a real LiteDB `mod_data.db`, collections with inheritance, v2 and v3 designs, a broken `meta.json`, a file the "game" holds open during a backup, and a `mod_data.db` that is locked exclusively. The bridge checks run a real named pipe.

## Layout

```
App/            WPF app (net10.0-windows10.0.22621.0)
  Core/         readers + analysis, no WPF and no Dalamud: Penumbra.cs, Glamourer.cs, Analysis.cs, Shots.cs, Bridge.cs (pipe protocol)
  Capture.cs    Windows.Graphics.Capture of the game window
Tests/          console self-check, links App/Core/*.cs
```

Dependencies: `LiteDB` 5.0.21 (MIT, app + tests). The plugin's `Penumbra.Api` / `Glamourer.Api` are in its own repo. `dotnet list package --vulnerable --include-transitive` reported nothing for any project on 2026-11-07.
