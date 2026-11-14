# FOCUS-MIGRATION.md — Focus Manager migration log

The central Focus Manager (`Nuvio/Input/FocusCoordinator.cs`) replaces the
`NativeInputRouter`'s scattered focus ownership one page at a time, behind a
rollback flag. The legacy router code is never deleted during migration — it
stays as the rollback path. The design contract lives in `FOCUS.md` (draft);
this file logs what was actually wired, when, and what is still open. If the
two disagree, the implementation wins and `FOCUS.md` is updated in the same
change.

No visual change in any phase: the white static focus ring, sidebar visuals
and hero behaviour are untouched. No new packages, no WinUI 3 / WPF APIs.

## Rollback flag

`Nuvio/Input/FocusCoordinator.cs`, line ~33:

```csharp
internal static bool UseFocusCoordinator = true;
```

- Default: `true` (coordinator owns input dispatch, the arrival settle
  for Settings, Home, Search and Catalog, the navigation guard, dialog
  pause state and the focus registry for those pages).
- Flip: edit that line to `false` and rebuild. No other change needed.
- When flipped off, every page takes the legacy router paths: `Handle` runs
  the router's own dispatch/dedupe, `SettleArrivalFocus` runs the router's
  settle for every page, the guard is armed/cleared on the shell bit
  directly, `DialogOpen` only pauses the `ControllerBridge`, and the base
  recorder writes only to the router's registry. The coordinator is still
  constructed in `MainPage.OnLoaded` (construction has no side effects: no
  subscriptions, no timers, no focus moves), but nothing calls it. The
  harness still works through `SimulateFocusInput` — it drives the legacy
  path then.

## Phase 0 — harness + coordinator skeleton (2026-11-12)

Files added (not yet wired in this phase):

- `Nuvio/Input/FocusCoordinator.cs` — the central coordinator (settle,
  guard, registry, dialog pause, input entry point). Ported from the router
  with the same algorithms and constants. One deliberate deviation: its
  watchdog never runs the expiry fallback after a user takeover stopped the
  settle.
- `Nuvio/Diagnostics/FocusTestHarness.cs` — scripted focus-test engine.
  Drives input only through the pinned `MainPage.SimulateFocusInput` seam.
  Script 1: launch → hero Play (`TEST launch-hero-play PASS/FAIL` in the
  log). Its logged `settleAttempts` is its own readiness-poll count, not
  the router's/coordinator's internal attempt count.

Problems addressed: no scripted way to prove focus behaviour on the device;
no single owner for settle/guard/registry (state split across `MainPage`,
the router and per-page handlers).

## Phase 1 — integration: coordinator wired, Settings migrated (2026-11-12)

Files changed:

- `Nuvio/Nuvio.csproj` — added `<Compile>` entries for
  `Input\FocusCoordinator.cs` and `Diagnostics\FocusTestHarness.cs` (the
  legacy project lists files explicitly; without these the build never sees
  them).
- `Nuvio/MainPage.xaml.cs` — constructs the coordinator in `OnLoaded` next
  to the router and links both directions (`_router.Coordinator`,
  `Navigation.Coordinator`); adds the pinned seam
  `SimulateFocusInput(action, repeat)` and the shared
  `HandleControllerInput(action, repeat)` the poll callback and the seam
  both call; adds `LaunchArguments` + an `OnNavigatedTo` override that arms
  the harness; `DialogOpen` routes through the coordinator when the flag is
  on (pause + invoker capture only, no focus move) and keeps the
  `ControllerBridge` pause in both branches; `FocusSidebar` clears through
  the coordinator when the flag is on; the poll callback and
  `OnPreviewKeyDown` guard blocks cover the coordinator's settle when the
  flag is on (see below).
- `Nuvio/Input/NativeInputRouter.cs` — `Handle` delegates to the
  coordinator when linked and the flag is on; `SettleArrivalFocus` routes
  `SettingsPage` to `Coordinator.RequestArrival` and cancels the other
  owner's settle on every navigation (only one settle ever runs);
  `MoveFocus` / `HandleBack` are `internal` now (accessibility only) so
  `MainPage` can hand them to the coordinator as delegates; `HandleBack`
  arms through the coordinator when the flag is on; the `AttemptSettle` log
  line now appends `focusName=` / `focusTag=` in the coordinator's exact
  format (Phase 0b). Legacy bodies are untouched in behaviour.
- `Nuvio/Services/NavigationService.cs` — adds a `Coordinator` property;
  the `Navigate` choke-point and `GoBackOrHome` arm through the coordinator
  when the flag is on, else the shell directly.
- `Nuvio/Views/FocusablePage.cs` — the base recorder dual-writes while
  migrating: the router's registry exactly as before, plus the
  coordinator's registry when the flag is on (same key format, same Tag
  discipline — single-character glyph Tags are still skipped).
- `Nuvio/Diagnostics/FocusTestHarness.cs` — `RunIfRequested` also arms on
  a flag file named exactly `focustest` (no extension) in
  `ApplicationData.Current.LocalFolder` (see "Harness gate" below).

No `SettingsPage` edit was needed: it already implements `IArrivalFocus`
(`TryFocusArrival` / `TryFocusKey`), raises `ContentReady` synchronously in
`OnNavigatedTo`, builds its rows in `ShowSection`, and has no
`IPageInputHandler` — the router delegation serves it with no page change.
No other page is migrated: Home/Search/Catalog/Details/Streams/Player/
Placeholder keep the legacy settle through the router.

### Guard routing and the recursion rule

`MainPage.Arm/ClearSidebarExpandSuppression` stay the low-level shell-bit
setters and NEVER delegate to the coordinator — the coordinator's
`Arm/ClearNavigationGuard` already call them, so delegating back would
recurse infinitely. The flag decision lives at the call sites only:

- Arm: `NavigationService.Navigate`, `NavigationService.GoBackOrHome`,
  `NativeInputRouter.HandleBack` — coordinator when the flag is on, shell
  directly when off. `ContentFrame_Navigated` still re-asserts the shell
  bit directly (belt-and-braces, same value the coordinator set).
- Clear: settle success / expiry / takeover inside the owner (coordinator
  for Settings, router for the rest), `FocusSidebar`, and genuine input
  with no settle pending of either kind.
- The poll callback and `OnPreviewKeyDown` evaluate the takeover for the
  settle that is actually running: the coordinator evaluates its own settle
  inside `HandleInput`; `MainPage` evaluates the router's legacy settle
  (`_router.NoteUserInput()`) only when the router is settling, and clears
  through the coordinator only when neither is settling. The coordinator's
  settle is never double-evaluated and its guard is never cleared mid-settle
  from `MainPage`.

### Harness gate (how to run the focus test on the device)

The harness runs the launch → hero Play script and writes one line to
`nuvio-vlc.log`:

```
TEST launch-hero-play PASS expected=hero/Button actual=U+E768/Button focused=Button settleAttempts=N ms=N
```

It arms in either of two ways (dormant otherwise — no auto-run):

1. Launch argument: start the app with arguments containing `focustest`
   (Visual Studio: Debug → command line arguments).
2. Flag file (msix fallback): a dashboard launch of the installed msix
   always carries empty Arguments, so the token can never fire there.
   Upload a file named exactly `focustest` (no extension, content
   irrelevant) into the app's LocalState folder
   (`%LocalAppData%\Packages\<package>\LocalState`) via Device Portal →
   File Explorer, then launch the app normally. Delete the file to disarm.
   No Settings UI row was added for this.

### Settings device test list (real controller, flag on)

Install the build, then run each step and check the log lines that prove it:

1. Launch → hero Play has focus. Press nothing; wait for the hero. Log:
   the sync attempt shows `settled=false` (hero not loaded yet), then a
   later attempt — `Settle attempt 4/10 (content-ready): entry=
   ...HomePage|..., settled=true, focused=Button,
   focusName=HeroPlayButton, focusTag=U+E768` — followed by `Settle
   succeeded`. The strip must stay collapsed.
2. Open Settings from the sidebar (or Home → Settings). Log:
   `Settle arrival: page=SettingsPage, entry=...SettingsPage|settings|root`
   then `Settle attempt ... settled=true, focusName=<first root row
   button>` — the ring must sit on the first row (Account), not on the
   sidebar, and the strip must stay collapsed.
3. Move down the root list (D-pad down, one row per press). Each press
   moves exactly one row; no double moves, no jump to the sidebar.
   Log: `Settings layout (loaded):` line (the alignment probe) plus the
   row buttons' Tags recorded under the `settings|root` entry.
