using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MixDeck.Model;

namespace MixDeck.Remote;

/// <summary>What the API can read and change; implemented by the main view-model on the UI thread.</summary>
public interface IRemoteBridge
{
    object State();
    object Levels();
    bool SetSend(string channelId, string mixId, double? volume, bool? muted);
    bool SetChannelMute(string channelId, bool? muted);
    bool SetMix(string mixId, double? volume, bool? muted);
    bool ApplyProfile(string profileId);
}

/// <summary>
/// Local API and phone remote, served by Kestrel.
/// REST: GET /api/state, POST /api/channels/{id}/mute, /api/channels/{id}/sends/{mix}, /api/mixes/{id}, /api/profiles/{id}/apply.
/// WebSocket /api/ws: pushes {"type":"state"} on change and {"type":"levels"} ~15×/s; accepts the same commands as JSON.
/// </summary>
public sealed class RemoteServer : IAsyncDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly RemoteConfig _cfg;
    readonly Func<Func<object?>, object?> _onUi;
    readonly IRemoteBridge _bridge;
    WebApplication? _app;

    public RemoteServer(RemoteConfig cfg, IRemoteBridge bridge, Func<Func<object?>, object?> onUiThread)
    {
        _cfg = cfg;
        _bridge = bridge;
        _onUi = onUiThread;
    }

    public string? Error { get; private set; }

    /// <summary>Addresses the phone can open (with the token), best first.</summary>
    public IReadOnlyList<string> Urls()
    {
        var hosts = new List<string>();
        if (_cfg.AllowLan) hosts.AddRange(LanAddresses());
        hosts.Add("localhost");
        return hosts.Select(h => $"http://{h}:{_cfg.Port}/?token={_cfg.Token}").ToList();
    }

    public async Task StartAsync()
    {
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "MixDeck" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                if (_cfg.AllowLan) k.ListenAnyIP(_cfg.Port);
                else k.ListenLocalhost(_cfg.Port);
            });
            var app = builder.Build();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

            app.MapGet("/", () => Results.Content(RemotePage(), "text/html; charset=utf-8"));
            app.MapGet("/api/state", (HttpContext ctx) => Authorized(ctx) ? Results.Json(Ui(_bridge.State), Json) : Results.Unauthorized());
            app.MapPost("/api/channels/{id}/mute", async (HttpContext ctx, string id) =>
                await Command(ctx, body => _bridge.SetChannelMute(id, Bool(body, "muted"))));
            app.MapPost("/api/channels/{id}/sends/{mix}", async (HttpContext ctx, string id, string mix) =>
                await Command(ctx, body => _bridge.SetSend(id, mix, Num(body, "volume"), Bool(body, "muted"))));
            app.MapPost("/api/mixes/{id}", async (HttpContext ctx, string id) =>
                await Command(ctx, body => _bridge.SetMix(id, Num(body, "volume"), Bool(body, "muted"))));
            app.MapPost("/api/profiles/{id}/apply", async (HttpContext ctx, string id) =>
                await Command(ctx, _ => _bridge.ApplyProfile(id)));
            app.Map("/api/ws", Socket);

            await app.StartAsync();
            _app = app;
            Error = null;
            Log.Write($"Remote API listening on port {_cfg.Port} ({(_cfg.AllowLan ? "home network" : "this PC only")})");
        }
        catch (Exception ex)
        {
            Error = ex is IOException or SocketException || ex.InnerException is SocketException
                ? $"Port {_cfg.Port} is used by another app."
                : ex.Message;
            Log.Write($"Remote API failed to start: {ex.Message}");
        }
    }

    object? Ui(Func<object?> f) => _onUi(f);

    bool Authorized(HttpContext ctx)
    {
        string? token = ctx.Request.Query["token"];
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(token) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = auth[7..].Trim();
        return token != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_cfg.Token));
    }

    async Task<IResult> Command(HttpContext ctx, Func<JsonElement, bool> action)
    {
        if (!Authorized(ctx)) return Results.Unauthorized();
        JsonElement body = default;
        if (ctx.Request.ContentLength > 0)
        {
            try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
            catch (JsonException) { return Results.BadRequest(new { error = "Body must be JSON" }); }
        }
        bool ok = Ui(() => action(body)) is true;
        return ok ? Results.Json(Ui(_bridge.State), Json) : Results.NotFound(new { error = "No such channel, output or profile" });
    }

    static double? Num(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? Math.Clamp(d, 0, 100) : null;

    static bool? Bool(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    async Task Socket(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        if (!Authorized(ctx)) { ctx.Response.StatusCode = 401; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var sendLock = new SemaphoreSlim(1, 1);

        async Task Send(object msg)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, Json);
            await sendLock.WaitAsync(cts.Token);
            try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token); }
            finally { sendLock.Release(); }
        }

        // Pusher: levels ~15×/s, full state when it changes
        var pusher = Task.Run(async () =>
        {
            string last = "";
            int tick = 0;
            while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                if (tick++ % 8 == 0)
                {
                    var state = Ui(_bridge.State);
                    var json = JsonSerializer.Serialize(state, Json);
                    if (json != last) { last = json; await Send(new { type = "state", data = state }); }
                }
                await Send(new { type = "levels", data = Ui(_bridge.Levels) });
                await Task.Delay(66, cts.Token);
            }
        });

        var buffer = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                try
                {
                    var msg = JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan(0, r.Count));
                    string? type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
                    string? Str(string n) => msg.TryGetProperty(n, out var v) ? v.GetString() : null;
                    Ui(() => type switch
                    {
                        "send" => _bridge.SetSend(Str("channel") ?? "", Str("mix") ?? "", Num(msg, "volume"), Bool(msg, "muted")),
                        "channel-mute" => _bridge.SetChannelMute(Str("channel") ?? "", Bool(msg, "muted")),
                        "mix" => _bridge.SetMix(Str("mix") ?? "", Num(msg, "volume"), Bool(msg, "muted")),
                        "profile" => _bridge.ApplyProfile(Str("profile") ?? ""),
                        _ => false,
                    });
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            cts.Cancel();
            try { await pusher; } catch { }
        }
    }

    static string RemotePage()
    {
        using var s = typeof(RemoteServer).Assembly.GetManifestResourceStream("remote.html")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>IPv4 addresses of active, non-virtual network adapters.</summary>
    static IEnumerable<string> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString())
            .Distinct();

    public async ValueTask DisposeAsync()
    {
        if (_app == null) return;
        try { await _app.StopAsync(new CancellationTokenSource(2000).Token); } catch { }
        await _app.DisposeAsync();
        _app = null;
    }
}
