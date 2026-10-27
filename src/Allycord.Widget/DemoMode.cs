using System;
using Windows.Data.Json;

namespace Allycord.Widget
{
    /// <summary>
    /// Fake bridge for design work and screenshots: launch with allycord://demo/&lt;scene&gt;
    /// (call, idle, channels, audio, setup, auth, connecting, nodiscord, pinned).
    /// Also reachable without the protocol: LocalState\demo.txt with the scene name.
    /// </summary>
    sealed class DemoMode
    {
        readonly string _scene;
        readonly long _joinedAt = DateTimeOffset.UtcNow.AddMinutes(-47).AddSeconds(-12).ToUnixTimeMilliseconds();
        bool _mute, _deaf, _inCall, _video, _screen;
        int _input = 82, _output = 100;

        DemoMode(string scene)
        {
            // "channels+open", "channels+search": optional flags after the scene for extra UI states.
            var parts = scene.Split('+');
            _scene = parts[0];
            Flags = parts;
            _inCall = _scene == "call" || _scene == "pinned" || _scene == "audio" || _scene == "channels";
        }

        public string[] Flags { get; }

        public bool Has(string flag) => Array.IndexOf(Flags, flag) > 0;

        public static DemoMode Parse(string parameter) =>
            parameter.StartsWith("demo:", StringComparison.Ordinal) ? new DemoMode(parameter.Substring(5)) : null;

        public bool Pinned => _scene == "pinned";
        public int Tab => _scene == "channels" ? 1 : _scene == "audio" ? 2 : 0;

        public JsonObject State()
        {
            var status = _scene == "setup" ? "needs_setup"
                : _scene == "auth" ? "needs_auth"
                : _scene == "connecting" ? "connecting"
                : _scene == "nodiscord" ? "no_discord"
                : "ready";

            var state = new JsonObject
            {
                ["status"] = JsonValue.CreateStringValue(status),
                ["error"] = JsonValue.CreateNullValue(),
                ["client_id"] = JsonValue.CreateStringValue("1234567890123456789"),
                ["user"] = status == "ready" ? (IJsonValue)User("100000000000000001", "Nova", null) : JsonValue.CreateNullValue(),
                ["settings"] = new JsonObject
                {
                    ["mute"] = JsonValue.CreateBooleanValue(_mute || _deaf),
                    ["deaf"] = JsonValue.CreateBooleanValue(_deaf),
                    ["input_volume"] = JsonValue.CreateNumberValue(_input),
                    ["output_volume"] = JsonValue.CreateNumberValue(_output),
                },
                ["channel"] = JsonValue.CreateNullValue(),
                ["members"] = new JsonArray(),
                ["media"] = new JsonObject
                {
                    ["video_supported"] = JsonValue.CreateBooleanValue(true),
                    ["video_active"] = JsonValue.CreateBooleanValue(_video),
                    ["screen_supported"] = JsonValue.CreateBooleanValue(true),
                    ["screen_active"] = JsonValue.CreateBooleanValue(_screen),
                },
            };

            if (status == "ready" && _inCall)
            {
                state["channel"] = new JsonObject
                {
                    ["id"] = JsonValue.CreateStringValue("200000000000000002"),
                    ["name"] = JsonValue.CreateStringValue("Ranked até cair"),
                    ["guild_id"] = JsonValue.CreateStringValue("300000000000000001"),
                    ["guild_name"] = JsonValue.CreateStringValue("Squad Ally"),
                    ["joined_at"] = JsonValue.CreateNumberValue(_joinedAt),
                };
                var members = new JsonArray
                {
                    Member("100000000000000001", "Nova", null, speaking: true, selfMute: _mute || _deaf, selfDeaf: _deaf),
                    Member("100000000000000002", "Raven", null, speaking: true),
                    Member("100000000000000003", "Kai", null, selfMute: true),
                    Member("100000000000000004", "Luna", null, selfDeaf: true),
                    Member("100000000000000005", "Felipe", null, volume: 140),
                    Member("100000000000000006", "Marina", null, localMute: true),
                };
                state["members"] = members;
            }
            return state;
        }