4. Open Addons, move to an addon action, press B. Log: Back arrival on
   the root list lands on the same row (`TryFocusKey` path — the
   coordinator's `Settle attempt ... isBack=True, settled=true` with the
   recorded row's `focusTag=root|addons`).
5. Open Addons again, press B from the root list. Focus returns to the
   exact row visited before (entry-keyed registry: `settings|root` vs
   `settings|addons` never overwrite each other).
6. Press B on the Settings root with an empty back stack. The app goes
   Home (not exit); Home's legacy settle lands on the hero.
7. Open the menu (right from Play / sidebar button), then press B.
   The strip collapses and focus is back on the page — the guard was
   armed before the navigation and cleared by the settle, not by the
   press itself.
8. Harness (optional): with the `focustest` flag file in LocalState,
   launch the app and check for the `TEST launch-hero-play PASS` line;
   then delete the flag file and relaunch — no `TEST` line must appear.

### Known Phase 1 limitations

- `OnPreviewKeyDown` stays dispatch-free. The console delivers one press
  twice (XAML key event + 30 ms controller poll); the coordinator's 150 ms
  dedupe covers directions only, so feeding back/menu/view/lb/rb from the
  key path would double-deliver them (double Back, double hero flip).
  The poll remains the single dispatch path, as before. Directions alone
  would be deduped, but a directions-only feed is asymmetric and held
  repeats bypass the dedupe, so no partial feed was added.
- The coordinator's nearest-sibling step in `NotifyContentChanged` is
  deferred to Phase 2 (it needs the row model). The fallback order today
  is: recorded key → arrival target → first focusable element.
- No eviction cap on either focus registry in Phase 1 (Phase 2 adds one).
- Dialog restore stays page-owned in Phase 1: the coordinator only pauses
  input and captures the invoker; `RequestTextAsync` / `ShowConfirmAsync`
  still restore focus themselves, and no focus move happens on dialog
  close from the coordinator.
- Guard-state mirror: the coordinator's `IsNavigationGuardArmed` only
  tracks coordinator-owned arms. After a legacy (non-Settings) settle
  clears the shell bit, the coordinator's bit can read stale-true until
  the next coordinator arm/clear. Unobservable in Phase 1 — nothing reads
  it, and the shell bit is the enforcement point — but Phase 2 should make
  the coordinator the single guard owner.
- The legacy settle's null-page branch clears the shell bit directly; if a
  coordinator settle was somehow pending there, the coordinator's bit
  would stay armed until the next navigation. Unreachable in practice
  (NavigationService refuses non-FocusablePage targets, so the content is
  never null).
- Direct per-page recorders (`HomePage` / `SearchPage` / `CatalogPage` /
  `DetailsPage` / `StreamsPage` calling `MainPage.RecordArrivalFocusKey`)
  still write only to the router's registry; only the `FocusablePage` base
  recorder dual-writes. Correct for Phase 1 (the coordinator settles
  Settings only, and Settings records only through the base recorder);
  Phase 2 moves recording to the single funnel.
- `FocusTestHarness`'s logged `settleAttempts` is its own readiness-poll
  count, not the router's/coordinator's internal attempt count.

## Fix round — guard hard timeout, Details error-path ready signal, probe removal (2026-11-13)

Files changed:

- `Nuvio/Input/FocusCoordinator.cs` (worker B) — hard timeout on the
  navigation guard: the guard clears at most 2 seconds after arming even
  if the settle never succeeds (enforced on the 500 ms watchdog tick, so
  effectively 2.0–2.5 s), so a slow/failed settle can no longer leave the
  strip unopenable for up to ~5 s (10 watchdog attempts). Also a FIFO
  registry cap in the coordinator (precaution, Mohammed 00:45): at most
  200 live entries, at most 20 search-keyed ones; evicting an entry also
  removes its row map. The legacy router registry stays unbounded behind
  the flag. (Coordinator fix: `IsSearchEntryKey` matches `"|search|"` —
  entry keys carry a type-name prefix, so `StartsWith("search|")` never
  matched and the search sub-cap was dead until this fix.)
- `Nuvio/ViewModels/DetailsViewModel.cs` + `Nuvio/Views/DetailsPage.xaml.cs`
  (worker A) — the Details error path now raises the content-ready signal
  on load failure instead of letting the settle burn all 10 attempts with
  the guard armed.
- `Nuvio/Views/SettingsPage.xaml.cs` (worker C) — removed both temporary
  Settings layout probes (v1 `Dispatcher.RunAsync` block in `OnNavigatedTo`
  logging `Settings layout: ...`, v2 `Loaded` handler logging
  `Settings layout (loaded): ...`) plus their TEMPORARY comments and the
  now-unused `using Windows.UI.Core;`. Pure deletion (23 lines removed,
  0 added). `_firstFocusTarget`, arrival logic, `Log` (still used by
  `Log.Error` elsewhere in the file) and `using Nuvio.Core;` /
  `using Nuvio.Diagnostics;` are untouched. The probes proved their point
  (device log: rows span 32..804 exactly as designed) and are done.

Rollback: flag off (`UseFocusCoordinator = false`) = legacy router paths
with the old guard behaviour and no coordinator hard timeout; the probe
removal is behaviour-neutral either way (the probes only wrote log lines).

Device test: the Phase 1 "Settings device test list" above still applies,
except step 3's `Settings layout (loaded):` log line no longer exists —
verify step 3 by the settle lines and the ring position instead. Plus:
Details load failure must settle promptly (guard clears, no ~5 s stall);
a stalled settle anywhere must clear the guard within the hard timeout.

## Phase 2 — row model, scroll ownership, Home/Search/Catalog migrated (2026-11-13)

Files changed:

- `Nuvio/Input/FocusCoordinator.cs` — the row model moves
  into the coordinator (`AnchorFocusedRowElement` / `KeepGridItemInView` /
  `StopScrolling`, absorbing `FocusScrollHelper`'s scroll ownership);
  the constructor drops the `moveFocus` delegate — fixed signature
  `FocusCoordinator(MainPage shell, Frame frame, NavigationService
  navigation, Action handleBack)` — the coordinator owns row/edge moves
  itself now. The guard hard timeout stays 2 s; the registry caps stay
  200 entries / 20 search-keyed.
- `Nuvio/Views/Shared/FocusScrollHelper.cs` — one method added,
  `ResetRowAnchor()`, called by `RequestArrival` on every navigation so
  the coordinator-owned shared helper gets fresh row-anchor state (the
  per-page instances it replaces were discarded on navigation). The
  engine code is otherwise byte-for-byte identical.
- `Nuvio/Input/NativeInputRouter.cs` — `SettleArrivalFocus`
  routes `HomePage`, `SearchPage` and `CatalogPage` to
  `Coordinator.RequestArrival` alongside `SettingsPage`; the
  `Coordinator.CancelArrival()` call for the legacy-takes-over case and
  the entire legacy settle body below are untouched. With the flag off
  the block is skipped exactly as before.
- `Nuvio/MainPage.xaml.cs` — `RecordArrivalFocusKey` is now
  the single recording funnel: it always forwards to the router's
  registry exactly as before, and when the flag is on also writes the
  coordinator's registry (`RecordFocus`, same key format). Every direct
  per-page recorder flows through it — Home/Search/Catalog, and also
  Details/Streams (their keys now land in the coordinator's registry
  too; inert data, since the coordinator never settles those pages in
  this phase, and they count toward the 200-entry FIFO cap, which has
  4–6x headroom). The coordinator construction call site now uses the
  fixed constructor signature (no `MoveFocus` delegate). No call site
  of `RecordArrivalFocusKey` changed.
- `Nuvio/Views/SearchPage.xaml.cs` — `TryFocusKey` now returns false
  on a missing or unknown key (it used to fall back to the search box
  silently inside the method); the coordinator applies the arrival
  fallback (the box) instead. `AnchorFocusedRowElement` and `Stop()`
  route through the coordinator when the flag is on, else the page's
  own `FocusScrollHelper` instance (flag-off path).
- `Nuvio/Views/CatalogPage.xaml.cs` — `KeepGridItemInView` and `Stop()`
  route through the coordinator when the flag is on, else the page's
  own `FocusScrollHelper` instance (flag-off path).
- `Nuvio/Views/FocusablePage.cs` — the base recorder's direct
  coordinator write (Phase 1) was removed in this phase: the funnel
  now does the coordinator write, so a base record reaches the
  coordinator exactly once, via `MainPage.RecordArrivalFocusKey`.
- No `HomePage` edit: its `TryFocusArrival` / `TryFocusKey` contracts
  were already correct, its `RowKeyForElement` override feeds the base
  recorder, and its `OnGotFocus` records through
  `MainPage.RecordArrivalFocusKey` — which the funnel now dual-writes
  (verified by reading `HomePage.xaml.cs`: `TryFocusArrival` 145–156,
  `TryFocusKey` 166–225, `RowKeyForContainer` 236–247,
  `RowKeyForElement` 255–262, `OnGotFocus` 989–1050, `HandleAction`
  1252–1400). `FocusScrollHelper` is not deleted in this phase; it
  stays as the flag-off scroll path.

Flag-on vs flag-off per migrated page (flag off = all legacy, as before):

- Home: settle — coordinator (`RequestArrival`) on / router legacy off;
  recording — router registry + coordinator registry on / router only
  off; scroll — coordinator row model on / page-owned
  `AnchorFocusedElement` (`HomePage.xaml.cs:443`, via `OnGotFocus:993`)
  off; moves — coordinator row/edge moves on / router `MoveFocus` +
  `HomePage.HandleAction` off.
- Search: settle — coordinator on / router legacy off; recording —
  dual on / router only off; scroll — coordinator on / page-owned
  `FocusScrollHelper` instance (`SearchPage.xaml.cs:35`) off; moves —
  coordinator on / router + `SearchPage.TryFocusKey` off.
- Catalog: settle — coordinator on / router legacy off; recording —
  dual on / router only off; scroll — coordinator on / page-owned
  `FocusScrollHelper` instance (`CatalogPage.xaml.cs:24`) off; moves —
  coordinator on / router + `CatalogPage.TryFocusKey` off.
- Settings: unchanged from Phase 1 (coordinator settle; the base
  recorder writes once via the funnel — router + coordinator — on;
  all legacy off).
- Details / Streams / Player / Placeholder: untouched by Phase 2 —
  legacy settle, legacy moves, page-owned scroll in both flag states;
  only their recorded keys additionally land in the coordinator's
  registry when the flag is on (inert, see above).

### Phase 2 device test list (real controller, flag on)

Install the build, then run each step and check the ring position and
the log lines that prove it:

1. Launch → hero Play has focus (same as Phase 1 step 1 — the
   coordinator now settles Home, so the `Settle attempt ... settled=true,
   focusName=HeroPlayButton` line must come from the coordinator path,
   and the strip must stay collapsed).
2. Home: press Down from the hero — the ring lands on the first card
   (index 0) of the first row with items. Move Down row to row: each
   press lands on the remembered poster of the next row (per-row
   memory), never on the sidebar, never skipping a row.
3. Home: open a title's Details, press B — the ring returns to the
   exact card opened (`TryFocusKey` path, composite `meta:` key pinned
   to the right row); open a Continue Watching card's Details, press B
   — the ring returns to that exact Continue card (`cw:` key).
4. Home: first entry after a cold start with no recorded key lands on
   the hero Play (first card rule applies only on Down from the hero).
5. Search: open Search from the sidebar — the ring lands on the search
   box. Type a query, move down into the results — one row per press,
   grid scrolls to keep the focused result visible.
6. Search: open a result's Details, press B — the ring returns to that
   exact result, or to the search box when the result is gone.
7. Catalog: open a catalog — the ring lands on the first card. Move
   through the grid (all four directions) — the grid scrolls to keep
   the focused card visible, no jump to the sidebar, no skipped card.
8. Catalog: open a card's Details, press B — the ring returns to the
   opened card.
9. First entry → first card everywhere: a first visit to Home (no hero
   yet — empty library) lands on Manage Addons; a first visit to Search
   lands on the box; a first visit to a catalog lands on its first card.
10. Flag-off sanity (same build, flag flipped): Home/Search/Catalog
    behave exactly as on the pre-Phase-2 build — legacy settle lines,
    router-only registry, page-owned scroll. No coordinator settle line
    (`RequestArrival`) must appear for any page.

### Known Phase 2 limitations (carried forward)

- Guard-state mirror (from Phase 1, still open): the coordinator's
  `IsNavigationGuardArmed` can still read stale-true after a
  Details/Streams/Player/Placeholder legacy settle clears the shell
  bit — the coordinator is NOT yet the single guard owner. Phase 3
  should make it so.
- Dialog restore stays page-owned until Phase 3: the coordinator only
  pauses input and captures the invoker; `RequestTextAsync` /
  `ShowConfirmAsync` still restore focus themselves, and no focus move
  happens on dialog close from the coordinator.
- `FocusScrollHelper` stays in the tree as the flag-off path; its
  absorption into the coordinator is complete for Search/Catalog only
  in the flag-on path — Home never used it (own anchor), and
  Details/Streams keep their own scroll handling.

## Fix round — sidebar opens on the first option (three-part fix, 2026-11-13)

Device report (build 561): at launch focus parks on the collapsed
sidebar first, then moves to the hero; sometimes it sticks there and
never reaches the hero. Two videos (Settings, Search) show the same
geometry from content: right toward the sidebar lands on the
geometrically nearest button by height (Debrid row → Settings, first
row → Home, bottom search chips → Settings), and the strip expands
afterward as a `GotFocus` side effect — so the ring flashes on an icon
while the strip is still collapsed. Root cause, verified by reading the
code: `FocusSidebar()` is called only from the hero's start-edge branch
(`HomePage.xaml.cs:1317`); Settings/Search fall through to the router,
whose `TryMoveFocus` is plain XYFocus and lands on the nearest button.
The framework parks initial focus on HomeButton (first focusable
element, no `TabIndex`) while the frame is empty and the hero loads from
the network; the guard only suppresses expansion, never landing.

Files changed:

- `Nuvio/MainPage.xaml.cs` (part 1 + part 3 shell side) — the
  constructor sets `IsTabStop = false` on the four sidebar buttons
  behind the flag, so the framework's launch-time park can never land
  on them while the strip is collapsed. Re-enabled on every real
  expansion in `SetSidebarExpanded(true)` and on a deliberate move in
  `FocusSidebar()` (which now sets `IsTabStop = true` on its target
  before `Focus()` and logs on failure — `Focus()` on a control with
  `IsTabStop=false` returns false without moving focus). New method
  `FocusSidebarFirst()` (next to `FocusSidebar()`): returns `false`
  immediately, with no side effect, when the sidebar is Hidden
  (Details / Streams / Player); otherwise clears the expand guard
  exactly as `FocusSidebar()` does (coordinator
  `ClearNavigationGuard()` when the flag is on, else
  `ClearSidebarExpandSuppression()`), opens the strip FIRST via
  `SetSidebarExpanded(true)` — which re-enables the four buttons —
  then sets `HomeButton.IsTabStop = true` explicitly (harmless when
  already expanded: `SetSidebarExpanded` returns early and the buttons
  are enabled already), focuses `HomeButton` with
  `Focus(FocusState.Programmatic)`, logs on failure only
  (`"FocusSidebarFirst failed to focus " + HomeButton.Name`), and
  returns `true`. It differs from `FocusSidebar()`, which focuses the
  current page's button and is called only by the hero branch.
  `FocusSidebarFirst()` is reached only through the coordinator (flag
  path), so it carries no flag check inside. No `FocusSidebar()`,
  `CollapseSidebar()`, fold branch or XAML change.
- `Nuvio/Input/FocusCoordinator.cs` (parts 2 + 3, coordinator side) —
  part 2: the watchdog tick and `NoteUserInput` both ignore a focus
  move that stays inside the sidebar (`IsFocusInSidebar`) — it is not
  a takeover, the settle keeps running and the arrival target lands
  when the content loads. Part 3: a new branch in `MoveFocus` runs
  before `TryMoveFocus` — only when focus is in content, not in the
  sidebar, and the move is toward the sidebar (right in RTL, left in
  LTR, via `IsTowardSidebar`). It peeks with
  `FocusManager.FindNextFocusableElement(direction)` (moves nothing,
  only reports the candidate): a sidebar button — or `null`, meaning no
  focusable element at all in that direction — takes the
  `FocusSidebarFirst()` path (`if (_shell.FocusSidebarFirst()) return;`
  — a Hidden sidebar returns `false` and the move falls through to
  XYFocus as today); a content element (a card / row neighbour, e.g.
  mid-row in Catalog, which has no `HandleAction` of its own) is left
  alone so `TryMoveFocus` carries it to the neighbour. The intercept
  condition is deliberately narrow: it protects multi-column row
  navigation in Catalog, where a toward-sidebar move from mid-row must
  reach the neighbouring card, not the strip. Dialogs never reach the
  branch: `HandleInput` gates dispatch while a dialog is open.

Accepted trade-off (explicit): browsing the sidebar deliberately
during a settle no longer cancels it — the settle keeps running and
pulls focus to the arrival target when the page loads. The stuck-focus
case (a D-pad press inside the strip during the settle leaving focus
parked there forever) is worse than losing a deliberate sidebar browse
during the ~1 s settle window; the press itself still moves between
sidebar buttons while the settle runs.

### Device test list after the fix (real controller, flag on)

Install the build, then run each step and check the ring position:

1. Cold launch, touch nothing — no ring anywhere until the hero loads,
   then the ring sits on the hero Play directly, never on the sidebar
   first; the strip stays collapsed.
2. Press Down while the hero is still loading — no stuck focus; when the
   hero loads, the settle lands on the hero Play (a press in that window
   moves nothing: no element can take focus yet, and the settle is not
   cancelled).
3. Settings: right from the Debrid row — the strip opens and focus lands
   on Settings (superseded: was Home under the old "first option" rule).
4. Search: right from the bottom chips — the strip opens and focus lands
   on Search (superseded: was Home under the old "first option" rule).
5. Home hero: right from Play — the strip opens and focus lands on Home,
   as before.
6. Press a sidebar button — navigates, the strip collapses, focus stays
   on the chosen button (not pulled back to the page).
7. Flag off (same build, flag flipped): today's behaviour exactly —
   launch parks on the sidebar, right lands on the nearest button.
   The coordinator path (and therefore `FocusSidebarFirst`) is never
   entered.

### Verification notes

- No local C# compiler: compile verification is by CI (build check on
  the push). `FocusSidebarFirst()` must ship in the SAME push as the
  coordinator call, in dependency order (runs 544–546 lesson: the file
  that defines the member lands BEFORE the file that calls it —
  `MainPage.xaml.cs` first, `FocusCoordinator.cs` second).
- Microsoft Learn recommends `FindNextElement` over
  `FindNextFocusableElement`: the latter returns `null` when the next
  candidate is not a `UIElement` (e.g. a `Hyperlink`). In our pages the
  candidates are buttons and cards, so there is no practical effect —
  but if a focusable `Hyperlink` ever appears, revisit the peek call.

## Fix — sidebar opens on the current page's button (2026-11-13, supersedes the "first option" rule above)

User request: opening the sidebar must land on the button for the page
currently shown (Settings from Settings, Search from Search), not always
on Home. The three-part fix above deliberately landed on Home; that rule
is now superseded.

Root cause: `FocusSidebarFirst()` hardcoded `HomeButton`, while
`FocusSidebar()` (hero branch) already resolved the current page's button
from `_activeRoot` — the two openings had drifted apart. Fix: a shared
helper `SidebarButtonForCurrentRoot()` in `Nuvio/MainPage.xaml.cs`
(`_activeRoot == "search" ? SearchButton : "library" ? LibraryButton :
"settings" ? SettingsButton : HomeButton`, unknown/null root falls back
to HomeButton) used by both methods; `FocusCoordinator.MoveFocus` is
unchanged (comment only). The root comes from `ContentFrame_Navigated` /
`SetActiveSidebarButton`, so `PlaceholderPage` resolves through its
`SidebarRoot` ("library" / "settings") — a page-type-only lookup could
not distinguish those two. Hidden-sidebar pages (Details / Catalog /
Streams / Player) still return `false` from `FocusSidebarFirst()` and
fall through to XYFocus.

Device test list (amends items 3–4 of the three-part fix list above):
Settings right from the Debrid row lands on Settings; Search right from
the bottom chips lands on Search; Home hero right from Play still lands
on Home. `FOCUS.md` §2 item 8 and §5 "Sidebar menu" updated from "first
option" to "current page's button".

## Fix round — bare-container focus at launch (build 564 gate failure)

### What the device showed

Mohammed tested build 564 (2.0.564.0, head 72ae032) and reported: no
focus ring at all on launch — focus never lands on the hero. Log
`nuvio-vlc_9.log` (2.0.564.0) proved it: settle attempt 1 (sync)
`focused=null`, attempt 2 (layout) `focused=null`, attempt 3 (watchdog)
`settled=True` but `focused=ItemsControl` with no Name and no Tag —
a false success.

### Mechanism

Part 1 of the sidebar fix (sidebar buttons `IsTabStop=false`) worked:
the framework could no longer park on the strip. But when the Home
content loaded, the framework parked focus on the first focusable
element in the tree — a bare `ItemsControl` (focusable by default in
UWP, no focus visual, so no ring anywhere). The settle then accepted
it: `AttemptSettle` checks `IsFocusInsidePage` first (the container
IS inside the page) and never calls `TrySettleOnce`; and
`HomePage.TryFocusArrival` bails out with `return true` whenever
focus is already inside the page, unable to tell "the framework
parked on a bare container" from "the user deliberately placed focus
on a card".

### The fix (two layers, in the Focus Manager)

1. **Bare containers are no longer tab stops:** `IsTabStop="False"`
   on all nine bare `<ItemsControl>` in the project — Home
   (`HeroGenres`, `CatalogRows`), Catalog (`Genres`), Details
   (`Genres`, `Credits`), Search (`RecentSearches`, `Rows`), Streams
   (`Badges`, `StatusItems`). Their children (text chips, buttons,
   `GridView`s) keep their own focus behaviour; no code calls
   `.Focus()` on any of them.
2. **Strict settle success:** new `FocusablePage.IsRealFocusTarget`
   — `false` for `null` and for a plain `ItemsControl` (not
   `ListViewBase`, whose items carry the ring), `true` otherwise.
   Applied in `FocusCoordinator.IsFocusInsidePage` + `AttemptSettle`
   (takeover branch now also requires a real target, so a parked
   container is not treated as user takeover), in
   `NativeInputRouter` (same flaw on the flag-off path), and in the
   `TryFocusArrival` bail-outs of Home / Player / Settings / Streams.
   The "never yank user focus" rule is preserved for real targets
   (Button / GridViewItem / TextBox …).

### Device test list (after the build)

1. Cold launch: ring on the hero Play button directly, no ring
   before the hero loads.
2. Press D-pad while the hero is still loading: settle completes
   and lands on Play (no stuck focus).
3. Flag off (same build, flag flipped): today's behaviour exactly.
4. Arrival on other pages, no regression: Details (Play), Streams
   (All chip), Settings (first row), Player (Back), Catalog (first
   card), Search (the box).
5. In the log: never `settled=True` with `focused=ItemsControl`; a
   `Settle: focus parked on a bare container` line proves layer 2
   fired.

### Known follow-ups (not in this round)

- `NoteUserInput` (coordinator + router) still cancels the settle on
  any focus move off the starting element, with no real-target check
  (sidebar excepted). Unreachable now: no path puts focus on a bare
  container (all nine are `IsTabStop=false`, nothing focuses them,
  no `new ItemsControl` in code-behind). Consider the same guard if
  a new bare container is ever added.
- `IsFocusValid` in the coordinator does not exclude a bare
  container (visible + enabled + in the live tree = valid). No
  effect today: its only caller `NotifyContentChanged` has no
  callers yet. When callers are wired (Phase 3), reuse
  `IsRealFocusTarget` there.

## Phase 3 — coordinator-owned dialog restore + first NotifyContentChanged caller (2026-11-13)

Two tracks, one phase. The coordinator track (dialog stack + one-tick
restore-on-close) lives in `FocusCoordinator.cs` / `MainPage.xaml.cs` /
`NativeInputRouter.cs`; the pages track below only rewires the pages to
it. The coordinator side was verified by reading: `NotifyDialogOpened`
pushes the invoker (null when focus was not on a Control) onto
`_dialogInvokers`; `NotifyDialogClosed` pops one and, only when the
stack empties, schedules the restore one dispatcher tick later
(`CoreDispatcher.RunAsync`, Normal). That restore skips when another
dialog opened, when an arrival settle is running, or when focus is
already on a real, valid target inside the current page; otherwise it
focuses the popped invoker when it is a real, valid target in the live
tree under the current page, else the page arrival target — never a
bare container.

Files changed (pages track):

- `Nuvio/Views/FocusablePage.cs` — `RestoreDialogInvoker` stays, and
  is now the flag-off compatibility path: when the coordinator is
  available and the flag is on it returns `true` at once without
  moving focus (the coordinator owns the restore then — a local move
  would race its one-tick restore); when the flag is off it runs the
  legacy behaviour verbatim (focus the Control, else
  `TryFocusArrival`). No usings added: `Nuvio.Input` was already
  imported and `MainPage` resolves through the enclosing `Nuvio`
  namespace. `RestoreDialogInvoker` was NOT deleted — deleting it
  would have left the flag-off path with no dialog restore at all,
  because `DialogOpen` only routes to the coordinator when the flag
  is on.
- `Nuvio/Views/HomePage.xaml.cs` — `ShowContinueMenuAsync` keeps the
  invoker capture and the `RestoreDialogInvoker` call in the cancel
  branch (flag-off path; a no-op when the flag is on). `invokerIndex`
  and `FocusContinueNeighbour` for the "remove" choice are untouched,
  as are the details/restart choices.
- `Nuvio/Views/PlayerPage.xaml.cs` — `ShowMenuDialogAsync` keeps the
  invoker capture and the `RestoreDialogInvoker` call in the finally
  (after `DialogOpen = false`, so the coordinator's tick is already
  scheduled when the flag is on); `MenuOpen = false` untouched.
- `Nuvio/Views/SettingsPage.xaml.cs` — `ShowConfirmAsync` keeps the
  invoker capture and the `RestoreDialogInvoker` call in the finally.
- `Nuvio/Views/StreamsPage.xaml.cs` — `ShowDebridSettingsDialogAsync`
  keeps the invoker capture and the `RestoreDialogInvoker` call in
  the finally. `OnEntriesChanged` is the first `NotifyContentChanged`
  caller (see below).
- `Nuvio/Views/DetailsPage.xaml.cs` — `ShowEpisodeMenuAsync` keeps
  the invoker capture; the deferred one-tick restore block in the
  else branch is kept verbatim but wrapped in a flag-off condition
  (`invoker != null && !coordinatorOwnsRestore`, where
  `coordinatorOwnsRestore` is the standard
  `MainPage.Current?.Coordinator != null &&
  FocusCoordinator.UseFocusCoordinator` check): with the flag on it
  is skipped entirely (the coordinator restores), with the flag off
  it runs exactly as before, including the in-page focus check.
  toggleWatched / previous / season handling untouched, and the
  "play" branch still navigates with no restore.

`NotifyContentChanged` wiring — one site wired, five skipped:

- Wired: `StreamsPage.OnEntriesChanged` — after `RenderAll`, before
  `RaiseContentReady`. `RenderAll` re-focuses the chip/row it can
  still find but ignores the `FocusChip` / `FocusEntry` bool returns,
  so when the focused chip/row vanished from the rebuilt list focus
  stays on a detached element with no fallback. The call is guarded
  by the standard flag check (CatalogPage / SearchPage pattern) and
  is a no-op when focus is still valid, so it only repairs real loss
  and cannot fight the settle: on first render it runs before
  `RaiseContentReady`, and its arrival fallback is the same target
  the settle would pick ("all" chip).
- Skipped: `SettingsPage.OnViewModelAddonsChanged` — `RenderAddons`
  restores focus itself with an `InstallAddonButton` fallback when
  the focused addon's button is gone, and `RenderDebrid` falls back
  to its first button the same way; focus is never lost there.
- Skipped: `HomePage` after `LoadAsync`, `DetailsPage.OnLoadCompleted`,
  `CatalogPage` after `LoadAsync` — first render in each case, and
  `RaiseContentReady` immediately after hands arrival to the router's
  settle, which owns it fully; a `NotifyContentChanged` call would
  only duplicate the settle's arrival focus.
- Skipped: `SearchPage.OnSearchCompleted` — when `_focusFirstResult`
  is set the deliberate `FocusFirstResult()` move follows immediately
  (a repair call would fight it); otherwise focus sits on the search
  box, outside the rebuilt rows, so nothing is lost.
- No other site wired. Note the follow-up below: `HomePage`
  `OnContinueRefreshed` has the same dropped-focus gap as Streams
  had (it re-focuses the Continue card only when the card still
  exists, with no fallback when it is gone).

### Device test list (real controller, flag on)

1. Dialogs, cancel/close each one and confirm the ring lands back on
   the element that opened it: Home Continue menu (cancel), Details
   episode menu (cancel + each mark-watched choice), Player subtitle
   menu, Player delay menu, Settings confirm dialog, Streams debrid
   invalid-key dialog (both buttons).
2. Dialog choice that navigates (Home "details" / "restart", Details
   "play", Streams "open debrid settings"): arrival on the new page
   is the settle's, no stuck focus, no double move.
3. Nested dialogs (a dialog opened from a dialog, where the UI
   allows it): the outer invoker is restored only when the outer
   dialog closes, not the inner one.
4. Streams content change: focus a stream row, change the filter so
   that row disappears — the ring must land on a neighbour / the
   "all" chip, never vanish.
5. Guard: no regression — cold launch still lands on the hero Play
   button, D-pad during hero load does not stick.
6. Flag off (same build, flag flipped): all six dialogs restore
   exactly as before (synchronous local restore / Details deferred
   tick), and Streams re-renders behave as before — the flag-off
   path is byte-identical behaviour to the pre-Phase-3 build.

### Known Phase 3 limitations (carried forward)

- `RestoreDialogInvoker` stays as the flag-off compatibility path by
  design (see above): it cannot be deleted until the flag itself is
  retired. The coordinator's class doc (`FocusCoordinator.cs`) still
  describes the pages as keeping "their own synchronous
  `RestoreDialogInvoker` calls as the first path" with the
  coordinator tick as "backstop" — now inverted: when the flag is on
  the page call is the no-op and the coordinator tick is the only
  path. That comment needs a one-line sync when the coordinator file
  is next touched.
- `HomePage.OnContinueRefreshed` re-focuses the Continue card only
  when the card still exists; when the refreshed list drops it,
  focus stays on the detached container. Same gap Streams had;
  candidate for the next `NotifyContentChanged` caller.
- `IsFocusValid` in the coordinator still does not exclude a bare
  container (visible + enabled + in the live tree = valid). Now
  matters in principle — `NotifyContentChanged` has its first caller
  — but no path parks focus on a bare container (all nine are
  `IsTabStop=false`, nothing focuses them), so reuse of
  `IsRealFocusTarget` there is still a hardening item, not a fix.
- The Home Continue-menu cancel path is still device-unverified in
  the coordinator-owned form (it was exercised only in the local
  form before); test list item 1 covers it.

### Phase 3 closure (2026-11-14)

Gate closed by the user's decision on 2026-11-14 00:57: the dialog
device test list above was WAIVED, not run — the six dialogs were
never exercised on the device in the coordinator-owned form, and
nothing here claims otherwise. The known limitations above (flag-off
`RestoreDialogInvoker` path, `HomePage.OnContinueRefreshed` gap,
`IsFocusValid` not excluding bare containers, Continue-cancel
device-unverified) carry forward as documented. Next step: the
AnimatedFocusBorder focus integration (the beam circles the focused
element; 7 DIP band, 132 DIP arcs, 2x supersampled surface), which
does not touch the coordinator, the router, or `FocusablePage`.

### Beam removal (2026-11-14)

The user said "remove it completely" after build 609 went green. The
beam is gone in full: `AnimatedFocusBorder.cs` deleted, the `FocusBeamHost`
canvas removed from `MainPage.xaml`, the wiring and field removed from
`MainPage.xaml.cs` (including the now-unused `Nuvio.Views.Shared` using),
and the `Compile` entry removed from `Nuvio.csproj`. The plain white
focus ring is untouched and is the only focus frame again. The beam's
code stays recoverable from git history (heads d224cda–611a997).

## Phase 4 — player OSD focus rules (2026-11-14)

### Audit findings (read-only, verified against the code)

- OSD auto-hide: `PlayerViewModel.HideControls` (`PlayerViewModel.cs:1048-1052`)
  sets `ControlsVisible = false` on a 4 s timer (`HideControlsMs = 4000`,
  `:99`; `ScheduleHide` `:1055-1059`; tick `:1061-1065`), guarded by
  `MenuOpen || PlayState != "playing" || !Ready` — so a hide can never
  fire while a dialog is open. `PlayerPage.UpdateOsdVisibility`
  (`PlayerPage.xaml.cs:252-260`) then sets `OsdRoot.Opacity = 0` and
  `IsHitTestVisible = false`; the OSD is never `Collapsed`, and **no code
  moved focus on hide** — focus stayed on a now-invisible button.
- Show paths all funnel through `ShowControls` / `Poke`
  (`PlayerViewModel.cs:1040-1070`): `ControlsVisible = true` is set
  **before** `RaiseFocus(hint)`, so a synchronous restore inside the
  `ControlsVisible` property change runs before the page's focus hint.
  The "play" hint (`PlayerPage.xaml.cs:170-178`) never yanks focus off
  another OSD button — it focuses Play only when focus is null, on Back,
  or outside the OSD.
- `PlayerPage.TryFocusKey` ignored its key entirely and always fell
  through to `TryFocusArrival` (Back). No OSD key could ever be recorded
  anyway: every OSD button's `Tag` is a single Segoe MDL2 glyph (or
  null), and `FocusablePage.OnGotFocusRecordArrival`
  (`FocusablePage.cs:149-175`) skips single-character tags — so the
  coordinator must hold a `Control` reference (like the dialog invoker),
  not a registry key.
