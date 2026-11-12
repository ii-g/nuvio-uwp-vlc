using System;
using System.Collections.Generic;
using Nuvio.Diagnostics;
using Nuvio.Services;
using Nuvio.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Nuvio.Input
{
    /// <summary>
    /// Phase 1 of the central Focus Manager (see FOCUS.md): one coordinator
    /// that owns arrival settle, the navigation guard, the last-focus
    /// registry, dialog pause state and focus validation. Ported from
    /// NativeInputRouter — same algorithms, same constants — so the
    /// integration worker can swap the router's calls for these one by one
    /// behind the UseFocusCoordinator rollback flag.
    ///
    /// Row/edge moves live here since Phase 2 (port of the router's
    /// MoveFocus and its helpers, reading this coordinator's registry);
    /// the router's MoveFocus stays as the legacy rollback path. The
    /// handleBack delegate is the router's existing HandleBack.
    /// Restore-on-close stays with the pages until Phase 3. The
    /// integration worker also adds this file to Nuvio.csproj
    /// (legacy project: Compile items are listed explicitly).
    /// </summary>
    internal sealed class FocusCoordinator
    {
        // ROLLBACK FLAG: true = coordinator owns settle/guard/input; false = legacy router paths. Flip by editing this line and rebuilding. Documented in FOCUS-MIGRATION.md.
        internal static bool UseFocusCoordinator = true;

        private readonly MainPage _shell;
        private readonly Frame _frame;
        private readonly Action _handleBack;

        // Coordinator-owned scroll engine (Phase 2): one shared
        // FocusScrollHelper for all pages, replacing the per-page
        // instances (discarded on navigation). RequestArrival resets
        // the row anchor on every new navigation, matching that
        // lifecycle.
        private readonly FocusScrollHelper _scrollHelper = new FocusScrollHelper();

        // Same-direction dedupe as the router: one physical press can reach
        // the app twice within 150 ms (poll + native key handling). The
        // window applies to the initial press only; held repeats arrive
        // every 110 ms and must not be throttled.
        private const double DirectionDedupeMs = 150;
        private string _lastDirection;
        private DateTime _lastDirectionTime = DateTime.MinValue;

        // Central last-focus registry: one entry per navigation ENTRY
        // (page type + parameter identity), plus a per-row map (rowKey ->
        // key) per entry. Keying by entry — not by page type — is the fix
        // for the Settings root ("settings|root") vs section
        // ("settings|addons") overwrite.
        //
        // Bounded by FIFO eviction (Phase 2 cap, applied here as a
        // precaution): at most MaxRegistryEntries live entries, and at
        // most MaxSearchEntries search-keyed ones. Numbers: a normal
        // session visits a few dozen entries at most (Home, Search, a
        // handful of catalogs/details/streams/player/settings, each one
        // entry key plus a few row keys), so 200 is 4-6x headroom over a
        // heavy session and the cap never fires in normal use; 20 keeps
        // the highest-churn key type (one entry per distinct search
        // query) from crowding the budget out, while still covering any
        // realistic Back-restore (nobody Back-restores to a search from
        // 20 queries ago — they retype). Smaller (e.g. 50) would sit too
        // close to a heavy session and risk evicting a live Back target;
        // larger (e.g. 1000) buys nothing — arrival restore only ever
        // needs recent entries. FIFO, not strict LRU: writes arrive on
        // every GotFocus (FocusablePage.OnGotFocusRecordArrival) while
        // reads happen only on arrival (Back restore / row landing), and
        // with the cap far above any plausible session size the two are
        // behaviorally identical in practice — FIFO keeps the getters
        // pure and costs nothing on read. A capped-out entry behaves
        // exactly like a never-recorded one: arrival falls back to the
        // page's default target, as it does for a missing key today.
        // Eviction removes the entry's row map with it, so the nested
        // dictionaries cannot outgrow the cap either; per-entry row maps
        // need no separate cap (a page has only a handful of rows).
        private readonly Dictionary<string, string> _arrivalFocusKeys = new Dictionary<string, string>();
        private readonly Dictionary<string, Dictionary<string, string>> _rowFocusKeys = new Dictionary<string, Dictionary<string, string>>();
        private const int MaxRegistryEntries = 200;
        private const int MaxSearchEntries = 20;
        // Insertion-order queues for FIFO eviction. Enqueued ONLY when a
        // key is first added — a re-record of an existing key (the common
        // GotFocus case) enqueues nothing and allocates nothing — so each
        // live key appears exactly once. Eviction pops from the head and
        // drops entries whose key is already gone (removed by the other
        // cap), so both queues stay O(cap): the queues themselves cannot
        // grow without bound.
        private readonly Queue<string> _registryOrder = new Queue<string>();
        private readonly Queue<string> _searchRegistryOrder = new Queue<string>();

        // Arrival-settle state: only one settle runs at a time; a new
        // navigation cancels the previous one. The guard is cleared only
        // by settle success, watchdog expiry, user takeover or the hard
        // guard timeout (GuardHardTimeout) — never in a finally.
        private FocusablePage _settlingPage;
        private bool _settleIsBack;
        private int _settleAttempts;
        private DateTime _settleStartedAt = DateTime.MinValue;
        private DispatcherTimer _settleTimer;
        // Focus captured at settle start (lag fix): a press only counts as
        // user takeover if it actually moved focus away from this element.
        // A press that could not move focus (content not laid out / hero
        // not loaded yet) must not kill the settle.
        private object _settleInitialFocus;
        // One-shot first-layout probe for the settle (lag fix): the
        // LayoutUpdated subscription that fires the first layout attempt.
        // Stored so StopSettle can unsubscribe it on every exit path.
        private Page _settleLayoutPage;
        private EventHandler<object> _settleLayoutHandler;
        // Source label of the last settle attempt, for ArrivalSettled.
        private string _settleLastSource;
        private const int SettleMaxAttempts = 10;
        private static readonly TimeSpan SettleWatchdogInterval = TimeSpan.FromMilliseconds(500);
        // Inputs arriving within this window of a settle starting are the
        // duplicate delivery of the press that caused the navigation (the
        // console delivers one press both as a XAML key event and through
        // the 30 ms controller poll): they must not count as user takeover
        // and kill the settle the navigation just started.
        private static readonly TimeSpan SettleInputIgnoreWindow = TimeSpan.FromMilliseconds(200);

        // Navigation guard state, owned here. Arm/Clear also sync the
        // shell's suppression bit — that bit is the enforcement point in
        // MainPage.ExpandSidebarOnFocus — so arming the coordinator has
        // the same observable effect as the router arming the shell
        // directly. The coordinator owns the policy (when to arm/clear);
        // MainPage keeps the visuals and never animates from here.
        private bool _navigationGuardArmed;
        // Hard bound on the guard (lag fix): the guard must never stay
        // armed beyond GuardHardTimeout, whatever the settle does. The
        // arm timestamp is recorded in ArmNavigationGuard and enforced
        // on every watchdog tick (OnSettleWatchdogTick) — the timeout
        // clears ONLY the guard, never the settle: the settle continues
        // to its own success/expiry and arrival focus can still land
        // later. Value: 2 s. It must be shorter than the ~5 s watchdog
        // expiry (10 x 500 ms) to matter, and longer than any legitimate
        // slow settle: worst observed on device is Home at ~1.0 s
        // (1.46 s on build 538, 1.0 s on 541), Settings at 26 ms — so
        // 2 s is 2x headroom over the slowest observed settle, while the
        // stuck case (e.g. the Details error path burning all 10
        // attempts) recovers ~2.5-3 s sooner. The check runs on the
        // 500 ms tick, so the guard actually clears at the first tick at
        // or after 2 s: effectively 2.0-2.5 s after arming. Even if a
        // future network-loaded page legitimately settled slower than
        // 2 s, the only cost is the sidebar becoming expandable early —
        // the settle is untouched, so arrival focus still lands.
        private DateTime _navigationGuardArmedAt = DateTime.MinValue;
        private static readonly TimeSpan GuardHardTimeout = TimeSpan.FromSeconds(2);

        // Dialog pause state (Phase 1 = pause switch + invoker capture
        // ONLY; restore-on-close is Phase 3). The invoker is retained
        // after close so Phase 3's restore can use it; the next open
        // overwrites it.
        private bool _dialogOpen;
        private Control _dialogInvoker;

        /// <summary>
        /// Stores references only — no subscriptions, no timers, no focus
        /// moves, so constructing the coordinator changes no app behaviour
        /// while the rollback flag is off. The navigation service is
        /// accepted now so the construction signature is stable for the
        /// integration worker; Phase 1 needs nothing from it (back is
        /// delegated to handleBack, and navigation itself stays behind
        /// NavigationService's choke-point). Phase 2 stores it when the
        /// row model needs it. Row/edge moves are the coordinator's own
        /// MoveFocus (Phase 2 port of the router's); back is delegated
        /// to handleBack, the router's existing HandleBack.
        /// </summary>
        public FocusCoordinator(MainPage shell, Frame frame, NavigationService navigation, Action handleBack)
        {
            _shell = shell;
            _frame = frame;
            _handleBack = handleBack;
        }

        /// <summary>True while an arrival settle (first attempt / ContentReady wait / watchdog) is active for the current page.</summary>
        public bool IsSettling { get { return _settlingPage != null; } }

        /// <summary>Raised when a settle completes — success or watchdog expiry only, not on takeover, page change or CancelArrival.</summary>
        public event EventHandler<ArrivalSettledEventArgs> ArrivalSettled;

        /// <summary>
        /// Stable identity for a navigation parameter, so two navigations to
        /// the same page with different parameters are different registry
        /// entries. Only ids and catalog manifest URLs appear here — never
        /// stream URLs, API keys or tokens (none of these parameter fields
        /// can carry one: TransportUrl is a catalog manifest URL, the same
        /// identity CatalogPage already uses as LastOpenedCatalogKey).
        /// </summary>
        internal static string EntryIdentity(object parameter)
        {
            if (parameter == null) return string.Empty;
            var settings = parameter as SettingsNavigation;
            if (settings != null) return "settings|" + (string.IsNullOrEmpty(settings.Section) ? "root" : settings.Section);
            var streams = parameter as StreamsNavigation;
            if (streams != null) return "streams|" + (streams.Type ?? string.Empty) + "|" + (streams.Id ?? string.Empty) + "|" + (streams.VideoId ?? string.Empty);
            var details = parameter as DetailsNavigation;
            if (details != null) return "details|" + (details.Type ?? string.Empty) + "|" + (details.Id ?? string.Empty);
            var catalog = parameter as CatalogNavigation;
            if (catalog != null) return "catalog|" + (catalog.TransportUrl ?? string.Empty) + "|" + (catalog.Type ?? string.Empty) + "|" + (catalog.Id ?? string.Empty);
            var player = parameter as PlayerNavigation;
            if (player != null)
            {
                var metaType = player.Meta != null ? player.Meta.Type ?? string.Empty : string.Empty;
                var metaId = player.Meta != null ? player.Meta.Id ?? string.Empty : string.Empty;
                var videoId = player.Video != null ? player.Video.Id ?? string.Empty : string.Empty;
                return "player|" + metaType + "|" + metaId + "|" + videoId;
            }
            var placeholder = parameter as PlaceholderNavigation;
            if (placeholder != null) return "placeholder|" + (placeholder.Title ?? string.Empty);
            var query = parameter as string;
            if (query != null) return "search|" + query;
            return string.Empty;
        }

        /// <summary>
        /// Records the last-focused key for a navigation entry (Back restore)
        /// and, when rowKey is supplied, the last-focused key for that row
        /// within the entry (row-to-row landing). Overwrites any earlier key
        /// for that entry / row. rowKey is null for non-row items.
        /// </summary>
        public void RecordFocus(string entryKey, string focusKey, string rowKey)
        {
            if (string.IsNullOrEmpty(entryKey) || string.IsNullOrEmpty(focusKey)) return;
            // One extra lookup, same cost class as the write below; the
            // cap work runs only for a genuinely new key. Re-records (the
            // common GotFocus case) cost nothing extra and allocate
            // nothing.
            var isNewEntry = !_arrivalFocusKeys.ContainsKey(entryKey);
            _arrivalFocusKeys[entryKey] = focusKey;
            if (isNewEntry)
            {
                _registryOrder.Enqueue(entryKey);
                if (IsSearchEntryKey(entryKey))
                {
                    _searchRegistryOrder.Enqueue(entryKey);
                    EnforceSearchCap();
                }
                EnforceRegistryCap();
            }
            if (!string.IsNullOrEmpty(rowKey))
            {
                Dictionary<string, string> rows;
                if (!_rowFocusKeys.TryGetValue(entryKey, out rows))
                {
                    rows = new Dictionary<string, string>();
                    _rowFocusKeys[entryKey] = rows;
                }
                rows[rowKey] = focusKey;
            }
        }

        private static bool IsSearchEntryKey(string entryKey)
        {
            // Entry keys are "<FullName>|<EntryIdentity>" (FocusablePage),
            // and the search identity is "search|<query>" — so the marker
            // sits after the type name, not at the start. StartsWith
            // ("search|") would never match a real search entry key.
            return entryKey.IndexOf("|search|", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Drops the oldest search-keyed entries past MaxSearchEntries.
        /// Queue entries whose key is already gone (evicted by the total
        /// cap) are dropped without counting as an eviction. Never throws:
        /// Dequeue is guarded by Count, Remove on a missing key is a
        /// no-op. Runs only when a NEW search key is recorded.
        /// </summary>
        private void EnforceSearchCap()
        {
            while (_searchRegistryOrder.Count > MaxSearchEntries)
            {
                var oldest = _searchRegistryOrder.Dequeue();
                if (_arrivalFocusKeys.ContainsKey(oldest)) RemoveRegistryEntry(oldest);
            }
        }

        /// <summary>
        /// Drops the oldest entries past MaxRegistryEntries. Stale head
        /// entries (already evicted by the search cap) are dropped without
        /// counting as an eviction; the loop re-checks the live count, so
        /// it always terminates — each iteration pops one queue entry and
        /// the queue is finite. Never throws. Runs only when a NEW entry
        /// is recorded.
        /// </summary>
        private void EnforceRegistryCap()
        {
            while (_arrivalFocusKeys.Count > MaxRegistryEntries && _registryOrder.Count > 0)
            {
                var oldest = _registryOrder.Dequeue();
                if (_arrivalFocusKeys.ContainsKey(oldest)) RemoveRegistryEntry(oldest);
            }
        }

        /// <summary>Removes one entry and its row map. Remove on a missing key is a no-op; never throws.</summary>
        private void RemoveRegistryEntry(string entryKey)
        {
            _arrivalFocusKeys.Remove(entryKey);
            _rowFocusKeys.Remove(entryKey);
        }

        /// <summary>Looks up the entry's last-focused key (Back arrival). False when none was recorded.</summary>
        public bool TryGetLastFocusKey(string entryKey, out string focusKey)
        {
            focusKey = null;
            if (string.IsNullOrEmpty(entryKey)) return false;
            return _arrivalFocusKeys.TryGetValue(entryKey, out focusKey) && !string.IsNullOrEmpty(focusKey);
        }

        /// <summary>Looks up the last-focused key for one row of an entry. False when the row was never visited.</summary>
        public bool TryGetRowFocusKey(string entryKey, string rowKey, out string focusKey)
        {
            focusKey = null;
            if (string.IsNullOrEmpty(entryKey) || string.IsNullOrEmpty(rowKey)) return false;
            Dictionary<string, string> rows;
            if (!_rowFocusKeys.TryGetValue(entryKey, out rows)) return false;
            return rows.TryGetValue(rowKey, out focusKey) && !string.IsNullOrEmpty(focusKey);
        }

        /// <summary>
        /// Single input entry point (port of NativeInputRouter.Handle):
        /// dialog gate, then 150 ms same-direction dedupe, then
        /// page-first via IPageInputHandler, then the global switch.
        /// Row/edge moves are the coordinator's own MoveFocus (Phase 2
        /// port of the router's, reading this coordinator's registry);
        /// back is delegated to handleBack, the router's HandleBack.
        /// The takeover check runs after dispatch on every exit
        /// path (dialog-gated and deduped presses included) — this is the
        /// coordinator's NoteUserInput, folded in here because the interface
        /// exposes no separate method for it; it mirrors MainPage calling
        /// _router.NoteUserInput() after _router.Handle(), so it sees the
        /// press's effect. The integration worker must NOT also wire the
        /// router's NoteUserInput alongside this, or a takeover would be
        /// evaluated twice (harmless — the second call is a no-op once the
        /// settle has stopped — but redundant).
        /// </summary>
        public void HandleInput(string action, bool repeat)
        {
            if (!IsDialogOpen)
            {
                DispatchInput(action, repeat);
            }
            NoteUserInput();
        }

        private void DispatchInput(string action, bool repeat)
        {
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
                    _handleBack();
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
                    // Continue Watching card, which the page handles above via IPageInputHandler.
                    break;
            }
        }

        /// <summary>
        /// Row/edge navigation (Phase 2 port of NativeInputRouter.MoveFocus
        /// and its helpers — same algorithm, same comments — except the
        /// row memory lookup reads this coordinator's registry
        /// (TryGetRowFocusKey below) instead of the router's. The router's
        /// MoveFocus stays untouched as the legacy rollback path.
        /// </summary>
        public void MoveFocus(FocusNavigationDirection direction)
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
            var item = FocusScrollHelper.FindAncestorGridViewItem(focused);
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
            // Row memory is keyed by navigation entry, like arrival focus:
            // a row visited under one entry must not leak into another.
            var fp = page as FocusablePage;
            var entryKey = fp != null && !string.IsNullOrEmpty(fp.EntryKey)
                ? fp.EntryKey
                : page.GetType().FullName;
            if (fp == null)
            {
                Log.Write("TryMoveToAdjacentRow: page " + page.GetType().Name + " is not a FocusablePage yet; row memory falls back to the type key");
            }
            string savedKey;
            if (arrival != null && TryGetRowFocusKey(entryKey, rowKey, out savedKey))
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
            var item = focused != null ? FocusScrollHelper.FindAncestorGridViewItem(focused) : null;
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
                var item = FocusScrollHelper.FindAncestorGridViewItem(focused);
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

        /// <summary>Pins the focused card to its row's start edge and the row to ~16% of the page (shared scroll engine; delegates to FocusScrollHelper).</summary>
        public void AnchorFocusedRowElement(DependencyObject focused, ScrollViewer pageScroller)
        {
            _scrollHelper.AnchorFocusedRowElement(focused, pageScroller);
        }

        /// <summary>Scrolls the minimal amount to reveal a card of a wrapping grid (shared scroll engine; delegates to FocusScrollHelper).</summary>
        public void KeepGridItemInView(GridView grid, GridViewItem item)
        {
            _scrollHelper.KeepGridItemInView(grid, item);
        }

        /// <summary>Stops every running scroll glide (shared scroll engine; delegates to FocusScrollHelper.Stop).</summary>
        public void StopScrolling()
        {
            _scrollHelper.Stop();
        }

        /// <summary>
        /// Central arrival focus (port of NativeInputRouter.SettleArrivalFocus).
        /// Event-driven, not a bounded tick chain: (a) the FIRST attempt runs
        /// synchronously, in this call — Frame.Navigated fires once the
        /// content is available from the frame (although it "may not have
        /// completed loading", Microsoft Learn: Frame.Navigated), so a sync
        /// page whose controls are already constructed can take focus here,
        /// before first paint; (b) when the page is not IsContentReady yet
        /// (async pages: the hero / streams / details data has not loaded),
        /// a one-shot ContentReady subscription settles the moment the
        /// content arrives; (c) a one-shot first-layout attempt (LayoutUpdated,
        /// only when the page has no width yet) catches sync pages at their
        /// first layout pass, ~one frame after Navigated; (d) a 500 ms
        /// watchdog retries up to 10 attempts (~5 s) and stops on success,
        /// on first genuine user input, on page change, or on expiry. The
        /// guard is cleared ONLY on success (focus verifiably inside the
        /// page), on watchdog expiry, on user takeover or on the hard
        /// guard timeout — never in a finally. A null page
        /// clears the guard and logs.
        /// </summary>
        public void RequestArrival(FocusablePage page, bool isBack)
        {
            // New navigation means fresh row-anchor state for the shared
            // scroll engine, matching the discarded per-page instance
            // lifecycle.
            _scrollHelper.ResetRowAnchor();
            StopSettle(clearGuard: false);
            if (page == null)
            {
                Log.Write("SettleArrivalFocus: content is not a FocusablePage, clearing the sidebar guard");
                ClearNavigationGuard();
                return;
            }
            _settlingPage = page;
            _settleIsBack = isBack;
            _settleAttempts = 0;
            _settleStartedAt = DateTime.UtcNow;
            _settleInitialFocus = FocusManager.GetFocusedElement();
            Log.Write("Settle arrival: page=" + page.GetType().Name + ", entry=" + page.EntryKey + ", isBack=" + isBack);
            // (a) Synchronous first attempt, in this call.
            if (AttemptSettle("sync"))
            {
                CompleteSettle(success: true);
                return;
            }
            // (b) Not ready yet: settle the moment the page says its content arrived.
            if (!page.IsContentReady)
            {
                page.ContentReady += OnSettleContentReady;
                // No await between the attempt and the subscribe above, so
                // the event cannot have fired in between — but re-check
                // anyway: a page that raised ContentReady during its own
                // Loaded (before Navigated) reports IsContentReady already.
                if (page.IsContentReady && AttemptSettle("content-ready"))
                {
                    CompleteSettle(success: true);
                    return;
                }
            }
            // (c) First-layout attempt: a sync page becomes focusable at
            // its first layout pass, ~one frame after Navigated — up to
            // 500 ms before the next watchdog tick. One-shot: unsubscribe
            // on the first fire that sees a laid-out page; StopSettle
            // unsubscribes on every other exit path.
            if (page.ActualWidth <= 0)
            {
                _settleLayoutPage = page;
                _settleLayoutHandler = (s, e) =>
                {
                    if (!ReferenceEquals(_settlingPage, page)) { UnsubscribeSettleLayout(); return; }
                    if (page.ActualWidth <= 0) return;
                    UnsubscribeSettleLayout();
                    if (AttemptSettle("layout")) CompleteSettle(success: true);
                };
                page.LayoutUpdated += _settleLayoutHandler;
            }
            // (d) Watchdog: keep trying while async content loads.
            _settleTimer = new DispatcherTimer();
            _settleTimer.Interval = SettleWatchdogInterval;
            _settleTimer.Tick += OnSettleWatchdogTick;
            _settleTimer.Start();
        }

        /// <summary>
        /// Cancels the pending settle WITHOUT clearing the guard: the caller
        /// is starting a new navigation (or a new settle), which owns the
        /// guard from here. Call ClearNavigationGuard explicitly if no new
        /// settle follows, or the strip stays unopenable.
        /// </summary>
        public void CancelArrival()
        {
            StopSettle(clearGuard: false);
        }

        private void OnSettleContentReady()
        {
            var page = _settlingPage;
            if (page == null) return;
            page.ContentReady -= OnSettleContentReady;
            if (AttemptSettle("content-ready"))
            {
                CompleteSettle(success: true);
            }
        }

        private void OnSettleWatchdogTick(object sender, object e)
        {
            // Hard guard timeout, checked FIRST on every tick so no path
            // can bypass it — including the armed-without-settle case
            // below (the arm-started timer ticks here too). Clears ONLY
            // the guard: the settle below keeps running and arrival focus
            // can still land. Never fires during a normal fast settle:
            // fast settles complete in milliseconds to ~1 s, well under
            // the 2 s bound.
            if (_navigationGuardArmed && DateTime.UtcNow - _navigationGuardArmedAt >= GuardHardTimeout)
            {
                var elapsedMs = (long)(DateTime.UtcNow - _navigationGuardArmedAt).TotalMilliseconds;
                Log.Write("Navigation guard hard timeout after " + elapsedMs + " ms; clearing the guard only, settle continues");
                ClearNavigationGuard();
            }
            var page = _settlingPage;
            if (page == null)
            {
                // No settle running: keep ticking only while the guard is
                // still armed (an arm with no settle behind it — refused
                // navigation, Back with nowhere to go — is enforced by
                // these ticks). Once the guard is down there is nothing
                // left to watch, so stop: no timer runs when idle.
                if (!_navigationGuardArmed) StopWatchdog();
                return;
            }
            // Page changed under us (a navigation the shell did not settle):
            // stop without clearing the guard — the new page's settle owns it.
            if (!ReferenceEquals(_frame.Content, page))
            {
                Log.Write("Settle watchdog: page changed, stopping without clearing the sidebar guard");
                StopSettle(clearGuard: false);
                return;
            }
            if (AttemptSettle("watchdog"))
            {
                CompleteSettle(success: true);
                return;
            }
            // A takeover inside the attempt above already stopped the settle
            // and cleared the guard: never run the expiry fallback after
            // one. (The router's code can reach the fallback on the 10th
            // attempt and yank focus to the first element right after the
            // user deliberately moved it — the one deliberate deviation
            // from the router in this file.)
            if (_settlingPage == null) return;
            if (_settleAttempts >= SettleMaxAttempts)
            {
                // Expiry: last resort once, then the guard MUST clear — a
                // stuck guard would leave the strip unopenable.
                Log.Write("Settle watchdog: expired after " + _settleAttempts + " attempts for entry=" + page.EntryKey + ", falling back to the first focusable element");
                try
                {
                    var first = FocusManager.FindFirstFocusableElement(_frame) as Control;
                    if (first != null) first.Focus(FocusState.Programmatic);
                }
                catch (Exception)
                {
                    // Fallback is best-effort; the guard still clears below.
                }
                CompleteSettle(success: false);
            }
        }

        /// <summary>
        /// One settle attempt. Focus already inside the page counts as
        /// success WITHOUT moving anything (a late attempt must never yank
        /// focus the user already placed); otherwise TrySettleOnce, then
        /// verify focus really landed inside the page. Logs the attempt
        /// number, entry key, result and the focused element's type, Name
        /// and Tag after the attempt (ids only — never URLs, keys or tokens).
        /// </summary>
        private bool AttemptSettle(string source)
        {
            var page = _settlingPage;
            if (page == null) return false;
            _settleLastSource = source;
            _settleAttempts++;
            var settled = false;
            try
            {
                if (IsFocusInsidePage(page)) settled = true;
                else if (!ReferenceEquals(FocusManager.GetFocusedElement(), _settleInitialFocus))
                {
                    // The user deliberately moved focus off the settle's
                    // starting element (e.g. Up/Down in the strip inside
                    // the 200 ms ignore window): treat as takeover instead
                    // of yanking focus back to the arrival target.
                    Log.Write("Settle takeover: focus moved by the user; stopping without placing arrival focus");
                    StopSettle(clearGuard: true);
                }
                else if (TrySettleOnce(page, _settleIsBack) && IsFocusInsidePage(page)) settled = true;
            }
            catch (Exception)
            {
                settled = false;
            }
            var focused = FocusManager.GetFocusedElement();
            var focusedElement = focused as FrameworkElement;
            var focusName = focusedElement != null && !string.IsNullOrEmpty(focusedElement.Name) ? focusedElement.Name : "(none)";
            var focusTag = "(none)";
            if (focusedElement != null && focusedElement.Tag != null)
            {
                var tagText = focusedElement.Tag.ToString();
                if (!string.IsNullOrEmpty(tagText)) focusTag = tagText;
            }
            Log.Write("Settle attempt " + _settleAttempts + "/" + SettleMaxAttempts + " (" + source + "): entry=" + page.EntryKey + ", isBack=" + _settleIsBack + ", settled=" + settled + ", focused=" + (focused != null ? focused.GetType().Name : "null") + ", focusName=" + focusName + ", focusTag=" + focusTag);
            return settled;
        }

        /// <summary>
        /// Settle finished: stop the watchdog, drop the ContentReady
        /// subscription, and clear the sidebar guard. Success, watchdog
        /// expiry and user takeover are the settle-side paths that clear
        /// it (plus the hard guard timeout, which clears the guard
        /// without ending the settle); a page change leaves it to the new
        /// settle.
        /// ArrivalSettled fires here only, on success or expiry — not on
        /// takeover, page change or CancelArrival.
        /// </summary>
        private void CompleteSettle(bool success)
        {
            var page = _settlingPage;
            var entryKey = page != null ? page.EntryKey : string.Empty;
            var attempts = _settleAttempts;
            var source = _settleLastSource ?? "sync";
            var milliseconds = (long)(DateTime.UtcNow - _settleStartedAt).TotalMilliseconds;
            Log.Write("Settle " + (success ? "succeeded" : "expired") + " after " + attempts + " attempts; clearing the sidebar guard");
            StopSettle(clearGuard: true);
            var handler = ArrivalSettled;
            if (handler != null)
            {
                handler(this, new ArrivalSettledEventArgs
                {
                    EntryKey = entryKey,
                    Attempts = attempts,
                    Source = source,
                    Milliseconds = milliseconds,
                    Succeeded = success,
                });
            }
        }

        private void StopSettle(bool clearGuard)
        {
            StopWatchdog();
            UnsubscribeSettleLayout();
            var page = _settlingPage;
            if (page != null) page.ContentReady -= OnSettleContentReady;
            _settlingPage = null;
            _settleInitialFocus = null;
            if (clearGuard) ClearNavigationGuard();
        }

        private void UnsubscribeSettleLayout()
        {
            var handler = _settleLayoutHandler;
            var page = _settleLayoutPage;
            _settleLayoutHandler = null;
            _settleLayoutPage = null;
            if (handler != null && page != null) page.LayoutUpdated -= handler;
        }

        private void StopWatchdog()
        {
            var timer = _settleTimer;
            _settleTimer = null;
            if (timer == null) return;
            timer.Tick -= OnSettleWatchdogTick;
            timer.Stop();
        }

        /// <summary>
        /// The user has taken over, so the settle stops trying to place
        /// focus. Inputs inside the ignore window after the settle started
        /// are the duplicate delivery of the press that caused the
        /// navigation (XAML key event + 30 ms controller poll) and are
        /// ignored — killing the settle for those would re-open the
        /// sidebar-guard bug the settle fixed. A genuine takeover also
        /// clears the guard: with no settle running there would be nothing
        /// left to clear it, and the strip would stay unopenable. Takeover
        /// only if the press actually moved focus: a press that could not
        /// move anything (content not laid out / hero not loaded yet)
        /// leaves the settle running, so the arrival target still lands
        /// when the page is ready. Called by HandleInput after dispatch so
        /// it sees the press's effect (mirrors MainPage's Handle-then-
        /// NoteUserInput order).
        /// </summary>
        private void NoteUserInput()
        {
            if (_settlingPage == null) return;
            if (DateTime.UtcNow - _settleStartedAt < SettleInputIgnoreWindow) return;
            if (ReferenceEquals(FocusManager.GetFocusedElement(), _settleInitialFocus)) return;
            Log.Write("Settle cancelled by user input after " + _settleAttempts + " attempts; clearing the sidebar guard");
            StopSettle(clearGuard: true);
        }

        private bool TrySettleOnce(Page page, bool isBack)
        {
            try
            {
                var arrival = page as IArrivalFocus;
                if (arrival == null) return false;
                if (isBack)
                {
                    var fp = page as FocusablePage;
                    var entryKey = fp != null && !string.IsNullOrEmpty(fp.EntryKey)
                        ? fp.EntryKey
                        : page.GetType().FullName;
                    if (string.IsNullOrEmpty(fp?.EntryKey))
                    {
                        Log.Write("Settle: page " + page.GetType().Name + " has no entry key (not a FocusablePage yet); Back restore falls back to the type key");
                    }
                    string key;
                    if (TryGetLastFocusKey(entryKey, out key))
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

        private bool IsFocusInsidePage(Page page)
        {
            return IsFocusInside(page);
        }

        /// <summary>
        /// Arms the navigation guard. Called BEFORE a navigation starts —
        /// by the shell/router that owns the navigation — never after.
        /// Also arms the shell's suppression bit (the enforcement point in
        /// MainPage.ExpandSidebarOnFocus).
        /// </summary>
        public void ArmNavigationGuard()
        {
            _navigationGuardArmed = true;
            _navigationGuardArmedAt = DateTime.UtcNow;
            _shell.ArmSidebarExpandSuppression();
            // Enforcement for the armed-without-settle case: the arm sites
            // (NavigationService.Navigate / GoBackOrHome, router
            // HandleBack) can arm the guard with no settle to follow — a
            // refused non-FocusablePage navigation, a failed
            // Frame.Navigate (no Navigated event), or a first Back press
            // on Home (toast, no navigation). With no settle there is no
            // watchdog to clear the guard, so start the enforcement
            // timer here when none is running: the tick above enforces
            // the hard timeout and stops the timer once the guard is
            // down. This reuses the settle's own timer field, so at most
            // one timer ever runs — RequestArrival / StopSettle own its
            // lifecycle from here (they stop and replace it when a settle
            // starts), and a running settle's timer enforces the timeout
            // the same way. No timer runs when idle: it starts only on
            // arm and stops on guard-clear / settle end.
            if (_settleTimer == null)
            {
                _settleTimer = new DispatcherTimer();
                _settleTimer.Interval = SettleWatchdogInterval;
                _settleTimer.Tick += OnSettleWatchdogTick;
                _settleTimer.Start();
            }
        }

        /// <summary>
        /// Clears the navigation guard. Called ONLY by the settle on
        /// success / watchdog expiry / user takeover, by the hard guard
        /// timeout, by FocusSidebar's deliberate move onto the strip (via
        /// the shell), or by genuine user input while no settle is
        /// pending. Never in a finally. Also clears the shell's
        /// suppression bit.
        /// </summary>
        public void ClearNavigationGuard()
        {
            _navigationGuardArmed = false;
            _shell.ClearSidebarExpandSuppression();
            // If no settle is running, nothing needs the enforcement
            // timer any more: stop it here instead of waiting for its
            // next tick. Never stop it while a settle runs — the settle's
            // watchdog lives in the same field, and the settle must keep
            // ticking after the guard clears (arrival focus can still
            // land). Called from StopSettle this is a no-op: the timer
            // was already stopped there.
            if (_settlingPage == null) StopWatchdog();
        }

        /// <summary>True while the navigation guard is armed (focus landing on a sidebar button after navigation must not expand the strip).</summary>
        public bool IsNavigationGuardArmed { get { return _navigationGuardArmed; } }

        /// <summary>
        /// Pauses coordinator input and captures the dialog invoker — the
        /// currently focused element when null is passed — for Phase 3's
        /// restore-on-close. Focus is NOT moved here or on close in this
        /// phase; pages keep their own restores until Phase 3.
        /// </summary>
        public void NotifyDialogOpened(Control invoker)
        {
            _dialogInvoker = invoker ?? FocusManager.GetFocusedElement() as Control;
            _dialogOpen = true;
            Log.Write("Dialog opened; coordinator input paused; invoker=" + (_dialogInvoker != null ? _dialogInvoker.GetType().Name : "null"));
        }

        /// <summary>Resumes coordinator input. Does NOT move focus (Phase 3 owns restore-on-close).</summary>
        public void NotifyDialogClosed()
        {
            _dialogOpen = false;
            Log.Write("Dialog closed; coordinator input resumed");
        }

        /// <summary>True while a dialog is open; the controller is paused (see HandleInput).</summary>
        public bool IsDialogOpen { get { return _dialogOpen; } }

        /// <summary>The element that currently has focus, or null.</summary>
        public DependencyObject CurrentFocused { get { return FocusManager.GetFocusedElement() as DependencyObject; } }

        /// <summary>True when the focused element is root or one of its descendants.</summary>
        public bool IsFocusInside(DependencyObject root)
        {
            if (root == null) return false;
            for (var node = CurrentFocused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, root)) return true;
            }
            return false;
        }

        /// <summary>
        /// True when focus sits on a sidebar button. Uses the shell's own
        /// IsSidebarButton (the router's test): the focused element, or an
        /// ancestor, is a Button inside the sidebar's items panel. That
        /// covers the strip's only focusable elements; a focusable element
        /// inside SidebarBorder but outside the items panel would not be
        /// detected — none exists today. No new MainPage API was added.
        /// </summary>
        public bool IsFocusInSidebar
        {
            get
            {
                var focused = CurrentFocused;
                if (focused == null) return false;
                bool isFirst, isLast;
                return _shell.IsSidebarButton(focused, out isFirst, out isLast);
            }
        }

        /// <summary>
        /// Validates the current focus after a page's content changed
        /// (items removed, rows rebuilt). If focus is still valid —
        /// visible, enabled, and in the live tree — nothing happens. If
        /// not, focus moves in order: (a) the same recorded key if it
        /// still resolves (via the page's TryFocusKey); (b) nearest valid
        /// sibling in the same container (Phase 2 row model); (c) the
        /// page arrival target. Never leaves focus null: the last resort
        /// is the frame's first focusable element. Has NO callers today —
        /// the step (b) implementation below was added in Phase 2 ahead
        /// of the callers that will use it.
        /// </summary>
        public void NotifyContentChanged(FocusablePage page)
        {
            if (page == null) return;
            try
            {
                if (IsFocusValid(CurrentFocused)) return;
                string key;
                if (!string.IsNullOrEmpty(page.EntryKey) && TryGetLastFocusKey(page.EntryKey, out key))
                {
                    if (page.TryFocusKey(key) && IsFocusValid(CurrentFocused)) return;
                }
                // (b) Nearest valid sibling in the same container: the
                // focused element's GridViewItem (if any) gives the row
                // and index; search outward (index-1, index+1, index-2,
                // index+2, ...) for the nearest container that is
                // Visible and enabled, realizing virtualized containers
                // via ScrollIntoView + UpdateLayout (same pattern as
                // TryFocusKey in the pages). Focus it and return if
                // focus is now valid. No GridViewItem ancestor, or no
                // valid sibling, falls through to (c) below.
                var currentItem = FocusScrollHelper.FindAncestorGridViewItem(CurrentFocused);
                if (currentItem != null)
                {
                    var grid = ItemsControl.ItemsControlFromItemContainer(currentItem) as GridView;
                    if (grid != null)
                    {
                        var index = grid.IndexFromContainer(currentItem);
                        if (index >= 0)
                        {
                            for (var distance = 1; distance < grid.Items.Count; distance++)
                            {
                                if (TryFocusSibling(grid, index - distance) && IsFocusValid(CurrentFocused)) return;
                                if (TryFocusSibling(grid, index + distance) && IsFocusValid(CurrentFocused)) return;
                            }
                        }
                    }
                }
                if (page.TryFocusArrival() && IsFocusValid(CurrentFocused)) return;
            }
            catch (Exception ex)
            {
                Log.Error("Validating focus after content change failed", ex);
            }
            try
            {
                var first = FocusManager.FindFirstFocusableElement(_frame) as Control;
                if (first != null) first.Focus(FocusState.Programmatic);
            }
            catch (Exception)
            {
                // Last resort is best-effort; focus may stay where it is.
            }
        }

        /// <summary>
        /// Focuses the container at one index of a GridView for step (b)
        /// of NotifyContentChanged. False when the index is out of range,
        /// the container cannot be realized, or it is collapsed/disabled
        /// (the caller then tries the next outward index). A virtualized
        /// container is realized via ScrollIntoView + UpdateLayout, the
        /// same pattern as TryFocusKey in the pages. Returns the Focus()
        /// result: true only when focus was actually set on the candidate.
        /// </summary>
        private bool TryFocusSibling(GridView grid, int candidateIndex)
        {
            if (candidateIndex < 0 || candidateIndex >= grid.Items.Count) return false;
            var candidate = grid.ContainerFromIndex(candidateIndex) as GridViewItem;
            if (candidate == null)
            {
                grid.ScrollIntoView(grid.Items[candidateIndex]);
                grid.UpdateLayout();
                candidate = grid.ContainerFromIndex(candidateIndex) as GridViewItem;
            }
            if (candidate == null) return false;
            if (candidate.Visibility != Visibility.Visible || !candidate.IsEnabled) return false;
            return candidate.Focus(FocusState.Programmatic);
        }

        private bool IsFocusValid(DependencyObject focused)
        {
            if (focused == null) return false;
            var element = focused as UIElement;
            if (element != null && element.Visibility != Visibility.Visible) return false;
            var control = focused as Control;
            if (control != null && !control.IsEnabled) return false;
            // Live-tree check: a live element's ancestor chain reaches the
            // shell (or the frame); a detached element's chain ends at null
            // before it gets there. Focus in the sidebar is valid — this
            // does not require focus to be inside the page.
            for (var node = focused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, _shell) || ReferenceEquals(node, _frame)) return true;
            }
            return false;
        }
    }

    /// <summary>Settle outcome, raised by FocusCoordinator.ArrivalSettled on success or watchdog expiry only — not on takeover, page change or CancelArrival.</summary>
    internal sealed class ArrivalSettledEventArgs : EventArgs
    {
        public string EntryKey;
        public int Attempts;
        public string Source;
        public long Milliseconds;
        public bool Succeeded;
    }
}
