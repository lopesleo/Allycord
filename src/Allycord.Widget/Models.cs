using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.Data.Json;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Allycord.Widget
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

        /// <summary>Decoded at display size: a full-resolution server icon would cost megabytes for a 48px circle.</summary>
        public static ImageSource Image(string url, int decodeWidth = 64) => string.IsNullOrEmpty(url) ? null
            : new BitmapImage(new Uri(url)) { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical };

        static readonly SolidColorBrush[] Tints =
        {
            new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x56, 0x4C, 0xC4)),
            new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2C, 0x7A, 0x6B)),
            new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x95, 0x4A, 0x76)),
            new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x38, 0x6A, 0xAD)),
            new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x9C, 0x63, 0x34)),
            new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x5A, 0x69, 0x2C)),
        };

        /// <summary>Stable per-id color behind initials, so people without a photo are still told apart.</summary>
        public static Brush Tint(string id)
        {
            var hash = 0;
            foreach (var c in id ?? "") hash = hash * 31 + c;
            return Tints[(hash & 0x7FFFFFFF) % Tints.Length];
        }
    }

    public sealed class MemberVm : Observable
    {
        string _name, _avatarUrl;
        ImageSource _avatar;
        bool _speaking, _selfMute, _selfDeaf, _localMute;
        int _volume = 100;

        public MemberVm(string id)
        {
            Id = id;
            Tint = Json.Tint(id);
        }

        public string Id { get; }
        public Brush Tint { get; }
        public ImageSource Avatar { get => _avatar; private set => Set(ref _avatar, value); }

        public string Name
        {
            get => _name;
            private set { if (Set(ref _name, value)) Raise(nameof(Initial)); }
        }

        public string Initial => string.IsNullOrEmpty(_name) ? "?" : char.ToUpperInvariant(_name[0]).ToString();

        public bool SelfMute
        {
            get => _selfMute;
            private set { if (Set(ref _selfMute, value)) RaiseBadge(); }
        }

        public bool SelfDeaf
        {
            get => _selfDeaf;
            private set { if (Set(ref _selfDeaf, value)) RaiseBadge(); }
        }

        public bool LocalMute
        {
            get => _localMute;
            private set { if (Set(ref _localMute, value)) Raise(nameof(AvatarOpacity)); }
        }

        public bool Speaking
        {
            get => _speaking;
            private set
            {
                if (!Set(ref _speaking, value)) return;
                Raise(nameof(SpeakingOpacity));
                Raise(nameof(PinnedOpacity));
            }
        }

        public double SpeakingOpacity => _speaking ? 1 : 0;
        public double PinnedOpacity => _speaking ? 1 : 0.55;
        public double AvatarOpacity => _localMute ? 0.4 : 1;

        // Deafened implies muted, so the deafen badge wins.
        public bool HasBadge => _selfMute || _selfDeaf;
        public string BadgeGlyph => _selfDeaf ? "" : "";

        void RaiseBadge()
        {
            Raise(nameof(HasBadge));
            Raise(nameof(BadgeGlyph));
        }

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

    public sealed class GuildVm : Observable
    {
        public GuildVm(JsonObject j)
        {
            Id = Json.Str(j, "id");
            Name = Json.Str(j, "name") ?? "";
            _iconUrl = Json.Str(j, "icon_url");
            Initials = MakeInitials(Name);
            Tint = Json.Tint(Id);
        }

        public string Id { get; }
        public string Name { get; }
        readonly string _iconUrl;
        ImageSource _icon;
        bool _isFavorite;

        /// <summary>Created on first read, i.e. when the virtualized list realizes this row, never for off-screen servers.</summary>
        public ImageSource Icon => _icon ?? (_icon = Json.Image(_iconUrl));
        public string Initials { get; }
        public Brush Tint { get; }

        public bool IsFavorite { get => _isFavorite; set => Set(ref _isFavorite, value); }



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

            var people = new List<PersonVm>();
            foreach (var m in Json.Arr(j, "members")) people.Add(new PersonVm(m.GetObject()));
            People = people;
            var extra = (int)Json.Num(j, "member_count") - people.Count;
            MoreText = extra > 0 ? "+" + extra : "";
        }

        public string Id { get; }
        public string Name { get; }
        public string Glyph { get; }

        /// <summary>First few people in the channel (the bridge caps the preview), plus "+N" for the rest.</summary>
        public IReadOnlyList<PersonVm> People { get; }
        public string MoreText { get; }
        public bool HasPeople => People.Count > 0;

        public bool IsCurrent
        {
            get => _isCurrent;
            set { if (Set(ref _isCurrent, value)) Raise(nameof(IsJoinable)); }
        }

        public bool IsJoinable => !_isCurrent;
    }

    /// <summary>Someone shown in a channel preview: avatar if any, tinted initial otherwise.</summary>
    public sealed class PersonVm
    {
        readonly string _avatarUrl;
        ImageSource _avatar;

        public PersonVm(JsonObject j)
        {
            Id = Json.Str(j, "id");
            Name = Json.Str(j, "name") ?? "";
            _avatarUrl = Json.Str(j, "avatar_url");
            Tint = Json.Tint(Id);
            Initial = Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
        }

        public string Id { get; }
        public string Name { get; }
        public string Initial { get; }
        public Brush Tint { get; }

        /// <summary>Lazy and tiny: decoded only when the row is on screen, at 2x its 22px size.</summary>
        public ImageSource Avatar => _avatar ?? (_avatar = Json.Image(_avatarUrl, 44));
    }
}