- The player is not a migrated page: the router routes only
  Settings/Home/Search/Catalog to `Coordinator.RequestArrival` and calls
  `Coordinator.CancelArrival()` for everything else, including the
  player (`NativeInputRouter.cs:247-256`) — so the coordinator never
  settles the player and the OSD rule must not depend on a settle.
- `IsFocusValid` cannot detect a hidden OSD: it checks `Visibility`,
  `IsEnabled` and the live tree, but not `Opacity`
  (`FocusCoordinator.cs:1375-1396`) — an opacity-0 OSD button still
  counts as valid. The OSD signal therefore has to be explicit
  (`ControlsVisible`), not derived from focus validity.
- Dialogs: the 7 player menus share `ShowMenuDialogAsync`
  (`PlayerPage.xaml.cs:608-690`), which sets `MainPage.DialogOpen`
  (coordinator stack, Phase 3) and `MenuOpen = true` (blocks hide).
  Dialog close restores to the invoker, which may be an OSD button.

### The rule

- **Hide:** when `ControlsVisible` flips false and focus is on an OSD
  element, the coordinator remembers that element and parks focus on
  Back via the page's `TryFocusKey("back")` contract. When focus is on
  a visible NextCard/ErrorBox button (outside `OsdRoot`, still visible
  while the OSD is hidden) or nowhere, nothing is parked and focus is
  left alone.
