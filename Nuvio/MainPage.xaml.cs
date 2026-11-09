using System;
using System.Threading.Tasks;
using LibVLCSharp.Platforms.Windows;
using Nuvio.Bridge;
using Nuvio.Diagnostics;
using Nuvio.Input;
using Nuvio.Playback;
using Nuvio.Services;
using Nuvio.Views;
using Windows.Globalization;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Nuvio
{
    /// <summary>
    /// Native app shell (Milestone 1): a collapsing sidebar plus a Frame that hosts the native pages
    /// (Home first; the rest land in later milestones). The media surfaces stay here — a
    /// MediaPlayerElement driven by FFmpegInteropX with a LibVLC VideoView as fallback — and the
    /// player is wired up in Milestone 4. The WebView2 control is kept collapsed and never
    /// navigated; the web app is no longer served (removed in M8 together with AppBridge).
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public static MainPage Current { get; private set; }

        private readonly HybridMediaPlayer _player;
        /// <summary>Kept for the M8 legacy import; never constructed in the native UI.</summary>
        private AppBridge _bridge;
        private ControllerBridge _controller;
        private NativeInputRouter _router;
        private bool _videoViewRequested;
        private bool _videoViewStarting;
        private bool _dialogOpen;
        private bool _sidebarExpanded;
        private Storyboard _sidebarStoryboard;

        internal AppSettings Settings { get; private set; }
        internal LocalStore Store { get; private set; }
        internal StremioService Stremio { get; private set; }
        internal LibraryService Library { get; private set; }
        internal ProgressService Progress { get; private set; }
        internal NavigationService Navigation { get; private set; }

        /// <summary>True while a ContentDialog is open; pauses the controller (see NativeInputRouter).</summary>
        public bool DialogOpen
        {
            get { return _dialogOpen; }
            set
            {
                _dialogOpen = value;
                if (_controller != null)
                {
                    _controller.Paused = value;
                }
            }
        }

        public MainPage()
        {
            Log.Write("Main page starting");
            // Settings first: the language override must be set before InitializeComponent so the
            // sidebar labels (x:Uid) resolve in the chosen language on first launch.
            Settings = new AppSettings();
            AppSettings.Current = Settings;
            var language = Settings.AppLanguage;
            try
            {
                ApplicationLanguages.PrimaryLanguageOverride = language;
            }
            catch (Exception ex)
            {
                Log.Error("Setting the app language failed", ex);
            }
            InitializeComponent();
            // The direction follows the UI language actually rendered, not only the
            // stored tag (build 162: Arabic labels, yet the strip landed on the left).
            SetFlowDirection(AppSettings.EffectiveIsRightToLeft());
            _player = new HybridMediaPlayer();
            // The page has its own focus navigation for the controller; the console's mouse-mode
            // cursor would only get in the way.
            SetPointerMode(false);
            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += OnLoaded;
            // Collapsing sidebar (web: icons only, expands on focus/hover): expand when
            // a sidebar button gains focus, collapse when focus enters the page content.
            HomeButton.GotFocus += (s, e) => ExpandSidebar();
            SearchButton.GotFocus += (s, e) => ExpandSidebar();
            LibraryButton.GotFocus += (s, e) => ExpandSidebar();
            SettingsButton.GotFocus += (s, e) => ExpandSidebar();
            ProfileButton.GotFocus += (s, e) => ExpandSidebar();
            ContentFrame.GotFocus += (s, e) => CollapseSidebar();
            SidebarBorder.PointerEntered += (s, e) => ExpandSidebar();
            SidebarBorder.PointerExited += (s, e) => { if (!IsFocusInSidebar()) CollapseSidebar(); };
        }

        public void SetFlowDirection(bool rightToLeft)
        {
            FlowDirection = rightToLeft
                ? Windows.UI.Xaml.FlowDirection.RightToLeft
                : Windows.UI.Xaml.FlowDirection.LeftToRight;
            // Hosted pages inherit through the frame; set it explicitly too, so a page
            // constructed before it is parented still lands in the right direction.
            if (ContentFrame != null) ContentFrame.FlowDirection = FlowDirection;
            // The sidebar is an overlay (web: position absolute): it docks to the
            // flow-start edge through the shell grid's first column (right in RTL, left
            // in LTR), its divider faces the content, and its buttons' content sits at
            // the start edge in both states (web: icon at the start, labels fade), so
            // the icons never move when the strip widens.
            SidebarBorder.BorderThickness = rightToLeft ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
            var contentAlignment = rightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            HomeButton.HorizontalContentAlignment = contentAlignment;
            SearchButton.HorizontalContentAlignment = contentAlignment;
            LibraryButton.HorizontalContentAlignment = contentAlignment;
            SettingsButton.HorizontalContentAlignment = contentAlignment;
            ProfileButton.HorizontalContentAlignment = contentAlignment;
            // The logo images are pinned Left in XAML (physical): in RTL they must sit
            // at the start edge like the buttons' content (build 170: the logo looked
            // off-centre next to the icon column while the buttons sat at the right).
            var logoAlignment = rightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            SidebarLogoIcon.HorizontalAlignment = logoAlignment;
            SidebarLogoWordmark.HorizontalAlignment = logoAlignment;
            Log.Write("Shell flow direction: " + (rightToLeft ? "RightToLeft" : "LeftToRight"));
        }

        /// <summary>Logs the strip's on-screen side against the effective direction. Build 162
        /// showed it on the left despite an RTL shell, so the next Xbox log must prove where
        /// it actually landed; the grid column already docks it to the flow-start edge, this
        /// only records the result.</summary>
        private void VerifySidebarSide()
        {
            try
            {
                var rtl = FlowDirection == Windows.UI.Xaml.FlowDirection.RightToLeft;
                // Measure against the shell grid, not the window content: the grid shares
                // the strip's RTL context, so the transform is a plain translation. Against
                // Window.Current.Content (an LTR root frame) the RTL mirroring folds into
                // the result — build 170 logged sidebarX=960 (= windowWidth) for a strip
                // visibly at 912..960, i.e. the right edge instead of the left.
                var origin = SidebarBorder.TransformToVisual(ShellGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
                var windowWidth = ShellGrid.ActualWidth;
                var onRight = origin.X + SidebarBorder.ActualWidth / 2 > windowWidth / 2;
                Log.Write("Sidebar side check: rtl=" + rtl + ", sidebarX=" + origin.X + ", sidebarWidth=" + SidebarBorder.ActualWidth + ", windowWidth=" + windowWidth + ", onRight=" + onRight);
            }
            catch (Exception ex)
            {
                Log.Error("Verifying the sidebar side failed", ex);
            }
        }

        /// <summary>Handles media surface initialization failure without taking down the app.</summary>
        internal bool TryRecoverFromVideoFailure(Exception error)
        {
            if (!_videoViewStarting) return false;
            _videoViewStarting = false;
            Log.Write("A media surface failed to initialize; continuing without playback");
            try
            {
                if (VideoView != null) XamlMarkupHelper.UnloadObject(VideoView);
            }
            catch (Exception ex)
            {
                Log.Error("Removing the VLC video surface failed", ex);
            }
            _player.SetVlcUnavailable("the VLC video surface failed to start (" + error.Message + ")");
            _player.SetUnavailable("the media surface failed to initialize (" + error.Message + ")");
            return true;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            Window.Current.VisibilityChanged -= OnWindowVisibilityChanged;
            _controller?.Dispose();
            _bridge?.Dispose();
            _player.Dispose();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            Log.Write("Main page loaded, starting native shell");
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            Window.Current.VisibilityChanged += OnWindowVisibilityChanged;
            Current = this;

            // The direction the constructor sees is not always the direction the page
            // renders in (build 162: the strip landed on the left while the rows render
            // RTL). Re-assert it from the UI language actually rendered, once the page
            // is in the visual tree, and log the strip's on-screen side after layout.
            SetFlowDirection(AppSettings.EffectiveIsRightToLeft());
            // Fire-and-forget: no local captures the IAsyncAction, otherwise the
            // `var _` would shadow the discard and line 224's `_ = Task` would fail
            // with CS0266 (Task cannot convert to IAsyncAction).
            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => VerifySidebarSide());

            Store = new LocalStore();
            Library = new LibraryService(Store);
            Progress = new ProgressService(Store);
            Stremio = new StremioService(Store);
            Navigation = new NavigationService(ContentFrame, Settings);
            _router = new NativeInputRouter(this, ContentFrame, Navigation);
            _controller = new ControllerBridge((action, repeat) => _router.Handle(action, repeat));

            try
            {
                await Library.LoadAsync().ConfigureAwait(true);
                await Progress.LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("Loading local library and progress failed", ex);
            }

            try
            {
                await Stremio.EnsureDefaultAddonsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("Ensuring default addons failed", ex);
            }

            Navigation.NavigateHome();
            // Manifests refresh in the background; Home already shows cached catalogs meanwhile.
            _ = Stremio.RefreshStaleManifestsAsync();
            CreateVideoView();
        }

        public void ShowToast(string message, string kind = "info", int durationMs = 3000)
        {
            Toast?.Show(message, kind, durationMs);
        }

        public void SetPointerMode(bool enabled)
        {
            Log.Write("Pointer mode " + (enabled ? "on" : "off"));
            RequiresPointer = enabled ? RequiresPointer.WhenFocused : RequiresPointer.Never;
        }

        // Lets the user type into a text box with the console's on-screen keyboard,
        // which reliably works with a XAML text box.
        internal async Task<TextEntryResult> RequestTextAsync(TextEntryRequest request)
        {
            if (DialogOpen)
            {
                return null;
            }
            var isPassword = request.Kind == "password";
            var passwordBox = new PasswordBox { Password = isPassword ? request.Value ?? string.Empty : string.Empty };
            var textBox = new TextBox { Text = request.Value ?? string.Empty, IsSpellCheckEnabled = false };
            var scopeName = InputScopeFor(request.Kind);
            if (scopeName.HasValue)
            {
                var scope = new InputScope();
                scope.Names.Add(new InputScopeName(scopeName.Value));
                textBox.InputScope = scope;
            }
            Control input = isPassword ? (Control)passwordBox : textBox;

            var dialog = new ContentDialog
            {
                Title = string.IsNullOrWhiteSpace(request.Label) ? "Enter text" : request.Label,
                Content = input,
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var submitted = false;
            // Accepting the on-screen keyboard sends Enter: treat that as OK.
            input.KeyDown += (s, e) =>
            {
                if (e.Key == VirtualKey.Enter)
                {
                    e.Handled = true;
                    submitted = true;
                    dialog.Hide();
                }
            };
            // Ask for the keyboard whenever the box gets focus, and again shortly after the dialog
            // opens: a password box isn't always ready for it straight away.
            input.GotFocus += (s, e) => ShowOnScreenKeyboard();
            dialog.Opened += async (s, e) =>
            {
                input.Focus(FocusState.Keyboard);
                textBox.SelectAll();
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                if (input.FocusState == FocusState.Unfocused)
                {
                    input.Focus(FocusState.Keyboard);
                }
                ShowOnScreenKeyboard();
            };

            DialogOpen = true;
            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            finally
            {
                DialogOpen = false;
            }
            if (result != ContentDialogResult.Primary && !submitted)
            {
                return null;
            }
            return new TextEntryResult(isPassword ? passwordBox.Password : textBox.Text, submitted);
        }

        private static InputScopeNameValue? InputScopeFor(string kind)
        {
            switch (kind)
            {
                case "email": return InputScopeNameValue.EmailSmtpAddress;
                case "url": return InputScopeNameValue.Url;
                case "search": return InputScopeNameValue.Search;
                case "number": return InputScopeNameValue.Number;
                default: return null;
            }
        }

        private static void ShowOnScreenKeyboard()
        {
            try
            {
                InputPane.GetForCurrentView().TryShow();
            }
            catch (Exception ex)
            {
                Log.Error("Showing the on-screen keyboard failed", ex);
            }
        }

        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // ControllerBridge handles the controller. Stop XAML from also using its buttons to move
            // focus around — except Gamepad A, which XAML uses to activate the focused control
            // natively, and while a dialog is open, where ContentDialog handles keys natively.
            if (DialogOpen) return;
            if (e.Key == VirtualKey.GamepadA) return;
            var key = (int)e.Key;
            var isController = (key >= (int)VirtualKey.GamepadA && key <= (int)VirtualKey.GamepadRightThumbstickLeft) ||
                (key >= (int)VirtualKey.NavigationView && key <= (int)VirtualKey.NavigationCancel);
            if (isController)
            {
                e.Handled = true;
            }
        }

        // Realize the media surfaces after the shell loads so startup remains independent of playback.
        // The FFmpeg surface and the VLC fallback surface are created independently: if one fails,
        // the other can still play.
        private void CreateVideoView()
        {
            if (_videoViewRequested) return;
            _videoViewRequested = true;
            _videoViewStarting = true;
            MediaPlayerElement nativeView = null;
            VideoView vlcView = null;
            try
            {
                FindName(nameof(MediaElement));
                nativeView = MediaElement;
            }
            catch (Exception ex)
            {
                Log.Error("Creating the native media surface failed", ex);
            }
            try
            {
                Log.Write("Creating the VLC video surface");
                FindName(nameof(VideoView));
                vlcView = VideoView;
                VideoView.Initialized += VideoView_Initialized;
            }
            catch (Exception ex)
            {
                Log.Error("Creating the VLC video surface failed", ex);
                _player.SetVlcUnavailable("the VLC video surface could not be created (" + ex.Message + ")");
            }
            try
            {
                Log.Write("Creating the native MediaPlayerElement surface");
                _player.Initialize(nativeView, vlcView, Dispatcher);
                _videoViewStarting = false;
                Log.Write("Media surfaces ready");
            }
            catch (Exception ex)
            {
                _videoViewStarting = false;
                Log.Error("Creating the media surfaces failed", ex);
                _player.SetUnavailable("the media surface could not be created (" + ex.Message + ")");
            }
        }

        private void VideoView_Initialized(object sender, InitializedEventArgs e)
        {
            Log.Write("VLC video surface ready");
            _player.SetVlcSwapChainOptions(e.SwapChainOptions);
        }

        private void VideoView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _player.SetViewSize(e.NewSize.Width, e.NewSize.Height);
        }

        private void MediaElement_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _player.SetViewSize(e.NewSize.Width, e.NewSize.Height);
        }

        private void SidebarButton_Click(object sender, RoutedEventArgs e)
        {
            var root = (sender as Button)?.Tag as string;
            if (string.IsNullOrEmpty(root)) return;
            Navigation?.NavigateRoot(root);
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            string root = null;
            if (e.Content is HomePage)
            {
                root = "home";
            }
            else if (e.Parameter is PlaceholderNavigation placeholder)
            {
                root = placeholder.SidebarRoot;
            }
            SetActiveSidebarButton(root);
        }

        private void ContentFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            Log.Error("Navigation to " + e.SourcePageType.Name + " failed", e.Exception);
        }

        private void SetActiveSidebarButton(string root)
        {
            SetSidebarActive(HomeButton, root == "home");
            SetSidebarActive(SearchButton, root == "search");
            SetSidebarActive(LibraryButton, root == "library");
            SetSidebarActive(SettingsButton, root == "settings");
        }

        private static void SetSidebarActive(Button button, bool active)
        {
            if (button == null) return;
            button.Background = active
                ? new Windows.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))
                : new Windows.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
        }

        /// <summary>
        /// Collapsing sidebar (web: 6rem icons-only, expands to 17rem on focus/hover,
        /// width .22s, labels fade .15s). XAML on Xbox is scaled 200%, so 6rem/17rem
        /// are 48/136 epx. The strip is an overlay over the content, so the width
        /// animates and the labels fade; the icons sit at the flow-start edge in both
        /// states and never move. The button template and its RootGrid/TargetName focus
        /// states are untouched, so expand/collapse cannot reintroduce the 0x800F1000
        /// animation crash.
        /// </summary>
        private void ExpandSidebar()
        {
            if (_sidebarExpanded) return;
            _sidebarExpanded = true;
            AnimateSidebar(136, true);
        }

        private void CollapseSidebar()
        {
            if (!_sidebarExpanded) return;
            _sidebarExpanded = false;
            AnimateSidebar(48, false);
        }

        private void AnimateSidebar(double targetWidth, bool labelsVisible)
        {
            var storyboard = new Storyboard();
            var widthAnimation = new DoubleAnimation
            {
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(widthAnimation, SidebarBorder);
            Storyboard.SetTargetProperty(widthAnimation, "Width");
            storyboard.Children.Add(widthAnimation);
            // Labels and the wordmark fade (web: opacity .15s): delayed until the
            // strip is wide enough to hold them when expanding, and at once when
            // collapsing. Collapsed the strip shows the logo icon alone; expanded
            // it shows the full logo with NUVIO.
            var labels = new UIElement[] { HomeLabel, SearchLabel, LibraryLabel, SettingsLabel, ProfileLabel, SidebarLogoWordmark };
            foreach (var label in labels)
            {
                var fade = new DoubleAnimation
                {
                    To = labelsVisible ? 1 : 0,
                    Duration = TimeSpan.FromMilliseconds(150),
                    BeginTime = labelsVisible ? TimeSpan.FromMilliseconds(70) : TimeSpan.Zero,
                };
                Storyboard.SetTarget(fade, label);
                Storyboard.SetTargetProperty(fade, "Opacity");
                storyboard.Children.Add(fade);
            }
            // The wordmark already contains the triangle, so the icon cross-fades the
            // other way: collapsed shows the icon alone, expanded shows the full lockup
            // once — never triangle + nuvio + triangle as in build 162.
            var iconFade = new DoubleAnimation
            {
                To = labelsVisible ? 0 : 1,
                Duration = TimeSpan.FromMilliseconds(150),
                BeginTime = labelsVisible ? TimeSpan.FromMilliseconds(70) : TimeSpan.Zero,
            };
            Storyboard.SetTarget(iconFade, SidebarLogoIcon);
            Storyboard.SetTargetProperty(iconFade, "Opacity");
            storyboard.Children.Add(iconFade);
            storyboard.Completed += (s, e) =>
            {
                // A newer animation supersedes this one; only the latest pins the end state.
                if (!ReferenceEquals(_sidebarStoryboard, storyboard)) return;
                _sidebarStoryboard = null;
                SidebarBorder.Width = targetWidth;
                foreach (var label in labels)
                {
                    label.Opacity = labelsVisible ? 1 : 0;
                }
                SidebarLogoIcon.Opacity = labelsVisible ? 0 : 1;
            };
            _sidebarStoryboard = storyboard;
            storyboard.Begin();
        }

        /// <summary>
        /// True when the focused element is a sidebar button; also reports whether it is
        /// the first or last visible one, so the input router can stop at the strip's ends
        /// instead of letting focus fall out into the content (which collapses the strip).
        /// </summary>
        internal bool IsSidebarButton(DependencyObject focused, out bool isFirst, out bool isLast)
        {
            isFirst = false;
            isLast = false;
            if (focused == null) return false;
            Button button = null;
            for (var node = focused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var candidate = node as Button;
                if (candidate != null)
                {
                    button = candidate;
                    break;
                }
                if (node == SidebarBorder) break;
            }
            if (button == null) return false;
            var inSidebar = false;
            for (var node = (DependencyObject)button; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node == SidebarBorder)
                {
                    inSidebar = true;
                    break;
                }
            }
            if (!inSidebar) return false;
            // The strip's child is a Grid (logo row + items row); the buttons live
            // in the named items panel.
            var panel = SidebarItemsPanel;
            if (panel == null) return false;
            Button first = null;
            Button last = null;
            foreach (var child in panel.Children)
            {
                var childButton = child as Button;
                if (childButton == null || childButton.Visibility != Visibility.Visible) continue;
                if (first == null) first = childButton;
                last = childButton;
            }
            isFirst = ReferenceEquals(button, first);
            isLast = ReferenceEquals(button, last);
            return true;
        }

        private bool IsFocusInSidebar()
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            for (var node = focused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node == SidebarBorder) return true;
            }
            return false;
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            // ControllerBridge already handles B; handling it here too would go back twice, and
            // leaving it unhandled would close the app.
            e.Handled = true;
        }

        private void OnWindowVisibilityChanged(object sender, VisibilityChangedEventArgs e)
        {
            // Nothing to forward in M1 (the web bridge is gone); visibility handling returns
            // with the player in M4.
        }
    }
}
