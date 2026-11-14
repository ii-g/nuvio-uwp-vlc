using System;
using System.Collections.Generic;
using Nuvio.Diagnostics;
using Nuvio.Services;
using Nuvio.ViewModels;
using Nuvio.Views;
using Windows.ApplicationModel.Resources;
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
        // Central last-focus registry: one entry per navigation ENTRY
        // (page type + parameter identity), plus a per-row map (rowKey ->
        // key) per entry. Keying by entry — not by page type — is the fix
        // for root cause (B): the Settings root ("settings|root") and a
        // section ("settings|addons") are different entries and never
        // overwrite each other, so Back restores the section actually
        // visited. Keys are stable content ids, never stream URLs, API
        // keys or tokens. The rowKey is supplied by the page at record
        // time (data identity, not ordinal position).
        private readonly Dictionary<string, string> _arrivalFocusKeys = new Dictionary<string, string>();
        private readonly Dictionary<string, Dictionary<string, string>> _rowFocusKeys = new Dictionary<string, Dictionary<string, string>>();

        // Arrival-settle state (Fix 2): the event-driven settle. Only one
        // settle runs at a time; a new navigation cancels the previous one.
        // The guard is cleared only by settle success or watchdog expiry
        // (or by user takeover via NoteUserInput) — never in a finally.
        private FocusablePage _settlingPage;
        private bool _settleIsBack;
        // Navigation parameter of the current settle (the SettleArrivalFocus
        // argument): TrySettleOnce rebuilds the entry key from it while
        // EntryKey is still empty (Navigated runs before OnNavigatedTo).
        private object _settleParameter;
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
        private const int SettleMaxAttempts = 10;
        private static readonly TimeSpan SettleWatchdogInterval = TimeSpan.FromMilliseconds(500);
        // Inputs arriving within this window of a settle starting are the
        // duplicate delivery of the press that caused the navigation (the
        // console delivers one press both as a XAML key event and through
        // the 30 ms controller poll): they must not count as user takeover
        // and kill the settle the navigation just started.
        private static readonly TimeSpan SettleInputIgnoreWindow = TimeSpan.FromMilliseconds(200);

        /// <summary>True while an arrival settle (first attempt / ContentReady wait / watchdog) is active for the current page.</summary>
        internal bool IsSettling { get { return _settlingPage != null; } }

        /// <summary>
        /// Phase 2 Focus Manager coordinator, linked by MainPage after both
        /// are constructed (the coordinator takes the router's HandleBack
        /// as its back delegate; row/edge moves live in the coordinator
        /// itself now, so the link cannot be passed in the constructor).
        /// Null until linked. When set and
        /// FocusCoordinator.UseFocusCoordinator is true, Handle delegates
        /// to the coordinator and SettleArrivalFocus routes Settings,
        /// Home, Search and Catalog pages to it; the legacy paths below
        /// stay as the rollback path. Never null-checked into a different
        /// behaviour when the flag is off: every flag check below falls
        /// through to the legacy body exactly as before.
        /// </summary>
        internal FocusCoordinator Coordinator { get; set; }

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

        public NativeInputRouter(MainPage shell, Frame frame, NavigationService navigation)
        {
            _shell = shell;
            _frame = frame;
            _navigation = navigation;
        }

        /// <summary>
        /// Records the last-focused key for a navigation entry (Back restore)
        /// and, when rowKey is supplied, the last-focused key for that row
        /// within the entry (row-to-row landing). Overwrites any earlier key
        /// for that entry / row. rowKey is null for non-row items.
        /// </summary>
        internal void RecordArrivalFocusKey(string entryKey, string key, string rowKey = null)
        {
            if (string.IsNullOrEmpty(entryKey) || string.IsNullOrEmpty(key)) return;
            _arrivalFocusKeys[entryKey] = key;
            if (!string.IsNullOrEmpty(rowKey))
            {
                Dictionary<string, string> rows;
                if (!_rowFocusKeys.TryGetValue(entryKey, out rows))
                {
                    rows = new Dictionary<string, string>();
                    _rowFocusKeys[entryKey] = rows;
                }
                rows[rowKey] = key;
            }
        }

        /// <summary>Looks up the entry's last-focused key (Back arrival). False when none was recorded.</summary>
        internal bool TryGetLastFocusKey(string entryKey, out string key)
        {
            key = null;
            if (string.IsNullOrEmpty(entryKey)) return false;
            return _arrivalFocusKeys.TryGetValue(entryKey, out key) && !string.IsNullOrEmpty(key);
        }

        /// <summary>Looks up the last-focused key for one row of an entry. False when the row was never visited.</summary>
        internal bool TryGetRowFocusKey(string entryKey, string rowKey, out string key)
        {
            key = null;
            if (string.IsNullOrEmpty(entryKey) || string.IsNullOrEmpty(rowKey)) return false;
            Dictionary<string, string> rows;
            if (!_rowFocusKeys.TryGetValue(entryKey, out rows)) return false;
            return rows.TryGetValue(rowKey, out key) && !string.IsNullOrEmpty(key);
        }

        /// <summary>Forgets the per-row memory of one entry (fresh-arrival invalidation). The entry's arrival key is untouched, so Back restore still works; only the row map is dropped, so the next row-to-row move in a fresh visit falls back to the row's first poster and the map rebuilds from GotFocus records during this visit.</summary>
        internal void ClearRowFocusKeys(string entryKey)
        {
            if (string.IsNullOrEmpty(entryKey)) return;
            _rowFocusKeys.Remove(entryKey);
        }

        /// <summary>
        /// Central arrival focus, called by the shell from
        /// ContentFrame_Navigated (Fix 2, root cause C). Event-driven, not a
        /// bounded tick chain: (a) the FIRST attempt runs synchronously, in
        /// this call — Frame.Navigated fires once the content is available
        /// from the frame (although it "may not have completed loading",
        /// Microsoft Learn: Frame.Navigated), so a sync page whose controls
        /// are already constructed can take focus here, before first paint;
        /// (b) when the page is not IsContentReady yet (async pages: the
        /// hero / streams / details data has not loaded), a one-shot
        /// ContentReady subscription settles the moment the content arrives;
        /// (c) a 500 ms watchdog retries up to 10 attempts (~5 s) and stops
        /// on success, on first genuine user input, on page change, or on
        /// expiry. The sidebar guard is cleared ONLY on success (focus
        /// verifiably inside the page) or on watchdog expiry — never in a
        /// finally "whatever the outcome". A null / non-FocusablePage
        /// content clears the guard and logs (defensive; after the page
        /// migration this must not happen). <paramref name="parameter"/> is
        /// the navigation parameter, already folded into
        /// <see cref="FocusablePage.EntryKey"/> by OnNavigatedTo; it is
        /// accepted so the shell passes the Navigated args through unchanged.
        /// </summary>
        internal void SettleArrivalFocus(FocusablePage page, bool isBack, object parameter)
        {
            // Phase 2: Settings, Home, Search and Catalog are migrated —
            // their settle is coordinator-owned. A coordinator settle from
            // a previous navigation is cancelled first when a legacy settle
            // takes over (and vice versa: RequestArrival stops its own
            // settle), so only one settle ever runs. Every other page keeps
            // the legacy settle below, unchanged. With the flag off this
            // block is skipped entirely and the legacy body runs exactly
            // as before.
            if (Coordinator != null && FocusCoordinator.UseFocusCoordinator)
            {
                if (page is SettingsPage || page is HomePage || page is SearchPage || page is CatalogPage)
                {
                    StopSettle(clearGuard: false);
                    Coordinator.RequestArrival(page, isBack);
                    return;
                }
                Coordinator.CancelArrival();
            }
            StopSettle(clearGuard: false);
            if (page == null)
            {
                Log.Write("SettleArrivalFocus: content is not a FocusablePage, clearing the sidebar guard");
                // Phase 3: same guard ownership as StopSettle — through
                // the coordinator when it owns the guard, else the shell
                // bit directly.
                if (Coordinator != null && FocusCoordinator.UseFocusCoordinator) Coordinator.ClearNavigationGuard();
                else _shell.ClearSidebarExpandSuppression();
                return;
            }
            _settlingPage = page;
            _settleIsBack = isBack;
            _settleParameter = parameter;
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
            var page = _settlingPage;
            if (page == null)
            {
                StopWatchdog();
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
        /// number, entry key, result and the focused element's type after
        /// the attempt (ids only — never URLs, keys or tokens).
        /// </summary>
        private bool AttemptSettle(string source)
        {
            var page = _settlingPage;
            if (page == null) return false;
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
                    if (TryGetLastFocusKey(SettleEntryKey(page), out backKey))
                    {
                        var restored = TrySettleOnce(page, true) && IsFocusInsidePage(page);
                        Log.Write("Settle back restore: key=" + backKey + ", restored=" + restored + ", focusedCard=" + (FocusedCardMetaId() ?? "(none)"));
                        if (restored) settled = true;
                    }
                    else settled = true;
                }
                else if (!ReferenceEquals(FocusManager.GetFocusedElement(), _settleInitialFocus)
                    && FocusablePage.IsRealFocusTarget(FocusManager.GetFocusedElement() as DependencyObject))
                {
                    // The user deliberately moved focus off the settle's
                    // starting element (e.g. Up/Down in the strip inside
                    // the 200 ms ignore window): treat as takeover instead
                    // of yanking focus back to the arrival target. A bare
                    // ItemsControl the framework parked on is not a
                    // deliberate move — it falls through to TrySettleOnce
                    // below instead.
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

        /// <summary>
        /// Settle finished: stop the watchdog, drop the ContentReady
        /// subscription, and clear the sidebar guard. Success and watchdog
        /// expiry are the only paths that clear it (plus user takeover in
        /// NoteUserInput); a page change leaves it to the new settle.
        /// </summary>
        private void CompleteSettle(bool success)
        {
            Log.Write("Settle " + (success ? "succeeded" : "expired") + " after " + _settleAttempts + " attempts; clearing the sidebar guard");
            StopSettle(clearGuard: true);
        }

        private void StopSettle(bool clearGuard)
        {
            StopWatchdog();
            UnsubscribeSettleLayout();
            var page = _settlingPage;
            if (page != null) page.ContentReady -= OnSettleContentReady;
            _settlingPage = null;
            _settleInitialFocus = null;
            _settleParameter = null;
            if (clearGuard)
            {
                // Phase 3: with the flag on the coordinator owns the
                // guard — clear through it (it clears the shell bit too
                // and stops the enforcement timer when no coordinator
                // settle runs). With the flag off the shell bit is
                // cleared directly, as before.
                if (Coordinator != null && FocusCoordinator.UseFocusCoordinator) Coordinator.ClearNavigationGuard();
                else _shell.ClearSidebarExpandSuppression();
            }
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
        /// Called by the shell from its input callbacks when a settle is
        /// pending: the user has taken over, so the settle stops trying to
        /// place focus. Inputs inside the ignore window after the settle
        /// started are the duplicate delivery of the press that caused the
        /// navigation (XAML key event + 30 ms controller poll) and are
        /// ignored — killing the settle for those would re-open root cause
        /// (A). A genuine takeover also clears the guard: with no settle
        /// running there would be nothing left to clear it, and the strip
        /// would stay unopenable.
        /// </summary>
        internal void NoteUserInput()
        {
            if (_settlingPage == null) return;
            if (DateTime.UtcNow - _settleStartedAt < SettleInputIgnoreWindow) return;
            // Takeover only if the press actually moved focus: a press that
            // could not move anything (content not laid out / hero not
            // loaded yet) leaves the settle running, so the arrival target
            // still lands when the page is ready.
            if (ReferenceEquals(FocusManager.GetFocusedElement(), _settleInitialFocus)) return;
            // A bare container is not a real target (Phase 2 fix): the
            // framework parks focus on an ItemsControl while content loads,
            // and that is not the user taking over — leave the settle
            // running so the arrival target still lands.
            if (!FocusablePage.IsRealFocusTarget(FocusManager.GetFocusedElement() as DependencyObject))
            {
                Log.Write("Settle takeover ignored: focus is on a bare container, not a real target; settle continues");
                return;
            }
            Log.Write("Settle cancelled by user input after " + _settleAttempts + " attempts; clearing the sidebar guard");
            StopSettle(clearGuard: true);
        }

        /// <summary>
        /// Entry key for a settle lookup: the page's EntryKey when set, else
        /// rebuilt from the navigation parameter SettleArrivalFocus received
        /// (same formula FocusablePage uses: type + "|" + EntryIdentity).
        /// Covers the first sync attempt, where Navigated runs before
        /// OnNavigatedTo sets EntryKey.
        /// </summary>
        private string SettleEntryKey(Page page)
        {
            var fp = page as FocusablePage;
            if (fp != null && !string.IsNullOrEmpty(fp.EntryKey)) return fp.EntryKey;
            return page.GetType().FullName + "|" + EntryIdentity(_settleParameter);
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
                    var entryKey = SettleEntryKey(page);
                    if (string.IsNullOrEmpty(fp?.EntryKey))
                    {
                        Log.Write("Settle: page " + page.GetType().Name + " has no entry key (not a FocusablePage yet); Back restore falls back to the parameter-derived key");
                    }
                    string key;
                    if (TryGetLastFocusKey(entryKey, out key))
                    {
                        if (arrival.TryFocusKey(key)) return true;
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

        private static bool IsFocusInsidePage(Page page)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            // A bare ItemsControl the framework parked on is inside the
            // page but is not a real target (no ring, no interaction):
            // it must not count as arrival.
            if (!FocusablePage.IsRealFocusTarget(focused)) return false;
            for (var node = focused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, page)) return true;
            }
            return false;
        }

        public void Handle(string action, bool repeat)
        {
            // Phase 1: the coordinator owns input when linked and the flag
            // is on — it performs dispatch plus the takeover decision
            // internally, so MainPage must not also call NoteUserInput
            // afterwards. With the flag off (or unlinked) the legacy body
            // below runs exactly as before.
            if (Coordinator != null && FocusCoordinator.UseFocusCoordinator)
            {
                Coordinator.HandleInput(action, repeat);
                return;
            }
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

        // Internal (not private) so the legacy path stays reachable from
        // the shell: with the flag off the router's own Handle calls this
        // directly. The coordinator no longer takes it as a delegate
        // (Phase 2: it owns row/edge moves itself and takes only
        // HandleBack from here). Accessibility change only; no logic change.
        internal void MoveFocus(FocusNavigationDirection direction)
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

        // Internal (not private) so MainPage can hand this to the
        // FocusCoordinator as its handleBack delegate. Accessibility
        // change only; no logic change.
        internal void HandleBack()
        {
            // A dialog open means the controller is paused and XAML handles back; this never runs then.
            // The current page already had its say via IPageInputHandler above.
            // Arm the sidebar guard BEFORE GoBack: the old page detaches
            // synchronously and the framework's focus fallback can land on
            // a sidebar button while the guard is down (root cause A). The
            // new page's settle clears it once arrival focus lands.
            // Phase 1: arm through the coordinator when it owns the guard
            // (it also arms the shell bit, the enforcement point); the
            // shell method stays the low-level bit setter and never
            // delegates back, so this cannot recurse.
            if (Coordinator != null && FocusCoordinator.UseFocusCoordinator) Coordinator.ArmNavigationGuard();
            else _shell.ArmSidebarExpandSuppression();
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
