using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.Resources;
using Windows.Data.Json;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Allycord.Widget
{
    public sealed partial class MainPage : Page
    {
        readonly BridgeClient _bridge = BridgeClient.Instance;
        readonly ResourceLoader _res = ResourceLoader.GetForViewIndependentUse();
        readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        readonly Debouncer _inputDebounce, _outputDebounce;

        XboxGameBarWidget _widget;
        DemoMode _demo;
        string _status, _error, _userId, _userName, _avatarUrl, _currentChannelId, _currentGuildId, _statusAction, _clientId, _recentKey;
        bool _bridgeFailed, _applyingState, _loaded, _setupRequested, _mute, _deaf, _pinned, _guildsLoaded;
        bool _screenActive, _screenBlocked;
        long _joinedAt;
        int _tab;
        double _inputVolume = 100, _outputVolume = 100;

        public ObservableCollection<MemberVm> Members { get; } = new ObservableCollection<MemberVm>();
        /// <summary>Single virtualized list for the Channels tab: section captions, recents, servers or channels.</summary>
        public ObservableCollection<object> BrowseRows { get; } = new ObservableCollection<object>();

        readonly List<GuildVm> _guilds = new List<GuildVm>();
        readonly List<ChannelVm> _channels = new List<ChannelVm>();
        GuildVm _openGuild;
        Action _toastAction;

        public MainPage()
        {
            InitializeComponent();
            _clock.Tick += (s, e) => UpdateCallTimer();
            _toastTimer.Tick += (s, e) => HideToast();
            _inputDebounce = new Debouncer(() => SetVolume("set_input_volume", InputSlider.Value));
            _outputDebounce = new Debouncer(() => SetVolume("set_output_volume", OutputSlider.Value));
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            foreach (var (button, key) in new[] { (VideoButton, "Camera"), (ScreenButton, "ScreenShare") })
            {
                ToolTipService.SetToolTip(button, T(key));
                Windows.UI.Xaml.Automation.AutomationProperties.SetName(button, T(key));
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _widget = e.Parameter as XboxGameBarWidget;
            if (e.Parameter is string demo) _demo = DemoMode.Parse(demo);
        }

        bool Connected => _demo != null || _bridge.IsConnected;

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;
            SelectTab(0);

            if (_widget != null)
            {
                _widget.GameBarDisplayModeChanged += OnDisplayModeChanged;
                ApplyDisplayMode();
            }

            if (_demo != null)
            {
                Prefs.UseMemory(new[] { "300000000000000002" }, new[]
                {
                    new RecentVm("200000000000000002", "Ranked até cair", "300000000000000001", "Squad Ally"),
                    new RecentVm("200000000000000011", "Lobby", "300000000000000002", "Ally Brasil"),
                });
                _pinned = _demo.Pinned;
                ApplyState(_demo.State());
                if (_demo.Tab > 0) SelectTab(_demo.Tab);
                if (_demo.Has("open") && _guilds.Count > 0) await OpenGuildAsync(_guilds[0]);
                if (_demo.Has("search")) SearchBox.Text = "br";
                ApplyDisplayMode();
                FocusDock();
                return;
            }

            _bridge.StateReceived += OnStateReceived;
            _bridge.Disconnected += OnBridgeDisconnected;
            await StartBridgeAsync();
            FocusDock();
        }

        /// <summary>
        /// Start focus on Mute: it's what a controller user wants first, and it sits outside the scroller,
        /// so focusing it doesn't scroll the call card out of view the way focusing a member tile would.
        /// </summary>
        void FocusDock()
        {
            // Demo scenes are for screenshots, where a focus ring reads as a "selected" button.
            if (_demo != null || MainView.Visibility != Visibility.Visible) return;
            if (_tab == 0) MuteButton.Focus(FocusState.Programmatic);
            else if (_tab == 1) BrowseList.Focus(FocusState.Programmatic);
            else if (_tab == 2) InputSlider.Focus(FocusState.Programmatic);
        }

        void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            _bridge.StateReceived -= OnStateReceived;
            _bridge.Disconnected -= OnBridgeDisconnected;
            if (_widget != null) _widget.GameBarDisplayModeChanged -= OnDisplayModeChanged;
            _clock.Stop();
        }

        string T(string key)
        {
            var value = _res.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];

        // ------------------------------------------------------------------ Game Bar display mode

        void OnDisplayModeChanged(XboxGameBarWidget sender, object args) =>
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                _pinned = _widget.GameBarDisplayMode == XboxGameBarDisplayMode.PinnedOnly;
                ApplyDisplayMode();
            });

        /// <summary>Game Bar closed with the widget pinned: show only a glanceable pill over the game.</summary>
        void ApplyDisplayMode()
        {
            if (_widget != null && _demo == null)
                _pinned = _widget.GameBarDisplayMode == XboxGameBarDisplayMode.PinnedOnly;

            Shell.Visibility = _pinned ? Visibility.Collapsed : Visibility.Visible;
            if (_pinned) FindName(nameof(PinnedView));
            if (PinnedView != null) PinnedView.Visibility = _pinned ? Visibility.Visible : Visibility.Collapsed;
            // Nothing in the pill shows the timer; don't tick under a game for it.
            if (_pinned) _clock.Stop(); else if (_currentChannelId != null) _clock.Start();
            Root.Background = _pinned ? new SolidColorBrush(Windows.UI.Colors.Transparent) : Res("XcInk1");
            UpdatePinned();
        }

        void UpdatePinned()
        {
            if (PinnedView == null) return;
            var inCall = _currentChannelId != null;
            PinnedMembers.Visibility = inCall ? Visibility.Visible : Visibility.Collapsed;
            PinnedIdleText.Visibility = inCall ? Visibility.Collapsed : Visibility.Visible;
            PinnedMicBg.Fill = Res(_mute || _deaf ? "XcDanger" : "XcInk3");
            PinnedMicIcon.Glyph = _deaf ? "" : _mute ? "" : "";
        }

        // ------------------------------------------------------------------ bridge

        async Task StartBridgeAsync()
        {
            _bridgeFailed = false;
            Render();

            if (!await _bridge.EnsureStartedAsync())
            {
                _bridgeFailed = true;
                Render();
                return;
            }

            try
            {
                var state = await _bridge.CallAsync("get_state");
                ApplyState(state.GetObject());
            }
            catch (Exception ex)
            {
                ShowToast(ex.Message);
            }
        }

        void OnStateReceived(JsonObject state) =>
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ApplyState(state));

        void OnBridgeDisconnected() =>
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                _status = null;
                Render();
                if (!_loaded) return;
                await Task.Delay(1000);
                if (_loaded && !_bridge.IsConnected) await StartBridgeAsync();
            });

        async Task<IJsonValue> CallAsync(string method, JsonObject args = null)
        {
            if (_demo != null)
            {
                var result = _demo.Call(method, args);
                ApplyState(_demo.State());
                return result;
            }
            try
            {
                return await _bridge.CallAsync(method, args);
            }
            catch (Exception ex)
            {
                ShowToast(ex.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ state → UI

        void ApplyState(JsonObject state)
        {
            var wasSetup = _status == "needs_setup";
            _status = Json.Str(state, "status");
            _error = Json.Str(state, "error");
            _clientId = Json.Str(state, "client_id");
            if (_status == "needs_setup" && !wasSetup && ClientIdBox != null && string.IsNullOrEmpty(ClientIdBox.Text))
                ClientIdBox.Text = _clientId ?? "";

            var user = Json.Obj(state, "user");
            _userId = Json.Str(user, "id");
            _userName = Json.Str(user, "name");
            UserInitialText.Text = string.IsNullOrEmpty(_userName) ? "" : char.ToUpperInvariant(_userName[0]).ToString();
            UserTint.Fill = _userId != null ? Json.Tint(_userId) : Res("XcInk3");
            var avatarUrl = Json.Str(user, "avatar_url");
            if (avatarUrl != _avatarUrl)
            {
                _avatarUrl = avatarUrl;
                UserAvatarBrush.ImageSource = Json.Image(avatarUrl);
            }

            var settings = Json.Obj(state, "settings");
            _mute = Json.Bool(settings, "mute");
            _deaf = Json.Bool(settings, "deaf");
            // Discord reports mute=true while deafened; show the stronger state only.
            var micOff = _mute || _deaf;
            MuteButton.Background = Res(micOff ? "XcDangerSoft" : "XcInk2");
            MuteButton.BorderBrush = micOff ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0x5B, 0x61)) : Res("XcLine");
            MuteButton.Foreground = Res(micOff ? "XcDanger" : "XcText");
            MuteIcon.Glyph = micOff ? "" : "";
            MuteLabel.Text = T(micOff ? "MicOff" : "MicOn");
            DeafButton.Background = Res(_deaf ? "XcDangerSoft" : "XcInk2");
            DeafButton.BorderBrush = _deaf ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0x5B, 0x61)) : Res("XcLine");
            DeafButton.Foreground = Res(_deaf ? "XcDanger" : "XcText");
            DeafSlash.Visibility = _deaf ? Visibility.Visible : Visibility.Collapsed;
            DeafLabel.Text = T(_deaf ? "SoundOff" : "SoundOn");

            _inputVolume = Json.Num(settings, "input_volume", 100);
            _outputVolume = Json.Num(settings, "output_volume", 100);
            ApplyVolumes();

            var channel = Json.Obj(state, "channel");
            _currentChannelId = Json.Str(channel, "id");
            _currentGuildId = Json.Str(channel, "guild_id");
            var inCall = _currentChannelId != null;
            HeroCard.Visibility = inCall ? Visibility.Visible : Visibility.Collapsed;
            EmptyCard.Visibility = inCall ? Visibility.Collapsed : Visibility.Visible;
            MembersSection.Visibility = inCall ? Visibility.Visible : Visibility.Collapsed;
            LeaveButton.IsEnabled = inCall;
            ChannelNameText.Text = Json.Str(channel, "name") ?? "";
            GuildNameText.Text = (Json.Str(channel, "guild_name") ?? "").ToUpperInvariant();
            _joinedAt = (long)Json.Num(channel, "joined_at");
            if (inCall && !_pinned) _clock.Start(); else _clock.Stop();
            UpdateCallTimer();

            RecordRecent(Json.Str(channel, "name"), Json.Str(channel, "guild_name"));
            foreach (var row in BrowseRows)
            {
                if (row is ChannelVm c) c.IsCurrent = c.Id == _currentChannelId;
                else if (row is RecentVm r) r.IsCurrent = r.ChannelId == _currentChannelId;
            }

            ApplyMedia(Json.Obj(state, "media"), inCall);
            SyncMembers(Json.Arr(state, "members"));
            MembersCountChip.Text = Members.Count.ToString();
            MemberCountText.Text = Members.Count == 1 ? T("OnePerson") : string.Format(T("People"), Members.Count);

            UpdatePinned();
            Render();
        }

        void ApplyMedia(JsonObject media, bool inCall)
        {
            var videoActive = Json.Bool(media, "video_active");
            _screenActive = Json.Bool(media, "screen_active");
            if (_screenActive) _screenBlocked = false; // it worked after all (e.g. picked a screen in Discord)

            VideoButton.Visibility = inCall && Json.Bool(media, "video_supported") ? Visibility.Visible : Visibility.Collapsed;
            ScreenButton.Visibility = inCall && Json.Bool(media, "screen_supported") && !_screenBlocked ? Visibility.Visible : Visibility.Collapsed;
            StyleMediaButton(VideoButton, VideoIcon, videoActive);
            StyleMediaButton(ScreenButton, ScreenIcon, _screenActive);
        }

        static void StyleMediaButton(Button button, FontIcon icon, bool active)
        {
            button.Background = active ? Res("XcLiveSoft") : new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            icon.Foreground = Res(active ? "XcLive" : "XcText");
        }

        async void Video_Click(object sender, RoutedEventArgs e) => await CallAsync("toggle_video");

        /// <summary>
        /// Discord accepts the command even where Go Live is off (e.g. Brazil), and simply doesn't start.
        /// When nothing changes, say so and hide the button for this session instead of failing silently.
        /// </summary>
        async void Screen_Click(object sender, RoutedEventArgs e)
        {
            var wasActive = _screenActive;
            var result = await CallAsync("toggle_screenshare");
            if (result == null || result.ValueType != JsonValueType.Object) return;
            if (!Json.Bool(result.GetObject(), "changed") && !wasActive)
            {
                _screenBlocked = true;
                ScreenButton.Visibility = Visibility.Collapsed;
                ShowToast(T("ScreenUnavailable"), info: true);
            }
        }

        void ApplyVolumes()
        {
            if (AudioView == null) return;
            // Don't fight the user while a slider change is still waiting to be sent.
            _applyingState = true;
            if (!_inputDebounce.Pending) InputSlider.Value = _inputVolume;
            if (!_outputDebounce.Pending) OutputSlider.Value = _outputVolume;
            _applyingState = false;
            InputVolumeText.Text = $"{Math.Round(InputSlider.Value)}%";
            OutputVolumeText.Text = $"{Math.Round(OutputSlider.Value)}%";
        }

        /// <summary>Updates the member list in place so avatars and focus don't flicker on every speaking event.</summary>
        void SyncMembers(JsonArray members)
        {
            var index = 0;
            foreach (var value in members)
            {
                var json = value.GetObject();
                var id = Json.Str(json, "id");
                var existing = -1;
                for (var i = index; i < Members.Count; i++)
                    if (Members[i].Id == id) { existing = i; break; }

                if (existing < 0)
                {
                    var vm = new MemberVm(id);
                    vm.Update(json);
                    Members.Insert(index, vm);
                }
                else
                {
                    Members[existing].Update(json);
                    if (existing != index) Members.Move(existing, index);
                }
                index++;
            }
            while (Members.Count > index) Members.RemoveAt(Members.Count - 1);
        }

        void UpdateCallTimer()
        {
            if (_joinedAt <= 0)
            {
                CallTimerText.Text = "";
                return;
            }
            var elapsed = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(_joinedAt);
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            CallTimerText.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        }

        void Render()
        {
            var status = !Connected ? (_bridgeFailed ? "bridge_failed" : "starting") : (_status ?? "connecting");
            var ready = status == "ready";
            var setup = Connected && (status == "needs_setup" || _setupRequested);

            if (setup && SetupView == null)
            {
                FindName(nameof(SetupView));
                ClientIdBox.Text = _clientId ?? "";
            }
            if (SetupView != null) SetupView.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
            MainView.Visibility = ready && !setup ? Visibility.Visible : Visibility.Collapsed;
            StatusView.Visibility = ready || setup ? Visibility.Collapsed : Visibility.Visible;
            LogoutItem.IsEnabled = ready;
            DiscordAppItem.IsEnabled = Connected;

            StatusDot.Fill = Res(ready ? "XcLive" : status == "bridge_failed" ? "XcDanger" : status == "needs_auth" || setup ? "XcWarn" : "XcText3");
            StatusText.Text = ready ? _userName ?? T("Status_Online") : T("Status_" + (setup ? "needs_setup" : status));

            if (setup)
            {
                SetupCancel.Visibility = status == "needs_setup" ? Visibility.Collapsed : Visibility.Visible;
                if (status == "needs_setup" && _error == "invalid_client_id") ShowSetupError(T("SetupInvalidId"));
            }

            var busy = false;
            if (!ready && !setup)
            {
                switch (status)
                {
                    case "bridge_failed":
                        SetStatusView("", T("Status_bridge_failed"), T("Status_bridge_failed_Detail"), "retry", T("Retry"));
                        break;
                    case "no_discord":
                        SetStatusView("", T("Status_no_discord"), _error ?? T("Status_no_discord_Detail"), "open_discord", T("OpenDiscord"));
                        break;
                    case "needs_auth":
                        var detail = _error == null ? T("Status_needs_auth_Detail")
                            : _error == "not_tester" ? T("AuthNotTester")
                            : _error == "not_public_client" ? T("AuthNotPublicClient")
                            : T("AuthFailed") + " " + _error;
                        SetStatusView("", T("Status_needs_auth"), detail, "authorize", T("Connect"));
                        break;
                    case "authorizing":
                        SetStatusView("", T("Status_authorizing"), T("Status_authorizing_Detail"), null, null);
                        busy = true;
                        break;
                    default: // starting, connecting
                        SetStatusView("", T("Status_" + status), null, null, null);
                        busy = true;
                        break;
                }
            }

            // The only spinner in the app, and it runs just while waiting on Discord.
            BusyRing.IsActive = busy;
        }

        void SetStatusView(string glyph, string title, string detail, string action, string actionText)
        {
            StatusIcon.Glyph = glyph;
            StatusTitle.Text = title;
            StatusDetail.Text = detail ?? "";
            StatusDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            _statusAction = action;
            StatusAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
            StatusAction.Content = actionText;
        }

        /// <summary>Errors by default; info toasts can carry one action (e.g. undo leaving a call).</summary>
        void ShowToast(string message, bool info = false, string actionText = null, Action action = null)
        {
            ToastText.Text = message;
            ToastIcon.Glyph = info ? "\uE946" : "\uE783";
            ToastIcon.Foreground = Res(info ? "XcText2" : "XcDanger");
            _toastAction = action;
            ToastAction.Content = actionText;
            ToastAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
            Toast.IsHitTestVisible = action != null;
            Toast.Opacity = 1;
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(action != null ? 6 : 4);
            _toastTimer.Start();
        }

        void HideToast()
        {
            _toastTimer.Stop();
            Toast.Opacity = 0;
            Toast.IsHitTestVisible = false;
            _toastAction = null;
        }

        void ToastAction_Click(object sender, RoutedEventArgs e)
        {
            var action = _toastAction;
            HideToast();
            action?.Invoke();
        }

        // ------------------------------------------------------------------ tabs + gamepad

        void Tab_Click(object sender, RoutedEventArgs e) => SelectTab(int.Parse((string)((Button)sender).Tag));

        void SelectTab(int tab)
        {
            _tab = tab;
            var tabs = new[] { TabCall, TabChannels, TabAudio };
            for (var i = 0; i < tabs.Length; i++)
            {
                tabs[i].Background = i == tab ? Res("XcInk3") : new SolidColorBrush(Windows.UI.Colors.Transparent);
                tabs[i].Foreground = Res(i == tab ? "XcText" : "XcText3");
            }
            // Channels and Audio are built on first visit (x:Load): most sessions never open them.
            if (tab == 1) FindName(nameof(ChannelsView));
            if (tab == 2 && AudioView == null)
            {
                FindName(nameof(AudioView));
                ApplyVolumes();
            }
            CallView.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (ChannelsView != null) ChannelsView.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (AudioView != null) AudioView.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;

            if (tab != 1) return;
            if (!_guildsLoaded) _ = LoadGuildsAsync();
            else
            {
                RebuildBrowse();
                FocusBrowse();
            }
        }

        protected override async void OnKeyDown(KeyRoutedEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || MainView.Visibility != Visibility.Visible || FocusManager.GetFocusedElement() is TextBox) return;

            switch (e.Key)
            {
                case VirtualKey.GamepadY:
                    ShowGamepadHints();
                    e.Handled = true;
                    await ToggleMuteAsync();
                    break;
                case VirtualKey.GamepadX:
                    ShowGamepadHints();
                    e.Handled = true;
                    await ToggleDeafAsync();
                    break;
                case VirtualKey.GamepadLeftShoulder:
                    ShowGamepadHints();
                    e.Handled = true;
                    SelectTab((_tab + 2) % 3);
                    break;
                case VirtualKey.GamepadB:
                case VirtualKey.Escape:
                    if (_tab != 1 || _openGuild == null) break;
                    e.Handled = true;
                    CloseGuild();
                    break;
                case VirtualKey.GamepadRightShoulder:
                    ShowGamepadHints();
                    e.Handled = true;
                    SelectTab((_tab + 1) % 3);
                    break;
                default:
                    if (e.Key >= VirtualKey.GamepadA && e.Key <= VirtualKey.GamepadRightThumbstickLeft) ShowGamepadHints();
                    break;
            }
        }

        /// <summary>Button hints only matter on a controller; reveal them once one is used.</summary>
        void ShowGamepadHints()
        {
            HintY.Visibility = Visibility.Visible;
            HintX.Visibility = Visibility.Visible;
        }

        // ------------------------------------------------------------------ actions

        async void StatusAction_Click(object sender, RoutedEventArgs e)
        {
            switch (_statusAction)
            {
                case "retry": await StartBridgeAsync(); break;
                case "open_discord": await CallAsync("launch_discord"); break;
                case "authorize": await CallAsync("authorize"); break;
            }
        }

        async void OpenDiscord_Click(object sender, RoutedEventArgs e)
        {
            if (Connected) await CallAsync("launch_discord");
        }

        async void Logout_Click(object sender, RoutedEventArgs e) => await CallAsync("logout");

        async void Mute_Click(object sender, RoutedEventArgs e) => await ToggleMuteAsync();

        async void Deaf_Click(object sender, RoutedEventArgs e) => await ToggleDeafAsync();

        Task ToggleMuteAsync() =>
            // Unmuting while deafened must undeafen too, or the click looks like it did nothing.
            CallAsync(_deaf ? "toggle_deaf" : "toggle_mute");

        Task ToggleDeafAsync() => CallAsync("toggle_deaf");

        /// <summary>Leave is one tap (it sits next to Mute), so recover instead of confirming: the toast offers to rejoin.</summary>
        async void Leave_Click(object sender, RoutedEventArgs e)
        {
            var channelId = _currentChannelId;
            var channelName = ChannelNameText.Text;
            if (await CallAsync("leave") == null || channelId == null) return;
            ShowToast(string.Format(T("LeftChannel"), channelName), info: true, actionText: T("Undo"),
                action: () => _ = JoinAsync(channelId, switchToCall: false));
        }

        void GoChannels_Click(object sender, RoutedEventArgs e) => SelectTab(1);

        void InputSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (InputVolumeText == null) return; // fires during InitializeComponent
            InputVolumeText.Text = $"{Math.Round(e.NewValue)}%";
            if (!_applyingState) _inputDebounce.Trigger();
        }

        void OutputSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (OutputVolumeText == null) return;
            OutputVolumeText.Text = $"{Math.Round(e.NewValue)}%";
            if (!_applyingState) _outputDebounce.Trigger();
        }

        async void SetVolume(string method, double value) =>
            await CallAsync(method, new JsonObject { ["volume"] = JsonValue.CreateNumberValue(Math.Round(value)) });

        void MembersGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!(MembersGrid.ItemsPanelRoot is ItemsWrapGrid panel) || e.NewSize.Width < 1) return;
            var columns = e.NewSize.Width >= 470 ? 4 : 3;
            var width = Math.Floor(e.NewSize.Width / columns);
            if (Math.Abs(panel.ItemWidth - width) >= 1) panel.ItemWidth = width;
        }

        void Member_ItemClick(object sender, ItemClickEventArgs e)
        {
            var member = (MemberVm)e.ClickedItem;
            if (member.Id == _userId) return; // Discord doesn't allow per-user settings on yourself

            var value = new TextBlock
            {
                Text = member.VolumeText,
                FontFamily = (FontFamily)Application.Current.Resources["XcDisplay"],
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var slider = new Slider { Minimum = 0, Maximum = 200, StepFrequency = 1, SmallChange = 5, LargeChange = 10, Value = member.Volume };
            var send = new Debouncer(() => _ = CallAsync("set_user_volume", new JsonObject
            {
                ["user_id"] = JsonValue.CreateStringValue(member.Id),
                ["volume"] = JsonValue.CreateNumberValue(Math.Round(slider.Value)),
            }));
            slider.ValueChanged += (s, a) =>
            {
                value.Text = $"{Math.Round(a.NewValue)}%";
                send.Trigger();
            };

            var toggle = new ToggleSwitch { Header = T("MuteForMe"), IsOn = member.LocalMute };
            toggle.Toggled += async (s, a) => await CallAsync("mute_user", new JsonObject
            {
                ["user_id"] = JsonValue.CreateStringValue(member.Id),
                ["mute"] = JsonValue.CreateBooleanValue(toggle.IsOn),
            });

            var header = new Grid();
            header.Children.Add(new TextBlock
            {
                Text = member.Name,
                FontFamily = (FontFamily)Application.Current.Resources["XcDisplay"],
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 56, 0),
            });
            header.Children.Add(value);

            var caption = new TextBlock { Text = T("MemberVolume").ToUpperInvariant(), Style = (Style)Application.Current.Resources["XcCaption"], Margin = new Thickness(0, 8, 0, 0) };
            var panel = new StackPanel { Width = 248 };
            panel.Children.Add(header);
            panel.Children.Add(caption);
            panel.Children.Add(slider);
            panel.Children.Add(toggle);

            var flyout = new Flyout { Content = panel };
            flyout.Closed += (s, a) => send.Flush();
            flyout.ShowAt((FrameworkElement)MembersGrid.ContainerFromItem(member) ?? MembersGrid);
        }

        // ------------------------------------------------------------------ setup

        void DiscordApp_Click(object sender, RoutedEventArgs e)
        {
            _setupRequested = true;
            FindName(nameof(SetupView));
            ClientIdBox.Text = _clientId ?? "";
            SetupError.Visibility = Visibility.Collapsed;
            Render();
        }

        void SetupCancel_Click(object sender, RoutedEventArgs e)
        {
            _setupRequested = false;
            Render();
        }

        async void OpenPortal_Click(object sender, RoutedEventArgs e) =>
            await Launcher.LaunchUriAsync(new Uri("https://discord.com/developers/applications"));

        async void CopyRedirect_Click(object sender, RoutedEventArgs e)
        {
            var data = new DataPackage();
            data.SetText("http://127.0.0.1");
            Clipboard.SetContent(data);
            CopyIcon.Glyph = "";
            CopyIcon.Foreground = Res("XcLive");
            await Task.Delay(1500);
            CopyIcon.Glyph = "";
            CopyIcon.Foreground = Res("XcText2");
        }

        async void SetupSave_Click(object sender, RoutedEventArgs e)
        {
            var id = ClientIdBox.Text.Trim();
            if (id.Length < 17 || id.Length > 20 || !ulong.TryParse(id, out _))
            {
                ShowSetupError(T("SetupInvalidId"));
                return;
            }

            SetupError.Visibility = Visibility.Collapsed;
            try
            {
                if (_demo == null)
                    await _bridge.CallAsync("set_client_id", new JsonObject { ["client_id"] = JsonValue.CreateStringValue(id) });
                _setupRequested = false;
                Render();
            }
            catch (Exception ex)
            {
                ShowSetupError(ex.Message == "invalid_client_id" ? T("SetupInvalidId") : ex.Message);
            }
        }

        void ShowSetupError(string message)
        {
            SetupError.Text = message;
            SetupError.Visibility = Visibility.Visible;
        }

        // ------------------------------------------------------------------ channels

        async Task LoadGuildsAsync()
        {
            SetBrowseBusy(true);
            try
            {
                var result = await LoadAsync("get_guilds", null);
                _guilds.Clear();
                foreach (var g in result.GetArray())
                {
                    var vm = new GuildVm(g.GetObject());
                    vm.IsFavorite = Prefs.IsFavorite(vm.Id);
                    _guilds.Add(vm);
                }
                _guildsLoaded = true;
            }
            catch (Exception ex)
            {
                ShowBrowseError(ex.Message);
            }
            finally
            {
                SetBrowseBusy(false);
            }
            if (_guildsLoaded)
            {
                RebuildBrowse();
                FocusBrowse();
            }
        }

        /// <summary>Puts a controller user on the first row once rows exist; never steals focus from the search box.</summary>
        void FocusBrowse()
        {
            if (_tab != 1 || BrowseRows.Count == 0 || FocusManager.GetFocusedElement() is TextBox) return;
            BrowseList.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// Servers view: Recents (one tap rejoins), Favorites, then all servers, each with its name.
        /// Typing filters servers; inside a server it filters that server's channels.
        /// </summary>
        void RebuildBrowse()
        {
            var query = SearchBox.Text.Trim();
            BrowseRows.Clear();
            BrowseError.Visibility = Visibility.Collapsed;
            SearchBox.PlaceholderText = _openGuild == null ? T("SearchServers") : T("SearchChannels");

            if (_openGuild != null)
            {
                foreach (var c in _channels)
                {
                    if (!Text.Matches(c.Name, query)) continue;
                    c.IsCurrent = c.Id == _currentChannelId;
                    BrowseRows.Add(c);
                }
            }
            else if (query.Length > 0)
            {
                foreach (var g in _guilds)
                    if (Text.Matches(g.Name, query)) BrowseRows.Add(g);
            }
            else
            {
                var recents = Prefs.Recents;
                if (recents.Count > 0)
                {
                    BrowseRows.Add(new SectionRow(T("RecentsCaption")));
                    foreach (var r in recents)
                    {
                        r.IsCurrent = r.ChannelId == _currentChannelId;
                        BrowseRows.Add(r);
                    }
                }

                var favorites = _guilds.Where(g => g.IsFavorite).ToList();
                if (favorites.Count > 0)
                {
                    BrowseRows.Add(new SectionRow(T("FavoritesCaption")));
                    foreach (var g in favorites) BrowseRows.Add(g);
                }

                if (_guilds.Count > favorites.Count)
                {
                    BrowseRows.Add(new SectionRow(T("ServersCaption")));
                    foreach (var g in _guilds)
                        if (!g.IsFavorite) BrowseRows.Add(g);
                }
            }

            if (BrowseRows.Count == 0 && !BrowseProgress.IsActive)
                ShowBrowseError(query.Length > 0 ? string.Format(T("NothingFound"), query)
                    : _openGuild != null ? T("NoVoiceChannels") : T("NoServers"));
        }

        void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_guildsLoaded) RebuildBrowse();
        }

        /// <summary>Captions are part of the list for virtualization, but must not take focus or clicks.</summary>
        void BrowseList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            var isSection = args.Item is SectionRow;
            args.ItemContainer.IsTabStop = !isSection;
            args.ItemContainer.IsHitTestVisible = !isSection;
        }

        async void Browse_ItemClick(object sender, ItemClickEventArgs e)
        {
            switch (e.ClickedItem)
            {
                case GuildVm guild:
                    await OpenGuildAsync(guild);
                    break;
                case RecentVm recent:
                    await JoinAsync(recent.ChannelId);
                    break;
                case ChannelVm channel:
                    await JoinAsync(channel.Id);
                    break;
            }
        }

        async Task OpenGuildAsync(GuildVm guild)
        {
            _openGuild = guild;
            _channels.Clear();
            GuildHeader.Visibility = Visibility.Visible;
            OpenGuildText.Text = guild.Name;
            UpdateFavoriteButton();
            SearchBox.Text = "";
            RebuildBrowse();

            SetBrowseBusy(true);
            try
            {
                var result = await LoadAsync("get_channels", new JsonObject { ["guild_id"] = JsonValue.CreateStringValue(guild.Id) });
                if (_openGuild != guild) return; // user moved on
                foreach (var c in result.GetArray()) _channels.Add(new ChannelVm(c.GetObject()));
            }
            catch (Exception ex)
            {
                ShowBrowseError(ex.Message);
            }
            finally
            {
                SetBrowseBusy(false);
            }
            if (_openGuild != guild) return;
            RebuildBrowse();
            FocusBrowse();
        }

        void CloseGuild()
        {
            _openGuild = null;
            GuildHeader.Visibility = Visibility.Collapsed;
            SearchBox.Text = "";
            RebuildBrowse();
            FocusBrowse();
        }

        void BackToServers_Click(object sender, RoutedEventArgs e) => CloseGuild();

        void Favorite_Click(object sender, RoutedEventArgs e)
        {
            if (_openGuild == null) return;
            Prefs.ToggleFavorite(_openGuild.Id);
            _openGuild.IsFavorite = Prefs.IsFavorite(_openGuild.Id);
            UpdateFavoriteButton();
        }

        void UpdateFavoriteButton()
        {
            var favorite = _openGuild?.IsFavorite == true;
            FavoriteIcon.Glyph = favorite ? "\uE735" : "\uE734";
            FavoriteIcon.Foreground = favorite ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF5, 0xC4, 0x51)) : Res("XcText2");
            FavoriteText.Text = T(favorite ? "Favorited" : "Favorite");
        }

        /// <summary>Remembers where the user lands, however they got there (Allycord, Discord itself, a friend's move).</summary>
        void RecordRecent(string channelName, string guildName)
        {
            if (_currentChannelId == null || string.IsNullOrEmpty(channelName)) return;
            var key = _currentChannelId + "|" + guildName;
            if (key == _recentKey) return;
            _recentKey = key;
            Prefs.PushRecent(_currentChannelId, channelName, _currentGuildId, guildName);
        }

        async Task JoinAsync(string channelId, bool switchToCall = true)
        {
            if (channelId != _currentChannelId)
            {
                SetBrowseBusy(true);
                var result = await CallAsync("join", new JsonObject { ["channel_id"] = JsonValue.CreateStringValue(channelId) });
                SetBrowseBusy(false);
                if (result == null) return;
            }
            if (switchToCall) SelectTab(0);
        }

        Task<IJsonValue> LoadAsync(string method, JsonObject args) =>
            _demo != null ? Task.FromResult(_demo.Call(method, args)) : _bridge.CallAsync(method, args);

        void SetBrowseBusy(bool busy)
        {
            BrowseProgress.IsActive = busy;
            BrowseProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy) BrowseError.Visibility = Visibility.Collapsed;
        }

        void ShowBrowseError(string message)
        {
            BrowseError.Text = message;
            BrowseError.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Runs the action once input has been quiet for a moment (slider drags).</summary>
    sealed class Debouncer
    {
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        readonly Action _action;

        public Debouncer(Action action)
        {
            _action = action;
            _timer.Tick += (s, e) => { _timer.Stop(); _action(); };
        }

        public bool Pending => _timer.IsEnabled;

        public void Trigger()
        {
            _timer.Stop();
            _timer.Start();
        }

        public void Flush()
        {
            if (!_timer.IsEnabled) return;
            _timer.Stop();
            _action();
        }
    }
}
