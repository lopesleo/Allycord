using System.Text.Json.Nodes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Allycord.Bridge.Discord;

namespace Allycord.Bridge;

/// <summary>
/// AppService link to the widget (same package). The widget is the AppService host; the bridge
/// connects as a client, answers requests and pushes state. When the widget process goes away the
/// service closes and the bridge exits.
/// </summary>
sealed class WidgetHost
{
    public const string ServiceName = "AllycordBridge";

    readonly DiscordSession _session;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    AppServiceConnection? _connection;

    public WidgetHost(DiscordSession session) => _session = session;

    public async Task RunAsync(CancellationTokenSource lifetime)
    {
        var connection = new AppServiceConnection
        {
            AppServiceName = ServiceName,
            PackageFamilyName = Package.Current.Id.FamilyName,
        };
        connection.RequestReceived += OnRequestReceived;
        connection.ServiceClosed += (_, e) =>
        {
            Log.Info($"AppService closed: {e.Status}");
            lifetime.Cancel();
        };

        var status = await connection.OpenAsync();
        if (status != AppServiceConnectionStatus.Success)
        {
            Log.Error($"AppService open failed: {status}");
            lifetime.Cancel();
            return;
        }

        Log.Info("AppService connected");
        _connection = connection;
        _session.StateChanged += json => _ = PushAsync("state", json);
        await PushAsync("state", _session.Snapshot());
    }

    async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        var response = new ValueSet();
        try
        {
            var method = args.Request.Message["m"] as string ?? "";
            var raw = args.Request.Message.TryGetValue("a", out var a) ? a as string : null;
            var payload = string.IsNullOrEmpty(raw) ? new JsonObject() : JsonNode.Parse(raw)!.AsObject();

            var result = await _session.HandleAsync(method, payload);
            response["ok"] = true;
            response["d"] = result?.ToJsonString() ?? "null";
        }
        catch (Exception ex)
        {
            Log.Error("Request failed", ex);
            response["ok"] = false;
            response["e"] = ex.Message;
        }

        try { await args.Request.SendResponseAsync(response); }
        catch (Exception ex) { Log.Error("SendResponse", ex); }
        finally { deferral.Complete(); }
    }

    async Task PushAsync(string evt, string json)
    {
        var connection = _connection;
        if (connection is null) return;

        await _sendLock.WaitAsync();
        try
        {
            await connection.SendMessageAsync(new ValueSet { ["evt"] = evt, ["d"] = json });
        }
        catch (Exception ex) { Log.Error("Push", ex); }
        finally { _sendLock.Release(); }
    }
}
