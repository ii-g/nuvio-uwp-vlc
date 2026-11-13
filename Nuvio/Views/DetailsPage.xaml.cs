using System;
using System.Threading.Tasks;
using Nuvio.Core;
using Nuvio.Diagnostics;
using Nuvio.Input;
using Nuvio.Models;
using Nuvio.Services;
using Nuvio.ViewModels;
using Windows.ApplicationModel.Resources;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;

namespace Nuvio.Views
{
    /// <summary>
    /// Details: hero, actions, episodes, credits, TMDB cast + More Like This.
    /// Ported from web/js/views/details.js. No playback here: Play routes to the
    /// Streams page (M4) through Navigation.PlayVideo.
    /// </summary>
    internal sealed partial class DetailsPage : FocusablePage, IPageInputHandler
    {
        private readonly DetailsViewModel _viewModel;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");
        private DetailsNavigation _navigationParam;
        private bool _backdropFrontIsA;
        private string _backdropCurrentUrl;
        private int _backdropGeneration;
        private bool _fadeIsRow;
        private string _lastFocusedVideoId;

        public DetailsPage()
        {
            InitializeComponent();
            var shell = MainPage.Current;
            _viewModel = new DetailsViewModel(shell.Stremio, shell.Progress, shell.Library, shell.Navigation, shell.Settings);
            DataContext = _viewModel;
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            Loaded += (s, e) => ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.LoadStarted += OnLoadStarted;
            _viewModel.LoadCompleted += OnLoadCompleted;
            _viewModel.TmdbExtrasLoaded += OnTmdbExtrasLoaded;
            GotFocus += OnGotFocus;
        }

        private void ApplyFlowDirection(bool rtl)
        {
            FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }

        protected override async void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            _navigationParam = e.Parameter as DetailsNavigation;
            var shell = MainPage.Current;
            shell.Progress.ProgressChanged += OnProgressChanged;
            shell.Progress.WatchedChanged += OnProgressChanged;
            shell.Library.LibraryChanged += OnLibraryChanged;
            if (_navigationParam == null)
            {
                // No title to load: the arrival target (Play button) exists
                // in XAML, so the content is ready now.
                RaiseContentReady();
                return;
            }
            await _viewModel.LoadAsync(_navigationParam.Type, _navigationParam.Id, _navigationParam.PreviewMeta).ConfigureAwait(true);
        }

        protected override void OnNavigatedFrom(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            var shell = MainPage.Current;
            if (shell != null)
            {
                shell.Progress.ProgressChanged -= OnProgressChanged;
                shell.Progress.WatchedChanged -= OnProgressChanged;
                shell.Library.LibraryChanged -= OnLibraryChanged;
            }
        }

