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
            SetFlowDirection(string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase));
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
        /// Collapsing sidebar (web: 6rem icons-only, expands to 17rem on focus/hover).
        /// Collapsed: 72px, labels hidden, icons centered. Expanded: 204px, labels shown.
        /// Only the column width, label visibility and content alignment change - the
        /// button template and its RootGrid/TargetName focus states are untouched, so
        /// expand/collapse cannot reintroduce the 0x800F1000 animation crash.
        /// </summary>
        private void ExpandSidebar()
        {
            if (_sidebarExpanded) return;
            _sidebarExpanded = true;
            SidebarColumn.Width = new GridLength(204);
            SetSidebarLabelsVisible(true);
            SetSidebarContentAlignment(HorizontalAlignment.Left);
        }

        private void CollapseSidebar()
        {
            if (!_sidebarExpanded) return;
            _sidebarExpanded = false;
            SidebarColumn.Width = new GridLength(72);
            SetSidebarLabelsVisible(false);
            SetSidebarContentAlignment(HorizontalAlignment.Center);
        }

        private void SetSidebarLabelsVisible(bool visible)
        {
            var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            HomeLabel.Visibility = visibility;
            SearchLabel.Visibility = visibility;
            LibraryLabel.Visibility = visibility;
            SettingsLabel.Visibility = visibility;
            ProfileLabel.Visibility = visibility;
        }

        private void SetSidebarContentAlignment(HorizontalAlignment alignment)
        {
            HomeButton.HorizontalContentAlignment = alignment;
            SearchButton.HorizontalContentAlignment = alignment;
            LibraryButton.HorizontalContentAlignment = alignment;
            SettingsButton.HorizontalContentAlignment = alignment;
            ProfileButton.HorizontalContentAlignment = alignment;
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
