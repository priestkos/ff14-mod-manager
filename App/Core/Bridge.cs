using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace FF14ModManager.Core;

/// <summary>
/// Desktop app ⇄ in-game plugin protocol: one JSON line request, one JSON line reply, per connection.
/// CurrentUserOnly on both ends: only this Windows user can connect, and the app only trusts a server this user owns.
/// Shared by the app (client) and the Dalamud plugin (server); keep it free of WPF and Dalamud types.
/// The same file lives in two repos: priestkos/ff14-mod-manager (App/Core/Bridge.cs) and
/// priestkos/FF14ModManagerBridge (FF14ModManagerBridge/Core/Bridge.cs). Change both together.
/// Commands: status | applyDesign {id} | setMod {collection, dir, enabled}
/// </summary>
public static class Bridge
{
    public const string PipeName = "FF14ModManager.Bridge";

    public static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    public static async Task Serve(Func<JsonObject, Task<JsonObject>> handle, CancellationToken ct,
        Action<Exception>? onError = null, string pipeName = PipeName)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(ct);
                JsonObject reply;
                try { reply = line is null ? Error("empty request") : await handle(JsonNode.Parse(line)?.AsObject() ?? Error("not an object")); }
                catch (Exception e) { reply = Error(e.Message); }
                await writer.WriteLineAsync(reply.ToJsonString().AsMemory(), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { onError?.Invoke(e); await Task.Delay(250, CancellationToken.None); }   // e.g. client hung up early
        }
    }

    /// <summary>Returns null when the bridge is not running (game closed or plugin not enabled).</summary>
    public static async Task<JsonObject?> Ask(JsonObject request, int timeoutMs = 2000, string pipeName = PipeName)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cts = new CancellationTokenSource(timeoutMs * 4);
        try
        {
            await pipe.ConnectAsync(timeoutMs, cts.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(request.ToJsonString().AsMemory(), cts.Token);
            var line = await reader.ReadLineAsync(cts.Token);
            return line is null ? null : JsonNode.Parse(line)?.AsObject();
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
