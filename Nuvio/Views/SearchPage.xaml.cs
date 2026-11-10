using System;
using System.ComponentModel;
using Nuvio.Input;
using Nuvio.Models;
using Nuvio.Services;
using Nuvio.ViewModels;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
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

        public SearchPage()
        {
            NavigationCacheMode = NavigationCacheMode.Required;
            InitializeComponent();
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

        private void FocusFirstResult()
        {
            var grid = FindFirstGridView(this);
            if (grid == null || grid.Items.Count == 0) return;
            grid.UpdateLayout();
            var container = grid.ContainerFromIndex(0) as GridViewItem;
            if (container != null) container.Focus(FocusState.Programmatic);
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
        }

        // Row moves by DOM index, like web focus.js horizontalRowNeighbor and
        // HomePage.HandleAction: the index is stable while the strip glides,
        // while XYFocus measures moving cards (and the 1.06 focus zoom) and can
        // skip or mis-pick a neighbour mid-glide. Falls through to XYFocus when
        // the neighbour is not realized yet.
        public bool HandleAction(string action, bool repeat)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return false;
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
    }
}