- **Show:** when `ControlsVisible` flips true, the coordinator restores
  focus to the remembered element when it is still a real, valid target
  in the live tree under the current page; otherwise focus stays on the
  parked Back button, so the "play" hint moves it to Play.
- **Dialog/settle guard:** hide-park is skipped while a dialog is open;
  show-restore is skipped while a dialog is open or an arrival settle
  is running (the slot is cleared in both cases — the dialog/settle
  owns focus from then on). The OSD rule never touches the dialog
  stack: reusing it would pause all controller input while the OSD is
  hidden (`HandleInput` gates on `IsDialogOpen`), contradicting the
  page's hidden-state input handling (`PlayerPage.xaml.cs:424-447`).
- **`TryFocusKey` contradiction resolved:** `PlayerPage.TryFocusKey`
  now resolves keys in the page's own focus-hint vocabulary —
  `back`/`play`/`seek`/`rewind`/`forward`/`subtitles`/`audio`/`speed`/
  `aspect`/`sources`/`episodes`/`next` (the Next-episode card's Play
  button, matching `OnFocusRequested`)/`error` (TryNext when visible,
  else Retry) — with a `Visibility` guard per button, and unknown keys
  still fall through to arrival (Back).

### Files

- `Nuvio/Input/FocusCoordinator.cs` — OSD park slot (`_lastOsdElement`,
  one slot: the OSD exists only on the player), `NotifyOsdHidden(Control)`
  (remember + park via `TryFocusKey("back")`),
  `NotifyOsdShown()` (validated restore, else leave on Back),
  `DescribeOsdTarget` (button `Name` for the log). Beside the dialog
  pair, reusing its validation pattern (`IsRealFocusTarget` +
  `IsFocusValid` + `IsDescendantOfCurrentPage`).
