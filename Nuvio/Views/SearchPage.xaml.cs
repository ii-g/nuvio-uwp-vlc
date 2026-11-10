using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Nuvio.Input;
using Nuvio.Models;
using Nuvio.Services;
using Nuvio.ViewModels;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Nuvio.Views
{
    /// <summary>
    /// Search: native TextBox, debounced run() over one row per searchCatalogs(),
    /// recent searches. Ported from views/search.js. NavigationCacheMode is Required
    /// so the query, results and focus survive back-navigation.
    /// </summary>
    internal sealed partial class SearchPage : Page, IPageInputHandler
    {
        private const int DebounceMs = 700;

        private readonly SearchViewModel _viewModel;
        private readonly FocusScrollHelper _scrollHelper = new FocusScrollHelper();
        private readonly DispatcherTimer _debounceTimer;
        private bool _focusFirstResult;
        private Storyboard _heroFadeStoryboard;
        private Storyboard _resultsFadeStoryboard;
        // On-screen keyboard row (Apple TV style), in layout order: keyboard key,
        // SPACE, letters a to z, backspace. The D-pad moves between these keys by
        // index, so the row and the navigation share this one list.
        private readonly List<Button> _keyboardKeys = new List<Button>();
        private Button _lastKeyButton;
        private Button _firstLetterKey;

        // Dynamic background (Apple TV style): the focused result's poster art,
        // decoded at 48x72 and box-blurred, cross-faded between two image layers
        // under a dark gradient. The art is cached by poster URL, so a poster
        // already shown costs nothing on refocus; a newer focus wins over a
        // still-loading older one via _backgroundGeneration.
        private static readonly HttpClient BackgroundHttpClient = CreateBackgroundHttpClient();
        private readonly Dictionary<string, WriteableBitmap> _backgroundArtCache = new Dictionary<string, WriteableBitmap>();
        private Image _backgroundFront;
        private Storyboard _backgroundStoryboard;
        private string _backgroundUrl;
        private int _backgroundGeneration;
        private const int BackgroundDecodeWidth = 48;
        private const int BackgroundDecodeHeight = 72;
        private const int BackgroundBlurRadius = 8;
        private const int BackgroundCacheLimit = 24;
        private const int BackgroundFadeMs = 250;

        private static HttpClient CreateBackgroundHttpClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            return client;
        }

        public SearchPage()
        {
            NavigationCacheMode = NavigationCacheMode.Required;
            InitializeComponent();
            BuildKeyboardRow();
            var shell = MainPage.Current;
            _viewModel = new SearchViewModel(shell.Stremio, shell.Progress, shell.Store, shell.Navigation);
            DataContext = _viewModel;
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            Loaded += (s, e) => ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
            _debounceTimer.Tick += OnDebounceTick;
            _viewModel.SearchCompleted += OnSearchCompleted;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SearchBox.TextChanged += OnSearchBoxTextChanged;
            SearchBox.KeyDown += OnSearchBoxKeyDown;
            SearchBox.GotFocus += OnSearchBoxGotFocus;
            SearchBox.LostFocus += OnSearchBoxLostFocus;
            GotFocus += OnGotFocus;
        }

        /// <summary>Builds the on-screen keyboard row (Apple TV style): a keyboard
        /// key, SPACE, the Latin letters a to z, and backspace. Letters are 22 epx
        /// wide on a 3 epx gap (26 letters = 647 epx), SPACE is 48 and the two icon
        /// keys are 30, so the whole row fits the ~860 epx content width. The keys
        /// are built here rather than in XAML so the row and the D-pad navigation
        /// share the one ordered _keyboardKeys list.</summary>
        private void BuildKeyboardRow()
        {
            AddKey("keyboard", new FontIcon { Glyph = "\uE636", FontSize = 14 }, 30);
            AddKey("space", "SPACE", 48);
            for (var c = 'a'; c <= 'z'; c++)
            {
                var key = AddKey(c.ToString(), c.ToString(), 22);
                if (c == 'a') _firstLetterKey = key;
            }
            AddKey("backspace", new FontIcon { Glyph = "\uE750", FontSize = 12 }, 30);
        }

        private Button AddKey(string tag, object content, double width)
        {
            // Dark surface-2 keys, white text, no system focus visuals: the focus
            // ring is the 2 epx accent border set by KeyButton_GotFocus, matching
            // the search bar's single accent ring.
            var button = new Button
            {
                Tag = tag,
                Content = content,
                Width = width,
                MinHeight = 30,
                Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 3, 0),
                UseSystemFocusVisuals = false,
            };
            button.Click += KeyButton_Click;
            button.GotFocus += KeyButton_GotFocus;
            button.LostFocus += KeyButton_LostFocus;
            _keyboardKeys.Add(button);
            KeyboardRow.Children.Add(button);
            return button;
        }

        /// <summary>Every key appends to the search box (backspace deletes one
        /// char), so the 700 ms debounce runs the search exactly as if typed.
        /// Focus stays on the key, so the user can keep typing. The keyboard key
        /// focuses the box and asks the InputPane to show the Xbox system keyboard
        /// (Arabic, numbers, symbols) for anything the Latin row cannot type.</summary>
        private void KeyButton_Click(object sender, RoutedEventArgs e)
        {
            var tag = (sender as Button)?.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;
            if (tag == "keyboard")
            {
                SearchBox.Focus(FocusState.Programmatic);
                InputPane.GetForCurrentView().TryShow();
                return;
            }
            if (tag == "backspace")
            {
                if (SearchBox.Text.Length > 0)
                {
                    SearchBox.Text = SearchBox.Text.Substring(0, SearchBox.Text.Length - 1);
                    SearchBox.SelectionStart = SearchBox.Text.Length;
                }
                return;
            }
            SearchBox.Text += tag == "space" ? " " : tag;
            SearchBox.SelectionStart = SearchBox.Text.Length;
        }

        private void KeyButton_GotFocus(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;
            _lastKeyButton = button;
            button.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2F, 0x6F, 0xED));
            button.BorderThickness = new Thickness(2);
        }

        private void KeyButton_LostFocus(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;
            button.BorderBrush = new SolidColorBrush(Colors.Transparent);
            button.BorderThickness = new Thickness(1);
        }

        /// <summary>Same re-assert as HomePage: the frame can set the hosted page's
        /// FlowDirection after construction, so the direction is re-derived from the
        /// stored language here, in OnNavigatedTo and on Loaded.</summary>
        private void ApplyFlowDirection(bool rtl)
        {
            FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            // Back-navigation preserves the cached query and results; a fresh
            // navigation re-runs the view (the web builds a new searchView per
            // route): a query param runs it, an empty one resets to recent searches.
            if (e.NavigationMode == NavigationMode.Back) return;
            var query = e.Parameter as string;
            if (!string.IsNullOrEmpty(query))
            {
                SearchBox.Text = query;
            }
            else
            {
                SearchBox.Text = string.Empty;
            }
            _focusFirstResult = false;
            RunSearch(SearchBox.Text);
            UpdateEmptyHero(false);
            SearchBox.Focus(FocusState.Programmatic);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _debounceTimer.Stop();
            _scrollHelper.Stop();
        }

        private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            // Typing searches as you go (web: the 700 ms debounced live() on input;
            // the console keyboard commits through the same TextChanged).
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void OnDebounceTick(object sender, object e)
        {
            _debounceTimer.Stop();
            RunSearch(SearchBox.Text);
        }

        private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Submit from the console keyboard (web text-submit): run at once and
            // move focus down to the first result when it lands.
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            _debounceTimer.Stop();
            _focusFirstResult = true;
            RunSearch(SearchBox.Text);
        }

        private void RunSearch(string query)
        {
            var _ = _viewModel.RunAsync(query);
        }

        private void OnSearchCompleted(object sender, EventArgs e)
        {
            if (!_focusFirstResult) return;
            _focusFirstResult = false;
            FocusFirstResult();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SearchViewModel.IsEmptyHero))
            {
                UpdateEmptyHero(true);
            }
        }

        /// <summary>Shows the empty-state hero or the results, cross-fading between them.
        /// The hero is bound to the ViewModel state (no query and no results), never to
        /// a timer: typing a query fades it out and the results in; clearing the query
        /// brings it back. The no-animation path pins the initial (or back-navigation)
        /// state at once.</summary>
        private void UpdateEmptyHero(bool animate)
        {
            var empty = _viewModel.IsEmptyHero;
            // The empty state keeps the dark default background: the art fades out
            // when the hero returns (query cleared) and only comes back when a
            // result card takes focus.
            if (empty) ShowDefaultBackground();
            if (animate)
            {
                _heroFadeStoryboard = FadeContainer(EmptyHero, empty, _heroFadeStoryboard);
                _resultsFadeStoryboard = FadeContainer(ResultsContainer, !empty, _resultsFadeStoryboard);
            }
            else
            {
                EmptyHero.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                EmptyHero.Opacity = 1;
                ResultsContainer.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                ResultsContainer.Opacity = 1;
            }
        }

        /// <summary>Fades one container in or out (150 ms, the sidebar label fade). A
        /// container already in the target state is pinned instead of replaying the
        /// fade; a running fade is stopped and superseded. The Completed handler pins
        /// the end state only if the ViewModel still wants it, so a newer
        /// UpdateEmptyHero call wins over a stale fade.</summary>
        private Storyboard FadeContainer(UIElement element, bool show, Storyboard previous)
        {
            if (previous != null) previous.Stop();
            var visible = element.Visibility == Visibility.Visible && element.Opacity > 0;
            if (visible == show)
            {
                element.Opacity = show ? 1 : 0;
                element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                return null;
            }
            if (show)
            {
                element.Visibility = Visibility.Visible;
                element.Opacity = 0;
            }
            var storyboard = new Storyboard();
            var fade = new DoubleAnimation
            {
                To = show ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(150),
            };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
            var target = element;
            var showTarget = show;
            storyboard.Completed += (s, e) =>
            {
                var stillWanted = ReferenceEquals(target, EmptyHero) ? _viewModel.IsEmptyHero : !_viewModel.IsEmptyHero;
                if (stillWanted != showTarget) return;
                target.Opacity = showTarget ? 1 : 0;
                if (!showTarget) target.Visibility = Visibility.Collapsed;
            };
            storyboard.Begin();
            return storyboard;
        }

        private bool TryFocusFirstResult()
        {
            var grid = FindFirstGridView(this);
            if (grid == null || grid.Items.Count == 0) return false;
            grid.UpdateLayout();
            var container = grid.ContainerFromIndex(0) as GridViewItem;
            if (container == null) return false;
            container.Focus(FocusState.Programmatic);
            return true;
        }

        private void FocusFirstResult()
        {
            TryFocusFirstResult();
        }

        private static GridView FindFirstGridView(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var grid = child as GridView;
                if (grid != null) return grid;
                grid = FindFirstGridView(child);
                if (grid != null) return grid;
            }
            return null;
        }

        private void RecentChip_Click(object sender, RoutedEventArgs e)
        {
            var query = (sender as Button)?.Content as string;
            if (string.IsNullOrEmpty(query)) return;
            SearchBox.Text = query;
            _focusFirstResult = true;
            RunSearch(query);
        }

        private void SearchRowGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var card = e.ClickedItem as CatalogCardItem;
            if (card != null && card.Meta != null) _viewModel.OpenTitle(card.Meta);
        }

        // Search-bar focus state: the native single ring is the accent border —
        // focused = 2 epx accent, unfocused = 1 epx --border. The TextBox's own
        // system focus visuals are off (XAML), so this border is the only focus
        // indicator; the web's white inversion was not ported (it would break the
        // dark bar the redesign just made consistent).
        private void OnSearchBoxGotFocus(object sender, RoutedEventArgs e)
        {
            SearchBarBorder.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x2F, 0x6F, 0xED));
            SearchBarBorder.BorderThickness = new Thickness(2);
        }

        private void OnSearchBoxLostFocus(object sender, RoutedEventArgs e)
        {
            SearchBarBorder.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            SearchBarBorder.BorderThickness = new Thickness(1);
        }

        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return;
            _scrollHelper.AnchorFocusedRowElement(focused, Scroller);
            UpdateDynamicBackground(focused);
        }

        /// <summary>Updates the dynamic background from the focused element: a
        /// focused result card shows its poster art (blurred) behind the page;
        /// focus anywhere else keeps the last art so typing on the keyboard row
        /// does not flicker the background; the empty hero resets to the dark
        /// default (see UpdateEmptyHero).</summary>
        private async void UpdateDynamicBackground(DependencyObject focused)
        {
            var item = FocusScrollHelper.FindAncestorGridViewItem(focused);
            var card = item != null ? item.Content as CatalogCardItem : null;
            var url = card != null && card.Meta != null ? card.Meta.Poster : null;
            if (string.IsNullOrEmpty(url)) return;
            if (url == _backgroundUrl) return;
            _backgroundUrl = url;
            var generation = ++_backgroundGeneration;
            WriteableBitmap art;
            try
            {
                art = await GetBackgroundArtAsync(url);
            }
            catch (Exception)
            {
                return;
            }
            if (generation != _backgroundGeneration) return;
            if (art == null) return;
            ShowBackgroundArt(art);
        }

        /// <summary>Returns the blurred art for a poster URL, from the cache when
        /// available. The bytes come through the platform HTTP stack, so a poster
        /// the card already loaded is normally served from its cache, not the
        /// network; the decode runs at 48x72 and the blur over 3,456 pixels, so a
        /// focus move costs almost nothing on Series S.</summary>
        private async Task<WriteableBitmap> GetBackgroundArtAsync(string url)
        {
            WriteableBitmap cached;
            if (_backgroundArtCache.TryGetValue(url, out cached)) return cached;
            byte[] bytes;
            try
            {
                bytes = await BackgroundHttpClient.GetByteArrayAsync(new Uri(url));
            }
            catch (Exception)
            {
                return null;
            }
            WriteableBitmap art;
            try
            {
                art = await DecodeAndBlurAsync(bytes);
            }
            catch (Exception)
            {
                return null;
            }
            if (art == null) return null;
            if (_backgroundArtCache.Count >= BackgroundCacheLimit) _backgroundArtCache.Clear();
            _backgroundArtCache[url] = art;
            return art;
        }

        /// <summary>Decodes image bytes at 48x72 and box-blurs them into a
        /// WriteableBitmap for the background layer. The tiny size is the whole
        /// performance story: the blur is ~120k pixel ops and the texture is
        /// 14 KB, so the GPU just draws one stretched quad.</summary>
        private async Task<WriteableBitmap> DecodeAndBlurAsync(byte[] bytes)
        {
            using (var stream = new InMemoryRandomAccessStream())
            {
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var transform = new BitmapTransform
                {
                    ScaledWidth = BackgroundDecodeWidth,
                    ScaledHeight = BackgroundDecodeHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    transform, ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                var pixels = pixelData.DetachPixelData();
                var blurred = BoxBlur(pixels, BackgroundDecodeWidth, BackgroundDecodeHeight, BackgroundBlurRadius);
                var bitmap = new WriteableBitmap(BackgroundDecodeWidth, BackgroundDecodeHeight);
                using (var pixelStream = bitmap.PixelBuffer.AsStream())
                {
                    await pixelStream.WriteAsync(blurred, 0, blurred.Length);
                }
                bitmap.Invalidate();
                return bitmap;
            }
        }

        /// <summary>Separable box blur over BGRA8: a horizontal pass then a vertical
        /// pass. Two passes at radius 8 on a 48x72 image read like a wide Gaussian
        /// and cost almost nothing.</summary>
        private static byte[] BoxBlur(byte[] source, int width, int height, int radius)
        {
            var temp = new byte[source.Length];
            var result = new byte[source.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    int b = 0, g = 0, r = 0, a = 0, n = 0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var xx = x + k;
                        if (xx < 0 || xx >= width) continue;
                        var i = (y * width + xx) * 4;
                        b += source[i]; g += source[i + 1]; r += source[i + 2]; a += source[i + 3];
                        n++;
                    }
                    var o = (y * width + x) * 4;
                    temp[o] = (byte)(b / n); temp[o + 1] = (byte)(g / n); temp[o + 2] = (byte)(r / n); temp[o + 3] = (byte)(a / n);
                }
            }
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    int b = 0, g = 0, r = 0, a = 0, n = 0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var yy = y + k;
                        if (yy < 0 || yy >= height) continue;
                        var i = (yy * width + x) * 4;
                        b += temp[i]; g += temp[i + 1]; r += temp[i + 2]; a += temp[i + 3];
                        n++;
                    }
                    var o = (y * width + x) * 4;
                    result[o] = (byte)(b / n); result[o + 1] = (byte)(g / n); result[o + 2] = (byte)(r / n); result[o + 3] = (byte)(a / n);
                }
            }
            return result;
        }

        /// <summary>Cross-fades the incoming art in (250 ms) over the outgoing one
        /// and brings the dark dim with it. A running cross-fade is retargeted, not
        /// queued: the live opacities are snapshotted before the running fade is
        /// stopped, because Stop() reverts animated properties to their base values
        /// and without the snapshot a retarget would jump.</summary>
        private void ShowBackgroundArt(WriteableBitmap art)
        {
            var incoming = ReferenceEquals(_backgroundFront, BackgroundImageA) ? BackgroundImageB : BackgroundImageA;
            var outgoing = _backgroundFront;
            incoming.Source = art;
            _backgroundFront = incoming;
            BeginBackgroundFade(incoming, outgoing, 1);
        }

        /// <summary>Fades the background back to the dark default (art and dim to 0).</summary>
        private void ShowDefaultBackground()
        {
            var outgoing = _backgroundFront;
            if (outgoing == null && BackgroundDim.Opacity == 0) return;
            _backgroundFront = null;
            BeginBackgroundFade(null, outgoing, 0);
        }

        private void BeginBackgroundFade(Image incoming, Image outgoing, double dimTarget)
        {
            var incomingOpacity = incoming != null ? incoming.Opacity : 0;
            var outgoingOpacity = outgoing != null ? outgoing.Opacity : 0;
            var dimOpacity = BackgroundDim.Opacity;
            if (_backgroundStoryboard != null) _backgroundStoryboard.Stop();
            if (incoming != null) incoming.Opacity = incomingOpacity;
            if (outgoing != null) outgoing.Opacity = outgoingOpacity;
            BackgroundDim.Opacity = dimOpacity;
            var storyboard = new Storyboard();
            if (incoming != null) AddBackgroundFade(storyboard, incoming, 1);
            if (outgoing != null) AddBackgroundFade(storyboard, outgoing, 0);
            AddBackgroundFade(storyboard, BackgroundDim, dimTarget);
            _backgroundStoryboard = storyboard;
            storyboard.Begin();
        }

        private static void AddBackgroundFade(Storyboard storyboard, UIElement element, double to)
        {
            var fade = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(BackgroundFadeMs),
            };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
        }

        // Row moves by DOM index, like web focus.js horizontalRowNeighbor and
        // HomePage.HandleAction: the index is stable while the strip glides,
        // while XYFocus measures moving cards (and the 1.06 focus zoom) and can
        // skip or mis-pick a neighbour mid-glide. Falls through to XYFocus when
        // the neighbour is not realized yet.
        // The on-screen keyboard row is indexed the same way: left/right move
        // between keys (the row is LTR, so left is the previous key), up goes to
        // the search box, down goes to the first result (or the first recent chip
        // while the empty hero shows). Down from the search box enters the row at
        // the last-used key (or 'a'), and up from the first results row or a
        // recent chip returns to it. At the row's ends left/right fall through so
        // the router's geometric move can carry focus to the sidebar.
        public bool HandleAction(string action, bool repeat)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return false;
            var key = focused as Button;
            if (key != null && _keyboardKeys.Contains(key))
            {
                var index = _keyboardKeys.IndexOf(key);
                if (action == "left" || action == "right")
                {
                    var target = index + (action == "left" ? -1 : 1);
                    if (target >= 0 && target < _keyboardKeys.Count)
                    {
                        _keyboardKeys[target].Focus(FocusState.Programmatic);
                        return true;
                    }
                    return false;
                }
                if (action == "up")
                {
                    SearchBox.Focus(FocusState.Programmatic);
                    return true;
                }
                if (action == "down")
                {
                    return FocusDownFromKeyboardRow();
                }
                return false;
            }
            if (action == "down" && ReferenceEquals(focused, SearchBox))
            {
                FocusKeyboardRow();
                return true;
            }
            if (action == "up" && (IsFirstResultsRow(focused) || IsDescendantOf(focused, EmptyHero)))
            {
                FocusKeyboardRow();
                return true;
            }
            if (action == "left" || action == "right")
            {
                var item = FocusScrollHelper.FindAncestorGridViewItem(focused);
                var grid = item != null ? ItemsControl.ItemsControlFromItemContainer(item) as GridView : null;
                if (grid != null)
                {
                    var index = grid.IndexFromContainer(item);
                    var rtl = grid.FlowDirection == FlowDirection.RightToLeft;
                    var step = action == "left" ? (rtl ? 1 : -1) : (rtl ? -1 : 1);
                    var target = index + step;
                    if (target >= 0 && target < grid.Items.Count)
                    {
                        var container = grid.ContainerFromIndex(target) as GridViewItem;
                        if (container != null)
                        {
                            container.Focus(FocusState.Programmatic);
                            return true;
                        }
                    }
                    else
                    {
                        // Past the row's end. Forward stops dead (build 148: the row must
                        // not drop to the row below); toward the sidebar falls through so
                        // the router can move focus into the strip, as in the web UI.
                        var forward = rtl ? action == "left" : action == "right";
                        if (forward) return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Focuses the keyboard row at the last-used key, or 'a' when no
        /// key has had focus yet.</summary>
        private void FocusKeyboardRow()
        {
            var key = _lastKeyButton ?? _firstLetterKey;
            if (key != null) key.Focus(FocusState.Programmatic);
        }

        /// <summary>Down from the keyboard row: the first result when results exist,
        /// else the first recent chip while the empty hero shows, else nowhere (the
        /// move is swallowed so the router does not wander).</summary>
        private bool FocusDownFromKeyboardRow()
        {
            if (TryFocusFirstResult()) return true;
            if (_viewModel.IsRecentVisible)
            {
                var chip = FindFirstButton(EmptyHero);
                if (chip != null)
                {
                    chip.Focus(FocusState.Programmatic);
                    return true;
                }
            }
            return true;
        }

        private bool IsFirstResultsRow(DependencyObject focused)
        {
            var item = FocusScrollHelper.FindAncestorGridViewItem(focused);
            if (item == null) return false;
            var grid = ItemsControl.ItemsControlFromItemContainer(item) as GridView;
            return grid != null && ReferenceEquals(grid, FindFirstGridView(this));
        }

        private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
        {
            for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, ancestor)) return true;
            }
            return false;
        }

        private static Button FindFirstButton(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var button = child as Button;
                if (button != null) return button;
                button = FindFirstButton(child);
                if (button != null) return button;
            }
            return null;
        }
    }
}
