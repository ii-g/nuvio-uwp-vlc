using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using Nuvio.Core;
using Nuvio.Input;
using Nuvio.Models;
using Nuvio.Services;
using Nuvio.ViewModels;
using Windows.ApplicationModel.Resources;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace Nuvio.Views
{
    /// <summary>
    /// Player: full-screen playback over the shell video surfaces (port of
    /// web/js/views/player.js). The ViewModel owns state, timers, fallback and
    /// subtitles; this page owns the XAML, the ContentDialog menus, focus and
    /// the seek-bar geometry. No playback engine code lives here.
    /// </summary>
    internal sealed partial class PlayerPage : Page, IPageInputHandler, IArrivalFocus
    {
        private sealed class MenuRow
        {
            public string Label { get; set; }
            public string Description { get; set; }
            public bool IsHeader { get; set; }
            public bool IsSelected { get; set; }
            public object Value { get; set; }
        }

        private readonly PlayerViewModel _viewModel;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");
        private PlayerNavigation _navigationParam;
        private bool _pointerSeeked;

        public PlayerPage()
        {
            InitializeComponent();
            var shell = MainPage.Current;
            _viewModel = new PlayerViewModel(shell.PlayerService, shell.Stremio, shell.Progress, shell.Settings, shell.Navigation);
            DataContext = _viewModel;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.ToastRequested += OnToastRequested;
            _viewModel.FocusRequested += OnFocusRequested;
            _viewModel.FlashRequested += OnFlashRequested;
            _viewModel.DelayMenuRequested += OnDelayMenuRequested;

            ApplyStaticLabels();
            ApplySubtitleStyle();
            UpdateOsdVisibility();
            RootGrid.SizeChanged += OnRootSizeChanged;
            RootGrid.PointerMoved += (s, e) => _viewModel.Poke();
            SeekBar.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekBar_PointerPressed), true);
        }

        private string S(string key, string fallback)
        {
            var text = _strings.GetString(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        private void ApplyStaticLabels()
        {
            AutomationProperties.SetName(BackButton, S("PlayerBack", "Back"));
            AutomationProperties.SetName(RewindButton, S("PlayerBack10", "Back 10 seconds"));
            AutomationProperties.SetName(PlayPauseButton, S("PlayerPlayPause", "Play or pause"));
            AutomationProperties.SetName(ForwardButton, S("PlayerForward10", "Forward 10 seconds"));
            AutomationProperties.SetName(SubtitlesButton, S("PlayerSubtitles", "Subtitles"));
            AutomationProperties.SetName(AudioButton, S("PlayerAudio", "Audio"));
            AutomationProperties.SetName(SpeedButton, S("PlayerSpeed", "Speed"));
            AutomationProperties.SetName(AspectButton, S("PlayerAspectRatio", "Aspect ratio"));
            AutomationProperties.SetName(SourcesButton, S("PlayerSources", "Sources"));
            AutomationProperties.SetName(EpisodesButton, S("PlayerEpisodes", "Episodes"));
            AutomationProperties.SetName(NextButton, S("PlayerNextEpisode", "Next episode"));
            AutomationProperties.SetName(SeekBar, S("PlayerPlaybackPosition", "Playback position"));
            NextPlayButton.Content = S("PlayerPlayNow", "Play now");
            NextCancelButton.Content = S("Cancel", "Cancel");
            ErrorTitleText.Text = S("PlayerUnableToPlay", "Unable to play this stream");
            TryNextButton.Content = S("PlayerTryNextSource", "Try next source");
            RetryButton.Content = S("PlayerRetry", "Retry");
            ChooseAnotherButton.Content = S("PlayerChooseAnotherSource", "Choose another source");
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _navigationParam = e.Parameter as PlayerNavigation;
            var shell = MainPage.Current;
            if (_navigationParam == null || _navigationParam.Meta == null || _navigationParam.Stream == null)
            {
                if (shell != null) shell.Navigation.GoBackOrHome();
                return;
            }
            if (shell != null) shell.SetVideoVisible(true);
            _viewModel.Init(_navigationParam);
            UpdateOsdVisibility();
            ApplySubtitleStyle();
            // Arrival focus (fresh or back) is owned by the router's
            // SettleArrivalFocus, which calls TryFocusArrival / TryFocusKey on
            // this page — nothing focuses here.
        }

        protected override void OnNavigatedFrom(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            var shell = MainPage.Current;
            if (shell != null) shell.SetVideoVisible(false);
            _viewModel.Dispose();
        }

        // ---------- Arrival focus (IArrivalFocus, router-owned) ----------

        /// <summary>Fresh-arrival target: the Back button. Called by the
        /// router's SettleArrivalFocus, which retries when this returns false.
        /// Bails out when focus is already inside the page, so a late settle
        /// never yanks focus away.</summary>
        public bool TryFocusArrival()
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused != null && IsDescendantOf(focused, this)) return true;
            return BackButton.Focus(FocusState.Programmatic);
        }

        /// <summary>Back-arrival target: the Back button as well — the player
        /// has a single arrival target, so any key falls through to arrival.</summary>
        public bool TryFocusKey(string key)
        {
            return TryFocusArrival();
        }

        /// <summary>Dialog restore (part C): back to the element that opened
        /// the dialog, else the page's arrival target.</summary>
        private void RestoreDialogInvoker(UIElement invoker)
        {
            var control = invoker as Control;
            if (control != null && control.Focus(FocusState.Programmatic)) return;
            TryFocusArrival();
        }

        // ---------- ViewModel events ----------

        private void OnToastRequested(string message)
        {
            var shell = MainPage.Current;
            if (shell != null) shell.ShowToast(message);
        }

        private void OnFocusRequested(string hint)
        {
            switch (hint)
            {
                case "seek":
                    SeekBar.Focus(FocusState.Programmatic);
                    break;
                case "back":
                    BackButton.Focus(FocusState.Programmatic);
                    break;
                case "next":
                    NextPlayButton.Focus(FocusState.Programmatic);
                    break;
                case "error":
                    if (TryNextButton.Visibility == Visibility.Visible) TryNextButton.Focus(FocusState.Programmatic);
                    else RetryButton.Focus(FocusState.Programmatic);
                    break;
                case "play":
                default:
                    // Web: focus Play when focus is on Back, nowhere, or
                    // outside the OSD — never yank it off another OSD button.
                    var focused = FocusManager.GetFocusedElement() as DependencyObject;
                    if (focused == null || focused == BackButton || !IsDescendantOf(focused, OsdRoot))
                    {
                        PlayPauseButton.Focus(FocusState.Programmatic);
                    }
                    break;
            }
        }

        private void OnFlashRequested(string glyph)
        {
            FlashIcon.Glyph = glyph;
            FlashBorder.Opacity = 1;
            var animation = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(600) };
            Storyboard.SetTarget(animation, FlashBorder);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void OnDelayMenuRequested(object sender, EventArgs e)
        {
            var _ = OpenDelayMenuAsync();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(PlayerViewModel.ControlsVisible):
                case nameof(PlayerViewModel.Ready):
                    UpdateOsdVisibility();
                    UpdateSubtitlePosition();
                    break;
                case nameof(PlayerViewModel.Fraction):
                case nameof(PlayerViewModel.IsScrubbing):
                    UpdateSeekBar();
                    break;
                case nameof(PlayerViewModel.IsNextVisible):
                    if (_viewModel.IsNextVisible)
                    {
                        var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            if (_viewModel.IsNextVisible) NextPlayButton.Focus(FocusState.Programmatic);
                        });
                    }
                    break;
                case nameof(PlayerViewModel.HasError):
                    if (_viewModel.HasError)
                    {
                        UpdateRetryButtonStyle();
                        var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            if (!_viewModel.HasError) return;
                            if (TryNextButton.Visibility == Visibility.Visible) TryNextButton.Focus(FocusState.Programmatic);
                            else RetryButton.Focus(FocusState.Programmatic);
                        });
                    }
                    break;
                case nameof(PlayerViewModel.HasNextSource):
                    UpdateRetryButtonStyle();
                    break;
                case nameof(PlayerViewModel.SubtitleFontSize):
                case nameof(PlayerViewModel.SubtitleColorHex):
                case nameof(PlayerViewModel.SubtitleBackground):
                case nameof(PlayerViewModel.SubtitleOffsetPercent):
                    ApplySubtitleStyle();
                    break;
            }
        }

        private void UpdateRetryButtonStyle()
        {
            // Web: Retry is primary when there is no next source, secondary otherwise.
            RetryButton.Style = (Style)Resources[_viewModel.HasNextSource
                ? "PlayerSecondaryButtonStyle"
                : "PlayerPrimaryButtonStyle"];
        }

        // ---------- OSD / subtitle geometry ----------

        private void UpdateOsdVisibility()
        {
            // Web: the OSD is shown while loading (top only, no scrim, bottom
            // hidden) and while the controls are visible; otherwise opacity 0.
            var showOsd = _viewModel.ControlsVisible || !_viewModel.Ready;
            OsdRoot.Opacity = showOsd ? 1 : 0;
            OsdRoot.IsHitTestVisible = showOsd;
            OsdTopScrim.Visibility = _viewModel.Ready ? Visibility.Visible : Visibility.Collapsed;
            OsdBottom.Visibility = _viewModel.Ready ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSeekBar();
            UpdateSubtitlePosition();
        }

        private void UpdateSeekBar()
        {
            var width = SeekBarArea.ActualWidth;
            if (width <= 0) return;
            var fillWidth = width * _viewModel.Fraction;
            SeekFill.Width = fillWidth;
            SeekThumb.Margin = new Thickness(fillWidth - 5, 0, 0, 0);
            if (_viewModel.IsScrubbing)
            {
                SeekPreview.Visibility = Visibility.Visible;
                var previewWidth = SeekPreview.ActualWidth > 0 ? SeekPreview.ActualWidth : 48;
                var left = Math.Max(0, Math.Min(width - previewWidth, fillWidth - previewWidth / 2));
                SeekPreview.Margin = new Thickness(left, -30, 0, 0);
            }
            else
            {
                SeekPreview.Visibility = Visibility.Collapsed;
            }
        }

        private void ApplySubtitleStyle()
        {
            SubtitleTextBlock.FontSize = _viewModel.SubtitleFontSize;
            SubtitleTextBlock.LineHeight = _viewModel.SubtitleFontSize * 1.25;
            SubtitleTextBlock.Foreground = new SolidColorBrush(ParseColor(_viewModel.SubtitleColorHex));
            if (_viewModel.SubtitleBackground == "box")
            {
                // Web .player-subs[data-background="box"]: dark box + padding.
                SubtitleBackgroundBorder.Background = new SolidColorBrush(Color.FromArgb(0xB8, 0, 0, 0));
                SubtitleBackgroundBorder.Padding = new Thickness(6, 1, 6, 1);
                Shared.TextGlow.SetIsEnabled(SubtitleTextBlock, false);
            }
            else
            {
                // "none" (outline) and "shadow" both read through the dark
                // halo behind the glyphs; the app's TextGlow is that halo.
                SubtitleBackgroundBorder.Background = new SolidColorBrush(Colors.Transparent);
                SubtitleBackgroundBorder.Padding = new Thickness(0);
                Shared.TextGlow.SetIsEnabled(SubtitleTextBlock, true);
            }
            UpdateSubtitlePosition();
        }

        private void UpdateSubtitlePosition()
        {
            var width = RootGrid.ActualWidth;
            var height = RootGrid.ActualHeight;
            if (width <= 0 || height <= 0) return;
            // Web: bottom = subtitleOffset%, raised to max(offset, 13rem =
            // 104 epx) while the controls are visible.
            var bottom = Math.Max(height * _viewModel.SubtitleOffsetPercent / 100.0,
                _viewModel.ControlsVisible ? 104 : 0);
            var side = width * 0.08;
            SubtitleBackgroundBorder.Margin = new Thickness(side, 0, side, bottom);
        }

        private static Color ParseColor(string value)
        {
            if (string.IsNullOrEmpty(value)) return Colors.White;
            switch (value.Trim().ToLowerInvariant())
            {
                case "white": return Colors.White;
                case "yellow": return Colors.Yellow;
                case "cyan": return Colors.Cyan;
                case "green": return Colors.Green;
                case "red": return Colors.Red;
                case "black": return Colors.Black;
            }
            var hex = value.Trim().TrimStart('#');
            try
            {
                if (hex.Length == 3)
                {
                    hex = new string(new[]
                    {
                        hex[0], hex[0], hex[1], hex[1], hex[2], hex[2],
                    });
                }
                if (hex.Length == 6)
                {
                    return Color.FromArgb(0xFF,
                        byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                }
                if (hex.Length == 8)
                {
                    return Color.FromArgb(
                        byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                }
            }
            catch (Exception)
            {
                // Fall through to white below.
            }
            return Colors.White;
        }

        // ---------- Input (port of player.js onInput/onBack) ----------

        public bool HandleAction(string action, bool repeat)
        {
            // Error box visible: focus moves natively inside it; back falls
            // through to the router (which leaves the player, like web).
            if (_viewModel.HasError) return false;
            if (_viewModel.IsNextVisible)
            {
                if (action == "back")
                {
                    _viewModel.CancelNext(true);
                    return true;
                }
                return false;
            }
            switch (action)
            {
                case "back":
                    return HandleBack();
                case "x":
                case "playpause":
                    _viewModel.TogglePause();
                    _viewModel.Poke();
                    return true;
                case "lt":
                    _viewModel.SeekBy(-30);
                    _viewModel.ShowControls("seek");
                    return true;
                case "rt":
                    _viewModel.SeekBy(30);
                    _viewModel.ShowControls("seek");
                    return true;
                case "rb":
                    if (_viewModel.IsSeries)
                    {
                        var _ = _viewModel.PlayNextAsync();
                        return true;
                    }
                    return false;
                case "y":
                    var __ = OpenSubtitleMenuAsync();
                    return true;
                case "menu":
                case "view":
                    var ___ = OpenAudioMenuAsync();
                    return true;
                case "lb":
                    return false;
            }
            if (!_viewModel.ControlsVisible)
            {
                switch (action)
                {
                    case "left":
                        _viewModel.ShowControls("seek");
                        _viewModel.Scrub(-1, repeat);
                        return true;
                    case "right":
                        _viewModel.ShowControls("seek");
                        _viewModel.Scrub(1, repeat);
                        return true;
                    case "select":
                        _viewModel.TogglePause();
                        _viewModel.ShowControls("play");
                        return true;
                    case "up":
                    case "down":
                        _viewModel.ShowControls("play");
                        return true;
                }
                return false;
            }
            _viewModel.Poke();
            var focused = FocusManager.GetFocusedElement();
            if (focused == SeekBar && (action == "left" || action == "right"))
            {
                _viewModel.Scrub(action == "left" ? -1 : 1, repeat);
                return true;
            }
            if (focused == SeekBar && action == "select")
            {
                if (_viewModel.HasScrubTarget) _viewModel.CommitScrub();
                else _viewModel.TogglePause();
                return true;
            }
            return false;
        }

        private bool HandleBack()
        {
            if (_viewModel.HasScrubTarget)
            {
                _viewModel.CancelScrub();
                return true;
            }
            if (_viewModel.ControlsVisible && _viewModel.Ready && _viewModel.PlayState == "playing")
            {
                _viewModel.HideControls();
                return true;
            }
            _viewModel.Exit();
            return true;
        }

        // ---------- OSD buttons (web osdButton: action, then poke) ----------

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.Exit();
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.TogglePause();
            _viewModel.Poke();
        }

        private void RewindButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.SeekBy(-10);
            _viewModel.Poke();
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.SeekBy(10);
            _viewModel.Poke();
        }

        private void SubtitlesButton_Click(object sender, RoutedEventArgs e)
        {
            var _ = OpenSubtitleMenuAsync();
        }

        private void AudioButton_Click(object sender, RoutedEventArgs e)
        {
            var _ = OpenAudioMenuAsync();
        }

        private void SpeedButton_Click(object sender, RoutedEventArgs e)
        {
            var _ = OpenSpeedMenuAsync();
        }

        private void AspectButton_Click(object sender, RoutedEventArgs e)
        {
            var _ = OpenAspectMenuAsync();
        }

        private void SourcesButton_Click(object sender, RoutedEventArgs e)
        {
            var _ = OpenSourcesMenuAsync();
        }

        private void EpisodesButton_Click(object sender, RoutedEventArgs e)
        {
            var _ = OpenEpisodesMenuAsync();
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            var _ = _viewModel.PlayNextAsync();
        }

        private void NextPlayButton_Click(object sender, RoutedEventArgs e)
        {
            var _ = _viewModel.PlayNextAsync();
        }

        private void NextCancelButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.CancelNext(true);
        }

        private void TryNextButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.TryNextFromError();
        }

        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.RetryFromError();
        }

        private void ChooseAnotherButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.Exit();
        }

        // ---------- Seek bar ----------

        private void SeekBar_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_viewModel.Duration <= 0) return;
            var width = SeekBarArea.ActualWidth;
            if (width <= 0) return;
            var point = e.GetCurrentPoint(SeekBarArea).Position;
            var fraction = Math.Max(0, Math.Min(1, point.X / width));
            _pointerSeeked = true;
            _viewModel.SeekTo(fraction * _viewModel.Duration);
            e.Handled = true;
        }

        private void SeekBar_Click(object sender, RoutedEventArgs e)
        {
            // A pointer press already seeked; only a keyboard/gamepad
            // activation reaches here (commit a scrub, else toggle pause).
            if (_pointerSeeked)
            {
                _pointerSeeked = false;
                return;
            }
            if (_viewModel.HasScrubTarget) _viewModel.CommitScrub();
            else _viewModel.TogglePause();
        }

        private void SeekBar_GotFocus(object sender, RoutedEventArgs e)
        {
            // Web .osd-seek.focused: taller track, bigger thumb.
            SeekTrack.Height = 4;
            SeekThumbScale.ScaleX = 1.3;
            SeekThumbScale.ScaleY = 1.3;
        }

        private void SeekBar_LostFocus(object sender, RoutedEventArgs e)
        {
            SeekTrack.Height = 3;
            SeekThumbScale.ScaleX = 1;
            SeekThumbScale.ScaleY = 1;
        }

        // ---------- Menus (ContentDialogs from ViewModel data) ----------

        private async Task<object> ShowMenuDialogAsync(string title, List<MenuRow> rows)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                CloseButtonText = S("Cancel", "Cancel"),
            };
            var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
            object choice = null;
            Button firstButton = null;
            foreach (var row in rows)
            {
                if (row.IsHeader)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = row.Label,
                        FontSize = 9,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                        Margin = new Thickness(0, 8, 0, 0),
                    });
                    continue;
                }
                var content = new StackPanel { Spacing = 2 };
                content.Children.Add(new TextBlock
                {
                    Text = (row.IsSelected ? "✓ " : string.Empty) + row.Label,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White),
                    TextWrapping = TextWrapping.Wrap,
                });
                if (!string.IsNullOrEmpty(row.Description))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = row.Description,
                        FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                var button = new Button
                {
                    Content = content,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    MinHeight = 56,
                };
                var captured = row;
                button.Click += (s, e) => { choice = captured.Value; dialog.Hide(); };
                if (firstButton == null) firstButton = button;
                panel.Children.Add(button);
            }
            dialog.Content = panel;
            if (firstButton != null)
            {
                var first = firstButton;
                dialog.Opened += (s, e) => first.Focus(FocusState.Programmatic);
            }
            var shell = MainPage.Current;
            // Part C: remember the invoker (the OSD button that opened the
            // menu) so focus returns to it when the menu closes.
            var invoker = FocusManager.GetFocusedElement() as UIElement;
            shell.DialogOpen = true;
            _viewModel.MenuOpen = true;
            try
            {
                await dialog.ShowAsync().AsTask().ConfigureAwait(true);
            }
            finally
            {
                shell.DialogOpen = false;
                _viewModel.MenuOpen = false;
                RestoreDialogInvoker(invoker);
            }
            return choice;
        }

        private async Task OpenSubtitleMenuAsync()
        {
            await _viewModel.LoadAddonSubtitlesAsync(true).ConfigureAwait(true);
            if (_viewModel.Closed) return;
            var rows = new List<MenuRow>();
            foreach (var item in _viewModel.GetSubtitleMenuItems())
            {
                rows.Add(new MenuRow
                {
                    Label = item.Label,
                    Description = item.Description,
                    IsHeader = item.Kind == SubtitleMenuKind.Header,
                    IsSelected = item.IsSelected,
                    Value = item,
                });
            }
            var choice = await ShowMenuDialogAsync(S("PlayerSubtitles", "Subtitles"), rows).ConfigureAwait(true);
            var selected = choice as SubtitleMenuItem;
            if (selected != null) _viewModel.ChooseSubtitle(selected);
        }

        private async Task OpenDelayMenuAsync()
        {
            var rows = new List<MenuRow>();
            foreach (var item in _viewModel.GetDelayMenuItems())
            {
                rows.Add(new MenuRow
                {
                    Label = item.Label,
                    Description = item.Description,
                    Value = item,
                });
            }
            var choice = await ShowMenuDialogAsync(S("PlayerSubtitleDelayMenu", "Subtitle delay"), rows).ConfigureAwait(true);
            var selected = choice as DelayMenuItem;
            if (selected != null) _viewModel.ChooseDelay(selected.Step);
        }

        private async Task OpenAudioMenuAsync()
        {
            var items = _viewModel.GetAudioMenuItems();
            if (items.Count == 0)
            {
                _viewModel.RequestToast(S("PlayerNoAudioTracks", "No audio tracks found yet"));
                return;
            }
            var rows = new List<MenuRow>();
            foreach (var item in items)
            {
                rows.Add(new MenuRow
                {
                    Label = item.Label,
                    Description = item.Description,
                    IsSelected = item.IsSelected,
                    Value = item,
                });
            }
            var choice = await ShowMenuDialogAsync(S("PlayerAudio", "Audio"), rows).ConfigureAwait(true);
            var selected = choice as AudioMenuItem;
            if (selected != null) _viewModel.ChooseAudio(selected.TrackId);
        }

        private async Task OpenSpeedMenuAsync()
        {
            var rows = new List<MenuRow>();
            foreach (var item in _viewModel.GetSpeedMenuItems())
            {
                rows.Add(new MenuRow
                {
                    Label = item.Label,
                    IsSelected = item.IsSelected,
                    Value = item,
                });
            }
            var choice = await ShowMenuDialogAsync(S("PlayerPlaybackSpeed", "Playback speed"), rows).ConfigureAwait(true);
            var selected = choice as SpeedMenuItem;
            if (selected != null) _viewModel.ChooseSpeed(selected.Value);
        }

        private async Task OpenAspectMenuAsync()
        {
            var rows = new List<MenuRow>();
            foreach (var item in _viewModel.GetAspectMenuItems())
            {
                rows.Add(new MenuRow
                {
                    Label = item.Label,
                    IsSelected = item.IsSelected,
                    Value = item,
                });
            }
            var choice = await ShowMenuDialogAsync(S("PlayerAspectRatio", "Aspect ratio"), rows).ConfigureAwait(true);
            var selected = choice as AspectMenuItem;
            if (selected != null) _viewModel.ChooseAspect(selected.Value);
        }

        private async Task OpenSourcesMenuAsync()
        {
            var rows = new List<MenuRow>();
            foreach (var item in _viewModel.GetSourcesMenuItems())
            {
                var label = item.Label;
                if (!string.IsNullOrEmpty(item.Badge)) label = label + " · " + item.Badge;
                rows.Add(new MenuRow
                {
                    Label = label,
                    Description = item.Description,
                    IsSelected = item.IsSelected,
                    Value = item,
                });
            }
            var choice = await ShowMenuDialogAsync(S("PlayerSources", "Sources"), rows).ConfigureAwait(true);
            var selected = choice as SourceMenuItem;
            if (selected != null) _viewModel.ChooseSource(selected.Index);
        }

        private async Task OpenEpisodesMenuAsync()
        {
            var items = await _viewModel.GetEpisodesMenuItemsAsync().ConfigureAwait(true);
            if (items == null) return;
            var rows = new List<MenuRow>();
            foreach (var item in items)
            {
                rows.Add(new MenuRow
                {
                    Label = item.Label,
                    Description = item.Description,
                    IsSelected = item.IsSelected,
                    Value = item,
                });
            }
            var choice = await ShowMenuDialogAsync(_viewModel.EpisodesMenuTitle, rows).ConfigureAwait(true);
            var selected = choice as EpisodeMenuItem;
            if (selected != null) _viewModel.ChooseEpisode(selected.Video);
        }

        private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
        {
            for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node == ancestor) return true;
            }
            return false;
        }
    }
}