- `Nuvio/Views/PlayerPage.xaml.cs` — `TryFocusKey` key map +
  `FocusButton` helper; `NotifyOsdVisibilityChanged` (flag-gated, same
  pattern as StreamsPage's `NotifyContentChanged` call) hooked into
  `OnViewModelPropertyChanged` on `ControlsVisible` flips only
  (`Ready` flips do not notify). The hook passes the focused element
  only when it is a descendant of `OsdRoot`.
- `FOCUS-MIGRATION.md` — this section.

### Flag-off behaviour

With `UseFocusCoordinator = false` the page hook returns before any
coordinator call, so the old behaviour (focus stays on the hidden
button) returns exactly. The `TryFocusKey` change is behaviour-
preserving for every existing caller: no OSD key is ever recorded
(single-character tags are skipped), the legacy settle therefore
always falls through to `TryFocusArrival` exactly as before, and the
only new key (`"back"`) is sent by the coordinator, which is never
called with the flag off. No button behaviour changed: X still toggles
pause (there is no stop), and the `"playpause"` case is untouched dead
code.

### Known limitations (carried forward)

- A stale slot: leaving the player while parked keeps a detached
  `Control` reference until the next hide overwrites it. It is
  validated at use and can never be focused; no proactive clearing
  was added to the settle paths (deliberate — the dialog invoker
  pattern validates the same way).
- Show with an explicit non-"play" hint (`"seek"` from LT/RT or
  left/right while hidden): the restore runs first, then the hint
  focuses the seek bar — final state correct, one wasted synchronous
  `Focus()` call, no visible flicker (same call stack, one frame).
- Y while the OSD is hidden opens the subtitles dialog with the
  parked Back as invoker; close restores to Back, and the next input
  re-shows the OSD and restores the remembered element. The dialog
  restore owns that path.
- Phase 3 doc correction: `IsFocusValid` **does** exclude bare
  containers (`FocusCoordinator.cs:1380`); the Phase 3 limitation
  text ("IsFocusValid not excluding bare containers") is stale — the
  bare-container fix round added the check after Phase 3.
- No local C# compiler in the VM — compilation is checked by CI.
  Device test not run yet (list below).

### Device test list (real controller, flag on)

1. Play a video, wait 4 s without input: OSD hides; log shows
   `OSD hidden; parked focus on Back; remembered <button>`.
2. Move focus to Speed (or Subtitles/Audio), wait 4 s: OSD hides,
   then press any direction: OSD shows and focus is back on the same
   button (not Play). Log shows `OSD shown; focus restored to <name>`.
3. While hidden, press Back once: OSD hides already — press Back
   again: leaves the player (first Back while visible hides, second
   exits — unchanged).
4. Focus the seek bar, wait 4 s, press Right: OSD shows on the seek
   bar (explicit hint wins over the restore).
5. Near the end of an episode, let the Next card appear, then wait
   4 s: focus stays on the Next card's Play button (not parked);
   press Back: card cancels, OSD shows, focus on the remembered OSD
   button.
6. While hidden, press Y: subtitles dialog opens; choose or cancel:
   focus returns to Back; press any direction: OSD shows, focus on
   the remembered button.
7. Pause (X) while hidden: OSD shows, focus on the remembered button.
8. Flag-off sanity (same build, flag flipped): OSD hide leaves focus
   on the hidden button exactly as before (old behaviour).

## Fix round — root back-stack + Home row memory + sidebar current-page button (2026-11-13)

User report: focus memory "saves across pages" (video: B on Home
re-opened the Account placeholder, looping Home ↔ Account). The audit
found the leak is not the focus registry at all — it is the back stack
— plus a real Home row-memory leak, and the user added a third request
(sidebar opens on the current page's button). All three fixed in one
local round, no push yet.

### 1. Root back-stack (NavigationService.cs)

Root navigations cleared `BackStack` *before* `Navigate`, but
`Frame.Navigate` itself pushes the current page (Microsoft Learn:
"When you navigate to a page with `Frame.Navigate`, a `PageStackEntry`
for the current page is added to the `Frame.BackStack` collection"),
so one stale entry always survived and B looped between two pages
instead of reaching the empty-stack branch. Fix: central `Navigate`
gained `bool clearBackStack = false`; the four roots
(`NavigateHome` / `NavigateSearch` / `NavigateSettingsRoot` /
`NavigatePlaceholder`) pass `true` and the stack is cleared *after* a
successful navigation only (`if (navigated && clearBackStack)` — a
refused/failed navigation must not wipe history). Pushes
(`NavigateSettings(section)`, `NavigateCatalog`, `OpenTitle`,
`PlayVideo`) pass nothing and keep their semantics; `ForwardStack`
is untouched anywhere. One known window remains: during `Navigated`
the stale entry is still present (`CanGoBack == true` for a moment);
no reader runs there today (`ContentFrame_Navigated` reads only
`e.Content`/`e.Parameter`, `HandleBack` runs on user input), so it is
unobservable — a future `CanGoBack` reader inside `Navigated` would
need `NavigateToType` + `IsNavigationStackEnabled = false` instead.

### 2. Home row memory (FocusCoordinator.cs + NativeInputRouter.cs + MainPage.xaml.cs)

`NavigateHome` always passes `null`, so every Home visit shares one
entry key (`Nuvio.Views.HomePage|`) and `TryMoveToAdjacentRow` read
that entry's row map with no back-path gate — a fresh visit landed on
a previous visit's poster. Fix: `ClearRowFocusKeys(entryKey)` (public
in the coordinator, `internal` in the router) drops one entry's row
map only; `ContentFrame_Navigated` calls it on both registries for
fresh arrivals (`NavigationMode != Back`) before the settle. Back
arrivals keep the map, so Back still restores the saved poster, and
within-visit row memory rebuilds from `GotFocus` records. The clear
lives on the `Navigated` boundary, not inside
`SettleArrivalFocus`/`RequestArrival`, because the text-dialog
restore re-settle (`MainPage.xaml.cs:480`) calls those with
`isBack=false` mid-visit. Only `HomePage` implements `IRowFocusInfo`,
so Search/Catalog are unaffected (their row maps are empty no-ops).
Arrival keys are untouched — `TrySettleOnce` already gates arrival
restore on `isBack`.

### 3. Sidebar current-page button (MainPage.xaml.cs + FocusCoordinator.cs comment)

Supersedes the "first option" rule from the three-part fix above
(see its own section). `FocusSidebarFirst()` hardcoded `HomeButton`
while `FocusSidebar()` (hero branch) already resolved the current
page's button from `_activeRoot`. Fix: shared helper
`SidebarButtonForCurrentRoot()` used by both
(`search` → `SearchButton`, `library` → `LibraryButton`, `settings`
→ `SettingsButton`, else `HomeButton`); the root comes from
`ContentFrame_Navigated`/`SetActiveSidebarButton`, so
`PlaceholderPage` resolves through its `SidebarRoot`. Hidden-sidebar
pages still return `false` and fall through to XYFocus.

Device test list: Home → Search (menu) → B lands on Home (not
Search); Details → Settings (root) → B lands on Home (not Details);
Settings → B lands on Home; Home → B shows the toast, second B
within 2500 ms exits; fresh Home visit: down to a row lands on its
first poster, Back to Home then Up/Down restores the saved poster,
and within one fresh visit down-then-up returns to the same poster;
Settings right from the Debrid row lands on Settings; Search right
from the bottom chips lands on Search.

## Phase 5 — test package + FOCUS.md + guard/race spec (2026-11-14)

Final phase of the Focus Manager programme. No behaviour change except
two log strings (see below). Everything here is local commits on
`codex/native-ui`; the `build.yml` changes in this section are a
ready-to-apply spec only — that file is edited through the GitHub web
editor in a later step, never in a code change.

### 1. What changed (commits, dependency order)

- `d3f6b42` "Expand focus test harness to full package" —
  `Nuvio/Diagnostics/FocusTestHarness.cs`: 1 scripted test → 46 automated
  tests in a registry (`BuildTests`), each writing one
  `TEST <name> PASS|FAIL|SKIP <detail>` line to `nuvio-vlc.log`, plus
  `TEST suite START …` / `TEST suite DONE passed=N failed=M skipped=S ms=N`.
  Tests are grouped: 2 gate self-checks, 9 Phase 1 (Settings), 13 Phase 2
  (Home/Search/Catalog), 10 Phase 3 (dialogs + Streams), 8 Phase 4 (OSD),
  3 flag-off sanity. The 2 remaining package tests are manual gate checks
  (no in-app suite can observe its own non-execution — see FOCUS.md §7).
  Input goes only through the pinned `MainPage.SimulateFocusInput` seam
  (the ControllerBridge poll path). Dialogs are closed with the dialog's
  own `Hide()` and options chosen with `ButtonAutomationPeer.Invoke()`
  (both verified on Microsoft Learn: `VisualTreeHelper.GetOpenPopups`,
  `ButtonAutomationPeer.Invoke`, UniversalApiContract v1.0, 10.0.10240.0+)
  because the seam cannot drive a dialog — `HandleInput` gates all dispatch
  while one is open, and the native system-back cannot be raised
  synthetically. The restore/navigation after the close is the real
  production path in both cases.
- `e27946c` "Distinguish null focus from bare container in settle log lines" —
  `Nuvio/Input/FocusCoordinator.cs`, text only, no behaviour: the two
  settle log lines that said "bare container" also fired when focus was
  null (`AttemptSettle` and `NoteUserInput` — `IsRealFocusTarget(null)` is
  false, same as a bare container). Both now log null focus as null.
- `d8e038b` "Add focus guard checker for Focus call sites" —
  `tools/check-focus-guard.py` (new). See §2.
- `ffd2792` "Finalize FOCUS.md for the Focus Manager contract" —
  `FOCUS.md` (new file, was an untracked draft). Corrections applied
  against the implementation: dialogs are a stack of invokers
  (`Stack<Control> _dialogInvokers`), not a single slot; registry eviction
  is FIFO (200 entries / 20 search queries), not LRU; the router migrates
  Settings/Home/Search/Catalog only; the re-render fallback order is
  key → nearest sibling → arrival → first element in the frame; the §7
  gate is the `focustest` launch token or flag file (not a Settings row);
  the §5 OSD rules now carry the full hint vocabulary, the Opacity=0
  mechanism, the NextCard/ErrorBox exception, and the dialog/settle
  guards; the sidebar rule states the current rule (current page's
  button — the early "always first option" rule is recorded as superseded);
  `FocusScrollHelper` is recorded as absorbed in Phase 2.
- This section (FOCUS-MIGRATION.md) — the guard/race spec in §2/§3 below.

### 2. CI guard spec — ready to apply to `.github/workflows/build.yml`

Apply through the GitHub web editor (never in a code change). After the
edit, validate the file parses (`yaml.safe_load`) and append the change
to the sync record manually. The `guard` job today (`build.yml:27`) has
two patterns only: banned identifiers (`:38`) and banned APIs (`:49`).
There is no `concurrency:` key anywhere in the file.

**(a) Focus guard — new step in the `guard` job, after the
"Fail if banned identifiers return" step.** Runs the checker committed
above; it exits 0 on the audited head (74 sites, all allowlisted, F0
clean) and 1 on any violation. Exact YAML (6 spaces for `- name:`,
8 for `run:` — the step is a single command, no block):

```yaml
      - name: Focus guard
        run: python3 tools/check-focus-guard.py
```

Rules the checker enforces (see its header for the full allowlist):
- **F0:** no `.Focus(` at all under `Nuvio/ViewModels`, `Nuvio/Playback`,
  `Nuvio/Services`, `Nuvio/Diagnostics` (zero hits on the audited head).
- **F1:** every other `.Focus(` site must be an allowlisted
  (file, method, receiver) triple. The allowlist is the complete inventory
  of the audited head — 74 sites, 72 distinct triples — keyed by method
  and receiver, never by line number (they drift). The 4 documented
  deferred sites are: `DetailsPage.ShowEpisodeMenuAsync` / `control`
  (audited :520, dialog-restore tick), `HomePage.FocusContinueNeighbour` /
  `container` (audited :1220, neighbour tick),
  `PlayerPage.OnViewModelPropertyChanged` / `NextPlayButton` (audited :259),
  `TryNextButton` (audited :270) and `RetryButton` (audited :271)
  (ViewModel-driven Next/error auto-focus). A new site fails CI until it
  is added to the allowlist deliberately, with review.

**(b) Alignment guard — extend the same new step into a block, or add a
second step; exact YAML for the block form (10 spaces for script lines,
12 for the `if` bodies):**

```yaml
      - name: Focus and alignment guards
        run: |
          python3 tools/check-focus-guard.py
          if grep -rn '\.Padding = new Thickness' --include='*Page.xaml.cs' Nuvio | grep -v 'SubtitleBackgroundBorder\.Padding'; then
            echo "::error::New code-behind Padding assignment on a page — use the shared anchor helper instead."
            exit 1
          fi
          if grep -rn 'new Thickness([^)]*\(88\|156\|74\)' --include='*.cs' Nuvio | grep -v 'Nuvio/Views/HomePage.xaml.cs'; then
            echo "::error::Hardcoded row/hero inset Thickness outside HomePage — use the anchor helpers."
            exit 1
          fi
```

Baselines verified on the audited head: the first grep finds exactly
2 hits, both `SubtitleBackgroundBorder.Padding` in `PlayerPage.xaml.cs`
(:375, :383) — the only legitimate code-behind padding assignments.
The second grep finds zero hits: the 88/156/74 inset values reach the
layout computed via `VisualAnchorInset`/`RowStartAnchorInset` in
`HomePage.xaml.cs` only, never as `Thickness` literals. Both greps are
tripwires for future hardcoded values, not descriptions of live code.

### 3. Rolling-draft race fix — ready to apply to `.github/workflows/build.yml`

**Problem:** two overlapping runs race the rolling draft — whichever
finishes last uploads over the newer build (observed: 597 over 598;
614 left the draft on 610). **Fix chosen: check `github.sha` is still
the branch head before upload — not `concurrency:`.** `concurrency` is
a workflow/job-level key only (there is none in the file); putting it
on the `package` job would serialize the whole job including the Debug
matrix leg. The head check is precise: only a stale upload is skipped,
fresh runs are untouched, and a failed API read warns instead of
failing the build.

**Exact insertion point:** in the "Publish rolling draft release" step
(`build.yml:290`, `shell: pwsh`), after line 308
(`$notes -join "`n" | Set-Content rolling-notes.md -Encoding utf8`)
and before line 309 (`gh release view native-latest *> $null`).
Base indent is 10 spaces, nested lines 12 (matching the existing
`if ($LASTEXITCODE …) {` blocks). Exact lines to insert:

```yaml
          $head = gh api "repos/$env:GITHUB_REPOSITORY/commits/$env:GITHUB_REF_NAME" --jq '.sha' 2>$null
          if ($head -and $head -ne $env:GITHUB_SHA) {
            Write-Host "Stale run: branch head is $head, this run is $env:GITHUB_SHA; skipping rolling-draft upload."
            exit 0
          }
          if (-not $head) { Write-Host "::warning::Could not read the branch head; uploading anyway." }
```

Semantics: a stale run exits 0 before any `gh release` call — the step
succeeds, the job stays green, the draft keeps the newer build. An API
failure leaves `$head` empty, logs a warning, and uploads anyway (a
failed read must never fail a good build). `GITHUB_REF_NAME` is the
branch name here because the step's `if:` already excludes tags and
pull requests.

### 4. Gate decision (recorded)

The harness stays inert behind its gate in Release — launch token
`focustest` or a flag file named exactly `focustest` in LocalState.
It is NOT switched to `#if DEBUG`: the package must be runnable on the
release msix on the Xbox, where there is no DEBUG build to install.

### 5. Deliberately left out

- **`NotifyContentChanged` in `HomePage.OnContinueRefreshed`** — left out.
  The page already restores itself there; wiring the coordinator call
  would double the restore (coordinator restore + page restore racing).
- **One-shot suppression of the Continue-remove jump** — left out. The
  jump is the coordinator restore followed by `FocusContinueNeighbour`
  re-focusing the neighbour; suppressing it needs shared state between
  the two, not a one-line fix. It is a follow-up, not Phase 5 scope.
- **`NoteUserInput` log line** — NOT left out: fixed in this phase
  (`e27946c`, text only — null focus is now logged as null, not as a
  bare container, in both `AttemptSettle` and `NoteUserInput`).

### 6. Known limits (honest)

- No physical Gamepad simulation is possible (`Gamepad.Gamepads` is
  hardware read-only): the suite drives the post-poll response path, so
  poll-level behaviour (repeat cadence, thumbstick thresholds, missed
  edges) stays untested.
- No headless UI tests run in CI: the suite needs a live visual tree, UI
  thread, and window. CI checks compile + guards; the Xbox runs the
  package.
- Two tests are documented SKIP with a manual device check instead of a
  fixture the suite cannot construct: the Debrid invalid-key dialog
  (needs a torrent stream resolved against an invalid Debrid key) and the
  Next-card park rule (needs playback near the end of an episode).
- The suite cannot close a dialog through the input seam (see §1); dialog
  tests close with `Hide()` / `ButtonAutomationPeer.Invoke()`.
- `settled=true` / `focusName=` log lines the suite reads are the
  coordinator's own settle lines, not a test hook; if their wording
  changes, `home-launch-coordinator-settle` must be updated with them.

### 7. Harness run on build 623 (2.0.623.0) — result and fixes

- **Run:** head `9c69353`, device run by Mohammed, 48 s:
  **19 PASS / 8 FAIL / 19 SKIP** (log `nuvio-vlc_11-2.log`).
  Most SKIP are "no playable stream" — the player/OSD tests need a real
  stream, so they stay manual checks.
- **Three harness false positives** (all in `FocusTestHarness.cs`,
  commit `08274a9`):
  - `LogContainsSince` searched the log for `"settled=true"` with a
    case-sensitive match while the log writes `"settled=True"` — now
    `OrdinalIgnoreCase`.
  - `TestSettingsMenuBackCollapsesAsync` pressed Left to expand the
    sidebar; in RTL the toward-sidebar direction is Right — now
    flow-aware (`Right` in RTL, `Left` in LTR).
  - `FocusedCardMetaId` cast card content `as Meta`; catalog items are
    `CatalogCardItem` wrapping `Meta` — now reads `card.Meta.Id`, and the
    test steps to the previous card (bounded, 3 attempts) off a ViewAll
    card, which has `Meta == null` by design.
- **One real app gap — cached Home + settle shortcut** (commits `516dc9f`
  coordinator, `3b83f2a` legacy router): `HomePage` is `Required`-cache,
  so re-navigating Home while already on it returns the same instance
  with focus still on a card, and the settle's sync attempt accepted
  that stale focus as settled instead of moving to hero Play. The
  "already settled" shortcut is now gated to Back arrivals in both
  settle paths; fresh arrivals always run `TrySettleOnce`. Home needed
  one more step: `HomePage.TryFocusArrival()` bails out (returns true)
  when focus is already inside the page, so on a fresh Home arrival the
  settle calls `TryFocusKey("hero")` first (no bail-out; focuses hero
  Play when a hero exists, else falls through to the arrival call for
  the empty-library target). The `TryFocusArrival` bail-out itself was
  deliberately NOT changed in this round — the workaround is Home-scoped
  in the settle. No new `.Focus(` sites.
- **Gate not yet re-run:** these fixes need a green build and a fresh
  device harness run to confirm the 8 FAILs are gone.

## Anchor removal — pinned focus removed in full (2026-11-14)

- **What was built:** the pinned-focus (المرساة) round, commits `341efd9`
  (instant anchor jump in `FocusScrollHelper`) ← `0358c5c`
  (entered-row offset reset in `FocusCoordinator`) ← `abaa51b`
  (pre-scroll before in-row focus moves in `HomePage`) ← `77cf387`
  (position test `home-anchor-pins-second-slot`) ← `af7c24f` / `4dc4bb9`
  (docs). Build 634 (2.0.634.0) was green on that head.
- **Why it was removed:** the pin position was misunderstood — the
  implementation pinned the focused card at the second-poster slot
  (619..707 RTL) while keeping the k−1 anchor target, and the result
  broke the behaviour the user wanted. Removed at the user's request
  ("remove it completely"), in full, the same day.
- **What the removal is:** all five code/doc files restored to their
  40b1aa9 content (commits `706e552` ← `43b70d7` ← `3e70439` ←
  `3d21ecd` ← `bc6fc50`); the anchor rule is gone from `FOCUS.md`;
  `TryComputeRowAnchorTarget` / `AnchorRow` / `SmoothScrollTo` /
  `VerifyRowAnchor` / `SnapRowEnd` are back to their pre-anchor
  behaviour exactly (post-move glide, k−1 target, second slot at rest).
  No new behaviour was added in place of the anchor.
- **Open question:** the correct pin position / anchor behaviour is
  still unknown — it needs a video from the user showing exactly where
  the ring should sit before any new attempt.

## Anchor re-attempt — focused card pinned on the anchor (2026-11-14)

Answers the open question in the removal section above: the pin
position is now defined — the focused card itself sits on the anchor —
and the build-634 failure is diagnosed below, not repeated.

- **(a) The new rule (inverts k−1):** the focused card itself (k) sits
  on the hero edge — RTL 716..804, LTR 26..114, the second poster from
  the right edge — instead of the card before it (k−1 target, build 292,
  kept through the removal round). The k−1 target is superseded. What
  does not change: which card is focused, the order, and per-row memory.
  At the row tail (offset clamped at `ScrollableWidth`) and in short rows
  the ring detaches and follows the card, as before. Catalog has no
  anchor; Search pins to its own 6 epx start edge.
- **(b) Why build 634 failed in practice — two parallel scroll engines:**
  the instant `JumpTo` was put in `FocusScrollHelper` (the shared helper
  behind the Search path only — `HomePage` never uses the helper at all),
  while `HomePage` owns a second, separate engine of its own
  (`SmoothScrollTo` / `_smoothScrolls` / `OnSmoothScrollRendering`, an
  exponential glide driven on `CompositionTarget.Rendering`). The
  pre-scroll in `HomePage.HandleAction` used a raw `ChangeView`, which
  moved the offset but never cancelled `HomePage`'s in-flight glide
  state — so the stuck glide dragged the strip back toward the previous
  target on every press, which matches the device video (the ring
  travelled with the posters and the strip scrolled late). Separately,
  tau 105 ms against the 110 ms repeat rate meant the glide was always
  still running during a hold: each step was only ~65% covered
  (`1 − exp(−110/105)`) when the next press landed, ~35% of every step
  still outstanding, so the strip could never catch up. The jump fixed
  the wrong engine, and the glide was too slow for the repeat rate.
- **(c) tau ≈ 35 ms for anchor slides only:** `HomePage.AnchorRow` now
  passes 35 ms to `SmoothScrollTo` (per-glide `SmoothScrollState.TauMs`;
  the 105 ms default is untouched for every other glide — row entry,
  vertical moves). One repeat interval (110 ms) now covers ~96% of a
  step (`1 − exp(−110/35)`) instead of ~65%, so the strip is effectively
  settled before the next press. Tunable 35–50 ms on the device if the
  slide feels too snappy or too loose — the constant lives in the
  `AnchorRow` call only.
- **(d) Harness:** the position test is re-added as
  `home-anchor-pins-focused-card` (47 automated tests now), with the new
  constants: it walks a long 88 epx poster row and asserts the focused
  card's rect sits at 716..804 RTL / 26..114 LTR (±2 epx) after the first
  step and after a 3-step walk, while `HorizontalOffset` grows. It skips
  when no 88 epx poster row is reached or the row is too short to move
  the strip (`ScrollableWidth < 400`). Gate: device walk across a long
  row — ring visually fixed on the anchor, strip gliding under it; tail
  walk shows the documented detach.
