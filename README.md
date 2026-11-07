# FF14 Mod Manager

A Windows app that lists your **Penumbra mods** and **Glamourer designs**, takes screenshots of the game window, and attaches them to designs and mods as thumbnails. It also includes cleanup and inventory tools. An optional in-game companion plugin, the **Bridge**, lets the app see which character you're on and apply designs or toggle mods.

The app **never writes to Penumbra or Glamourer files.** Penumbra rewrites its configs while the game runs, so a second writer could corrupt a collection. Every change goes through the Bridge plugin, which asks Penumbra and Glamourer to make it through their own IPC.

## What it does

| Tab | |
|---|---|
| **Mods** | Every mod in your Penumbra mod directory, with its Penumbra folder, ★ favourite, name, author, version, tags (meta + local) and size on disk. ✓ marks mods enabled in the collection picked at the top; inherited settings count. Filter by search, folder (a parent folder includes its subfolders), enabled only, or favourites. The details pane lists which collections enable the mod, which designs use it, and its description. Buttons: open the mod folder, attach a screenshot, enable/disable it in the game (Bridge). |
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

1. `dotnet build Bridge/FF14ModManagerBridge.csproj -c Release`
2. In the game: `/xlsettings` → Experimental → Dev Plugin Locations → add `<repo>\Bridge\bin\Release\FF14ModManagerBridge.dll` → Save.
3. `/xlplugins` → Dev Tools → enable **FF14 Mod Manager Bridge**.

The Bridge has no window. It answers on the named pipe `FF14ModManager.Bridge`, opened with `PipeOptions.CurrentUserOnly`, so only your Windows user can talk to it. It supports three commands: `status`, `applyDesign {id}` (Glamourer `ApplyDesign`, one-off, equipment + customisation) and `setMod {collection, dir, enabled}` (Penumbra `TrySetMod`, then a redraw of you). It uses Penumbra.Api 5.19.2 and Glamourer.Api 2.8.2, built against Dalamud API 15.

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
Bridge/         Dalamud plugin, shares App/Core/Bridge.cs
Tests/          console self-check, links App/Core/*.cs
```

Dependencies: `LiteDB` 5.0.21 (MIT, app + tests) and `Penumbra.Api` / `Glamourer.Api` (MIT, Bridge only). `dotnet list package --vulnerable --include-transitive` reported nothing for all three projects on 2026-11-07.
