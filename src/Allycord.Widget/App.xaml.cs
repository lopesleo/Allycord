using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Allycord.Widget
{
    sealed partial class App : Application
    {
        // Same footprint as the Game Bar widget's default size, so the windowed app previews it faithfully.
        static readonly Size WidgetSize = new Size(380, 640);

        XboxGameBarWidget _widget;

        public App()
        {
            InitializeComponent();
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            if (!(args is IProtocolActivatedEventArgs protocol)) return;

            // Game Bar opens the widget through the ms-gamebarwidget protocol. Check the scheme before
            // casting: probing other protocol args for the Game Bar type crashes the runtime.
            if (protocol.Uri.Scheme == "ms-gamebarwidget")
            {
                var widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
                if (widgetArgs == null || !widgetArgs.IsLaunchActivation) return;
                var frame = new Frame();
                Window.Current.Content = frame;
                _widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, frame);
                frame.Navigate(typeof(MainPage), _widget);
                Window.Current.Closed += OnWidgetWindowClosed;
                Window.Current.Activate();
                return;
            }

            // allycord://demo/<scene>: fake data for design review and screenshots.
            if (protocol.Uri.Scheme == "allycord" && protocol.Uri.Host == "demo")
                ShowWindowed("demo:" + protocol.Uri.AbsolutePath.Trim('/'));
        }

        void OnWidgetWindowClosed(object sender, Windows.UI.Core.CoreWindowEventArgs e)
        {
            _widget = null;
            Window.Current.Closed -= OnWidgetWindowClosed;
        }

        /// <summary>Launched from Start: same page in a normal window (handy for testing without Game Bar).</summary>
        protected override void OnLaunched(LaunchActivatedEventArgs e) => ShowWindowed(DemoSceneFromFile());

        /// <summary>LocalState\demo.txt containing a scene name opens that demo scene (screenshot tooling).</summary>
        static string DemoSceneFromFile()
        {
            try
            {
                var path = System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "demo.txt");
                return System.IO.File.Exists(path) ? "demo:" + System.IO.File.ReadAllText(path).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        void ShowWindowed(object parameter)
        {
            ApplicationView.PreferredLaunchViewSize = WidgetSize;
            ApplicationView.PreferredLaunchWindowingMode = ApplicationViewWindowingMode.PreferredLaunchViewSize;

            var frame = new Frame();
            Window.Current.Content = frame;
            frame.Navigate(typeof(MainPage), parameter);
            Window.Current.Activate();

            var view = ApplicationView.GetForCurrentView();
            view.SetPreferredMinSize(new Size(300, 260));
            view.TryResizeView(WidgetSize);
        }

        /// <summary>The bridge connecting to our in-process AppService.</summary>
        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);
            if (args.TaskInstance.TriggerDetails is AppServiceTriggerDetails details
                && details.CallerPackageFamilyName == Package.Current.Id.FamilyName)
            {
                BridgeClient.Instance.Attach(details.AppServiceConnection, args.TaskInstance);
            }
        }
    }
}
