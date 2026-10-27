using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Xcord.Widget
{
    sealed partial class App : Application
    {
        XboxGameBarWidget _widget;

        public App()
        {
            InitializeComponent();
        }

        /// <summary>Game Bar opens the widget through the ms-gamebarwidget protocol.</summary>
        protected override void OnActivated(IActivatedEventArgs args)
        {
            if (args.Kind != ActivationKind.Protocol) return;
            if (!(args is XboxGameBarWidgetActivatedEventArgs widgetArgs) || !widgetArgs.IsLaunchActivation) return;

            var frame = new Frame();
            Window.Current.Content = frame;
            _widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, frame);
            frame.Navigate(typeof(MainPage), _widget);

            Window.Current.Closed += OnWidgetWindowClosed;
            Window.Current.Activate();
        }

        void OnWidgetWindowClosed(object sender, Windows.UI.Core.CoreWindowEventArgs e)
        {
            _widget = null;
            Window.Current.Closed -= OnWidgetWindowClosed;
        }

        /// <summary>Launched from Start: same page in a normal window (handy for testing without Game Bar).</summary>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            if (!(Window.Current.Content is Frame frame))
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }
            if (frame.Content == null)
                frame.Navigate(typeof(MainPage), null);
            Window.Current.Activate();
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
