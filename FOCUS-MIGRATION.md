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
   on Home (first option), not on Settings.
4. Search: right from the bottom chips — the strip opens and focus lands
   on Home, not on Settings.
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
