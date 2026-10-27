using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Windows.Data.Json;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Allycord.Widget
{
    /// <summary>Caption row inside the browse list ("RECENTES", "FAVORITOS"...). Not focusable.</summary>
    public sealed class SectionRow
    {
        public SectionRow(string title) { Title = title; }
        public string Title { get; }
    }

    /// <summary>A channel the user was in recently: one tap rejoins, no server drill-down needed.</summary>
    public sealed class RecentVm : Observable
    {
        bool _isCurrent;

        public RecentVm(string channelId, string channelName, string guildId, string guildName)
        {
            ChannelId = channelId;
            ChannelName = channelName;
            GuildId = guildId;
            GuildName = guildName ?? "";
            Tint = Json.Tint(guildId ?? channelId);
            Initial = string.IsNullOrEmpty(GuildName) ? "#" : char.ToUpperInvariant(GuildName[0]).ToString();
        }

        public string ChannelId { get; }
        public string ChannelName { get; }
        public string GuildId { get; }
        public string GuildName { get; }
        public Brush Tint { get; }
        public string Initial { get; }

        public bool IsCurrent
        {
            get => _isCurrent;
            set { if (Set(ref _isCurrent, value)) Raise(nameof(IsJoinable)); }
        }

        public bool IsJoinable => !_isCurrent;
    }

    public sealed class BrowseTemplateSelector : DataTemplateSelector
    {
        public DataTemplate Section { get; set; }
        public DataTemplate Guild { get; set; }
        public DataTemplate Recent { get; set; }
        public DataTemplate Channel { get; set; }

        protected override DataTemplate SelectTemplateCore(object item) =>
            item is SectionRow ? Section : item is GuildVm ? Guild : item is RecentVm ? Recent : Channel;

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
    }

    /// <summary>Favorites and recents, kept in LocalSettings (tiny, per user, survives updates).</summary>
    static class Prefs
    {
        const int MaxRecents = 4;
        static HashSet<string> _favorites;
        static List<RecentVm> _recents;
        static bool _memoryOnly;

        /// <summary>Demo mode: never touch the user's real settings.</summary>
        public static void UseMemory(IEnumerable<string> favorites, IEnumerable<RecentVm> recents)
        {
            _memoryOnly = true;
            _favorites = new HashSet<string>(favorites);
            _recents = recents.ToList();
        }

        static ApplicationDataContainer Store => ApplicationData.Current.LocalSettings;

        public static bool IsFavorite(string guildId) => Favorites.Contains(guildId);

        public static void ToggleFavorite(string guildId)
        {
            if (!Favorites.Remove(guildId)) Favorites.Add(guildId);
            if (!_memoryOnly) Store.Values["favorite_guilds"] = string.Join(",", Favorites);
        }

        static HashSet<string> Favorites
        {
            get
            {
                if (_favorites != null) return _favorites;
                var raw = Store.Values["favorite_guilds"] as string ?? "";
                return _favorites = new HashSet<string>(raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            }
        }

        public static IReadOnlyList<RecentVm> Recents
        {
            get
            {
                if (_recents != null) return _recents;
                _recents = new List<RecentVm>();
                if (Store.Values["recent_channels"] is string raw && JsonArray.TryParse(raw, out var array))
                    foreach (var v in array)
                    {
                        var o = v.GetObject();
                        _recents.Add(new RecentVm(Json.Str(o, "id"), Json.Str(o, "name"), Json.Str(o, "guild_id"), Json.Str(o, "guild_name")));
                    }
                return _recents;
            }
        }

        /// <summary>Moves the channel to the front. Called whenever the user lands in a channel, wherever they joined from.</summary>
        public static void PushRecent(string channelId, string channelName, string guildId, string guildName)
        {
            var list = (List<RecentVm>)Recents;
            list.RemoveAll(r => r.ChannelId == channelId);
            list.Insert(0, new RecentVm(channelId, channelName, guildId, guildName));
            if (list.Count > MaxRecents) list.RemoveRange(MaxRecents, list.Count - MaxRecents);
            if (_memoryOnly) return;

            var array = new JsonArray();
            foreach (var r in list)
                array.Add(new JsonObject
                {
                    ["id"] = JsonValue.CreateStringValue(r.ChannelId),
                    ["name"] = JsonValue.CreateStringValue(r.ChannelName ?? ""),
                    ["guild_id"] = r.GuildId == null ? JsonValue.CreateNullValue() : JsonValue.CreateStringValue(r.GuildId),
                    ["guild_name"] = JsonValue.CreateStringValue(r.GuildName),
                });
            Store.Values["recent_channels"] = array.Stringify();
        }
    }

    static class Text
    {
        /// <summary>Case- and accent-insensitive "contains", so "pagode" finds "Pagodão".</summary>
        public static bool Matches(string text, string query) =>
            string.IsNullOrEmpty(query)
            || CultureInfo.InvariantCulture.CompareInfo.IndexOf(text ?? "", query,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }
}
