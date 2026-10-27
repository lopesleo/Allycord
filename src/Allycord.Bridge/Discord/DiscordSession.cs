using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Allycord.Bridge.Discord;

/// <summary>
/// Owns the Discord connection and the voice state the widget renders.
/// Reconnects on its own; state changes are coalesced and pushed through <see cref="StateChanged"/>.
/// </summary>
sealed class DiscordSession
{
    static readonly string[] Scopes = ["rpc", "rpc.voice.read", "rpc.voice.write"];
    static readonly string[] ChannelEvents = ["VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP"];

    readonly TokenStore _tokens;
    readonly AppConfig _config;
    readonly object _gate = new();
    readonly SemaphoreSlim _channelLock = new(1, 1);
    readonly SemaphoreSlim _wake = new(0);
    IpcClient? _ipc;
    int _pushScheduled;

    // ---- state (guarded by _gate) ----
    string _status = "connecting";
    string? _error;
    JsonObject? _user;
    bool _mute, _deaf;
    int _inputVolume = 100, _outputVolume = 100;
    string? _channelId, _channelName, _guildId, _guildName;
    long _joinedAt;
    readonly Dictionary<string, JsonObject> _members = new();
    readonly HashSet<string> _speaking = new();

    public DiscordSession(TokenStore tokens, AppConfig config)
    {
        _tokens = tokens;
        _config = config;
    }

    string ClientId => _config.ClientId ?? throw new InvalidOperationException("Client ID não configurado");

    /// <summary>Full state snapshot as JSON, raised (debounced) whenever something changes.</summary>
    public event Action<string>? StateChanged;

