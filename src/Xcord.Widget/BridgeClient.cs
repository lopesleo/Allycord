using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.Data.Json;
using Windows.Foundation.Collections;
using Windows.Foundation.Metadata;

namespace Xcord.Widget
{
    /// <summary>
    /// Widget side of the AppService link to Xcord.Bridge. The bridge connects to us; requests go out
    /// as { m, a } and the bridge pushes { evt: "state", d } whenever Discord state changes.
    /// </summary>
    public sealed class BridgeClient
    {
        public static BridgeClient Instance { get; } = new BridgeClient();

        readonly object _gate = new object();
        AppServiceConnection _connection;
        BackgroundTaskDeferral _deferral;
        TaskCompletionSource<bool> _attached = new TaskCompletionSource<bool>();

        /// <summary>Raised on a background thread with the full state snapshot.</summary>
        public event Action<JsonObject> StateReceived;

        /// <summary>Raised on a background thread when the bridge goes away.</summary>
        public event Action Disconnected;

        public bool IsConnected => _connection != null;

        public void Attach(AppServiceConnection connection, IBackgroundTaskInstance task)
        {
            lock (_gate)
            {
                _deferral = task.GetDeferral();
                _connection = connection;
            }
            task.Canceled += (s, reason) => Detach();
            connection.ServiceClosed += (s, e) => Detach();
            connection.RequestReceived += OnRequestReceived;
            _attached.TrySetResult(true);
        }

        void Detach()
        {
            BackgroundTaskDeferral deferral;
            lock (_gate)
            {
                if (_connection == null) return;
                _connection = null;
                deferral = _deferral;
                _deferral = null;
                _attached = new TaskCompletionSource<bool>();
            }
            deferral?.Complete();
            Disconnected?.Invoke();
        }

        /// <summary>Starts the bridge if needed and waits for it to connect back.</summary>
        public async Task<bool> EnsureStartedAsync()
        {
            if (IsConnected) return true;
            var attached = _attached.Task;

            if (!ApiInformation.IsApiContractPresent("Windows.ApplicationModel.FullTrustAppContract", 1, 0))
                return false;

            try
            {
                await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
            }
            catch (Exception)
            {
                return false;
            }

            var winner = await Task.WhenAny(attached, Task.Delay(TimeSpan.FromSeconds(15)));
            return winner == attached;
        }

        async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                var message = args.Request.Message;
                if (message.TryGetValue("evt", out var evt) && (evt as string) == "state"
                    && JsonObject.TryParse(message["d"] as string ?? "", out var state))
                {
                    StateReceived?.Invoke(state);
                }
                await args.Request.SendResponseAsync(new ValueSet());
            }
            catch (Exception)
            {
                // The bridge treats a missing ack as a dropped push; the next push carries the full state.
            }
            finally
            {
                deferral.Complete();
            }
        }

        /// <summary>Calls a bridge method. Throws with the bridge's error message on failure.</summary>
        public async Task<IJsonValue> CallAsync(string method, JsonObject args = null)
        {
            var connection = _connection ?? throw new InvalidOperationException("Bridge not connected");
            var request = new ValueSet { ["m"] = method, ["a"] = args?.Stringify() ?? "" };

            var response = await connection.SendMessageAsync(request);
            if (response.Status != AppServiceResponseStatus.Success)
                throw new InvalidOperationException($"Bridge: {response.Status}");

            var message = response.Message;
            if (!(message["ok"] is bool ok) || !ok)
                throw new InvalidOperationException(message.TryGetValue("e", out var e) ? e as string : "Bridge error");

            return JsonValue.Parse(message.TryGetValue("d", out var d) ? d as string ?? "null" : "null");
        }
    }
}