        private string S(string key, string fallback)
        {
            var text = _strings.GetString(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DetailsViewModel.Seasons) ||
                e.PropertyName == nameof(DetailsViewModel.SelectedSeason))
            {
                RebuildSeasonChips();
            }
        }

        private void OnLoadStarted(object sender, EventArgs e)
        {
            // Preview artwork goes up at once, so the hero never sits on a blank
            // backdrop while the addons answer.
            var preview = _viewModel.PreviewMeta;
            if (preview != null) ShowBackdrop(preview.Background ?? preview.Poster);
            SetBackdropFadeRowState(false);
        }

        private void OnLoadCompleted(object sender, EventArgs e)
        {
            var meta = _viewModel.Meta;
            if (meta != null) ShowBackdrop(meta.Background);
            // First render (episodes/meta rendered): the arrival target
            // (Play button) exists now.
            RaiseContentReady();
            // Arrival focus is owned by the router's SettleArrivalFocus, which
            // calls TryFocusArrival / TryFocusKey — nothing focuses here.
        }

        private void OnTmdbExtrasLoaded(object sender, EventArgs e)
        {
            // TMDB may have filled a background/logo the addons did not have.
            var meta = _viewModel.Meta;
            if (meta != null) ShowBackdrop(meta.Background);
        }

        private void OnProgressChanged(object sender, EventArgs e)
        {
            _viewModel.RefreshDynamicState();
            RestoreEpisodeFocus();
        }

        private void OnLibraryChanged(object sender, EventArgs e)
        {
            _viewModel.RefreshDynamicState();
        }

        // ---------- Backdrop ----------

        private static string UpgradeBackdropUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            url = url.Replace("images.metahub.space/background/medium/",
                              "images.metahub.space/background/large/");
            url = url.Replace("image.tmdb.org/t/p/w780/", "image.tmdb.org/t/p/w1920/")
                     .Replace("image.tmdb.org/t/p/w1280/", "image.tmdb.org/t/p/w1920/");
            return url;
        }

        private void ShowBackdrop(string url)
        {
            url = UpgradeBackdropUrl(url);
            if (string.IsNullOrEmpty(url) || url == _backdropCurrentUrl) return;
            _backdropCurrentUrl = url;
            var generation = ++_backdropGeneration;
            Image back = _backdropFrontIsA ? BackdropB : BackdropA;
            Image front = _backdropFrontIsA ? BackdropA : BackdropB;
            BitmapImage image;
            try
            {
                image = new BitmapImage();
                image.DecodePixelWidth = 1920;
                image.UriSource = new Uri(url);
            }
            catch (Exception ex)
            {
                Log.Error("Details backdrop failed", ex);
                return;
            }
            back.Opacity = 0;
            back.Source = image;
            // Swap only once the new art has actually loaded, so a bad URL keeps
            // the old backdrop; a newer request cancels this one.
            back.ImageOpened += (s, e) =>
            {
                if (generation != _backdropGeneration) return;
                AnimateOpacity(back, 0, 1);
                AnimateOpacity(front, 1, 0);
                _backdropFrontIsA = !_backdropFrontIsA;
            };
        }

        private static void AnimateOpacity(Image image, double from, double to)
        {
            var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(600) };
            Storyboard.SetTarget(animation, image);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        // Dynamic backdrop fade (variant D), same as HomePage: hero focus keeps
        // the image visible longer (opaque to 45%, gone by 90%); focus on any
        // row below fades it earlier (opaque to 25%, gone by 70%). 200ms.
        private void SetBackdropFadeRowState(bool isRow)
        {
            if (_fadeIsRow == isRow) return;
            _fadeIsRow = isRow;
            BackdropFade?.SetRowState(isRow);
        }

        // ---------- Arrival focus (IArrivalFocus, router-owned) ----------

        /// <summary>Fresh-arrival target: the Play button; on a load error
        /// the hero (and Play) is collapsed with the content, so the error
        /// panel's Retry button is the arrival target instead. Called by
        /// the router's SettleArrivalFocus, which retries when this returns
        /// false.</summary>
        public override bool TryFocusArrival()
        {
            if (PlayButton.Focus(FocusState.Programmatic)) return true;
            return RetryButton.Focus(FocusState.Programmatic);
        }

        /// <summary>Back-arrival target: the episode for a recorded "video:" key.
        /// Returns false when the episode is gone (season changed, list
        /// refreshed without it) so the router falls back to TryFocusArrival.</summary>
        public override bool TryFocusKey(string key)
        {
            if (string.IsNullOrEmpty(key) || !key.StartsWith("video:", StringComparison.Ordinal)) return false;
            return RestoreEpisodeFocus(key.Substring(6));
        }

        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return;
            var item = FindAncestorGridViewItem(focused);
            if (item != null)
            {
                var episode = item.Content as EpisodeCardItem;
                if (episode != null)
                {
                    _lastFocusedVideoId = episode.Video != null ? episode.Video.Id : null;
                    // Record the focused episode into the shell registry — the
                    // page instance is destroyed on Back, so the registry is
                    // what the router's TryFocusKey call reads back. The base
                    // GotFocus hook cannot record this: episode cards are
                    // GridViewItems, not Buttons with Tags (season chips carry
                    // an int Tag, hero buttons a glyph Tag — both skipped).
                    if (_lastFocusedVideoId != null)
                    {
                        MainPage.Current?.RecordArrivalFocusKey(EntryKey, "video:" + _lastFocusedVideoId);
                    }
                    SetBackdropFadeRowState(true);
                    return;
                }
                // Cast and More Like This cards sit below the hero too.
                SetBackdropFadeRowState(true);
                return;
            }
            if (IsDescendantOf(focused, HeroSection))
            {
                SetBackdropFadeRowState(false);
            }
        }

        // ---------- Season chips ----------

        private void RebuildSeasonChips()
        {
            SeasonChipsPanel.Children.Clear();
            foreach (var season in _viewModel.Seasons)
            {
                var selected = season == _viewModel.SelectedSeason;
                var chip = new Button
                {
                    Content = SeasonLabel(season),
                    Style = (Style)Resources["DetailsChipButtonStyle"],
                    Tag = season,
                    Background = selected
                        ? new SolidColorBrush(Color.FromArgb(0xFF, 0x2F, 0x6F, 0xED))
                        : new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                    Foreground = selected
                        ? new SolidColorBrush(Colors.White)
                        : new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
                };
                chip.Click += SeasonChip_Click;
                SeasonChipsPanel.Children.Add(chip);
            }
        }

        private string SeasonLabel(int season)
        {
            if (season == 0) return S("Specials", "Specials");
            return string.Format(S("SeasonLabel", "Season {0}"), season);
        }

        private void SeasonChip_Click(object sender, RoutedEventArgs e)
        {
            var chip = sender as Button;
            if (chip == null || !(chip.Tag is int)) return;
            _viewModel.SelectSeason((int)chip.Tag);
            RebuildSeasonChips();
            FocusNextUpEpisode();
        }

        private void FocusNextUpEpisode()
        {
            if (EpisodesGridView.Items.Count == 0) return;
            var nextUpId = _viewModel.GetNextUpVideoId();
            var index = 0;
            if (nextUpId != null)
            {
                for (var i = 0; i < EpisodesGridView.Items.Count; i++)
                {
                    var episode = EpisodesGridView.Items[i] as EpisodeCardItem;
                    if (episode != null && episode.Video != null && episode.Video.Id == nextUpId)
                    {
                        index = i;
                        break;
                    }
                }
            }
            var container = EpisodesGridView.ContainerFromIndex(index) as GridViewItem;
            if (container == null)
            {
                EpisodesGridView.ScrollIntoView(EpisodesGridView.Items[index]);
                EpisodesGridView.UpdateLayout();
                container = EpisodesGridView.ContainerFromIndex(index) as GridViewItem;
            }
            if (container != null) container.Focus(FocusState.Programmatic);
        }

        private void RestoreEpisodeFocus()
        {
            RestoreEpisodeFocus(_lastFocusedVideoId);
        }

        /// <summary>Focuses the episode card for a video id. Returns false when
        /// the episode is not in the loaded list, so the caller falls back.</summary>
        private bool RestoreEpisodeFocus(string videoId)
        {
            if (videoId == null || EpisodesGridView.Items.Count == 0) return false;
            for (var i = 0; i < EpisodesGridView.Items.Count; i++)
            {
                var episode = EpisodesGridView.Items[i] as EpisodeCardItem;
                if (episode != null && episode.Video != null && episode.Video.Id == videoId)
                {
                    var container = EpisodesGridView.ContainerFromIndex(i) as GridViewItem;
                    if (container == null)
                    {
                        EpisodesGridView.ScrollIntoView(EpisodesGridView.Items[i]);
                        EpisodesGridView.UpdateLayout();
                        container = EpisodesGridView.ContainerFromIndex(i) as GridViewItem;
                    }
                    return container != null && container.Focus(FocusState.Programmatic);
                }
            }
            return false;
        }

        // ---------- Actions ----------

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            var meta = _viewModel.Meta ?? _viewModel.PreviewMeta;
            if (meta == null)
            {
                Log.Write("Play pressed on details, no meta yet - ignoring");
                return;
            }
            Log.Write("Play pressed on details, opening streams for " + meta.Type + "/" + meta.Id);
            MainPage.Current.Navigation.PlayVideo(meta, _viewModel.PlayTargetVideo, restart: !_viewModel.PlayIsResume);
        }

        private void LibraryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.Meta == null) return;
            var inLibrary = _viewModel.ToggleLibrary();
            MainPage.Current.ShowToast(S(inLibrary ? "AddedToYourLibrary" : "RemovedFromYourLibrary",
                inLibrary ? "Added to your library" : "Removed from your library"));
        }

        private void WatchedButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ToggleWatchedMovie();
        }

        private async void TrailerButton_Click(object sender, RoutedEventArgs e)
        {
            var ytId = _viewModel.TrailerId;
            if (string.IsNullOrEmpty(ytId)) return;
            await Launcher.LaunchUriAsync(new Uri("https://www.youtube.com/watch?v=" + ytId)).AsTask().ConfigureAwait(true);
        }

        private async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_navigationParam == null) return;
            await _viewModel.LoadAsync(_navigationParam.Type, _navigationParam.Id, _navigationParam.PreviewMeta).ConfigureAwait(true);
        }

        private void ErrorBackButton_Click(object sender, RoutedEventArgs e)
        {
            MainPage.Current.Navigation.GoBackOrHome();
        }

        private void EpisodesGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var episode = e.ClickedItem as EpisodeCardItem;
            var meta = _viewModel.Meta ?? _viewModel.PreviewMeta;
            if (episode == null || episode.Video == null || meta == null)
            {
                Log.Write("Episode clicked but meta or video missing - ignoring");
                return;
            }
            Log.Write("Episode clicked, opening streams for " + meta.Type + "/" + meta.Id);
            MainPage.Current.Navigation.PlayVideo(meta, episode.Video);
        }

        private async void SimilarGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var card = e.ClickedItem as CatalogCardItem;
            if (card == null || card.Meta == null) return;
            if (!await _viewModel.OpenSimilarAsync(card.Meta).ConfigureAwait(true))
            {
                MainPage.Current.ShowToast(S("CantOpenTitle", "Your addons can't open this title"));
            }
        }

        // ---------- Episode choice menu (Y / Menu) ----------

        public bool HandleAction(string action, bool repeat)
        {
            if (action != "y" && action != "menu") return false;
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return false;
            var item = FindAncestorGridViewItem(focused);
            var episode = item != null ? item.Content as EpisodeCardItem : null;
            if (episode == null || episode.Video == null) return false;
            var _ = ShowEpisodeMenuAsync(episode);
            return true;
        }

        private async Task ShowEpisodeMenuAsync(EpisodeCardItem episode)
        {
            var video = episode.Video;
            var meta = _viewModel.Meta;
            var dialog = new ContentDialog
            {
                Title = episode.Title ?? string.Empty,
                CloseButtonText = S("Cancel", "Cancel"),
            };
            var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
            string choice = null;
            var playButton = new Button { Content = S("Play", "Play"), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 56 };
            playButton.Click += (s, e) => { choice = "play"; dialog.Hide(); };
            panel.Children.Add(playButton);
            var watchedButton = new Button
            {
                Content = S(episode.IsWatched ? "MarkAsUnwatched" : "MarkAsWatched",
                    episode.IsWatched ? "Mark as unwatched" : "Mark as watched"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 56,
            };
            watchedButton.Click += (s, e) => { choice = "toggleWatched"; dialog.Hide(); };
            panel.Children.Add(watchedButton);
            if (video.Season > 0)
            {
                var previousButton = new Button { Content = S("MarkPreviousAsWatched", "Mark previous as watched"), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 56 };
                previousButton.Click += (s, e) => { choice = "previous"; dialog.Hide(); };
                panel.Children.Add(previousButton);
            }
            var seasonLabel = SeasonLabel(video.Season);
            var markSeasonButton = new Button
            {
                Content = string.Format(S(episode.IsWatched ? "MarkSeasonAsUnwatched" : "MarkSeasonAsWatched",
                    episode.IsWatched ? "Mark {0} as unwatched" : "Mark {0} as watched"), seasonLabel),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 56,
            };
            markSeasonButton.Click += (s, e) => { choice = "season"; dialog.Hide(); };
            panel.Children.Add(markSeasonButton);
            dialog.Content = panel;
            dialog.Opened += (s, e) => playButton.Focus(FocusState.Programmatic);
            var shell = MainPage.Current;
            // Dialog restore: the coordinator owns it when the flag is on
            // (it captures the invoker when DialogOpen is set below, and
            // restores focus to it when DialogOpen clears); the capture
            // below is the flag-off path only (the deferred restore in the
            // else below is skipped when the flag is on).
            var invoker = FocusManager.GetFocusedElement() as DependencyObject;
            shell.DialogOpen = true;
            try
            {
                await dialog.ShowAsync().AsTask().ConfigureAwait(true);
            }
            finally
            {
                shell.DialogOpen = false;
            }
            if (choice == "play")
            {
                // Navigates to Streams: no restore here, arrival on return
                // is the router's job.
                if (meta != null) shell.Navigation.PlayVideo(meta, video);
            }
            else
            {
                // Cancelled, or a mark-watched choice (the episode stays):
                // back to the card that opened the menu. Flag off only:
                // with the flag on the coordinator owns the restore
                // (DialogOpen = false triggers it) and this local tick
                // would race it, so it is skipped. Deferred one
                // dispatcher tick so the dialog teardown finishes first.
                var coordinatorOwnsRestore = MainPage.Current?.Coordinator != null && FocusCoordinator.UseFocusCoordinator;
                if (invoker != null && !coordinatorOwnsRestore)
                {
                    var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        var focused = FocusManager.GetFocusedElement() as DependencyObject;
                        if (focused != null && IsDescendantOf(focused, this)) return;
                        var control = invoker as Control;
                        if (control != null) control.Focus(FocusState.Programmatic);
                    });
                }
                if (choice == "toggleWatched")
                {
                    _viewModel.MarkEpisodeWatched(video, !episode.IsWatched);
                }
                else if (choice == "previous")
                {
                    _viewModel.MarkPreviousWatched(video);
                }
                else if (choice == "season")
                {
                    _viewModel.MarkSeasonWatched(video, !episode.IsWatched);
                }
            }
        }

        private static GridViewItem FindAncestorGridViewItem(DependencyObject element)
        {
            for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var item = node as GridViewItem;
                if (item != null) return item;
            }
            return null;
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
