using System;
using Nuvio.Diagnostics;
using Nuvio.Services;
using Nuvio.ViewModels;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;

namespace Nuvio.Views
{
    /// <summary>Playable row at full opacity, non-playable row at half (web .stream-item.disabled).</summary>
    internal sealed class StreamRowOpacityConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool && (bool)value ? 1.0 : 0.5;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Streams: filter chips, status lines and the stream list for one title/episode.
    /// Ported from web/js/views/streams.js. The ViewModel owns loading and state;
    /// this class renders chips/list/status imperatively so focus can be restored
    /// by chip key / entry order across re-renders, and scroll position is kept
    /// unless the filter changed. No custom input handling: the router owns the
    /// D-pad and back is the frame's GoBack.
    /// </summary>
    internal sealed partial class StreamsPage : Page
    {
        private readonly StreamsViewModel _viewModel;
        private StreamsNavigation _navigationParam;
        private bool _focusedFirst;
        private bool _initialFocusDone;
        private bool _backdropFrontIsA;
        private string _backdropCurrentUrl;
        private int _backdropGeneration;

        public StreamsPage()
        {
            InitializeComponent();
            NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
            var shell = MainPage.Current;
            _viewModel = new StreamsViewModel(shell.Stremio, shell.Settings, shell.Navigation);
            DataContext = _viewModel;
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            Loaded += (s, e) => ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            _viewModel.EntriesChanged += OnEntriesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void ApplyFlowDirection(bool rtl)
        {
            FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            // Web .streams-layout padding-inline: 5rem (40) start / 4rem (32) end,
            // 3.5rem (28) top. Thickness is physical, so the sides flip here.
            LayoutGrid.Padding = rtl ? new Thickness(32, 28, 40, 0) : new Thickness(40, 28, 32, 0);
        }

        protected override async void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            _navigationParam = e.Parameter as StreamsNavigation;
            if (_navigationParam == null) return;
            await _viewModel.Init(_navigationParam.Type, _navigationParam.Id, _navigationParam.VideoId, _navigationParam.Meta, _navigationParam.Video, _navigationParam.Restart).ConfigureAwait(true);
            ShowBackdrop(_viewModel.BackdropUrl);
            LoadStreamsFireAndForget();
        }

