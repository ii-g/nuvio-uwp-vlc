using System;
using Nuvio.Input;
using Nuvio.Models;
using Nuvio.Services;
using Nuvio.ViewModels;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
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
            SearchBox.TextChanged += OnSearchBoxTextChanged;
            SearchBox.KeyDown += OnSearchBoxKeyDown;
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