        public IJsonValue Call(string method, JsonObject args)
        {
            switch (method)
            {
                case "toggle_mute": _mute = !_mute; break;
                case "toggle_deaf": _deaf = !_deaf; if (!_deaf) _mute = false; break;
                case "leave": _inCall = false; break;
                case "toggle_video": _video = !_video; break;
                case "toggle_screenshare":
                    _screen = !_screen;
                    return new JsonObject { ["changed"] = JsonValue.CreateBooleanValue(true) };
                case "join": _inCall = true; break;
                case "set_input_volume": _input = (int)args.GetNamedNumber("volume"); break;
                case "set_output_volume": _output = (int)args.GetNamedNumber("volume"); break;
                case "get_guilds":
                    return new JsonArray
                    {
                        Guild("300000000000000001", "Squad Ally"),
                        Guild("300000000000000002", "Ally Brasil"),
                        Guild("300000000000000003", "Valorant BR"),
                        Guild("300000000000000004", "Dev Club"),
                        Guild("300000000000000005", "Minecraft SMP"),
                        Guild("300000000000000006", "Indie Devs"),
                    };
                case "get_channels":
                    return new JsonArray
                    {
                        Channel("200000000000000001", "Lobby", 2, "Bruno", "Carla"),
                        Channel("200000000000000002", "Ranked até cair", 2, "Nova", "Raven", "Kai", "Luna", "Felipe", "Marina"),
                        Channel("200000000000000003", "Casual", 2),
                        Channel("200000000000000004", "AFK", 2, "Diego"),
                        Channel("200000000000000005", "Palco de sexta", 13, "Rafa", "Gabi", "Teo", "Nina", "Caio", "Lia", "Duda", "Iago"),
                    };
            }
            return JsonValue.CreateNullValue();
        }

        static JsonObject User(string id, string name, int? avatar) => new JsonObject
        {
            ["id"] = JsonValue.CreateStringValue(id),
            ["name"] = JsonValue.CreateStringValue(name),
            ["avatar_url"] = avatar is int n
                ? JsonValue.CreateStringValue($"https://cdn.discordapp.com/embed/avatars/{n}.png")
                : JsonValue.CreateNullValue(),
        };

        static JsonObject Member(string id, string name, int? avatar, bool speaking = false, bool selfMute = false,
            bool selfDeaf = false, bool localMute = false, int volume = 100)
        {
            var m = User(id, name, avatar);
            m["speaking"] = JsonValue.CreateBooleanValue(speaking);
            m["self_mute"] = JsonValue.CreateBooleanValue(selfMute || selfDeaf);
            m["self_deaf"] = JsonValue.CreateBooleanValue(selfDeaf);
            m["local_mute"] = JsonValue.CreateBooleanValue(localMute);
            m["volume"] = JsonValue.CreateNumberValue(volume);
            return m;
        }

        static JsonObject Guild(string id, string name) => new JsonObject
        {
            ["id"] = JsonValue.CreateStringValue(id),
            ["name"] = JsonValue.CreateStringValue(name),
            ["icon_url"] = JsonValue.CreateNullValue(),
        };

        static JsonObject Channel(string id, string name, int type, params string[] people)
        {
            var members = new JsonArray();
            for (var i = 0; i < people.Length && i < 5; i++)
                members.Add(User("4000000000000000" + i.ToString("00") + id.Substring(id.Length - 1), people[i], null));
            return new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue(id),
                ["name"] = JsonValue.CreateStringValue(name),
                ["type"] = JsonValue.CreateNumberValue(type),
                ["member_count"] = JsonValue.CreateNumberValue(people.Length),
                ["members"] = members,
            };
        }
    }
}
