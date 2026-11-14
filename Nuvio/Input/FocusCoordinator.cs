using System;
using System.Collections.Generic;
using Nuvio.Diagnostics;
using Nuvio.Services;
using Nuvio.ViewModels;
using Nuvio.Views;
using Windows.UI.Core;
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
    /// Dialog restore-on-close lives here since Phase 3 (invoker stack
    /// + one-tick restore below); the pages keep their synchronous
    /// RestoreDialogInvoker calls as the flag-off compatibility path
    /// (a no-op when the flag is on), so the coordinator's tick is the
    /// only restore path when the flag is on — it skips when focus
    /// already landed on a valid target. The integration worker also adds this
    /// file to Nuvio.csproj (legacy project: Compile items are listed
    /// explicitly).
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
        // Key the last TrySettleOnce call in this settle restored via
        // (null when it fell through to TryFocusArrival or failed).
        // Reporting only: cleared at the top of TrySettleOnce and in
        // StopSettle, read by CompleteSettle to decide whether a
        // post-settle hold check applies. The restore logic is untouched.
        private string _settleRestoredKey;

        // Post-settle hold check: a Back settle that restored a recorded
        // key can still lose focus ~450 ms later (log nuvio-vlc_15:
        // settle succeeded at 20.892, focus gone by 21.349, no log line
        // in between — most likely a late layout pass re-realizing the
        // GridViewItem once the posters load). The check verifies that
        // focus is still on the restored card and, if it dropped with
        // no user input, re-applies the recorded key exactly once.
        // Timing: at most two checks, one HoldCheckInterval (200 ms)
        // apart, so the last check lands ~400 ms after success — before
        // the harness reads focus (~457 ms after success in that log).
        // A 500 ms check would fire after the read and be useless for
        // the test. Every check must also land inside the 2 s guard
        // budget measured from settle start; the guard itself is never
        // re-armed or extended (it was already cleared by CompleteSettle).
        // The _settleTimer is NOT reused for this: ClearNavigationGuard
        // stops it when the settle ends and its tick's idle branch stops
        // it while no settle runs, so a separate one-shot timer is the
        // only way to watch past settle end without touching the guard
        // enforcement invariants.
        private DispatcherTimer _holdCheckTimer;
        private FocusablePage _holdCheckPage;
        private string _holdCheckKey;
        private object _holdCheckExpected;
        private int _holdCheckChecksLeft;
        private bool _holdCheckRestoreUsed;
        // User-input clock for the hold check: bumped on every HandleInput
        // call (the single input entry point), snapshotted in ArmHoldCheck.
        // A tick whose count still matches the snapshot saw no user input
        // since arming, so a focus move in between is the framework's own
        // doing (late layout re-realizing a container), not a user move.
        private int _inputCount;
        private int _holdCheckInputCount;
        private static readonly TimeSpan HoldCheckInterval = TimeSpan.FromMilliseconds(200);
        private const int HoldCheckMaxChecks = 2;
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

        // Dialog pause state (Phase 3 owns the stack and restore-on-
        // close). The stack keeps open/close pairs matched across
        // nested dialogs: each NotifyDialogOpened pushes its invoker
        // (null when focus was not on a Control — pushed as-is so the
        // pairs stay matched), each NotifyDialogClosed pops one.
        // Restore runs only when the stack empties; while another
        // dialog is still open the outer dialog's invoker stays buried
        // and is restored when that dialog closes.
        private readonly Stack<Control> _dialogInvokers = new Stack<Control>();

        // OSD anchor state: true after an OSD hide parked (or tried to
        // park) focus on Play/Pause, until the next show consumes it.
        // The anchor is always Play/Pause — no element is remembered,
        // so a stale reference (user left the player while parked) can
        // never focus a detached element. One bool is enough: the OSD
        // exists only on PlayerPage, and a new hide re-arms it.
        private bool _osdPlayPauseAnchor;

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
        /// identity CatalogPage already uses as its catalog key).
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

        /// <summary>Forgets the per-row memory of one entry (fresh-arrival invalidation). The entry's arrival key is untouched, so Back restore still works; only the row map is dropped, so the next row-to-row move in a fresh visit falls back to the row's first poster and the map rebuilds from GotFocus records during this visit.</summary>
        public void ClearRowFocusKeys(string entryKey)
        {
            if (string.IsNullOrEmpty(entryKey)) return;
            _rowFocusKeys.Remove(entryKey);
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
        /// Every call also bumps _inputCount, the hold check's user-input
        /// clock: the B press that caused a Back navigation is counted
        /// here, before the settle arms the hold check, so it can never
        /// look like input after arming.
        /// </summary>
        public void HandleInput(string action, bool repeat)
        {
            _inputCount++;
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
            // Toward the sidebar from content: intercept ONLY when XYFocus
            // itself would land on the strip — open it and land on the
            // button for the current page instead of the geometrically
            // nearest button. Toward-start is right in RTL, left in LTR. Peek at
            // the move XYFocus would make (FindNextFocusableElement moves
            // nothing, it only reports the candidate): a sidebar button —
            // or null, meaning no focusable element at all in that
            // direction — takes the FocusSidebarFirst path; a content
            // element (a card / row neighbour, e.g. mid-row in Catalog,
            // which has no HandleAction of its own) is left alone so the
            // TryMoveFocus below carries it to the neighbour. When the
            // sidebar is Hidden (Details / Catalog / Streams / Player)
            // FocusSidebarFirst returns false and the move falls through
            // to XYFocus as today. Dialogs never reach here: HandleInput
            // gates DispatchInput while a dialog is open.
            if (focused != null && !IsFocusInSidebar && IsTowardSidebar(direction))
            {
                var next = FocusManager.FindNextFocusableElement(direction);
                bool nextIsFirst;
                bool nextIsLast;
                bool nextIsSidebar = next != null && _shell.IsSidebarButton(next, out nextIsFirst, out nextIsLast);
                if (next == null || nextIsSidebar)
                {
                    if (_shell.FocusSidebarFirst()) return;
                }
            }
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

        /// <summary>Toward the sidebar = toward the flow-start edge: right in RTL, left in LTR (the strip sits at the start edge).</summary>
        private bool IsTowardSidebar(FocusNavigationDirection direction)
        {
            var rtl = _shell.FlowDirection == FlowDirection.RightToLeft;
            return rtl ? direction == FocusNavigationDirection.Right : direction == FocusNavigationDirection.Left;
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
            // (d) Watchdog: keep trying while async content loads. Reuse
            // the running timer when one exists (an armed-without-settle
            // enforcement timer, or the previous settle's timer, which
            // StopSettle deliberately leaves running so CancelArrival
            // cannot strand the hard-timeout enforcement); create and
            // attach only when none runs, so the Tick handler is attached
            // exactly once and at most one timer ever runs.
            if (_settleTimer == null)
            {
                _settleTimer = new DispatcherTimer();
                _settleTimer.Interval = SettleWatchdogInterval;
                _settleTimer.Tick += OnSettleWatchdogTick;
                _settleTimer.Start();
            }
        }

        /// <summary>
        /// Cancels the pending settle WITHOUT clearing the guard: the caller
        /// is starting a new navigation (or a new settle), which owns the
        /// guard from here. Call ClearNavigationGuard explicitly if no new
        /// settle follows, or the strip stays unopenable. The shared
        /// watchdog/enforcement timer keeps running across the cancel —
        /// stopping it here used to strand the guard armed with no tick
        /// left to enforce the hard timeout; the tick's idle branch stops
        /// it once the guard is down.
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
            // stop the settle without clearing the guard — the new page's
            // settle owns it. The shared timer keeps running: the new
            // settle reuses it in RequestArrival, or the idle branch below
            // stops it once the guard is down.
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
        /// One settle attempt. On a Back arrival, focus already inside the
        /// page counts as success WITHOUT moving anything when no key is
        /// recorded for the entry (a late attempt must never yank focus the
        /// user already placed); when a key IS recorded, the recorded card
        /// is tried first (TrySettleOnce) before accepting the focus already
        /// in the page — that focus may be the framework's park, not the
        /// recorded card. On a fresh arrival it does not — the focus may be
        /// the cached page's stale
        /// focus (Home/Search are Required-cache, so re-navigating Home
        /// returns the same instance with focus still on a card), and
        /// accepting it would declare the settle done on a card instead of
        /// the arrival target. Fresh arrivals always run TrySettleOnce,
        /// then verify focus really landed inside the page. Logs the attempt
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
                if (_settleIsBack && IsFocusInsidePage(page))
                {
                    // Back arrival with focus already inside the page: that
                    // focus may be the framework's park (first card / grid),
                    // not the card this entry recorded. When a key is
                    // recorded for the entry, try it first — TrySettleOnce
                    // restores the recorded card, falling back to the
                    // arrival target when the key cannot land. The shortcut
                    // stays only when no key is recorded for the entry.
                    string backKey;
                    if (TryGetLastFocusKey(page.EntryKey, out backKey))
                    {
                        var restored = TrySettleOnce(page, true) && IsFocusInsidePage(page);
                        Log.Write("Settle back restore: key=" + backKey + ", restored=" + restored + ", focusedCard=" + (FocusedCardMetaId() ?? "(none)"));
                        if (restored) settled = true;
                    }
                    else settled = true;
                }
                else if (!ReferenceEquals(FocusManager.GetFocusedElement(), _settleInitialFocus) && !IsFocusInSidebar && FocusablePage.IsRealFocusTarget(CurrentFocused))
                {
                    // The user deliberately moved focus off the settle's
                    // starting element (e.g. Up/Down in the strip inside
                    // the 200 ms ignore window): treat as takeover instead
                    // of yanking focus back to the arrival target. A bare
                    // ItemsControl the framework parked on is not a
                    // deliberate move — it falls through below instead.
                    Log.Write("Settle takeover: focus moved by the user; stopping without placing arrival focus");
                    StopSettle(clearGuard: true);
                }
                else
                {
                    // Focus moved off the settle's starting element but
                    // not onto a real target — inside the sidebar (a
                    // D-pad press while the strip was still collapsed and
                    // the content not loaded yet), or parked by the
                    // framework on a bare ItemsControl: not a takeover —
                    // keep retrying below until the content loads and the
                    // arrival target lands.
                    if (IsFocusInSidebar)
                    {
                        Log.Write("Settle: focus moved inside the sidebar during settle; ignoring, settle continues");
                    }
                    else if (!ReferenceEquals(FocusManager.GetFocusedElement(), _settleInitialFocus))
                    {
                        // Null focus is not a bare container: the framework
                        // dropped focus entirely (e.g. the old page detached
                        // before the new one loaded). Log it as null so the
                        // log never claims a container was parked on.
                        Log.Write(FocusManager.GetFocusedElement() == null
                            ? "Settle: focus is null during settle; ignoring, settle continues"
                            : "Settle: focus parked on a bare container during settle; ignoring, settle continues");
                    }
                    if (TrySettleOnce(page, _settleIsBack) && IsFocusInsidePage(page)) settled = true;
                }
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
            Log.Write("Settle attempt " + _settleAttempts + "/" + SettleMaxAttempts + " (" + source + "): entry=" + page.EntryKey + ", isBack=" + _settleIsBack + ", settled=" + settled + ", focused=" + (focused != null ? focused.GetType().Name : "null") + ", focusName=" + focusName + ", focusTag=" + focusTag + ", focusedCard=" + (FocusedCardMetaId() ?? "(none)"));
            return settled;
        }

        /// <summary>Meta.Id of the currently focused catalog card, if focus is
        /// on a GridViewItem holding one; null otherwise. Diagnostic only —
        /// Meta.Id only, never URLs or keys.</summary>
        private static string FocusedCardMetaId()
        {
            var container = FocusManager.GetFocusedElement() as GridViewItem;
            if (container == null) return null;
            var card = container.Content as CatalogCardItem;
            return card != null && card.Meta != null ? card.Meta.Id : null;
        }

        /// <summary>Index of the currently focused card in its GridView,
        /// or -1 when focus is not on a GridViewItem in a GridView.
        /// Diagnostic only — pairs with FocusedCardMetaId so a restore
        /// log line names both the card and where it sits.</summary>
        private static int FocusedCardIndex()
        {
            var container = FocusManager.GetFocusedElement() as GridViewItem;
            if (container == null) return -1;
            var grid = ItemsControl.ItemsControlFromItemContainer(container) as GridView;
            if (grid == null) return -1;
            return grid.IndexFromContainer(container);
        }

        /// <summary>
        /// Settle finished: drop the ContentReady subscription and clear
        /// the sidebar guard — the guard clear also stops the shared
        /// watchdog/enforcement timer now that no settle runs (see
        /// ClearNavigationGuard). Success, watchdog expiry and user
        /// takeover are the settle-side paths that clear it (plus the hard
        /// guard timeout, which clears the guard without ending the
        /// settle); a page change leaves it to the new settle.
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
            // Capture before StopSettle clears the settle state: a Back
            // settle that restored a recorded key earns a post-settle
            // hold check.
            var isBack = _settleIsBack;
            var restoredKey = _settleRestoredKey;
            var expectedFocus = FocusManager.GetFocusedElement();
            Log.Write("Settle " + (success ? "succeeded" : "expired") + " after " + attempts + " attempts; clearing the sidebar guard");
            // cancelHoldCheck: false — CompleteSettle runs twice for
            // Catalog arrivals (LayoutUpdated re-enters inside
            // TryFocusKey's UpdateLayout), and the second call's
            // StopSettle must not cancel the hold check the first call
            // just armed. A stale check from an earlier settle cannot
            // be pending here: RequestArrival / CancelArrival /
            // takeover cancel it through StopSettle's default.
            StopSettle(clearGuard: true, cancelHoldCheck: false);
            if (success && isBack && page != null && !string.IsNullOrEmpty(restoredKey))
            {
                ArmHoldCheck(page, restoredKey, expectedFocus, milliseconds);
            }
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

        /// <summary>
        /// Arms the post-settle hold check for a Back settle that restored
        /// a recorded key. At most HoldCheckMaxChecks checks, one
        /// HoldCheckInterval apart, each landing inside the 2 s guard
        /// budget from settle start; checks that would land past the
        /// budget are not armed. Idempotent: the second (re-entrant)
        /// CompleteSettle of the same arrival must not arm a second
        /// timer — but that call normally never reaches here, because
        /// StopSettle cleared _settleRestoredKey before it reads it.
        /// Snapshots _inputCount here: the press that caused the Back
        /// navigation was already counted in HandleInput before the
        /// settle ran, so only a press after this snapshot counts as
        /// user input at a tick.
        /// </summary>
        private void ArmHoldCheck(FocusablePage page, string key, object expectedFocus, long elapsedMs)
        {
            if (_holdCheckTimer != null)
            {
                Log.Write("Hold check already armed for key=" + _holdCheckKey + "; skipping duplicate arm");
                return;
            }
            var remainingMs = (long)GuardHardTimeout.TotalMilliseconds - elapsedMs;
            var checks = 0;
            for (var k = 1; k <= HoldCheckMaxChecks; k++)
            {
                if (remainingMs >= k * (long)HoldCheckInterval.TotalMilliseconds) checks = k;
            }
            if (checks == 0)
            {
                Log.Write("Hold check not armed: key=" + key + ", elapsed=" + elapsedMs + "ms leaves no room inside the 2s guard budget");
                return;
            }
            _holdCheckPage = page;
            _holdCheckKey = key;
            _holdCheckExpected = expectedFocus;
            _holdCheckChecksLeft = checks;
            _holdCheckRestoreUsed = false;
            _holdCheckInputCount = _inputCount;
            _holdCheckTimer = new DispatcherTimer();
            _holdCheckTimer.Interval = HoldCheckInterval;
            _holdCheckTimer.Tick += OnHoldCheckTick;
            _holdCheckTimer.Start();
            Log.Write("Hold check armed: key=" + key + ", checks=" + checks + ", focusedCard=" + (FocusedCardMetaId() ?? "(none)") + ", inputCount=" + _inputCount + ", elapsed=" + elapsedMs + "ms");
        }

        /// <summary>Stops the hold-check timer and drops its state. No
        /// timer runs when idle: this is called when the checks are done,
        /// when the user moved focus deliberately, when the page changed,
        /// and by StopSettle (new navigation / cancel / takeover).</summary>
        private void CancelHoldCheck()
        {
            var timer = _holdCheckTimer;
            _holdCheckTimer = null;
            if (timer != null)
            {
                timer.Tick -= OnHoldCheckTick;
                timer.Stop();
            }
            _holdCheckPage = null;
            _holdCheckKey = null;
            _holdCheckExpected = null;
            _holdCheckChecksLeft = 0;
            _holdCheckRestoreUsed = false;
            _holdCheckInputCount = 0;
        }

        /// <summary>
        /// One hold check: is focus still on the card the Back settle
        /// restored? Still there — verified, and the next check (if any)
        /// still runs, so a later drop is caught too. Same card in a new
        /// container (a late layout pass re-realized the GridViewItem)
        /// is verified too: the focused card's Meta.Id matches the
        /// restored "meta:" key, so focus never left the card. On a real
        /// target elsewhere, the input clock decides: a press since
        /// arming means the user moved focus deliberately — skip and
        /// cancel, never yank focus the user placed. No press since
        /// arming means the framework moved focus itself (a late layout
        /// pass re-realizing the container parks focus on the nearest
        /// target) — fall through to the re-restore below instead of
        /// cancelling. Focus gone with no user move — re-apply the
        /// recorded key through TrySettleOnce, exactly once per arrival;
        /// a second check after a re-restore only verifies it held.
        /// The re-restore routes through the page's TryFocusKey, so no
        /// new .Focus() site is added here.
        /// </summary>
        private void OnHoldCheckTick(object sender, object e)
        {
            var timer = _holdCheckTimer;
            if (timer != null) timer.Stop();
            var page = _holdCheckPage;
            var key = _holdCheckKey;
            if (page == null || key == null || !ReferenceEquals(_frame.Content, page))
            {
                Log.Write("Hold check cancelled: page changed");
                CancelHoldCheck();
                return;
            }
            _holdCheckChecksLeft--;
            var focused = FocusManager.GetFocusedElement();
            if (ReferenceEquals(focused, _holdCheckExpected))
            {
                Log.Write("Hold check verified: key=" + key + ", focusedCard=" + (FocusedCardMetaId() ?? "(none)") + ", checksLeft=" + _holdCheckChecksLeft);
                FinishHoldCheckTick();
                return;
            }
            // Same card, new container: a late layout pass re-realized
            // the GridViewItem for the restored card, so ReferenceEquals
            // fails even though focus never left the card. The new
            // container becomes the expected focus for the next check.
            if (focused != null && key.StartsWith("meta:", StringComparison.Ordinal)
                && string.Equals(FocusedCardMetaId(), key.Substring(5), StringComparison.Ordinal))
            {
                _holdCheckExpected = focused;
                Log.Write("Hold check verified: key=" + key + ", focusedCard=" + (FocusedCardMetaId() ?? "(none)") + ", checksLeft=" + _holdCheckChecksLeft + " (re-realized container)");
                FinishHoldCheckTick();
                return;
            }
            if (focused != null && FocusablePage.IsRealFocusTarget(CurrentFocused))
            {
                var focusedElement = focused as FrameworkElement;
                var focusName = focusedElement != null && !string.IsNullOrEmpty(focusedElement.Name) ? focusedElement.Name : "(none)";
                if (_inputCount != _holdCheckInputCount)
                {
                    Log.Write("Hold check skipped-user-input: key=" + key + ", focused=" + focused.GetType().Name + ", focusName=" + focusName + ", focusedCard=" + (FocusedCardMetaId() ?? "(none)"));
                    CancelHoldCheck();
                    return;
                }
                // No input since arming: the framework moved focus itself
                // — a late layout pass re-realizing the container parks
                // focus on the nearest target (log nuvio-vlc_16: the
                // "All" filter chip, the only Button in the page). That
                // is not a user move, so never-yank does not apply: fall
                // through to the re-restore below instead of cancelling.
                Log.Write("Hold check framework-move: key=" + key + ", focused=" + focused.GetType().Name + ", focusName=" + focusName + ", focusedCard=" + (FocusedCardMetaId() ?? "(none)") + ", checksLeft=" + _holdCheckChecksLeft);
            }
            if (_holdCheckRestoreUsed)
            {
                Log.Write("Hold check: focus lost, restore already used; leaving as-is, key=" + key);
                CancelHoldCheck();
                return;
            }
            _holdCheckRestoreUsed = true;
            string currentKey;
            var keyStillRecorded = TryGetLastFocusKey(page.EntryKey, out currentKey) && currentKey == key;
            var restored = false;
            if (keyStillRecorded)
            {
                restored = TrySettleOnce(page, true) && IsFocusInsidePage(page);
                if (restored) _holdCheckExpected = FocusManager.GetFocusedElement();
            }
            Log.Write("Hold check re-restored: key=" + key + ", keyStillRecorded=" + keyStillRecorded + ", restored=" + restored + ", focusedCard=" + (FocusedCardMetaId() ?? "(none)") + ", checksLeft=" + _holdCheckChecksLeft);
            FinishHoldCheckTick();
        }

        /// <summary>After one check: run the next check one interval
        /// later while checks remain, else drop the hold-check state.</summary>
        private void FinishHoldCheckTick()
        {
            if (_holdCheckChecksLeft > 0 && _holdCheckTimer != null)
            {
                _holdCheckTimer.Start();
                return;
            }
            CancelHoldCheck();
        }

        private void StopSettle(bool clearGuard, bool cancelHoldCheck = true)
        {
            // The shared watchdog/enforcement timer is NOT stopped here:
            // it is the same timer ArmNavigationGuard starts for the
            // guard hard timeout, and stopping it on CancelArrival used
            // to strand the guard armed with no tick left to enforce the
            // 2 s bound. It stops in ClearNavigationGuard (settle end
            // with the guard down) or in the tick's idle branch (no
            // settle, guard down) — so no timer runs when idle.
            // The hold-check timer IS stopped here by default: a new
            // navigation, a cancel or a takeover ends the premise of
            // any pending check. CompleteSettle passes false — its
            // second (re-entrant) call must not cancel the check the
            // first call just armed (see CompleteSettle).
            if (cancelHoldCheck) CancelHoldCheck();
            UnsubscribeSettleLayout();
            var page = _settlingPage;
            if (page != null) page.ContentReady -= OnSettleContentReady;
            _settlingPage = null;
            _settleInitialFocus = null;
            _settleRestoredKey = null;
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
            if (IsFocusInSidebar)
            {
                // A D-pad press inside the strip during the settle is not
                // a takeover: the strip is collapsed and the content is
                // not loaded yet, so the press only shuffled focus between
                // sidebar buttons. Keep the settle running; the arrival
                // target lands when the content loads.
                Log.Write("Settle: user input moved focus inside the sidebar during settle; ignoring, settle continues");
                return;
            }
            if (!FocusablePage.IsRealFocusTarget(CurrentFocused))
            {
                // Focus parked on a bare container is not a deliberate
                // move either — the framework parks focus there while
                // content loads, same as the sidebar ignore above. Keep
                // the settle running; the arrival target lands when the
                // content loads. (A null CurrentFocused is not a real
                // target either, and is ignored the same way — but it is
                // null focus, not a bare container, so log it as such.)
                Log.Write(CurrentFocused == null
                    ? "Settle: user input left focus null during settle; ignoring, settle continues"
                    : "Settle: user input moved focus onto a bare container during settle; ignoring, settle continues");
                return;
            }
            Log.Write("Settle cancelled by user input after " + _settleAttempts + " attempts; clearing the sidebar guard");
            StopSettle(clearGuard: true);
        }

        private bool TrySettleOnce(Page page, bool isBack)
        {
            // Reporting only: cleared on every call so the field always
            // says what THIS call restored via a recorded key (null when
            // it fell through to TryFocusArrival or failed). The restore
            // logic below is untouched.
            _settleRestoredKey = null;
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
                        if (arrival.TryFocusKey(key))
                        {
                            _settleRestoredKey = key;
                            Log.Write("Settle restore via key: key=" + key + ", focusedCard=" + (FocusedCardMetaId() ?? "(none)") + ", index=" + FocusedCardIndex());
                            return true;
                        }
                    }
                }
                if (!isBack && page is HomePage)
                {
                    // Cached Home re-navigation: HomePage.TryFocusArrival
                    // bails out (returns true without moving) when focus is
                    // already inside the page, so it cannot move focus off
                    // the stale card. The keyed form has no bail-out:
                    // "hero" always focuses the hero Play button when a
                    // hero exists. When there is none it returns false and
                    // the arrival call below handles the empty-library
                    // target (ManageAddons) exactly as before.
                    if (arrival.TryFocusKey("hero")) return true;
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
            // A bare ItemsControl the framework parked on is inside the
            // page but is not a real target (no ring, no interaction):
            // it must not count as arrival.
            return IsFocusInside(page) && FocusablePage.IsRealFocusTarget(CurrentFocused);
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
            // down. This is the same timer field the settle uses, so at
            // most one timer ever runs — RequestArrival reuses a running
            // one and creates one only when none runs, StopSettle never
            // stops it (a cancel must not strand the hard-timeout
            // enforcement), and it stops in ClearNavigationGuard (settle
            // end, guard down) or the tick's idle branch. No timer runs
            // when idle: it starts only on arm / settle and stops on
            // guard-clear / settle end.
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
            // land). This is the stop point for a finished settle:
            // StopSettle no longer stops the timer (it must survive
            // CancelArrival), so the guard clear here is what ends it.
            if (_settlingPage == null) StopWatchdog();
        }

        /// <summary>True while the navigation guard is armed (focus landing on a sidebar button after navigation must not expand the strip).</summary>
        public bool IsNavigationGuardArmed { get { return _navigationGuardArmed; } }

        /// <summary>
        /// Pauses coordinator input and pushes the dialog invoker — the
        /// currently focused element when null is passed — onto the
        /// dialog stack for the restore-on-close. The invoker may be null
        /// (focus was not on a Control); it is pushed as-is so open/close
        /// pairs stay matched. Focus is NOT moved here.
        /// </summary>
        public void NotifyDialogOpened(Control invoker)
        {
            var captured = invoker ?? FocusManager.GetFocusedElement() as Control;
            _dialogInvokers.Push(captured);
            Log.Write("Dialog opened; coordinator input paused; invoker=" + (captured != null ? captured.GetType().Name : "null") + "; depth=" + _dialogInvokers.Count);
        }

        /// <summary>
        /// Resumes coordinator input and schedules the restore-on-close
        /// one tick later — but only when the stack empties: while another
        /// dialog is still open the outer dialog's invoker stays buried
        /// and is restored when that dialog closes. A close with no open
        /// dialog logs a warning and does nothing (never crashes).
        /// </summary>
        public void NotifyDialogClosed()
        {
            if (_dialogInvokers.Count == 0)
            {
                Log.Write("Dialog closed with no dialog open; ignoring (no restore)");
                return;
            }
            var invoker = _dialogInvokers.Pop();
            if (_dialogInvokers.Count > 0)
            {
                Log.Write("Dialog closed; another dialog still open (depth=" + _dialogInvokers.Count + "); restore deferred");
                return;
            }
            Log.Write("Dialog closed; coordinator input resumed; scheduling restore to invoker=" + (invoker != null ? invoker.GetType().Name : "null"));
            // One tick later, on the UI thread, so the dialog's close has
            // unwound before focus moves (CoreDispatcher.RunAsync with
            // CoreDispatcherPriority.Normal — Windows.UI.Core, UWP since
            // 10.0.10240.0; the same call FocusTestHarness already uses).
            var _ = _shell.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => RestoreDialogInvoker(invoker));
        }

        /// <summary>True while a dialog is open; the controller is paused (see HandleInput).</summary>
        public bool IsDialogOpen { get { return _dialogInvokers.Count > 0; } }

        /// <summary>
        /// Park OSD focus when the player OSD hides — auto-hide or manual,
        /// the rule is the same. The page passes the OSD element that had
        /// focus (null when focus was outside the OSD or nowhere — then
        /// nothing is parked: a visible NextCard/ErrorBox button keeps
        /// focus). Focus is parked on Play/Pause via the page's
        /// TryFocusKey("play") contract, so the hidden OSD never holds
        /// focus on a random invisible button and the next show lands on
        /// Play/Pause. No element is remembered: the anchor is always
        /// Play/Pause. No-op when a dialog is open (the dialog owns
        /// focus) or the element is not a real target. When the park key
        /// fails, focus is left as-is; the anchor is still armed, so the
        /// show still focuses Play/Pause.
        /// </summary>
        public void NotifyOsdHidden(Control osdElement)
        {
            if (osdElement == null || !FocusablePage.IsRealFocusTarget(osdElement)) return;
            if (IsDialogOpen)
            {
                Log.Write("OSD hidden; park skipped: dialog open");
                return;
            }
            _osdPlayPauseAnchor = true;
            var page = _frame.Content as IArrivalFocus;
            if (page != null && page.TryFocusKey("play"))
            {
                Log.Write("OSD hidden; parked focus on Play/Pause (OSD anchor)");
            }
            else
            {
                Log.Write("OSD hidden; park on Play/Pause failed; anchor armed (focus left as-is)");
            }
        }

        /// <summary>
        /// Focus Play/Pause when the player OSD shows again after a hide
        /// (auto-hide or manual — the anchor was armed by
        /// NotifyOsdHidden either way). A show with no preceding hide is
        /// a no-op. Skipped entirely when a dialog is open (the dialog
        /// owns focus) or an arrival settle is running — the anchor is
        /// cleared in both cases, since the dialog/settle owns focus from
        /// then on. Synchronous: it runs inside the ControlsVisible
        /// property change, before the ViewModel raises its focus hint, so
        /// a specific hint ("seek", "back", ...) still wins after it —
        /// the hint runs second and moves focus to its own target.
        /// </summary>
        public void NotifyOsdShown()
        {
            if (!_osdPlayPauseAnchor) return;
            _osdPlayPauseAnchor = false;
            if (IsDialogOpen)
            {
                Log.Write("OSD shown; Play/Pause focus skipped: dialog open");
                return;
            }
            if (_settlingPage != null)
            {
                Log.Write("OSD shown; Play/Pause focus skipped: arrival settle is running");
                return;
            }
            var page = _frame.Content as IArrivalFocus;
            if (page != null && page.TryFocusKey("play"))
            {
                Log.Write("OSD shown; focus on Play/Pause (OSD anchor)");
                return;
            }
            Log.Write("OSD shown; Play/Pause focus failed; leaving focus as-is");
        }

        /// <summary>
        /// One-tick-later dialog restore (Phase 3 — the only restore path
        /// when the flag is on; the pages' synchronous
        /// RestoreDialogInvoker is the flag-off path and a no-op when the
        /// flag is on). Skip when a dialog opened again before the tick ran, or
        /// when an arrival settle is running (the settle owns arrival —
        /// e.g. a navigation started from a dialog choice); skip when
        /// focus is already on a real, valid target inside the current
        /// page (the page's own restore already landed). Otherwise focus
        /// the popped invoker when it is a real, valid target in the live
        /// tree under the current page, else fall back to the page's
        /// arrival target — and leave focus as-is when that fails too.
        /// Never parks focus on a bare container.
        /// </summary>
        private void RestoreDialogInvoker(Control invoker)
        {
            if (IsDialogOpen)
            {
                Log.Write("Dialog restore skipped: another dialog is open");
                return;
            }
            if (_settlingPage != null)
            {
                Log.Write("Dialog restore skipped: arrival settle is running");
                return;
            }
            var page = _frame.Content as FocusablePage;
            if (page != null && IsFocusInsidePage(page) && IsFocusValid(CurrentFocused))
            {
                Log.Write("Dialog restore skipped: focus is already on a valid target in the current page");
                return;
            }
            if (invoker != null && FocusablePage.IsRealFocusTarget(invoker) && IsFocusValid(invoker) && IsDescendantOfCurrentPage(invoker))
            {
                if (invoker.Focus(FocusState.Programmatic))
                {
                    Log.Write("Dialog restore: focus returned to invoker " + invoker.GetType().Name);
                    return;
                }
                Log.Write("Dialog restore: invoker Focus() failed; falling back to the page arrival target");
            }
            else
            {
                Log.Write("Dialog restore: invoker is not a valid target in the current page; falling back to the page arrival target");
            }
            if (page != null)
            {
                var arrived = page.TryFocusArrival();
                Log.Write("Dialog restore: arrival fallback " + (arrived ? "succeeded" : "failed") + "; leaving focus as-is on failure");
            }
            else
            {
                Log.Write("Dialog restore: no FocusablePage is current; leaving focus as-is");
            }
        }

        /// <summary>True when element is the frame's current content or one of its descendants (live page membership for the dialog restore).</summary>
        private bool IsDescendantOfCurrentPage(DependencyObject element)
        {
            var content = _frame.Content as DependencyObject;
            if (content == null || element == null) return false;
            for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, content)) return true;
            }
            return false;
        }

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
                if (first != null)
                {
                    first.Focus(FocusState.Programmatic);
                    // Guarantee: FindFirstFocusableElement walks tab order,
                    // and the nine bare ItemsControls are all
                    // IsTabStop=false, so it cannot return one — verified
                    // by reading the XAML (Catalog Genres; Details Genres
                    // + Credits; Home HeroGenres + CatalogRows; Search
                    // RecentSearches + Rows; Streams Badges + StatusItems).
                    // The check below is the backstop anyway: a future
                    // bare container added without the flag would fail it
                    // and log instead of parking focus invisibly.
                    if (!IsFocusValid(CurrentFocused))
                    {
                        Log.Write("NotifyContentChanged: last-resort focus did not land on a valid target; leaving focus as-is");
                    }
                }
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
            // A bare ItemsControl the framework parked on is not a real
            // target (no ring, no interaction) — never valid, even
            // though it is visible, enabled and in the live tree.
            if (!FocusablePage.IsRealFocusTarget(focused)) return false;
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
