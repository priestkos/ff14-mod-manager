# FF14 Mod Manager

<img src="images/icon.png" width="96">

A Windows app that lists your Penumbra mods and Glamourer designs, finds the mods that clash, and screenshots your character for a design gallery.

`FF14ModManager.exe` · Windows 10/11 · .NET 10 (self-contained) · Penumbra 1.7 / Glamourer 1.7

> **No support will be provided. This software is to be used as is, without warranty of any kind.**

## What it does

| Tab | |
| --- | --- |
| **Mods** | Every mod in your Penumbra mod directory with its Penumbra folder, ★ favourite, author, version, tags and size on disk. ✓ = enabled in the collection picked at the top (inheritance counts). Filters: search (name, author, tags, folder, gear pieces, game paths and description; name matches are listed first, description-only matches last), **Tags** (click a tag once for ✓ must have, again for ✗ must not have; mod and local tags alike, plus *(no tags)*), folder (a parent includes its subfolders), **Enabled only**, **Favourites**, **Overlapping only**. The details pane shows which collections enable the mod, which designs use it, and every mod it overlaps. **Show only mods overlapping this** narrows the list to them. |
| **Overlaps** | Every mod that changes a **game file** or a **gear piece** another mod also changes: who it overlaps, the shared gear, the number of shared files. Default is **in game**: only mods enabled in the selected collection, with only their selected options, plus the **Priority** that decides who wins. Untick it to check every installed mod and every option. Double-click a row to jump to the mod. |
| **Designs** | Every Glamourer design with its folder, tags, last edit and linked mods (✗ = not installed). **Apply to my character** (needs the Bridge), attach a screenshot, show the design file. |
| **Gallery** | Designs as a thumbnail grid of the screenshots you attached. |
| **Screenshot** | Captures the FFXIV window, even behind other windows (not minimised): now, after 3 seconds, or **Ctrl+Shift+F12** from inside the game. Captures land in *Unsorted*; attach one to the selected design or mod. With the Bridge running, each capture also records character, world, collection and the last design applied. |
| **Tools** | **Cleanup report** (mods enabled nowhere and used by no design, designs linking missing mods, broken `meta.json`, settings kept for deleted mods; report only). **Duplicates** (same name + author, `(2)` reimports, similar names). **Disk usage by folder.** **Compare two collections.** **Export CSV / HTML.** **Back up configs** (zip of `pluginConfigs/Penumbra` + `Glamourer`). |

## How it works

The app **never writes to Penumbra or Glamourer files**. Penumbra rewrites its configs while the game runs, so a second writer could corrupt a collection. Every change (applying a design, toggling a mod) goes through the optional in-game plugin **[FF14 Mod Manager Bridge](https://github.com/priestkos/FF14ModManagerBridge)**, which asks Penumbra and Glamourer to make it.

| Reads | From |
| --- | --- |
| Mod directory | `%APPDATA%\XIVLauncher\pluginConfigs\Penumbra\config\*.json` → `ModDirectory` |
| Folders, favourites, local tags, notes | `Penumbra\mod_data.db` (LiteDB), read from a **copy**, so Penumbra's file is never held open |
| Files each mod changes | `meta.json` → `DefaultData` and `Groups[].Options[]` (Penumbra 1.7+), or `default_mod.json` + `group_*.json` (older mods) |
| Collections | `Penumbra\collections\*.json`, `active_collections.json` |
| Designs | `Glamourer\designs\*.json` (file versions 2 and 3) |

- **Gear piece** = model set + slot from the game path: `chara/equipment/e6015/…_top…` → `e6015 Body`. `e0000` is the bare body ("smallclothes") that body mods replace.
- **Similar names** = the same words once case, punctuation, accents, word order and version tags (`V2`, `1.3.2`) are ignored, a dropped or doubled letter allowed, or one name plus up to two words (`Japanese Alley` / `Japanese Alley V2`). When each name has words the other lacks, it is a set, not a duplicate (`Hooked (Front)` / `(Behind)`).
- **Screenshots** use Windows.Graphics.Capture: no hooks into the game, no admin rights.

## Settings

- **Theme** (top right): System follows Windows light/dark; Dark and Light force one. Remembered in `%LOCALAPPDATA%\FF14ModManager\theme.txt`.
- **Collection** (top left): the collection the ✓ column, **Enabled only** and the in-game overlaps use. Starts at Penumbra's *Current* collection.

Your data stays in `%LOCALAPPDATA%\FF14ModManager\`: `shots\{unsorted, design\<id>, mod\<dir>}`, `backups\configs-<timestamp>.zip`, and the `mod_data.db` copy in `cache\`.

## Build

Requires the .NET 10 SDK.

```
dotnet publish App/FF14ModManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/app
```

Output: `artifacts/app/FF14ModManager.exe`, one file, no .NET install needed to run it.

## Releases

Each push to `main` that changes the app (not docs-only paths) creates a GitHub release with `FF14ModManager.exe` attached. The version is the next four-part number above the latest release tag, or the csproj version when that is higher.

## Install

1. Download `FF14ModManager.exe` from the latest [release](https://github.com/priestkos/ff14-mod-manager/releases/latest) and run it. Windows SmartScreen may warn, because the exe is not code-signed.
2. Optional, for *Apply to my character*, *Enable/Disable in collection* and character info on screenshots: install **FF14 Mod Manager Bridge** in game from [PlogonYes](https://github.com/priestkos/PlogonYes) (`/xlsettings` → Experimental → Custom Plugin Repositories → `https://raw.githubusercontent.com/priestkos/PlogonYes/main/pluginmaster.json`).

## Tests

The readers and analysis are WPF-free, so they run as a console self-check against a generated fixture (never your live files):

```
dotnet run --project Tests
# ALL PASS (54 checks)
dotnet run --project Tests -- --live   # read-only summary of this machine's real Penumbra/Glamourer data
```

The fixture builds a fake `pluginConfigs` with a real LiteDB `mod_data.db`, collections with inheritance, Single/Multi option groups, both mod layouts, v2 and v3 designs, a broken `meta.json`, a file held open during a backup and an exclusively locked `mod_data.db`. The pipe checks run a real named pipe.

## Limits

- Gear pieces are model IDs (`e6015 Body`), not item names: names live in the game's own data, and one model can be several items.
- Overlaps count file replacements and file swaps, not Penumbra meta edits (EQP/IMC).
- On equal priority the app does not say which mod wins.
- The screenshot is the whole game window as it looks on screen; hide the HUD (Scroll Lock) for a clean shot. No crop yet.
- Apply design and toggle mod need the Bridge plugin running in game.

## Layout

```
App/Core/Penumbra.cs    mod directory, meta.json + option files, mod_data.db, collections, inheritance
App/Core/Glamourer.cs   designs (v2 + v3), folders
App/Core/Analysis.cs    cleanup, duplicates, similar names, overlaps, sizes, compare, export, backup
App/Core/Shots.cs       screenshot store + sidecar info
App/Core/Bridge.cs      pipe protocol, duplicated in the plugin repo: change both together
App/Capture.cs          Windows.Graphics.Capture of the game window
App/MainWindow.xaml     the window
Tests/Program.cs        offline self-check
images/                 icon.svg (source), icon.png; App/app.ico is the exe icon
.github/workflows/      build.yml (exe + self-check on every push), release.yml (release on main)
```

Dependency: `LiteDB` 5.0.21 (MIT).
