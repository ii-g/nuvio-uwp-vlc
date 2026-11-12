using System;
using System.Collections.Generic;
using Nuvio.Services;
using Windows.ApplicationModel.Resources;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Nuvio.Input
{
    /// <summary>Implemented by pages that handle controller actions before the router's global handling.</summary>
    internal interface IPageInputHandler
    {
        /// <summary>Returns true if the action was handled and must not fall through.</summary>
        bool HandleAction(string action, bool repeat);
    }

    /// <summary>
    /// Implemented by pages that own their arrival focus target. The router's
    /// SettleArrivalFocus calls these after navigation; pages implement them
    /// (see HomePage/SearchPage) and record their last-focused key through
    /// MainPage.RecordArrivalFocusKey so Back restores the same card.
    /// </summary>
    internal interface IArrivalFocus
    {
        /// <summary>Focuses the page's default arrival target. False when nothing could take focus.</summary>
        bool TryFocusArrival();

        /// <summary>Focuses the element for a remembered key. False when the key no longer exists.</summary>
        bool TryFocusKey(string key);
    }

    /// <summary>
    /// Implemented only by pages with multiple remembered rows (HomePage).
    /// Returns the stable row key for a row GridView — data identity, not
    /// ordinal position, since rows can appear/disappear — or null when the
    /// container is not a remembered row. The router consumes this to land
    /// row-to-row moves on the row's last-focused poster.
    /// </summary>
    internal interface IRowFocusInfo
    {
        string RowKeyForContainer(GridView grid);
    }

    /// <summary>
    /// Named controller actions → focus moves / page back / global handling (architecture §4).
    /// ControllerBridge polling code is unchanged; its callback becomes this router instead of
    /// AppBridge.SendInput. Gamepad A ("select") is intentionally NOT handled here: XAML
    /// activates the focused control natively (MainPage leaves GamepadA unswallowed).
    /// </summary>
    internal sealed class NativeInputRouter
    {
        private readonly MainPage _shell;
        private readonly Frame _frame;
        private readonly NavigationService _navigation;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");
        private DateTime _lastBackPress = DateTime.MinValue;
        // Web focus.js dedupes the same action arriving twice within 150 ms (one physical
        // press can reach the app through the poll and through native key handling); mirror
        // it here so a single tap can never move focus twice. The window applies to the
        // initial press only: held repeats arrive every 110 ms (ControllerBridge, like
        // the web UI), so throttling them would stop a hold after the first card.
        private const double DirectionDedupeMs = 150;
        private string _lastDirection;
        private DateTime _lastDirectionTime = DateTime.MinValue;
        // Central last-focus registry: one entry per page type, overwritten on
        // every record, plus a per-row map (rowKey -> key) per page type.
        // Keys are stable content ids (card/item ids), never stream URLs,
        // API keys or tokens. The rowKey is supplied by the page at record
        // time (data identity, not ordinal position).
        private readonly Dictionary<Type, string> _arrivalFocusKeys = new Dictionary<Type, string>();
        private readonly Dictionary<Type, Dictionary<string, string>> _rowFocusKeys = new Dictionary<Type, Dictionary<string, string>>();

        public NativeInputRouter(MainPage shell, Frame frame, NavigationService navigation)
        {
            _shell = shell;
            _frame = frame;
            _navigation = navigation;
        }

        /// <summary>
        /// Records the last-focused key for a page type (Back restore) and,
        /// when rowKey is supplied, the last-focused key for that row within
        /// the page (row-to-row landing). Overwrites any earlier key for that
        /// page type / row. rowKey is null for non-row items.
        /// </summary>
        internal void RecordArrivalFocusKey(Type pageType, string key, string rowKey = null)
        {
            if (pageType == null || string.IsNullOrEmpty(key)) return;
            _arrivalFocusKeys[pageType] = key;
            if (!string.IsNullOrEmpty(rowKey))
            {
                Dictionary<string, string> rows;
                if (!_rowFocusKeys.TryGetValue(pageType, out rows))
                {
                    rows = new Dictionary<string, string>();
                    _rowFocusKeys[pageType] = rows;
                }
                rows[rowKey] = key;
            }
        }

        /// <summary>Looks up the page's last-focused key (Back arrival). False when none was recorded.</summary>
        internal bool TryGetLastFocusKey(Type pageType, out string key)
        {
            key = null;
            if (pageType == null) return false;
            return _arrivalFocusKeys.TryGetValue(pageType, out key) && !string.IsNullOrEmpty(key);
        }

        /// <summary>Looks up the last-focused key for one row of a page. False when the row was never visited.</summary>
        internal bool TryGetRowFocusKey(Type pageType, string rowKey, out string key)
        {
            key = null;
            if (pageType == null || string.IsNullOrEmpty(rowKey)) return false;
            Dictionary<string, string> rows;
            if (!_rowFocusKeys.TryGetValue(pageType, out rows)) return false;
            return rows.TryGetValue(rowKey, out key) && !string.IsNullOrEmpty(key);
        }

        /// <summary>
        /// Central arrival focus, called by the shell from ContentFrame_Navigated.
        /// Fire-and-forget dispatcher tick chain (same pattern as the pages'
        /// Dispatcher.RunAsync fire-and-forget restores): one tick tries the
        /// arrival target — on Back, the registry key for this page type via
        /// TryFocusKey, else TryFocusArrival — then ONE retry on the next tick
        /// with the same logic, then the existing FindFirstFocusableElement net
        /// as last resort. The retry/fallback are skipped when focus is
        /// already inside the page content (the user moved already). The
        /// sidebar expansion suppression guard is cleared exactly once, in a
        /// finally, whatever the outcome.
        /// </summary>
        internal void SettleArrivalFocus(Page page, bool isBack)
        {
            if (page == null)
            {
                _shell.ClearSidebarExpandSuppression();
                return;
            }
            var _ = _frame.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                var retryScheduled = false;
                try
                {
                    if (TrySettleOnce(page, isBack)) return;
                    if (IsFocusInsidePage(page)) return;
                    retryScheduled = true;
                    var __ = _frame.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        try
                        {
                            if (TrySettleOnce(page, isBack)) return;
                            if (IsFocusInsidePage(page)) return;
                            var first = FocusManager.FindFirstFocusableElement(_frame) as Control;
                            if (first != null) first.Focus(FocusState.Programmatic);
                        }
                        finally
                        {
                            _shell.ClearSidebarExpandSuppression();
                        }
                    });
                }
                finally
                {
                    if (!retryScheduled) _shell.ClearSidebarExpandSuppression();
                }
            });
        }

        private bool TrySettleOnce(Page page, bool isBack)
        {
            try
            {
                var arrival = page as IArrivalFocus;
                if (arrival == null) return false;
                if (isBack)
                {
                    string key;
                    if (TryGetLastFocusKey(page.GetType(), out key))
                    {
                        if (arrival.TryFocusKey(key)) return true;
                    }
                }
                return arrival.TryFocusArrival();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsFocusInsidePage(Page page)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            for (var node = focused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, page)) return true;
            }
            return false;
        }

        public void Handle(string action, bool repeat)
        {
            if (_shell.DialogOpen) return;
            if (!repeat && (action == "up" || action == "down" || action == "left" || action == "right"))
            {
                var now = DateTime.UtcNow;
                if (action == _lastDirection && (now - _lastDirectionTime).TotalMilliseconds < DirectionDedupeMs)
                {
                    return;
                }
                _lastDirection = action;
                _lastDirectionTime = now;
            }
            var page = _frame.Content as Page;
            var handler = page as IPageInputHandler;
            if (handler != null && handler.HandleAction(action, repeat)) return;
            switch (action)
            {
                case "up":
                    MoveFocus(FocusNavigationDirection.Up);
                    break;
                case "down":
                    MoveFocus(FocusNavigationDirection.Down);
                    break;
                case "left":
                    MoveFocus(FocusNavigationDirection.Left);
                    break;
                case "right":
                    MoveFocus(FocusNavigationDirection.Right);
                    break;
                case "select":
                    // XAML activates the focused element natively; nothing to do here.
                    break;
                case "back":
                    HandleBack();
                    break;
                case "x":
                case "y":
                case "menu":
                case "view":
                case "lb":
                case "rb":
                case "lt":
                case "rt":
                    // Ignored outside the player (player is M4), except y/menu on a focused
                    // Continue Watching card, which the Home page handles above.
                    break;
            }
        }

        private void MoveFocus(FocusNavigationDirection direction)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null)
            {
                var first = FocusManager.FindFirstFocusableElement(_frame) as Control;
                if (first != null)
                {
                    first.Focus(FocusState.Programmatic);
                    return;
                }
            }
            if (focused != null && IsEdgeMove(focused, direction)) return;
            // Vertical moves between rows use per-row remembered position: a
            // row entered for the first time lands on its first poster; a row
            // visited before lands on the poster last focused in that row.
            // Falls through to XYFocus when there is no adjacent row (first/
            // last row, single-grid pages like Catalog) or the target row is
            // not a remembered row. HomePage/SearchPage handle up/down only at
            // their hero/box edges, so in-row vertical moves reach here.
            if (focused != null && (direction == FocusNavigationDirection.Up || direction == FocusNavigationDirection.Down))
            {
                if (TryMoveToAdjacentRow(focused, direction)) return;
            }
            // Sidebar → content is a geometric XYFocus move (the page's HandleAction has
            // no row to index from while focus sits in the strip), and the geometry skips
            // the row's first card: the strip is expanded (136 epx) while a sidebar button
            // has focus, so the button (832..952) overlaps the first card (798..886) and
            // the first card is not strictly to the left of it — XYFocus takes the second
            // card instead (the web restores the first element instead of measuring).
            // Pin the landing to index 0 of whatever row the framework picked.
            bool fromSidebar = false;
            if (focused != null)
            {
                bool sidebarFirst;
                bool sidebarLast;
                fromSidebar = _shell.IsSidebarButton(focused, out sidebarFirst, out sidebarLast);
            }
            FocusManager.TryMoveFocus(direction);
            if (fromSidebar)
            {
                FocusFirstCardOfEnteredRow();
            }
        }

        /// <summary>
        /// Moves up/down into the adjacent row GridView in visual-tree order
        /// within the current page (up = previous, down = next), landing on
        /// the poster's remembered position in that row: the key recorded for
        /// the row (via IRowFocusInfo's stable row key) when the row was
        /// visited before, else the row's first poster (index 0). Returns
        /// false (fall through to XYFocus) when focus is not in a row, there
        /// is no adjacent GridView, the target row is not a remembered row
        /// (RowKeyForContainer returns null — e.g. single-grid pages), or
        /// the target row is empty / unrealized.
        /// </summary>
        private bool TryMoveToAdjacentRow(DependencyObject focused, FocusNavigationDirection direction)
        {
            var item = FindAncestorGridViewItem(focused);
            if (item == null) return false;
            var grid = ItemsControl.ItemsControlFromItemContainer(item) as GridView;
            if (grid == null) return false;
            var page = _frame.Content as Page;
            if (page == null) return false;
            var grids = new List<GridView>();
            CollectGridViews(page, grids);
            var at = grids.IndexOf(grid);
            if (at < 0) return false;
            var target = direction == FocusNavigationDirection.Down ? at + 1 : at - 1;
            if (target < 0 || target >= grids.Count) return false;
            var targetGrid = grids[target];
            if (targetGrid.Items.Count == 0) return false;
            var rowInfo = page as IRowFocusInfo;
            var rowKey = rowInfo != null ? rowInfo.RowKeyForContainer(targetGrid) : null;
            if (string.IsNullOrEmpty(rowKey)) return false;
            var arrival = page as IArrivalFocus;
            string savedKey;
            if (arrival != null && TryGetRowFocusKey(page.GetType(), rowKey, out savedKey))
            {
                if (arrival.TryFocusKey(savedKey)) return true;
                // Saved poster is gone (item removed): fall through to index 0 below.
            }
            var first = targetGrid.ContainerFromIndex(0) as GridViewItem;
            if (first == null)
            {
                targetGrid.ScrollIntoView(targetGrid.Items[0]);
                targetGrid.UpdateLayout();
                first = targetGrid.ContainerFromIndex(0) as GridViewItem;
            }
            if (first == null) return false;
            first.Focus(FocusState.Programmatic);
            return true;
        }

        /// <summary>Collects every GridView under a root in visual-tree (document) order — the same DFS order as SearchPage.FindFirstGridView.</summary>
        private static void CollectGridViews(DependencyObject root, List<GridView> grids)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var grid = child as GridView;
                if (grid != null)
                {
                    grids.Add(grid);
                }
                CollectGridViews(child, grids);
            }
        }

        /// <summary>
        /// After a sidebar → content move, the focus may sit on any card of the row the
        /// framework picked; move it to that row's first card (index 0 in flow order).
        /// No-op when focus did not land in a row (e.g. the hero) or is already on the
        /// first card. Scrolls the row to the start first when the first container is
        /// virtualized away.
        /// </summary>
        private void FocusFirstCardOfEnteredRow()
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            var item = focused != null ? FindAncestorGridViewItem(focused) : null;
            if (item == null) return;
            var grid = ItemsControl.ItemsControlFromItemContainer(item) as GridView;
            if (grid == null || grid.Items.Count == 0) return;
            if (grid.IndexFromContainer(item) == 0) return;
            var first = grid.ContainerFromIndex(0) as GridViewItem;
            if (first == null)
            {
                grid.ScrollIntoView(grid.Items[0]);
                grid.UpdateLayout();
                first = grid.ContainerFromIndex(0) as GridViewItem;
            }
            if (first != null) first.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// Moves that must stop dead instead of letting XYFocus carry focus somewhere else:
        /// past the last card of a row (the old code dropped to the next row) and past
        /// either end of the sidebar (which also collapsed the strip and threw focus into
        /// the content). The row's start edge is left open on purpose: pressing toward the
        /// sidebar there returns to the sidebar, as in the web UI.
        /// </summary>
        private bool IsEdgeMove(DependencyObject focused, FocusNavigationDirection direction)
        {
            if (direction == FocusNavigationDirection.Left || direction == FocusNavigationDirection.Right)
            {
                var item = FindAncestorGridViewItem(focused);
                if (item != null)
                {
                    var grid = ItemsControl.ItemsControlFromItemContainer(item) as GridView;
                    if (grid != null)
                    {
                        var index = grid.IndexFromContainer(item);
                        var rtl = grid.FlowDirection == FlowDirection.RightToLeft;
                        var forward = rtl ? direction == FocusNavigationDirection.Left : direction == FocusNavigationDirection.Right;
                        if (forward && index >= 0 && index == grid.Items.Count - 1) return true;
                    }
                }
            }
            if (direction == FocusNavigationDirection.Up || direction == FocusNavigationDirection.Down)
            {
                bool isFirst;
                bool isLast;
                if (_shell.IsSidebarButton(focused, out isFirst, out isLast))
                {
                    if (direction == FocusNavigationDirection.Up && isFirst) return true;
                    if (direction == FocusNavigationDirection.Down && isLast) return true;
                }
            }
            return false;
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

        private void HandleBack()
        {
            // A dialog open means the controller is paused and XAML handles back; this never runs then.
            // The current page already had its say via IPageInputHandler above.
            if (_frame.CanGoBack)
            {
                _frame.GoBack();
                return;
            }
            if (!(_frame.Content is Views.HomePage))
            {
                _navigation.NavigateHome();
                return;
            }
            var now = DateTime.UtcNow;
            if ((now - _lastBackPress).TotalMilliseconds <= 2500)
            {
                Application.Current.Exit();
                return;
            }
            _lastBackPress = now;
            var message = _strings.GetString("PressBackAgainToExit");
            _shell.ShowToast(string.IsNullOrEmpty(message) ? "Press back again to exit" : message, "info", 2500);
        }
    }
}
