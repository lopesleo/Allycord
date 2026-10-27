using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.Data.Json;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Xcord.Widget
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    static class Json
    {
        public static string Str(JsonObject o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v.ValueType == JsonValueType.String ? v.GetString() : null;

        public static bool Bool(JsonObject o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v.ValueType == JsonValueType.Boolean && v.GetBoolean();

        public static double Num(JsonObject o, string key, double fallback = 0) =>
            o != null && o.TryGetValue(key, out var v) && v.ValueType == JsonValueType.Number ? v.GetNumber() : fallback;

        public static JsonObject Obj(JsonObject o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v.ValueType == JsonValueType.Object ? v.GetObject() : null;

        public static JsonArray Arr(JsonObject o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v.ValueType == JsonValueType.Array ? v.GetArray() : new JsonArray();

        public static ImageSource Image(string url) => string.IsNullOrEmpty(url) ? null : new BitmapImage(new Uri(url));
    }

    public sealed class MemberVm : Observable
    {
        string _name, _avatarUrl;
        ImageSource _avatar;
        bool _speaking, _selfMute, _selfDeaf, _localMute;
        int _volume = 100;

        public MemberVm(string id) { Id = id; }

        public string Id { get; }
        public string Name { get => _name; private set => Set(ref _name, value); }
        public ImageSource Avatar { get => _avatar; private set => Set(ref _avatar, value); }
        public bool SelfMute { get => _selfMute; private set => Set(ref _selfMute, value); }
        public bool SelfDeaf { get => _selfDeaf; private set => Set(ref _selfDeaf, value); }
        public bool LocalMute { get => _localMute; private set => Set(ref _localMute, value); }

        public bool Speaking
        {
            get => _speaking;
            private set { if (Set(ref _speaking, value)) Raise(nameof(SpeakingOpacity)); }
        }

        public double SpeakingOpacity => _speaking ? 1 : 0;

        public int Volume
        {
            get => _volume;
            private set
            {
                if (!Set(ref _volume, value)) return;
                Raise(nameof(VolumeText));
                Raise(nameof(HasCustomVolume));
            }
        }

        public string VolumeText => $"{_volume}%";
        public bool HasCustomVolume => _volume != 100;

        public void Update(JsonObject j)
        {
            Name = Json.Str(j, "name");
            var url = Json.Str(j, "avatar_url");
            if (url != _avatarUrl)
            {
                _avatarUrl = url;
                Avatar = Json.Image(url);
            }
            Speaking = Json.Bool(j, "speaking");
            SelfMute = Json.Bool(j, "self_mute");
            SelfDeaf = Json.Bool(j, "self_deaf");
            LocalMute = Json.Bool(j, "local_mute");
            Volume = (int)Math.Round(Json.Num(j, "volume", 100));
        }
    }

    public sealed class GuildVm
    {
        public GuildVm(JsonObject j)
        {
            Id = Json.Str(j, "id");
            Name = Json.Str(j, "name") ?? "";
            Icon = Json.Image(Json.Str(j, "icon_url"));
            Initials = MakeInitials(Name);
        }

        public string Id { get; }
        public string Name { get; }
        public ImageSource Icon { get; }
        public string Initials { get; }

        static string MakeInitials(string name)
        {
            var result = "";
            foreach (var word in name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                result += char.ToUpperInvariant(word[0]);
                if (result.Length == 3) break;
            }
            return result;
        }
    }

    public sealed class ChannelVm : Observable
    {
        bool _isCurrent;

        public ChannelVm(JsonObject j)
        {
            Id = Json.Str(j, "id");
            Name = Json.Str(j, "name") ?? "";
            // Speaker for voice channels, broadcast for stage channels.
            Glyph = Json.Num(j, "type") == 13 ? "" : "";
        }

        public string Id { get; }
        public string Name { get; }
        public string Glyph { get; }
        public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    }
}
