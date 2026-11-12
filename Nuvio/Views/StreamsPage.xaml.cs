using System;
using System.Threading.Tasks;
using Nuvio.Diagnostics;
using Nuvio.Input;
using Nuvio.Services;
using Nuvio.ViewModels;
using Windows.ApplicationModel.Resources;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
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
    internal sealed partial class StreamsPage : Page, IArrivalFocus
    {
        private readonly StreamsViewModel _viewModel;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");
        private StreamsNavigation _navigationParam;
        private bool _backdropFrontIsA;
        private string _backdropCurrentUrl;
        private int _backdropGeneration;

        public StreamsPage()
        {
            InitializeComponent();
            NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
            var shell = MainPage.Current;
            _viewModel = new StreamsViewModel(shell.Stremio, shell.Settings, shell.Navigation);
            _viewModel.Debrid = shell.Debrid;
            DataContext = _viewModel;
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            Loaded += (s, e) => ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            GotFocus += OnGotFocus;
            _viewModel.EntriesChanged += OnEntriesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private string S(string key, string fallback)
        {
            var text = _strings.GetString(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        private void ApplyFlowDirection(bool rtl)
        {
            FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            // Page padding comes from the shared ContentPagePadding (68 start
            // / 32 end / 28 top) — do not hardcode it here. The old web
            // .streams-layout 5rem/40 start value does not apply in the native
            // shell (its sidebar is 48 + 20 bleed, not the web's width).
            LayoutGrid.Padding = FlowDirectionToThicknessConverter.ContentPagePadding(rtl);
        }

        protected override async void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            _navigationParam = e.Parameter as StreamsNavigation;
            if (_navigationParam == null)
            {
                Log.Write("Streams page opened without navigation parameter");
                return;
            }
            Log.Write("Streams page opened for " + _navigationParam.Type + "/" + _navigationParam.Id);
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
            // No initial focus here: arrival focus is router-owned (see
            // TryFocusArrival below); this method only restores focus across
            // re-renders.
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

        private async void StreamsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as StreamRowItem;
            if (row == null || row.Entry == null) return;
            StreamsViewModel.PlayResult result;
            try
            {
                result = await _viewModel.PlayAsync(row.Entry).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("Playing stream failed", ex);
                return;
            }
            if (result != null && result.Toast != null)
            {
                MainPage.Current.ShowToast(result.Toast, "error");
            }
            if (result != null && result.OfferDebridSettings)
            {
                var open = await ShowDebridSettingsDialogAsync().ConfigureAwait(true);
                if (open)
                {
                    MainPage.Current.Navigation.NavigateSettings("debrid");
                }
            }
        }

        /// <summary>Invalid-key dialog (DetailsPage pattern): primary opens debrid settings.</summary>
        private async Task<bool> ShowDebridSettingsDialogAsync()
        {
            var dialog = new ContentDialog
            {
                Title = S("StreamsDebridInvalidKeyTitle", "Debrid key invalid"),
                Content = new TextBlock
                {
                    Text = S("StreamsDebridInvalidKeyMessage", "Your debrid key is no longer valid. Open debrid settings to check it and enter a new key."),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = S("StreamsOpenDebridSettings", "Open debrid settings"),
                CloseButtonText = S("Cancel", "Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.Opened += (s, e) => dialog.Focus(FocusState.Programmatic);
            var shell = MainPage.Current;
            // Part C: remember the invoker (the focused stream row) so focus
            // returns to it when the dialog closes instead of being lost.
            var invoker = FocusManager.GetFocusedElement() as UIElement;
            shell.DialogOpen = true;
            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync().AsTask().ConfigureAwait(true);
            }
            finally
            {
                shell.DialogOpen = false;
                RestoreDialogInvoker(invoker);
            }
            return result == ContentDialogResult.Primary;
        }

        private void ManageAddonsButton_Click(object sender, RoutedEventArgs e)
        {
            MainPage.Current.Navigation.NavigateSettings("addons");
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

        private bool FocusChip(string key)
        {
            foreach (var child in FiltersPanel.Children)
            {
                var button = child as Button;
                if (button != null && (button.Tag as string) == key)
                {
                    return button.Focus(FocusState.Programmatic);
                }
            }
            return false;
        }

        private bool FocusEntry(int order)
        {
            for (var i = 0; i < StreamsListView.Items.Count; i++)
            {
                var row = StreamsListView.Items[i] as StreamRowItem;
                if (row != null && row.Entry != null && row.Entry.Order == order)
                {
                    return FocusEntryAt(i);
                }
            }
            return false;
        }

        private bool FocusEntryAt(int index)
        {
            if (index < 0 || index >= StreamsListView.Items.Count) return false;
            var container = StreamsListView.ContainerFromIndex(index) as ListViewItem;
            if (container == null)
            {
                StreamsListView.ScrollIntoView(StreamsListView.Items[index]);
                StreamsListView.UpdateLayout();
                container = StreamsListView.ContainerFromIndex(index) as ListViewItem;
            }
            return container != null && container.Focus(FocusState.Programmatic);
        }

        // ---------- Arrival focus (IArrivalFocus, router-owned) ----------

        /// <summary>Fresh-arrival target: the "all" filter chip, else the first
        /// stream row. Called by the router's SettleArrivalFocus, which retries
        /// when this returns false (chips/rows are not focusable until the
        /// first render). Bails out when focus is already inside the page, so
        /// a late settle never yanks focus away.</summary>
        public bool TryFocusArrival()
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused != null && IsDescendantOf(focused, this)) return true;
            if (FocusChip("all")) return true;
            if (StreamsListView.Items.Count > 0) return FocusEntryAt(0);
            return false;
        }

        /// <summary>Back-arrival target: the chip or stream row for a recorded
        /// key. Returns false when the key is gone so the router falls back to
        /// TryFocusArrival. Keys are "chip:" plus the filter-chip key ("all" or
        /// the addon manifest URL the ViewModel uses as the filter id) or
        /// "entry:" plus the row's Order (the ViewModel's stable per-load sort
        /// key, never a stream URL).</summary>
        public bool TryFocusKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key.StartsWith("chip:", StringComparison.Ordinal))
            {
                return FocusChip(key.Substring(5));
            }
            if (key.StartsWith("entry:", StringComparison.Ordinal))
            {
                int order;
                if (int.TryParse(key.Substring(6), out order)) return FocusEntry(order);
                return false;
            }
            return false;
        }

        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            var chipKey = FocusedChipKey();
            if (!string.IsNullOrEmpty(chipKey))
            {
                MainPage.Current?.RecordArrivalFocusKey(GetType(), "chip:" + chipKey);
                return;
            }
            var order = FocusedEntryOrder();
            if (order.HasValue)
            {
                MainPage.Current?.RecordArrivalFocusKey(GetType(), "entry:" + order.Value.ToString());
            }
        }

        /// <summary>Dialog restore (part C): back to the element that opened
        /// the dialog, else the page's arrival target.</summary>
        private void RestoreDialogInvoker(UIElement invoker)
        {
            if (invoker != null && invoker.Focus(FocusState.Programmatic)) return;
            TryFocusArrival();
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
