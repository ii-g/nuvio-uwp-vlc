using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace Nuvio.Views.Shared
{
    /// <summary>In-app toast (ports ui.js toast()): info/success/error, 3000 ms default.</summary>
    internal sealed partial class ToastControl : UserControl
    {
        private readonly DispatcherTimer _timer = new DispatcherTimer();

        public ToastControl()
        {
            InitializeComponent();
            _timer.Tick += OnTimerTick;
        }

        public void Show(string message, string kind = "info", int durationMs = 3000)
        {
            ToastText.Text = message ?? string.Empty;
            Color border;
            switch (kind)
            {
                case "success":
                    border = Color.FromArgb(0xFF, 0x4C, 0xAF, 0x7D);
                    break;
                case "error":
                    border = Color.FromArgb(0xFF, 0xFF, 0x5A, 0x5F);
                    break;
                default:
                    border = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
                    break;
            }
            ToastBorder.BorderBrush = new SolidColorBrush(border);
            _timer.Stop();
            ToastBorder.Visibility = Visibility.Visible;
            Fade(0, 1);
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, durationMs));
            _timer.Start();
        }

        private void OnTimerTick(object sender, object e)
        {
            _timer.Stop();
            Fade(1, 0);
            // Hide after the fade-out finishes.
            var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            hideTimer.Tick += (s, args) =>
            {
                hideTimer.Stop();
                ToastBorder.Visibility = Visibility.Collapsed;
            };
            hideTimer.Start();
        }

        private void Fade(double from, double to)
        {
            var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(250) };
            Storyboard.SetTarget(animation, ToastBorder);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
    }
}