        protected override void OnNavigatedFrom(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _viewModel.Close();
            _viewModel.EntriesChanged -= OnEntriesChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        private async void LoadStreamsFireAndForget()
        {
            try
            {
                await _viewModel.LoadStreamsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("Loading streams failed", ex);
            }
        }

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StreamsViewModel.BackdropUrl))
            {
                ShowBackdrop(_viewModel.BackdropUrl);
            }
        }

        private void OnEntriesChanged(object sender, EventArgs e)
        {
            RenderAll(resetScroll: false);
        }

        // ---------- Render ----------

        private void RenderAll(bool resetScroll)
        {
            var focusedChipKey = FocusedChipKey();
            var focusedOrder = FocusedEntryOrder();
            var scrollViewer = FindScrollViewer(StreamsListView);
            var scrollOffset = scrollViewer != null ? scrollViewer.VerticalOffset : 0;

            RebuildChips();
            StatusItems.ItemsSource = _viewModel.StatusLines;
            StreamsListView.ItemsSource = _viewModel.VisibleRows;

            StreamsListView.UpdateLayout();
            if (scrollViewer != null)
            {
                scrollViewer.ChangeView(null, resetScroll ? 0 : scrollOffset, null, true);
            }

            if (!string.IsNullOrEmpty(focusedChipKey))
            {
                FocusChip(focusedChipKey);
            }
            else if (focusedOrder.HasValue)
            {
                FocusEntry(focusedOrder.Value);
            }
            else if (!_initialFocusDone)
            {
                // Web focuses the filter row on entry; the first results then
                // move focus to the first row (see below).
                _initialFocusDone = true;
                var focused = FocusManager.GetFocusedElement() as DependencyObject;
                if (focused == null || !IsDescendantOf(focused, this))
                {
                    FocusChip("all");
                }
            }

            if (!_focusedFirst && StreamsListView.Items.Count > 0)
            {
                _focusedFirst = true;
                // Land on the first result once some arrive, unless the user
                // already moved focus into the page (a filter chip still counts
                // as "not yet placed", as on the web).
                var focused = FocusManager.GetFocusedElement() as DependencyObject;
                if (focused == null || !IsDescendantOf(focused, this) || IsDescendantOf(focused, FiltersPanel))
                {
                    FocusEntryAt(0);
                }
            }
        }

        private void RebuildChips()
        {
            FiltersPanel.Children.Clear();
            foreach (var chip in _viewModel.FilterChips)
            {
                var selected = chip.Selected;
                var button = new Button
                {
                    Content = chip.Label,
                    Style = (Style)Resources["StreamsChipButtonStyle"],
                    Tag = chip.Key,
                    Background = selected
                        ? new SolidColorBrush(Color.FromArgb(0xFF, 0x2F, 0x6F, 0xED))
                        : new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                    Foreground = selected
                        ? new SolidColorBrush(Colors.White)
                        : new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
                };
                button.Click += FilterChip_Click;
                FiltersPanel.Children.Add(button);
            }
        }

        private void FilterChip_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;
            _viewModel.SetFilter(button.Tag as string);
            // The filter change re-renders through EntriesChanged; the scroll
            // position resets, unlike a plain arrival of results.
            var scrollViewer = FindScrollViewer(StreamsListView);
            if (scrollViewer != null) scrollViewer.ChangeView(null, 0, null, true);
        }

        private void StreamsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as StreamRowItem;
            if (row == null || row.Entry == null) return;
            var toast = _viewModel.Play(row.Entry);
            if (toast != null)
            {
                MainPage.Current.ShowToast(toast, "error");
            }
        }

        private void ManageAddonsButton_Click(object sender, RoutedEventArgs e)
        {
            MainPage.Current.Navigation.NavigateRoot("settings");
        }

        // ---------- Focus restore ----------

        private string FocusedChipKey()
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null || !IsDescendantOf(focused, FiltersPanel)) return null;
            for (var node = focused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var button = node as Button;
                if (button != null) return button.Tag as string;
            }
            return null;
        }

        private int? FocusedEntryOrder()
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null || !IsDescendantOf(focused, StreamsListView)) return null;
            for (var node = focused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var item = node as ListViewItem;
                if (item == null) continue;
                var row = item.Content as StreamRowItem;
                if (row != null && row.Entry != null) return row.Entry.Order;
                return null;
            }
            return null;
        }

        private void FocusChip(string key)
        {
            foreach (var child in FiltersPanel.Children)
            {
                var button = child as Button;
                if (button != null && (button.Tag as string) == key)
                {
                    button.Focus(FocusState.Programmatic);
                    return;
                }
            }
        }

        private void FocusEntry(int order)
        {
            for (var i = 0; i < StreamsListView.Items.Count; i++)
            {
                var row = StreamsListView.Items[i] as StreamRowItem;
                if (row != null && row.Entry != null && row.Entry.Order == order)
                {
                    FocusEntryAt(i);
                    return;
                }
            }
        }

        private void FocusEntryAt(int index)
        {
            var container = StreamsListView.ContainerFromIndex(index) as ListViewItem;
            if (container == null)
            {
                StreamsListView.ScrollIntoView(StreamsListView.Items[index]);
                StreamsListView.UpdateLayout();
                container = StreamsListView.ContainerFromIndex(index) as ListViewItem;
            }
            if (container != null) container.Focus(FocusState.Programmatic);
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var viewer = child as ScrollViewer;
                if (viewer != null) return viewer;
                viewer = FindScrollViewer(child);
                if (viewer != null) return viewer;
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
                Log.Error("Streams backdrop failed", ex);
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
            BackdropFade?.SetRowState(false);
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
    }
}