    /// <summary>Sleeps between reconnect attempts; <see cref="_wake"/> cuts it short when settings change.</summary>
    Task PauseAsync(int ms, CancellationToken ct) => _wake.WaitAsync(ms, ct);

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_config.ClientId is null)
            {
                SetStatus("needs_setup");
                await PauseAsync(Timeout.Infinite, ct);
                continue;
            }

            if (!IpcClient.IsDiscordPipePresent())
            {
                SetStatus("no_discord");
                await PauseAsync(3000, ct);
                continue;
            }

            SetStatus("connecting");
            var ipc = new IpcClient(ClientId);
            var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ipc.Disconnected += _ => lost.TrySetResult();
            ipc.EventReceived += OnEvent;

            if (await ipc.ConnectAsync(ct) is null)
            {
                await ipc.DisposeAsync();
                if (ipc.RejectReason?.Contains("Client ID", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Not a transient problem: wait for the user to fix the ID instead of hammering Discord.
                    SetStatus("needs_setup", "invalid_client_id");
                    await PauseAsync(Timeout.Infinite, ct);
                }
                else
                {
                    SetStatus("no_discord", ipc.RejectReason);
                    await PauseAsync(3000, ct);
                }
                continue;
            }

            _ipc = ipc;
            await ResumeAuthAsync();
            await Task.WhenAny(lost.Task, Task.Delay(Timeout.Infinite, ct));

            _ipc = null;
            await ipc.DisposeAsync();
            ResetVoiceState();
            if (!ct.IsCancellationRequested) await PauseAsync(2000, ct);
        }
    }

    // ------------------------------------------------------------------ auth

    async Task ResumeAuthAsync()
    {
        var token = _tokens.Load();
        if (token is null)
        {
            SetStatus("needs_auth");
            return;
        }

        if (token.IsExpired) token = await TryRefreshAsync(token);
        if (token is not null && await AuthenticateAsync(token.AccessToken)) return;

        // Access token may have been revoked before its expiry; one refresh attempt.
        if (token?.RefreshToken is not null)
        {
            token = await TryRefreshAsync(token);
            if (token is not null && await AuthenticateAsync(token.AccessToken)) return;
        }

        SetStatus("needs_auth");
    }

    async Task<TokenSet?> TryRefreshAsync(TokenSet token)
    {
        if (token.RefreshToken is null) return null;
        try
        {
            var refreshed = await OAuth.RefreshAsync(ClientId, token.RefreshToken);
            _tokens.Save(refreshed);
            Log.Info("Token refreshed");
            return refreshed;
        }
        catch (Exception ex)
        {
            Log.Error("Token refresh", ex);
            return null;
        }
    }

    async Task AuthorizeAsync()
    {
        var ipc = RequireIpc();
        SetStatus("authorizing");
        try
        {
            var (verifier, challenge) = OAuth.CreatePkcePair();
            var data = await ipc.SendAsync("AUTHORIZE", new JsonObject
            {
                ["client_id"] = ClientId,
                ["scopes"] = new JsonArray(Scopes.Select(s => (JsonNode)s).ToArray()),
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
            }, timeout: TimeSpan.FromMinutes(5));

            var code = data.GetProperty("code").GetString()!;
            var token = await OAuth.ExchangeCodeAsync(ClientId, code, verifier);
            _tokens.Save(token);

            if (!await AuthenticateAsync(token.AccessToken))
                throw new InvalidOperationException("AUTHENTICATE falhou após autorizar");
        }
        catch (Exception ex)
        {
            Log.Error("Authorize", ex);
            // Codes the widget turns into setup instructions:
            // - invalid_scope: RPC scopes are private beta; the account isn't owner/tester of the app.
            // - invalid_client: the token exchange needs "Public Client" enabled (PKCE, no secret).
            var error = ex.Message.Contains("invalid_scope") ? "not_tester"
                : ex.Message.Contains("invalid_client") ? "not_public_client"
                : ex.Message;
            SetStatus("needs_auth", error);
            throw;
        }
    }

    async Task<bool> AuthenticateAsync(string accessToken)
    {
        var ipc = RequireIpc();
        try
        {
            var data = await ipc.SendAsync("AUTHENTICATE", new JsonObject { ["access_token"] = accessToken });
            var user = data.GetProperty("user");
            lock (_gate) _user = UserJson(user);
            Log.Info($"Authenticated as {user.GetProperty("username").GetString()}");
        }
        catch (DiscordRpcException ex)
        {
            Log.Error("Authenticate", ex);
            return false;
        }

        SetStatus("ready");
        await ipc.SubscribeAsync("VOICE_SETTINGS_UPDATE");
        await ipc.SubscribeAsync("VOICE_CHANNEL_SELECT");
        ApplyVoiceSettings(await ipc.SendAsync("GET_VOICE_SETTINGS"));
        await RefreshSelectedChannelAsync();
        return true;
    }

    // ------------------------------------------------------------------ events

    // Runs on the IPC read loop: anything that awaits a Discord response must be pushed off this thread.
    void OnEvent(string evt, JsonElement data)
    {
        switch (evt)
        {
            case "VOICE_SETTINGS_UPDATE":
                ApplyVoiceSettings(data);
                break;
            case "VOICE_CHANNEL_SELECT":
                _ = Task.Run(RefreshSelectedChannelAsync);
                break;
            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
                var member = MemberJson(data);
                lock (_gate) _members[(string)member["id"]!] = member;
                MarkDirty();
                break;
            case "VOICE_STATE_DELETE":
                var id = data.GetProperty("user").GetProperty("id").GetString()!;
                lock (_gate)
                {
                    _members.Remove(id);
                    _speaking.Remove(id);
                }
                MarkDirty();
                break;
            case "SPEAKING_START":
            case "SPEAKING_STOP":
                if (data.TryGetProperty("channel_id", out var ch) && ch.GetString() != _channelId) break;
                var userId = data.GetProperty("user_id").GetString()!;
                lock (_gate)
                {
                    if (evt == "SPEAKING_START") _speaking.Add(userId);
                    else _speaking.Remove(userId);
                }
                MarkDirty();
                break;
        }
    }

    void ApplyVoiceSettings(JsonElement data)
    {
        lock (_gate)
        {
            if (data.TryGetProperty("mute", out var m)) _mute = m.GetBoolean();
            if (data.TryGetProperty("deaf", out var d)) _deaf = d.GetBoolean();
            if (data.TryGetProperty("input", out var i) && i.TryGetProperty("volume", out var iv)) _inputVolume = (int)Math.Round(iv.GetDouble());
            if (data.TryGetProperty("output", out var o) && o.TryGetProperty("volume", out var ov)) _outputVolume = (int)Math.Round(ov.GetDouble());
        }
        MarkDirty();
    }

    async Task RefreshSelectedChannelAsync()
    {
        var ipc = _ipc;
        if (ipc is null) return;

        await _channelLock.WaitAsync();
        try
        {
            var data = await ipc.SendAsync("GET_SELECTED_VOICE_CHANNEL");
            var newId = data.ValueKind == JsonValueKind.Object ? data.GetProperty("id").GetString() : null;
            var oldId = _channelId;

            if (oldId is not null && oldId != newId)
                foreach (var evt in ChannelEvents)
                    try { await ipc.UnsubscribeAsync(evt, new JsonObject { ["channel_id"] = oldId }); } catch { }

            lock (_gate)
            {
                _channelId = newId;
                _members.Clear();
                _speaking.Clear();
                if (newId is null)
                {
                    _channelName = _guildId = _guildName = null;
                    _joinedAt = 0;
                }
                else
                {
                    _channelName = data.GetProperty("name").GetString();
                    _guildId = data.TryGetProperty("guild_id", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
                    if (oldId != newId) _joinedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    foreach (var vs in data.GetProperty("voice_states").EnumerateArray())
                    {
                        var member = MemberJson(vs);
                        _members[(string)member["id"]!] = member;
                    }
                }
            }
            MarkDirty();

            if (newId is null || oldId == newId) return;

            foreach (var evt in ChannelEvents)
                await ipc.SubscribeAsync(evt, new JsonObject { ["channel_id"] = newId });

            if (_guildId is { } guildId)
            {
                var guild = await ipc.SendAsync("GET_GUILD", new JsonObject { ["guild_id"] = guildId });
                lock (_gate) _guildName = guild.GetProperty("name").GetString();
                MarkDirty();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Refresh selected channel", ex);
        }
        finally
        {
            _channelLock.Release();
        }
    }

    // ------------------------------------------------------------------ commands

    public async Task<JsonNode?> HandleAsync(string method, JsonObject args)
    {
        switch (method)
        {
            case "get_state":
                return JsonNode.Parse(Snapshot());
            case "authorize":
                // The user may take minutes to approve in Discord; the outcome arrives as a state push.
                _ = AuthorizeAsync().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return null;
            case "logout":
                _tokens.Clear();
                lock (_gate) _user = null;
                if (_ipc is { } current) await current.DisposeAsync(); // reconnect unauthenticated
                return null;
            case "launch_discord":
                return LaunchDiscord();
            case "set_client_id":
                var clientId = ((string?)args["client_id"])?.Trim();
                if (!AppConfig.IsValidClientId(clientId))
                    throw new ArgumentException("invalid_client_id");
                if (clientId == _config.ClientId && _status != "needs_setup") return null;

                _config.SetClientId(clientId!);
                _tokens.Clear(); // tokens belong to the previous application
                lock (_gate) _user = null;
                SetStatus("connecting");
                if (_ipc is { } previous) await previous.DisposeAsync(); // reconnect with the new ID
                else _wake.Release();
                return null;
        }

        var ipc = RequireReady();
        switch (method)
        {
            case "toggle_mute":
                bool mute;
                lock (_gate) mute = !_mute;
                ApplyVoiceSettings(await ipc.SendAsync("SET_VOICE_SETTINGS", new JsonObject { ["mute"] = mute }));
                return null;
            case "toggle_deaf":
                bool deaf;
                lock (_gate) deaf = !_deaf;
                ApplyVoiceSettings(await ipc.SendAsync("SET_VOICE_SETTINGS", new JsonObject { ["deaf"] = deaf }));
                return null;
            case "set_input_volume":
                ApplyVoiceSettings(await ipc.SendAsync("SET_VOICE_SETTINGS", new JsonObject
                {
                    ["input"] = new JsonObject { ["volume"] = Math.Clamp((int)args["volume"]!, 0, 100) },
                }));
                return null;
            case "set_output_volume":
                ApplyVoiceSettings(await ipc.SendAsync("SET_VOICE_SETTINGS", new JsonObject
                {
                    ["output"] = new JsonObject { ["volume"] = Math.Clamp((int)args["volume"]!, 0, 200) },
                }));
                return null;
            case "leave":
                await ipc.SendAsync("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = null });
                return null;
            case "join":
                await ipc.SendAsync("SELECT_VOICE_CHANNEL", new JsonObject
                {
                    ["channel_id"] = (string)args["channel_id"]!,
                    ["force"] = true,
                }, timeout: TimeSpan.FromSeconds(20));
                return null;
            case "get_guilds":
                var guilds = await ipc.SendAsync("GET_GUILDS");
                return new JsonArray(guilds.GetProperty("guilds").EnumerateArray().Select(g => (JsonNode)new JsonObject
                {
                    ["id"] = g.GetProperty("id").GetString(),
                    ["name"] = g.GetProperty("name").GetString(),
                    ["icon_url"] = g.TryGetProperty("icon_url", out var icon) && icon.ValueKind == JsonValueKind.String ? icon.GetString() : null,
                }).ToArray());
            case "get_channels":
                var channels = await ipc.SendAsync("GET_CHANNELS", new JsonObject { ["guild_id"] = (string)args["guild_id"]! });
                var voiceChannels = channels.GetProperty("channels").EnumerateArray()
                    .Where(c => c.GetProperty("type").GetInt32() is 2 or 13) // voice, stage
                    .ToList();
                // GET_CHANNELS has no occupants; ask each voice channel (in parallel, only for the server being viewed).
                var rows = await Task.WhenAll(voiceChannels.Select(c => ChannelRowAsync(ipc, c)));
                return new JsonArray(rows.Select(r => (JsonNode)r).ToArray());
            case "set_user_volume":
                await ipc.SendAsync("SET_USER_VOICE_SETTINGS", new JsonObject
                {
                    ["user_id"] = (string)args["user_id"]!,
                    ["volume"] = Math.Clamp((int)args["volume"]!, 0, 200),
                });
                return null;
            case "mute_user":
                await ipc.SendAsync("SET_USER_VOICE_SETTINGS", new JsonObject
                {
                    ["user_id"] = (string)args["user_id"]!,
                    ["mute"] = (bool)args["mute"]!,
                });
                return null;
            default:
                throw new InvalidOperationException($"Unknown method {method}");
        }
    }

    /// <summary>A voice channel with a preview of who's in it (first few people + total).</summary>
    static async Task<JsonObject> ChannelRowAsync(IpcClient ipc, JsonElement channel)
    {
        var id = channel.GetProperty("id").GetString()!;
        var row = new JsonObject
        {
            ["id"] = id,
            ["name"] = channel.GetProperty("name").GetString(),
            ["type"] = channel.GetProperty("type").GetInt32(),
            ["member_count"] = 0,
            ["members"] = new JsonArray(),
        };
        try
        {
            var detail = await ipc.SendAsync("GET_CHANNEL", new JsonObject { ["channel_id"] = id });
            if (detail.TryGetProperty("voice_states", out var states) && states.ValueKind == JsonValueKind.Array)
            {
                row["member_count"] = states.GetArrayLength();
                row["members"] = new JsonArray(states.EnumerateArray().Take(5).Select(vs =>
                {
                    var m = MemberJson(vs);
                    return (JsonNode)new JsonObject { ["id"] = m["id"]?.DeepClone(), ["name"] = m["name"]?.DeepClone(), ["avatar_url"] = m["avatar_url"]?.DeepClone() };
                }).ToArray());
            }
        }
        catch (Exception ex)
        {
            Log.Error($"GET_CHANNEL {id}", ex); // the row still shows, just without occupants
        }
        return row;
    }

    static JsonObject LaunchDiscord()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (dir, exe) in new[] { ("Discord", "Discord.exe"), ("DiscordPTB", "DiscordPTB.exe"), ("DiscordCanary", "DiscordCanary.exe") })
        {
            var updater = Path.Combine(local, dir, "Update.exe");
            if (!File.Exists(updater)) continue;
            // Same switch Discord's own "open on startup" uses: straight to the tray, no window. A hidden
            // Chromium window doesn't paint, so it stays off the GPU while a game is running.
            Process.Start(new ProcessStartInfo(updater, $"--processStart {exe} --process-start-args --start-minimized") { UseShellExecute = false });
            return new JsonObject { ["launched"] = true };
        }
        return new JsonObject { ["launched"] = false };
    }

    IpcClient RequireIpc() => _ipc ?? throw new InvalidOperationException("Discord não está conectado");

    IpcClient RequireReady()
    {
        var ipc = RequireIpc();
        lock (_gate)
            if (_status != "ready") throw new InvalidOperationException("Não autorizado");
        return ipc;
    }

    // ------------------------------------------------------------------ state

    void SetStatus(string status, string? error = null)
    {
        lock (_gate)
        {
            if (_status == status && _error == error) return;
            _status = status;
            _error = error;
        }
        MarkDirty();
    }

    void ResetVoiceState()
    {
        lock (_gate)
        {
            _user = null;
            _channelId = _channelName = _guildId = _guildName = null;
            _joinedAt = 0;
            _members.Clear();
            _speaking.Clear();
        }
        MarkDirty();
    }

    void MarkDirty()
    {
        if (Interlocked.Exchange(ref _pushScheduled, 1) == 1) return;
        _ = Task.Delay(50).ContinueWith(_ =>
        {
            Volatile.Write(ref _pushScheduled, 0);
            try { StateChanged?.Invoke(Snapshot()); }
            catch (Exception ex) { Log.Error("StateChanged handler", ex); }
        });
    }

    public string Snapshot()
    {
        lock (_gate)
        {
            var members = new JsonArray(_members.Values
                .OrderBy(m => (string?)m["name"], StringComparer.CurrentCultureIgnoreCase)
                .Select(m =>
                {
                    var copy = m.DeepClone().AsObject();
                    copy["speaking"] = _speaking.Contains((string)m["id"]!);
                    return (JsonNode)copy;
                }).ToArray());

            return new JsonObject
            {
                ["status"] = _status,
                ["error"] = _error,
                ["client_id"] = _config.ClientId,
                ["user"] = _user?.DeepClone(),
                ["settings"] = new JsonObject
                {
                    ["mute"] = _mute,
                    ["deaf"] = _deaf,
                    ["input_volume"] = _inputVolume,
                    ["output_volume"] = _outputVolume,
                },
                ["channel"] = _channelId is null ? null : new JsonObject
                {
                    ["id"] = _channelId,
                    ["name"] = _channelName,
                    ["guild_id"] = _guildId,
                    ["guild_name"] = _guildName,
                    ["joined_at"] = _joinedAt,
                },
                ["members"] = members,
            }.ToJsonString();
        }
    }

    static JsonObject UserJson(JsonElement user)
    {
        var id = user.GetProperty("id").GetString()!;
        var name = user.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String
            ? gn.GetString()
            : user.GetProperty("username").GetString();
        var avatar = user.TryGetProperty("avatar", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        return new JsonObject { ["id"] = id, ["name"] = name, ["avatar_url"] = AvatarUrl(id, avatar) };
    }

    /// <summary>One entry of voice_states / a VOICE_STATE_* event.</summary>
    static JsonObject MemberJson(JsonElement vs)
    {
        var member = UserJson(vs.GetProperty("user"));
        if (vs.TryGetProperty("nick", out var nick) && nick.ValueKind == JsonValueKind.String && nick.GetString() is { Length: > 0 } n)
            member["name"] = n;

        member["volume"] = vs.TryGetProperty("volume", out var v) ? (int)Math.Round(v.GetDouble()) : 100;
        member["local_mute"] = vs.TryGetProperty("mute", out var lm) && lm.GetBoolean();

        if (vs.TryGetProperty("voice_state", out var state))
        {
            member["self_mute"] = Flag(state, "self_mute") || Flag(state, "mute") || Flag(state, "suppress");
            member["self_deaf"] = Flag(state, "self_deaf") || Flag(state, "deaf");
        }
        return member;

        static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
    }

    static string AvatarUrl(string userId, string? hash) => hash is null
        ? $"https://cdn.discordapp.com/embed/avatars/{(ulong.Parse(userId) >> 22) % 6}.png"
        : $"https://cdn.discordapp.com/avatars/{userId}/{hash}.png?size=64";
}
