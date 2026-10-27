using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel.Resources;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Xcord.Widget
{
    public sealed partial class MainPage : Page
    {
        readonly BridgeClient _bridge = BridgeClient.Instance;
        readonly ResourceLoader _res = ResourceLoader.GetForViewIndependentUse();
        readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer _errorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        readonly Debouncer _inputDebounce, _outputDebounce;

        XboxGameBarWidget _widget;
        string _status, _error, _userId, _avatarUrl, _currentChannelId, _statusAction, _transientError, _clientId;
        bool _bridgeFailed, _applyingState, _loaded, _setupRequested;
        long _joinedAt;

        public ObservableCollection<MemberVm> Members { get; } = new ObservableCollection<MemberVm>();
        public ObservableCollection<GuildVm> Guilds { get; } = new ObservableCollection<GuildVm>();
        public ObservableCollection<ChannelVm> Channels { get; } = new ObservableCollection<ChannelVm>();

        public MainPage()
        {
            InitializeComponent();
            _clock.Tick += (s, e) => UpdateCallTimer();
            _errorTimer.Tick += (s, e) => { _errorTimer.Stop(); _transientError = null; Render(); };
            _inputDebounce = new Debouncer(() => SetVolume("set_input_volume", InputSlider.Value));
            _outputDebounce = new Debouncer(() => SetVolume("set_output_volume", OutputSlider.Value));
            MainPivot.SelectionChanged += MainPivot_SelectionChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _widget = e.Parameter as XboxGameBarWidget;
        }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;
            _bridge.StateReceived += OnStateReceived;
            _bridge.Disconnected += OnBridgeDisconnected;
            await StartBridgeAsync();
        }

        void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            _bridge.StateReceived -= OnStateReceived;
            _bridge.Disconnected -= OnBridgeDisconnected;
            _clock.Stop();
        }

        string T(string key)
        {
            var value = _res.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
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
                ShowError(ex.Message);
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
            try
            {
                return await _bridge.CallAsync(method, args);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                return null;
            }
        }

        void ShowError(string message)
        {
            _transientError = message;
            _errorTimer.Stop();
            _errorTimer.Start();
            Render();
        }

        // ------------------------------------------------------------------ state → UI

        void ApplyState(JsonObject state)
        {
            var wasSetup = _status == "needs_setup";
            _status = Json.Str(state, "status");
            _error = Json.Str(state, "error");
            _clientId = Json.Str(state, "client_id");
            if (_status == "needs_setup" && !wasSetup && string.IsNullOrEmpty(ClientIdBox.Text))
                ClientIdBox.Text = _clientId ?? "";

            var user = Json.Obj(state, "user");
            _userId = Json.Str(user, "id");
            UserNameText.Text = Json.Str(user, "name") ?? "Xcord";
            var avatarUrl = Json.Str(user, "avatar_url");
            if (avatarUrl != _avatarUrl)
            {
                _avatarUrl = avatarUrl;
                UserAvatarBrush.ImageSource = Json.Image(avatarUrl);
            }

            var settings = Json.Obj(state, "settings");
            var mute = Json.Bool(settings, "mute");
            var deaf = Json.Bool(settings, "deaf");
            MuteButton.Background = (Brush)Application.Current.Resources[mute ? "XcRed" : "XcSurface"];
            DeafButton.Background = (Brush)Application.Current.Resources[deaf ? "XcRed" : "XcSurface"];
            MuteLabel.Text = T(mute ? "Unmute" : "Mute");
            DeafLabel.Text = T(deaf ? "Undeafen" : "Deafen");

            // Don't fight the user while a slider change is still waiting to be sent.
            _applyingState = true;
            if (!_inputDebounce.Pending) InputSlider.Value = Json.Num(settings, "input_volume", 100);
            if (!_outputDebounce.Pending) OutputSlider.Value = Json.Num(settings, "output_volume", 100);
            _applyingState = false;
            InputVolumeText.Text = $"{Math.Round(InputSlider.Value)}%";
            OutputVolumeText.Text = $"{Math.Round(OutputSlider.Value)}%";

            var channel = Json.Obj(state, "channel");
            _currentChannelId = Json.Str(channel, "id");
            var inChannel = _currentChannelId != null;
            ChannelCard.Visibility = inChannel ? Visibility.Visible : Visibility.Collapsed;
            NoChannelCard.Visibility = inChannel ? Visibility.Collapsed : Visibility.Visible;
            LeaveButton.IsEnabled = inChannel;
            ChannelNameText.Text = Json.Str(channel, "name") ?? "";
            GuildNameText.Text = Json.Str(channel, "guild_name") ?? "";
            _joinedAt = (long)Json.Num(channel, "joined_at");
            if (inChannel) _clock.Start(); else _clock.Stop();
            UpdateCallTimer();

            foreach (var c in Channels) c.IsCurrent = c.Id == _currentChannelId;

            SyncMembers(Json.Arr(state, "members"));
            MembersHeader.Text = string.Format(T("MembersHeader"), Members.Count);
            MembersHeader.Visibility = inChannel ? Visibility.Visible : Visibility.Collapsed;

            Render();
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
            var status = !_bridge.IsConnected ? (_bridgeFailed ? "bridge_failed" : "starting") : (_status ?? "connecting");
            var ready = status == "ready";
            var setup = _bridge.IsConnected && (status == "needs_setup" || _setupRequested);

            SetupPanel.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
            MainPivot.Visibility = ready && !setup ? Visibility.Visible : Visibility.Collapsed;
            StatusPanel.Visibility = ready || setup ? Visibility.Collapsed : Visibility.Visible;
            LogoutItem.IsEnabled = ready;
            DiscordAppItem.IsEnabled = _bridge.IsConnected;

            if (setup)
            {
                SetupCancel.Visibility = status == "needs_setup" ? Visibility.Collapsed : Visibility.Visible;
                if (status == "needs_setup" && _error == "invalid_client_id") ShowSetupError(T("SetupInvalidId"));
            }

            if (_transientError != null)
            {
                StatusDot.Fill = (Brush)Application.Current.Resources["XcRed"];
                StatusText.Text = _transientError;
            }
            else
            {
                StatusDot.Fill = (Brush)Application.Current.Resources[ready ? "XcGreen" : status == "needs_auth" ? "XcAccent" : "XcMuted"];
                StatusText.Text = ready ? T("Status_Online") : T("Status_" + status);
            }

            if (ready || setup) return;

            switch (status)
            {
                case "bridge_failed":
                    SetStatusPanel("", T("Status_bridge_failed"), T("Status_bridge_failed_Detail"), "retry", T("Retry"));
                    break;
                case "no_discord":
                    SetStatusPanel("", T("Status_no_discord"), _error ?? T("Status_no_discord_Detail"), "open_discord", T("OpenDiscord"));
                    break;
                case "needs_auth":
                    var detail = _error == null ? T("Status_needs_auth_Detail")
                        : _error == "not_tester" ? T("AuthNotTester")
                        : _error == "not_public_client" ? T("AuthNotPublicClient")
                        : T("AuthFailed") + " " + _error;
                    SetStatusPanel("", T("Status_needs_auth"), detail, "authorize", T("Connect"));
                    break;
                case "authorizing":
                    SetStatusPanel(null, T("Status_authorizing"), T("Status_authorizing_Detail"), null, null);
                    break;
                default: // starting, connecting
                    SetStatusPanel(null, T("Status_" + status), null, null, null);
                    break;
            }
        }

        void SetStatusPanel(string glyph, string title, string detail, string action, string actionText)
        {
            StatusIcon.Visibility = glyph != null ? Visibility.Visible : Visibility.Collapsed;
            if (glyph != null) StatusIcon.Glyph = glyph;
            StatusProgress.IsActive = glyph == null;
            StatusProgress.Visibility = glyph == null ? Visibility.Visible : Visibility.Collapsed;
            StatusTitle.Text = title;
            StatusDetail.Text = detail ?? "";
            StatusDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;

            _statusAction = action;
            StatusAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
            StatusAction.Content = actionText;
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
            if (_bridge.IsConnected) await CallAsync("launch_discord");
        }

        async void Logout_Click(object sender, RoutedEventArgs e) => await CallAsync("logout");

        void DiscordApp_Click(object sender, RoutedEventArgs e)
        {
            _setupRequested = true;
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
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://discord.com/developers/applications"));

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

        async void Mute_Click(object sender, RoutedEventArgs e) => await CallAsync("toggle_mute");

        async void Deaf_Click(object sender, RoutedEventArgs e) => await CallAsync("toggle_deaf");

        async void Leave_Click(object sender, RoutedEventArgs e) => await CallAsync("leave");

        void PickChannel_Click(object sender, RoutedEventArgs e) => MainPivot.SelectedIndex = 1;

        void InputSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (InputVolumeText == null) return; // fires during InitializeComponent
            InputVolumeText.Text = $"{Math.Round(e.NewValue)}%";
            if (!_applyingState) _inputDebounce.Trigger();
        }

        void OutputSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (OutputVolumeText == null) return;
            OutputVolumeText.Text = $"{Math.Round(e.NewValue)}%";
            if (!_applyingState) _outputDebounce.Trigger();
        }

        async void SetVolume(string method, double value) =>
            await CallAsync(method, new JsonObject { ["volume"] = JsonValue.CreateNumberValue(Math.Round(value)) });

        void Member_ItemClick(object sender, ItemClickEventArgs e)
        {
            var member = (MemberVm)e.ClickedItem;
            if (member.Id == _userId) return; // Discord doesn't allow per-user settings on yourself

            var label = new TextBlock { Text = $"{T("MemberVolume")} {member.VolumeText}", Style = (Style)Application.Current.Resources["XcCaption"] };
            var slider = new Slider { Minimum = 0, Maximum = 200, StepFrequency = 1, SmallChange = 5, LargeChange = 10, Value = member.Volume, Width = 240 };
            var send = new Debouncer(() => _ = CallAsync("set_user_volume", new JsonObject
            {
                ["user_id"] = JsonValue.CreateStringValue(member.Id),
                ["volume"] = JsonValue.CreateNumberValue(Math.Round(slider.Value)),
            }));
            slider.ValueChanged += (s, a) =>
            {
                label.Text = $"{T("MemberVolume")} {Math.Round(a.NewValue)}%";
                send.Trigger();
            };

            var toggle = new ToggleSwitch { Header = T("MuteForMe"), IsOn = member.LocalMute };
            toggle.Toggled += async (s, a) => await CallAsync("mute_user", new JsonObject
            {
                ["user_id"] = JsonValue.CreateStringValue(member.Id),
                ["mute"] = JsonValue.CreateBooleanValue(toggle.IsOn),
            });

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = member.Name, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(label);
            panel.Children.Add(slider);
            panel.Children.Add(toggle);

            var flyout = new Flyout { Content = panel };
            flyout.Closed += (s, a) => send.Flush();
            flyout.ShowAt((FrameworkElement)MembersList.ContainerFromItem(member) ?? MembersList);
        }

        // ------------------------------------------------------------------ server / channel browser

        async void MainPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MainPivot.SelectedIndex == 1 && Guilds.Count == 0 && ChannelList.Visibility != Visibility.Visible)
                await LoadGuildsAsync();
        }

        async Task LoadGuildsAsync()
        {
            SetBrowseBusy(true);
            try
            {
                var result = await _bridge.CallAsync("get_guilds");
                Guilds.Clear();
                foreach (var g in result.GetArray()) Guilds.Add(new GuildVm(g.GetObject()));
            }
            catch (Exception ex)
            {
                ShowBrowseError(ex.Message);
            }
            finally
            {
                SetBrowseBusy(false);
            }
        }

        async void Guild_ItemClick(object sender, ItemClickEventArgs e)
        {
            var guild = (GuildVm)e.ClickedItem;
            GuildList.Visibility = Visibility.Collapsed;
            ChannelsHeader.Visibility = Visibility.Visible;
            ChannelList.Visibility = Visibility.Visible;
            SelectedGuildText.Text = guild.Name;
            Channels.Clear();

            SetBrowseBusy(true);
            try
            {
                var result = await _bridge.CallAsync("get_channels", new JsonObject { ["guild_id"] = JsonValue.CreateStringValue(guild.Id) });
                foreach (var c in result.GetArray())
                {
                    var vm = new ChannelVm(c.GetObject());
                    vm.IsCurrent = vm.Id == _currentChannelId;
                    Channels.Add(vm);
                }
                if (Channels.Count == 0) ShowBrowseError(T("NoVoiceChannels"));
            }
            catch (Exception ex)
            {
                ShowBrowseError(ex.Message);
            }
            finally
            {
                SetBrowseBusy(false);
            }
        }

        void BackToGuilds_Click(object sender, RoutedEventArgs e)
        {
            ChannelList.Visibility = Visibility.Collapsed;
            ChannelsHeader.Visibility = Visibility.Collapsed;
            BrowseError.Visibility = Visibility.Collapsed;
            GuildList.Visibility = Visibility.Visible;
            if (Guilds.Count == 0) _ = LoadGuildsAsync();
        }

        async void Channel_ItemClick(object sender, ItemClickEventArgs e)
        {
            var channel = (ChannelVm)e.ClickedItem;
            if (channel.Id == _currentChannelId)
            {
                MainPivot.SelectedIndex = 0;
                return;
            }

            SetBrowseBusy(true);
            var result = await CallAsync("join", new JsonObject { ["channel_id"] = JsonValue.CreateStringValue(channel.Id) });
            SetBrowseBusy(false);
            if (result != null) MainPivot.SelectedIndex = 0;
        }

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
