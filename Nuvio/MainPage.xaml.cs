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
        /// <summary>Phase 1 Focus Manager coordinator (null until OnLoaded constructs it). Owns input dispatch, the Settings arrival settle and the navigation guard while FocusCoordinator.UseFocusCoordinator is true; the router stays the rollback path.</summary>
        private FocusCoordinator _coordinator;
        private bool _videoViewRequested;
        private bool _videoViewStarting;
        private bool _dialogOpen;
        /// <summary>Single sidebar state machine: Hidden (full-screen pages), Collapsed (icons only), Expanded (136 epx, labels visible).</summary>
        internal enum SidebarState { Hidden, Collapsed, Expanded }
        private SidebarState _sidebarState = SidebarState.Collapsed;
        /// <summary>
        /// Coordinator-owned guard: while set, focus landing on a sidebar button
        /// after navigation must not expand the strip. Armed BEFORE a
        /// navigation starts (NavigationService.Navigate choke-point and
        /// NativeInputRouter.HandleBack), so the framework's synchronous
        /// focus fallback can never land on a sidebar button while the
        /// guard is down. Cleared ONLY by the router's settle succeeding,
        /// by watchdog expiry, by user takeover (NoteUserInput), by genuine
        /// user input while no settle is pending, or by FocusSidebar.
        /// Never cleared by focus entering the content.
        /// </summary>
        private bool _suppressExpandUntilArrivalSettled;
        private Storyboard _sidebarStoryboard;
        private bool _userInputSeen;
        private string _activeRoot;
        private Storyboard _sidebarIconAnimation;
        private CompositeTransform _sidebarIconAnimationTarget;

        internal AppSettings Settings { get; private set; }
        internal LocalStore Store { get; private set; }
        internal StremioService Stremio { get; private set; }
        internal LibraryService Library { get; private set; }
        internal ProgressService Progress { get; private set; }
        internal NavigationService Navigation { get; private set; }
        internal PlayerService PlayerService { get; private set; }
        internal DebridService Debrid { get; private set; }

        internal FocusCoordinator Coordinator { get { return _coordinator; } }

        /// <summary>Launch arguments the app was started with (App passes e.Arguments as the MainPage navigation parameter). Read by the FocusTestHarness gate; empty on a plain dashboard launch of the installed msix.</summary>
        internal static string LaunchArguments { get; private set; }

        /// <summary>True while a ContentDialog is open; pauses the controller (see NativeInputRouter).</summary>
        public bool DialogOpen
        {
            get { return _dialogOpen; }
            set
            {
                _dialogOpen = value;
                // Phase 1: route through the coordinator when it owns input
                // (it pauses its own dispatch and captures the invoker for
                // Phase 3's restore; focus is NOT moved here or on close).
                // The ControllerBridge pause below is kept in both branches.
                if (_coordinator != null && FocusCoordinator.UseFocusCoordinator)
                {
                    if (value) _coordinator.NotifyDialogOpened(null);
                    else _coordinator.NotifyDialogClosed();
                }
                if (_controller != null)
                {
                    _controller.Paused = value;
                }
            }
        }

        /// <summary>True while the sidebar strip is expanded (136 epx, labels visible).
        /// HomePage freezes the hero auto-rotate while this is true: the strip's live
        /// blur samples the hero backdrop, so a timer flip while the menu is open
        /// would change the frosted menu under the user's hands.</summary>
        public bool IsSidebarExpanded => _sidebarState == SidebarState.Expanded;

        /// <summary>Records a page's last-focused key for Back restore under its navigation-entry key, and when rowKey is supplied the last-focused key for that row. Single funnel: always forwards to the router's central registry, and when the flag is on also writes the coordinator's registry (same key format) so migrated pages restore through the coordinator.</summary>
        internal void RecordArrivalFocusKey(string entryKey, string key, string rowKey = null)
        {
            if (_router != null) _router.RecordArrivalFocusKey(entryKey, key, rowKey);
            if (_coordinator != null && FocusCoordinator.UseFocusCoordinator) _coordinator.RecordFocus(entryKey, key, rowKey);
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
            // Phase 1: at launch the framework parks initial focus on HomeButton
            // (the first focusable element) while the frame is still empty and
            // the hero loads from the network, so the sidebar buttons must not
            // accept focus while the strip is collapsed. Re-enabled on expand
            // (SetSidebarExpanded) and on a deliberate move (FocusSidebar).
            // ProfileButton stays untouched: it is Collapsed, so it never
            // takes focus in the first place.
            if (FocusCoordinator.UseFocusCoordinator)
            {
                HomeButton.IsTabStop = false;
                SearchButton.IsTabStop = false;
                LibraryButton.IsTabStop = false;
                SettingsButton.IsTabStop = false;
            }
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
            // Focus-driven expansion is gated on user input (ExpandSidebarOnFocus): at
            // launch the framework parks initial focus on HomeButton before any input,
            // and that activation focus must not open the strip — the strip starts
            // collapsed. Pointer hover goes through the same gated path (it is a user
            // action, but it must not open the strip while arrival focus is still
            // settling after a navigation).
            HomeButton.GotFocus += SidebarButton_GotFocus;
            SearchButton.GotFocus += SidebarButton_GotFocus;
            LibraryButton.GotFocus += SidebarButton_GotFocus;
            SettingsButton.GotFocus += SidebarButton_GotFocus;
            ProfileButton.GotFocus += SidebarButton_GotFocus;
            ContentFrame.GotFocus += (s, e) => { CollapseSidebar(); };
            SidebarBorder.PointerEntered += (s, e) => ExpandSidebarOnFocus();
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
            // the icons never move when the strip widens. The divider, the logo and the
            // button content alignment are bound in MainPage.xaml (the "divider"/"logo"
            // Thickness parameters and the alignment converters on each element's own
            // FlowDirection): code-behind sets here did not reliably reach the render —
            // build 180 still showed the divider on the outer edge and the logo off the
            // icon column while the log proved RTL, and build 216 still showed the
            // labels left-aligned after two rounds of HorizontalContentAlignment sets —
            // so the bindings own those now and nothing here touches them.
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
                // TransformToVisual reports RTL-origin coordinates: an RTL element's (0,0)
                // is its top-RIGHT corner, so against the RTL ShellGrid a strip visibly at
                // 912..960 logged sidebarX=0 in build 180 (and against the LTR window root
                // it logged 960 in build 170) — both are the strip's right edge, not its
                // left. Measure BOTH corners against the LTR window root (physical pixels)
                // and take min/max, so sidebarX is the physical left edge in either
                // direction: 912 for a strip docked right in RTL, 0 docked left in LTR.
                var root = Window.Current.Content as FrameworkElement;
                var reference = root ?? ShellGrid;
                var p0 = SidebarBorder.TransformToVisual(reference).TransformPoint(new Windows.Foundation.Point(0, 0));
                var pW = SidebarBorder.TransformToVisual(reference).TransformPoint(new Windows.Foundation.Point(SidebarBorder.ActualWidth, 0));
                var left = Math.Min(p0.X, pW.X);
                var windowWidth = reference.ActualWidth;
                var onRight = left + SidebarBorder.ActualWidth / 2 > windowWidth / 2;
                Log.Write("Sidebar side check: rtl=" + rtl + ", sidebarX=" + left + ", sidebarWidth=" + SidebarBorder.ActualWidth + ", windowWidth=" + windowWidth + ", onRight=" + onRight);
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
            PlayerService = new PlayerService(_player, Settings);
            Debrid = new DebridService(Settings);
            _router = new NativeInputRouter(this, ContentFrame, Navigation);
            // Phase 2: construct the coordinator next to the router (router
            // first — the coordinator owns row/edge moves itself now and
            // takes the router's HandleBack for Back dispatch) and link both
            // directions right away, so no input can arrive before the wiring
            // is complete. Constructing the coordinator changes no behaviour
            // on its own (no subscriptions, no timers, no focus moves); the
            // UseFocusCoordinator flag selects whether it owns anything.
            _coordinator = new FocusCoordinator(this, ContentFrame, Navigation, _router.HandleBack);
            _router.Coordinator = _coordinator;
            Navigation.Coordinator = _coordinator;
            _controller = new ControllerBridge(HandleControllerInput);

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

        /// <summary>
        /// Single controller-input entry point, shared by the ControllerBridge
        /// poll callback and the FocusTestHarness seam (SimulateFocusInput)
        /// so the two can never drift. The router is the funnel: with the
        /// flag on it delegates to the coordinator, which performs dispatch
        /// plus its own settle's takeover decision internally.
        /// </summary>
        private void HandleControllerInput(string action, bool repeat)
        {
            _userInputSeen = true;
            // Settling-aware guard rule (Fix 2, root cause A; lag fix):
            // handle the press FIRST, then the takeover decision, so
            // NoteUserInput sees the press's effect: it cancels the
            // settle only if focus actually moved. A press that could
            // not move focus (content not ready yet) leaves the settle
            // running, so the arrival target still lands. A back press
            // is safe either way: HandleBack re-arms the guard before
            // navigating, and the new settle's 200 ms ignore window
            // covers the duplicate delivery.
            _router.Handle(action, repeat);
            if (_coordinator != null && FocusCoordinator.UseFocusCoordinator)
            {
                // The coordinator handled dispatch and its own settle's
                // takeover inside HandleInput — never call
                // _router.NoteUserInput() for a coordinator-owned settle
                // (double evaluation) and never clear the guard while a
                // coordinator settle is pending. The two lines below cover
                // only what the coordinator does NOT own: a legacy router
                // settle (every page but Settings in Phase 1) still needs
                // its takeover evaluated, and genuine input with no settle
                // of either kind clears a stale guard (e.g. a refused
                // navigation armed it with no settle to follow).
                if (_router != null && _router.IsSettling) _router.NoteUserInput();
                if (!_coordinator.IsSettling && (_router == null || !_router.IsSettling)) _coordinator.ClearNavigationGuard();
                return;
            }
            if (_router != null && _router.IsSettling) _router.NoteUserInput();
            else ClearSidebarExpandSuppression();
        }

        /// <summary>
        /// Pinned test seam for the FocusTestHarness: drives one input
        /// through the IDENTICAL code path the ControllerBridge poll
        /// callback uses (HandleControllerInput above). The harness never
        /// calls the router or FocusManager directly for input.
        /// </summary>
        internal void SimulateFocusInput(string action, bool repeat)
        {
            HandleControllerInput(action, repeat);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // App passes the launch arguments as the MainPage navigation
            // parameter; the harness arms itself from them (or from its
            // flag file) and stays dormant otherwise. Safe before OnLoaded:
            // RunIfRequested waits for the shell to be ready.
            LaunchArguments = e.Parameter as string;
            FocusTestHarness.RunIfRequested(this, LaunchArguments);
        }

        public void SetVideoVisible(bool visible)
        {
            if (ShellGrid == null) return;
            ShellGrid.Background = visible
                ? new SolidColorBrush(Colors.Transparent)
                : new SolidColorBrush(Color.FromArgb(0xFF, 0x0D, 0x0E, 0x13));
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

            // Record the invoker before opening so focus can go back to it when
            // the dialog closes (or to the page's arrival target when it cannot).
            // Flag off only: with the flag on the coordinator owns the restore
            // (DialogOpen = false below notifies it) and a local move here
            // would race its one-tick restore.
            var invoker = FocusManager.GetFocusedElement() as DependencyObject;
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
            if (_coordinator == null || !FocusCoordinator.UseFocusCoordinator)
            {
                try
                {
                    var control = invoker as Control;
                    var restored = control != null && control.Focus(FocusState.Programmatic);
                    if (!restored && _router != null)
                    {
                        _router.SettleArrivalFocus(ContentFrame.Content as FocusablePage, false, null);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Restoring focus after the text dialog failed", ex);
                }
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
            // Any key reaching here is real user input: it arms focus-driven sidebar
            // expansion (the launch activation focus must not open the strip).
            _userInputSeen = true;
            // Settling-aware guard rule (Fix 2, root cause A): same as the
            // controller callback — clear only when no arrival settle is
            // pending, else hand the press to the router as a takeover.
            // Phase 1: the key path stays DISPATCH-FREE (see
            // FOCUS-MIGRATION.md — the coordinator's 150 ms dedupe covers
            // directions only, so feeding back/menu/view from here would
            // double-deliver them, poll + key). Only guard/takeover state
            // is touched here; the poll path feeds Coordinator.HandleInput
            // and evaluates the coordinator takeover there. When the flag
            // is on, a coordinator settle must never be cleared from here,
            // and a legacy router settle (non-Settings pages) still gets
            // its takeover evaluated.
            if (_coordinator != null && FocusCoordinator.UseFocusCoordinator)
            {
                if (_router != null && _router.IsSettling) _router.NoteUserInput();
                if (!_coordinator.IsSettling && (_router == null || !_router.IsSettling)) _coordinator.ClearNavigationGuard();
            }
            else if (_router != null && _router.IsSettling) _router.NoteUserInput();
            else ClearSidebarExpandSuppression();
            // ControllerBridge handles the controller. Stop XAML from also using its buttons to move
            // focus around — except Gamepad A, which XAML uses to activate the focused control
            // natively, and while a dialog is open, where ContentDialog handles keys natively.
            // The console also delivers the D-pad as plain arrow keys (VirtualKey.Left/Right/Up/
            // Down): the old web UI proved it — its key map only ever saw arrows, and its
            // cross-source dedupe existed because the same press arrived both through the poll
            // and as a key. Arrows are NOT in the ranges below, so they reached the framework's
            // XYFocus navigation and moved focus a second card per press (system move + router
            // move = "pressing twice"). The poll owns every move now, so swallow them too.
            if (DialogOpen) return;
            if (e.Key == VirtualKey.GamepadA) return;
            var key = (int)e.Key;
            var isController = (key >= (int)VirtualKey.GamepadA && key <= (int)VirtualKey.GamepadRightThumbstickLeft) ||
                (key >= (int)VirtualKey.NavigationView && key <= (int)VirtualKey.NavigationCancel);
            var isArrow = key == (int)VirtualKey.Left || key == (int)VirtualKey.Right ||
                key == (int)VirtualKey.Up || key == (int)VirtualKey.Down;
            if (isController || isArrow)
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
            // The strip closes after a choice: the user picked a page, so the content
            // takes the screen and the strip goes back to icons only. Focus stays on
            // the chosen button (now in the collapsed strip); moving right enters the
            // page, moving within the strip re-opens it.
            CollapseSidebar();
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            Log.Write("Navigated to " + (e.Content != null ? e.Content.GetType().Name : "null"));
            // The guard was already armed BEFORE this navigation started
            // (NavigationService.Navigate / HandleBack); re-assert it here
            // as a belt-and-braces for navigations that bypass the
            // choke-point. It is cleared only by the settle succeeding, by
            // watchdog expiry, by user takeover, or by genuine user input
            // while no settle is pending — never by focus entering the
            // content (ContentFrame.GotFocus only collapses the strip).
            // Phase 3: with the flag on re-arm through the coordinator (it
            // re-arms the shell bit too and restarts the hard-timeout clock
            // from this navigation); with the flag off set the bit directly.
            if (_coordinator != null && FocusCoordinator.UseFocusCoordinator) _coordinator.ArmNavigationGuard();
            else _suppressExpandUntilArrivalSettled = true;
            // The details, catalog, player and streams pages take the full
            // screen: hide the sidebar strip while one is open, and restore
            // it on any other page — back navigation included, since
            // Frame.GoBack raises Navigated too.
            if (e.Content is DetailsPage || e.Content is CatalogPage || e.Content is PlayerPage || e.Content is StreamsPage)
            {
                HideSidebar();
            }
            else if (_sidebarState == SidebarState.Hidden)
            {
                ShowSidebar();
            }
            string root = null;
            if (e.Content is HomePage)
            {
                root = "home";
            }
            else if (e.Content is SearchPage)
            {
                root = "search";
            }
            else if (e.Content is SettingsPage)
            {
                root = "settings";
            }
            else if (e.Content is CatalogPage)
            {
                // Catalogs are not a sidebar root: no button is highlighted.
                root = null;
            }
            else if (e.Parameter is PlaceholderNavigation placeholder)
            {
                root = placeholder.SidebarRoot;
            }
            SetActiveSidebarButton(root);
            // Central arrival focus (Fix 2): the settle's synchronous first
            // attempt runs inside this call, before first paint; async pages
            // settle via ContentReady / the watchdog. The navigation
            // parameter travels with the call so the settle can log the
            // entry it is settling. A non-FocusablePage content logs and
            // clears the guard (defensive; after the page migration this
            // must not happen).
            // Fresh arrival (not Back): the previous visit's per-row memory is stale — drop this entry's row map so row-to-row moves land on each row's first poster, then rebuild from this visit's GotFocus records. Back arrivals keep the map, so row landings restore the saved poster. Must live here, on the Navigated boundary — not inside SettleArrivalFocus/RequestArrival, which the dialog-restore re-settle (line 480) also calls with isBack=false mid-visit.
            if (e.NavigationMode != NavigationMode.Back)
            {
                var navigatedPage = e.Content as FocusablePage;
                if (navigatedPage != null && !string.IsNullOrEmpty(navigatedPage.EntryKey))
                {
                    if (_router != null) _router.ClearRowFocusKeys(navigatedPage.EntryKey);
                    if (_coordinator != null) _coordinator.ClearRowFocusKeys(navigatedPage.EntryKey);
                }
            }
            if (_router != null) _router.SettleArrivalFocus(e.Content as FocusablePage, e.NavigationMode == NavigationMode.Back, e.Parameter);
        }

        private void ContentFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            Log.Error("Navigation to " + e.SourcePageType.Name + " failed", e.Exception);
        }

        private void SetActiveSidebarButton(string root)
        {
            _activeRoot = root;
            SetSidebarActive(HomeButton, root == "home");
            SetSidebarActive(SearchButton, root == "search");
            SetSidebarActive(LibraryButton, root == "library");
            SetSidebarActive(SettingsButton, root == "settings");
        }

        /// <summary>
        /// The sidebar button for the page currently shown in the content
        /// frame, from the root recorded by ContentFrame_Navigated
        /// (SetActiveSidebarButton): HomePage -> HomeButton, SearchPage ->
        /// SearchButton, SettingsPage -> SettingsButton, PlaceholderPage ->
        /// the button for its SidebarRoot ("library" -> LibraryButton,
        /// "settings" -> SettingsButton). Unknown / null root (Catalog and
        /// the hidden-sidebar pages, which set no root) falls back to
        /// HomeButton. Shared by FocusSidebar() and FocusSidebarFirst() so
        /// the two openings can never drift apart again.
        /// </summary>
        private Button SidebarButtonForCurrentRoot()
        {
            return _activeRoot == "search" ? SearchButton
                : _activeRoot == "library" ? LibraryButton
                : _activeRoot == "settings" ? SettingsButton
                : HomeButton;
        }

        /// <summary>
        /// Moves focus to the sidebar's button for the current page (Home when
        /// the page is unknown). Used by the hero's start-edge move: right from
        /// Play opens the strip from any slide instead of wrapping the
        /// carousel to the last slide. Focusing a sidebar button expands
        /// the strip through ExpandSidebarOnFocus (gated on real user input,
        /// which a D-pad press always is).
        /// </summary>
        public void FocusSidebar()
        {
            if (_sidebarState == SidebarState.Hidden) return;
            // A deliberate move onto the strip: the back-navigation suppression
            // (if still set) must not swallow a real opening of the menu.
            // Phase 1: clear through the coordinator when it owns the guard
            // (it clears the shell bit too); the shell method stays the
            // low-level bit setter and never delegates back.
            if (_coordinator != null && FocusCoordinator.UseFocusCoordinator) _coordinator.ClearNavigationGuard();
            else ClearSidebarExpandSuppression();
            var button = SidebarButtonForCurrentRoot();
            // A deliberate move onto the strip re-enables the button first:
            // the buttons stop accepting focus while the strip is collapsed
            // at launch (see the constructor), and Focus() on a control with
            // IsTabStop=false returns false without moving focus.
            button.IsTabStop = true;
            if (!button.Focus(FocusState.Programmatic))
            {
                Log.Write("FocusSidebar failed to focus " + button.Name);
            }
        }

        /// <summary>
        /// Deliberate menu open that lands on the button for the current
        /// page (SidebarButtonForCurrentRoot — Settings from Settings,
        /// Search from Search, Home when the page is unknown): opens the
        /// strip FIRST, then focuses that button, so the ring never appears
        /// on a collapsed strip. ("First" is the open-first order, not the
        /// first option: the old always-Home landing was superseded by the
        /// current-page rule.) Same target as FocusSidebar(), which is
        /// called only by the hero's start-edge branch. Reached only
        /// through the coordinator (flag path, MoveFocus intercepts a move
        /// toward the sidebar); no flag check inside this method. Returns
        /// false when the sidebar is Hidden (Details / Catalog / Streams /
        /// Player) so the caller falls through to XYFocus as today; true
        /// otherwise.
        /// </summary>
        public bool FocusSidebarFirst()
        {
            if (_sidebarState == SidebarState.Hidden) return false;
            // Same guard clear as FocusSidebar: a deliberate opening must not
            // be swallowed by the back-navigation suppression (if still set).
            if (_coordinator != null && FocusCoordinator.UseFocusCoordinator) _coordinator.ClearNavigationGuard();
            else ClearSidebarExpandSuppression();
            // Open first: this re-enables the four buttons (see the
            // constructor) and returns early when already expanded, in which
            // case the buttons are enabled already — the explicit set below
            // is harmless either way.
            SetSidebarExpanded(true);
            var button = SidebarButtonForCurrentRoot();
            button.IsTabStop = true;
            if (!button.Focus(FocusState.Programmatic))
            {
                Log.Write("FocusSidebarFirst failed to focus " + button.Name);
            }
            return true;
        }

        private static void SetSidebarActive(Button button, bool active)
        {
            if (button == null) return;
            // The active look: surface-2 bg + white text + a thin light transparent
            // underline under the icon. The underline lives inside the button template,
            // so it is reached through the visual tree; a missing underline (template
            // not applied yet) just skips that half of the look.
            button.Background = active
                ? new Windows.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))
                : new Windows.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
            button.Foreground = active
                ? new Windows.UI.Xaml.Media.SolidColorBrush(Colors.White)
                : new Windows.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            var underline = FindTemplateChild(button, "ActiveUnderline");
            if (underline != null)
            {
                underline.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                if (active)
                {
                    // Fade the underline in over 150 ms instead of popping it on:
                    // the active item changes with navigation, so the line blends
                    // in with the rest of the sidebar's motion (build 241 button
                    // animation). A true slide between buttons would need a shared
                    // underline outside the per-button template, so the fade is the
                    // animation here; the line itself is unchanged.
                    var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(150), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    var storyboard = new Storyboard();
                    Storyboard.SetTarget(fade, underline);
                    Storyboard.SetTargetProperty(fade, "Opacity");
                    storyboard.Children.Add(fade);
                    storyboard.Begin();
                }
            }
        }

        /// <summary>Finds a named element inside a control's applied template.</summary>
        private static FrameworkElement FindTemplateChild(DependencyObject root, string name)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var element = child as FrameworkElement;
                if (element != null && element.Name == name) return element;
                var found = FindTemplateChild(child, name);
                if (found != null) return found;
            }
            return null;
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
        /// <summary>Focus-driven expansion, gated on real user input. At launch the
        /// framework parks initial focus on HomeButton before any input arrives; that
        /// activation focus must not open the strip, or the app starts expanded (build
        /// 216). The first controller poll or key arms the gate; pointer hover goes
        /// through this same gate. HomePage.FocusFirst is the other
        /// half: once the hero loads it moves focus into the content, which collapses
        /// the strip through ContentFrame.GotFocus.</summary>
        private void ExpandSidebarOnFocus()
        {
            if (!_userInputSeen) return;
            // Phase 3: with the flag on the coordinator is the single
            // source of truth for the guard (it owns the arm/clear and the
            // hard timeout); with the flag off the shell bit is the guard.
            var guardArmed = _coordinator != null && FocusCoordinator.UseFocusCoordinator
                ? _coordinator.IsNavigationGuardArmed
                : _suppressExpandUntilArrivalSettled;
            if (guardArmed)
            {
                Log.Write("Sidebar expand suppressed after navigation (focus fallback)");
                return;
            }
            ExpandSidebar();
        }

        /// <summary>
        /// Arms the navigation expansion suppression. Called BEFORE a
        /// navigation starts — by the NavigationService.Navigate
        /// choke-point and by the router's HandleBack — never after.
        /// </summary>
        internal void ArmSidebarExpandSuppression()
        {
            _suppressExpandUntilArrivalSettled = true;
        }

        /// <summary>
        /// Clears the navigation expansion suppression (see
        /// ContentFrame_Navigated). Called ONLY on genuine user input while
        /// no arrival settle is pending (controller/key callbacks check
        /// NativeInputRouter.IsSettling first), by FocusSidebar, by the
        /// router's settle on success / watchdog expiry / user takeover.
        /// Per-page focus restores no longer call this — the router owns
        /// the guard until arrival settles.
        /// </summary>
        internal void ClearSidebarExpandSuppression()
        {
            _suppressExpandUntilArrivalSettled = false;
        }

        /// <summary>
        /// A sidebar button got focus: expand the strip (as before) and play
        /// that button's icon motion once. The motion is on the FontIcon's own
        /// RenderTransform, never on the button: the glyph itself moves, its
        /// shape never changes (same Segoe MDL2 glyph, same size).
        /// </summary>
        private void SidebarButton_GotFocus(object sender, RoutedEventArgs e)
        {
            ExpandSidebarOnFocus();
            PlaySidebarIconAnimation(sender as Button);
        }

        /// <summary>
        /// Icon motion on focus arrival (Mohammed: the icon itself moves, like
        /// a ready-made animated icon, but the glyphs stay exactly the ones in
        /// use now). Ready-made animated sets (Lottie / WinUI AnimatedIcon /
        /// GIF) are different icon designs, so using one would replace the
        /// shapes, which he ruled out; no mechanism animates a Segoe MDL2
        /// glyph in place. So the light motion is applied to the glyph itself:
        /// a short one-shot Storyboard (200-300 ms) on the FontIcon's
        /// CompositeTransform, returning to identity, never looping. Gated on
        /// real user input so the framework's launch focus does not play it.
        /// Home (E80F house) hops, Search (E721 lens) wobbles, Library (E8F1
        /// books) slides up and settles, Settings (E713 gear) turns a little
        /// and back, Profile (E77B person, hidden) pulses.
        /// </summary>
        private void PlaySidebarIconAnimation(Button button)
        {
            if (button == null || !_userInputSeen) return;
            CompositeTransform transform;
            string property;
            double[] timesMs;
            double[] values;
            if (button == HomeButton)
            {
                transform = HomeIconTransform; property = "TranslateY";
                timesMs = new[] { 0.0, 100.0, 250.0 };
                values = new[] { 0.0, -3.0, 0.0 };
            }
            else if (button == SearchButton)
            {
                transform = SearchIconTransform; property = "Rotation";
                timesMs = new[] { 0.0, 90.0, 180.0, 280.0 };
                values = new[] { 0.0, -10.0, 8.0, 0.0 };
            }
            else if (button == LibraryButton)
            {
                transform = LibraryIconTransform; property = "TranslateY";
                timesMs = new[] { 0.0, 90.0, 170.0, 250.0 };
                values = new[] { 0.0, -4.0, 1.0, 0.0 };
            }
            else if (button == SettingsButton)
            {
                transform = SettingsIconTransform; property = "Rotation";
                timesMs = new[] { 0.0, 150.0, 300.0 };
                values = new[] { 0.0, 30.0, 0.0 };
            }
            else if (button == ProfileButton)
            {
                transform = ProfileIconTransform; property = "ScaleX";
                timesMs = new[] { 0.0, 100.0, 250.0 };
                values = new[] { 1.0, 1.15, 1.0 };
            }
            else return;
            if (transform == null) return;
            // A fast re-entry restarts the motion: stop the running one and
            // reset the icon to identity first, so no icon is ever left
            // translated, rotated or scaled mid-motion.
            if (_sidebarIconAnimation != null)
            {
                _sidebarIconAnimation.Stop();
                ResetIconTransform(_sidebarIconAnimationTarget);
            }
            var storyboard = new Storyboard();
            AddIconTrack(storyboard, transform, property, timesMs, values);
            if (property == "ScaleX")
                AddIconTrack(storyboard, transform, "ScaleY", timesMs, values);
            _sidebarIconAnimation = storyboard;
            _sidebarIconAnimationTarget = transform;
            storyboard.Begin();
        }

        private static void AddIconTrack(Storyboard storyboard, CompositeTransform transform, string property, double[] timesMs, double[] values)
        {
            var animation = new DoubleAnimationUsingKeyFrames();
            Storyboard.SetTarget(animation, transform);
            Storyboard.SetTargetProperty(animation, property);
            for (var i = 0; i < timesMs.Length; i++)
            {
                animation.KeyFrames.Add(new EasingDoubleKeyFrame
                {
                    KeyTime = TimeSpan.FromMilliseconds(timesMs[i]),
                    Value = values[i],
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            storyboard.Children.Add(animation);
        }

        private static void ResetIconTransform(CompositeTransform transform)
        {
            if (transform == null) return;
            transform.TranslateX = 0;
            transform.TranslateY = 0;
            transform.Rotation = 0;
            transform.ScaleX = 1;
            transform.ScaleY = 1;
        }

        /// <summary>All sidebar expand/collapse transitions go through here.</summary>
        private void SetSidebarExpanded(bool expanded)
        {
            if (expanded)
            {
                if (_sidebarState == SidebarState.Hidden || _sidebarState == SidebarState.Expanded) return;
                _sidebarState = SidebarState.Expanded;
                // Re-enable the sidebar buttons on every real expansion
                // (focus and hover both arrive here): they stop accepting
                // focus while the strip is collapsed at launch.
                HomeButton.IsTabStop = true;
                SearchButton.IsTabStop = true;
                LibraryButton.IsTabStop = true;
                SettingsButton.IsTabStop = true;
                // Always Verity tones while the strip is open (Mohammed's call):
                // the strip's live blur samples the hero, and the white Verity
                // logo is the bright band at the strip's start edge, so pin the
                // carousel to the Verity slide on expand — whatever slide is
                // current. The hero auto-rotate is already frozen while expanded,
                // so the slide stays on Verity until the strip collapses. Only
                // the Home page has a hero; other pages leave the tones as they
                // are. Visible side effect, disclosed: the hero title/logo/
                // description/dots jump to Verity as the menu opens.
                var home = ContentFrame.Content as HomePage;
                if (home != null) home.PinHeroToReferenceSlide();
                AnimateSidebar(136, true);
            }
            else
            {
                if (_sidebarState != SidebarState.Expanded) return;
                _sidebarState = SidebarState.Collapsed;
                AnimateSidebar(48, false);
            }
        }

        private void ExpandSidebar()
        {
            SetSidebarExpanded(true);
        }

        private void CollapseSidebar()
        {
            SetSidebarExpanded(false);
        }

        /// <summary>
        /// All sidebar hide/show transitions go through here. Hides the strip
        /// while a full-screen page (details/catalog/player/streams) is open. The strip
        /// is an overlay over the content frame (the frame spans both shell
        /// columns), so Visibility.Collapsed cannot move or resize the content —
        /// the home rows and hero keep their alignment when the strip comes back.
        /// Collapsed elements leave the focus order too, so no hidden button can
        /// ever take focus. If the strip was expanded, it snaps to the collapsed
        /// end state first (48 epx, labels hidden), so the restore lands in the
        /// normal state.
        /// </summary>
        private void SetSidebarHidden(bool hidden)
        {
            if (hidden)
            {
                if (_sidebarState == SidebarState.Hidden) return;
                _sidebarState = SidebarState.Hidden;
                if (_sidebarStoryboard != null)
                {
                    _sidebarStoryboard.Stop();
                    _sidebarStoryboard = null;
                }
                SidebarBorder.Width = 48;
                foreach (var label in new UIElement[] { HomeLabel, SearchLabel, LibraryLabel, SettingsLabel, ProfileLabel, SidebarLogoWordmark })
                {
                    label.Opacity = 0;
                }
                SidebarLogoIcon.Opacity = 1;
                SidebarLogoWordmark.Visibility = Visibility.Collapsed;
                SidebarLogoIcon.Visibility = Visibility.Visible;
                SidebarBorder.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (_sidebarState != SidebarState.Hidden) return;
                _sidebarState = SidebarState.Collapsed;
                SidebarBorder.Visibility = Visibility.Visible;
            }
        }

        private void HideSidebar()
        {
            SetSidebarHidden(true);
        }

        private void ShowSidebar()
        {
            SetSidebarHidden(false);
        }

        private void AnimateSidebar(double targetWidth, bool labelsVisible)
        {
            // Both logo elements must be Visible while either fades; the Completed
            // handler pins Visibility with Opacity, so the wordmark can never paint
            // outside the strip while collapsed (its 69.4 epx lockup overflows the
            // 32 epx cell and build 232 showed its triangle left of the divider).
            SidebarLogoIcon.Visibility = Visibility.Visible;
            if (labelsVisible)
            {
                SidebarLogoWordmark.Visibility = Visibility.Visible;
            }
            else
            {
                // Collapsing: hide the wordmark at once, not at the end of the
                // 150 ms fade. It is 69.4 epx wide in a 32 epx cell, so while it
                // stays Visible it paints "nuvio" past the strip edge for the
                // whole fade-out, and a superseded animation never pins its end
                // state, so the fade alone cannot be trusted to hide it. The
                // icon still cross-fades in; only the wordmark is pinned early.
                // The Completed handler below still pins Visibility with
                // Opacity, so the end state is the same either way.
                SidebarLogoWordmark.Visibility = Visibility.Collapsed;
            }
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
                SidebarLogoWordmark.Visibility = labelsVisible ? Visibility.Visible : Visibility.Collapsed;
                SidebarLogoIcon.Visibility = labelsVisible ? Visibility.Collapsed : Visibility.Visible;
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
            // M4: pause playback when the window is hidden (e.g. the user
            // switches away); nothing else is forwarded here.
            if (!e.Visible && PlayerService != null && PlayerService.IsPlaying)
            {
                try
                {
                    PlayerService.Pause();
                }
                catch (Exception ex)
                {
                    Log.Error("Pausing playback on window hide failed", ex);
                }
            }
        }
    }
}
