using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FF14ModManager.Core;
using Glamourer.Api.Enums;
using Penumbra.Api.Enums;

namespace FF14ModManagerBridge;

/// <summary>
/// Serves the FF14 Mod Manager desktop app over a current-user-only named pipe (see Core/Bridge.cs).
/// No UI and no config: it only answers status, applies a Glamourer design to you, or toggles a Penumbra mod.
/// Every game/IPC call runs on the framework thread, as Dalamud requires.
/// </summary>
public sealed class BridgePlugin : IDalamudPlugin
{
    readonly IObjectTable objects;
    readonly IFramework framework;
    readonly Penumbra.Api.IpcSubscribers.GetCollectionForObject getCollection;
    readonly Penumbra.Api.IpcSubscribers.TrySetMod trySetMod;
    readonly Penumbra.Api.IpcSubscribers.RedrawObject redraw;
    readonly Glamourer.Api.IpcSubscribers.ApplyDesign applyDesign;
    readonly CancellationTokenSource stop = new();
    readonly Task server;

    public BridgePlugin(IDalamudPluginInterface pluginInterface, IObjectTable objects, IFramework framework, IPluginLog log)
    {
        this.objects = objects;
        this.framework = framework;
        getCollection = new(pluginInterface);
        trySetMod = new(pluginInterface);
        redraw = new(pluginInterface);
        applyDesign = new(pluginInterface);
        server = Task.Run(() => Bridge.Serve(Handle, stop.Token, e => log.Warning(e, "FF14 Mod Manager bridge: pipe error")));
    }

    Task<JsonObject> Handle(JsonObject req) => framework.RunOnFrameworkThread(() => (string?)req["cmd"] switch
    {
        "status" => Status(),
        "applyDesign" => ApplyDesign(req),
        "setMod" => SetMod(req),
        var other => Bridge.Error($"unknown command '{other}'"),
    });

    static JsonObject Ok() => new() { ["ok"] = true };

    JsonObject Status()
    {
        var me = objects.LocalPlayer;
        if (me == null) return Ok();   // at the title screen: connected, nobody on screen
        var reply = Ok();
        reply["character"] = me.Name.TextValue;
        reply["world"] = me.HomeWorld.ValueNullable?.Name.ExtractText();
        try
        {
            var (valid, _, collection) = getCollection.Invoke(me.ObjectIndex);
            if (valid) { reply["collection"] = collection.Name; reply["collectionId"] = collection.Id.ToString(); }
        }
        catch (Exception e) { reply["collection"] = $"(Penumbra unavailable: {e.Message})"; }
        return reply;
    }

    JsonObject ApplyDesign(JsonObject req)
    {
        if (!Guid.TryParse((string?)req["id"], out var id)) return Bridge.Error("bad design id");
        if (objects.LocalPlayer == null) return Bridge.Error("not logged in");
        // Glamourer's own default for applying a design: equipment + customisation, as a one-off (not locked).
        var ec = applyDesign.Invoke(id, 0, 0, ApplyFlag.Once | ApplyFlag.Equipment | ApplyFlag.Customization);
        return ec == GlamourerApiEc.Success ? Ok() : Bridge.Error($"Glamourer said {ec}");
    }

    JsonObject SetMod(JsonObject req)
    {
        if (!Guid.TryParse((string?)req["collection"], out var collection)) return Bridge.Error("bad collection id");
        if ((string?)req["dir"] is not { Length: > 0 } dir) return Bridge.Error("missing mod directory");
        if (req["enabled"]?.GetValue<bool>() is not { } enabled) return Bridge.Error("missing enabled flag");
        var ec = trySetMod.Invoke(collection, dir, enabled);
        if (ec is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)) return Bridge.Error($"Penumbra said {ec}");
        if (objects.LocalPlayer != null) redraw.Invoke(0, RedrawType.Redraw);   // show the change on you now
        return Ok();
    }

    public void Dispose()
    {
        stop.Cancel();
        try { server.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        stop.Dispose();
    }
}
