using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Nuvio.Input;
using Nuvio.Models;
using Nuvio.Services;
using Nuvio.ViewModels;
using Nuvio.Views;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Nuvio.Diagnostics
{
    /// <summary>
    /// Phase 5 focus-test package (Focus Manager programme, final phase).
    ///
    /// 49 tests in the package: 47 automated here (the registry in
    /// <see cref="BuildTests"/>) + 2 manual gate checks (dormant-with-no-gate,
    /// flag-file-removal-disarms) that no in-app suite can observe about
    /// itself — see FOCUS.md §7 for the manual procedures.
    ///
    /// Drives D-pad/back input ONLY through the pinned MainPage seam
    /// <see cref="MainPage.SimulateFocusInput(string, bool)"/> — the same code
    /// path the ControllerBridge poll callback uses (router Handle, then the
    /// settling-aware takeover decision). This harness never calls the router
    /// or FocusManager directly for input, and never sets focus itself except
    /// through the seam. Activation (Gamepad A) cannot go through the seam:
    /// the router ignores "select" by design (XAML activates the focused
    /// control natively with a real controller), so ActivateFocusedAsync
    /// performs the activation the focused element would perform — the
    /// NavigationService entry the page's own ItemClick handler calls with
    /// the same arguments, or ButtonAutomationPeer.Invoke() for a focused
    /// Button, which runs the button's real Click handler. Two further
    /// documented exceptions, both test-only closers for dialogs the seam
    /// cannot reach (see below); neither moves focus: closing a dialog with
    /// ContentDialog.Hide() found through VisualTreeHelper.GetOpenPopups,
    /// and choosing a dialog option with ButtonAutomationPeer.Invoke(). The
    /// coordinator restore / navigation that runs after the close is the real
    /// production path in both cases.
    ///
    /// WHY the seam cannot drive dialogs: FocusCoordinator.HandleInput gates
    /// all dispatch on IsDialogOpen, so D-pad/select presses sent through the
    /// seam while a dialog is open are swallowed by design; the dialog closes
    /// natively on the real system-back (Gamepad B), which a synthetic press
    /// cannot raise. Opening a dialog IS driven through the seam (Y/Menu on
    /// the invoker); closing it uses the dialog's own Hide(), which completes
    /// the awaited ShowAsync and runs the production finally block
    /// (DialogOpen = false → coordinator restore tick).
    ///
    /// OUTCOMES: each test writes one line
    /// `TEST &lt;name&gt; PASS|FAIL|SKIP &lt;detail&gt;` to nuvio-vlc.log.
    /// SKIP means the fixture the test needs was absent (no Continue items,
    /// no catalog data, no playable stream, cold-start-only) — it is not a
    /// pass and not a failure. A FAIL is a real focus-rule violation.
    /// The suite also writes `TEST suite START …` and
    /// `TEST suite DONE passed=N failed=M skipped=S ms=N`.
    ///
    /// DORMANCY: the suite never runs unless the launch arguments contain
    /// the token "focustest" (see <see cref="LaunchToken"/>) OR a flag file
    /// named exactly "focustest" (no extension) exists in the app's local
    /// folder (ApplicationData.Current.LocalFolder — the LocalState folder;
    /// no Settings UI row, no capability needed for the app's own folder).
    /// The flag file is the msix fallback: a dashboard launch of the
    /// installed msix always carries empty Arguments, so the token could
    /// never fire there. There is no auto-run, no timer and no constructor
    /// side effect: the constructor only stores the shell, and
    /// <see cref="RunIfRequested"/> returns immediately when neither gate
    /// fires. Launch with the token from Visual Studio (Debug &gt; command
    /// line arguments) or any launcher that forwards arguments to
    /// LaunchActivatedEventArgs.Arguments; on the device, upload the flag
    /// file via Device Portal File Explorer and delete it to disarm.
    /// The gate decision stands as built: inert behind the gate in Release
    /// (NOT #if DEBUG) — the package must be runnable on the release msix
    /// on the Xbox, where there is no DEBUG build to install.
    ///
    /// NOTE on the settle observation: NativeInputRouter.IsSettling and its
    /// attempt counter are private to the router, and MainPage exposes no
    /// accessor for them, so this harness cannot poll IsSettling. It observes
    /// readiness instead through the page's IsContentReady plus the focused
    /// element's identity (FocusManager.GetFocusedElement, verified on
    /// Microsoft Learn: Windows.UI.Xaml.Input.FocusManager, UWP, in
    /// 10.0.10240.0+; the project targets 10.0.22621.0, min 10.0.22000.0).
    /// The logged settleAttempts is therefore the number of readiness polls
    /// this harness performed — NOT the router's internal attempt count,
    /// which is unreachable from here. Never read it as the router's number.
    ///
    /// NOTE on the hero Play identity: HomePage.TryFocusArrival focuses
    /// HeroPlayButton by name when the hero exists (ManageAddonsButton when
    /// the library is empty). The hero button's Tag is NOT "hero": it is the
    /// single Segoe MDL2 glyph &#xE768; (HomePage.xaml), and FocusablePage
    /// explicitly treats single-character tags as icons, never arrival keys.
    /// The arrival key "hero" is what HomePage.TryFocusKey resolves; the
    /// assertion below therefore identifies the button by Name
    /// ("HeroPlayButton") and HomePage ancestry, and logs the expected
    /// arrival key ("hero") against the button's actual Tag.
    ///
    /// DIALOG CLOSE APIs (verified on Microsoft Learn before use):
    /// VisualTreeHelper.GetOpenPopups(Window) —
    /// Windows.UI.Xaml.Media, public static IReadOnlyList&lt;Popup&gt;
    /// GetOpenPopups(Window window), UniversalApiContract v1.0 (10.0.10240.0+);
    /// ButtonAutomationPeer.Invoke() — Windows.UI.Xaml.Automation.Peers,
    /// public void Invoke(), "sends a request to click the button associated
    /// with the automation peer", UniversalApiContract v1.0 (10.0.10240.0+).
    /// </summary>
    internal sealed class FocusTestHarness
    {
        /// <summary>Launch-argument token that arms the harness. Case-insensitive substring match.</summary>
        public const string LaunchToken = "focustest";

        /// <summary>Gap between simulated presses: above the router's 150 ms direction dedupe, so one scripted press is one move, as on the controller.</summary>
        public const int InterPressDelayMs = 200;

        /// <summary>How often the settle wait re-checks readiness (100 ms polls; the router watchdog ticks every 500 ms).</summary>
        private const int SettlePollIntervalMs = 100;

        /// <summary>Bounded wait for the shell to finish loading (NavigationService is created in MainPage.OnLoaded).</summary>
        private const int ShellReadyTimeoutMs = 10000;

        /// <summary>Bounded wait for the arrival settle: hero load is ~1.3 s on device and the router watchdog runs up to ~5 s.</summary>
        private const int SettleTimeoutMs = 15000;

        /// <summary>Bounded wait for a page's content to become ready after navigation (network data).</summary>
        private const int PageReadyTimeoutMs = 15000;

        /// <summary>Bounded wait for playback to open (stream resolve + player ready).</summary>
        private const int PlayerOpenTimeoutMs = 20000;

        private const string HeroPlayButtonName = "HeroPlayButton";
        private const string ManageAddonsButtonName = "ManageAddonsButton";
        private const string ExpectedHeroTagAndType = "hero/Button";
        private const string LogFileName = "nuvio-vlc.log";
        private const string CinemetaTransportUrl = "https://v3-cinemeta.strem.io/manifest.json";

        private readonly MainPage _shell;
        private readonly string _armedVia;
        private long _suiteLogOffset;

        /// <summary>Failing step recorded by TryReachPlayerAsync on every
        /// false return, so its callers can skip with the real reason
        /// ("reach failed: ...") instead of the blanket "no playable
        /// stream". Cleared at the start of each reach attempt.</summary>
        private string _lastReachFailure;

        /// <summary>Start-of-suite addon snapshot for the guard (b): the
        /// installed addons as they were before the first test, so the
        /// suite can warn about and restore anything a test removed or
        /// disabled. Null when the guard was skipped (no service / error).</summary>
        private List<Addon> _addonGuardStart;

        /// <summary>Name of the test currently running in the suite loop,
        /// for the addon guard's CHANGE lines (null outside a test). Set at
        /// the top of each loop iteration, cleared when the suite ends.
        /// No current-test tracking existed before the guard watch needed it.</summary>
        private string _addonGuardCurrentTest;

        /// <summary>Last-seen addon list as a comparison key (transport URL
        /// + enabled flag per addon — never logged, only compared), so the
        /// event handler and the per-test poll can tell a real change from
        /// a repeat notification of the same list.</summary>
        private string _addonGuardLastSeen;

        public FocusTestHarness(MainPage shell)
            : this(shell, "unknown")
        {
        }

        internal FocusTestHarness(MainPage shell, string armedVia)
        {
            _shell = shell;
            _armedVia = armedVia;
        }

        /// <summary>One test's outcome. Skip = fixture absent, not a pass and not a failure.</summary>
        internal enum FocusTestOutcome { Pass, Fail, Skip }

        /// <summary>One test's result, logged as `TEST &lt;name&gt; PASS|FAIL|SKIP &lt;detail&gt;`.</summary>
        internal sealed class FocusTestResult
        {
            public string Name { get; private set; }
            public FocusTestOutcome Outcome { get; private set; }
            public string Detail { get; private set; }

            private FocusTestResult(string name, FocusTestOutcome outcome, string detail)
            {
                Name = name;
                Outcome = outcome;
                Detail = detail;
            }

            public static FocusTestResult Pass(string name, string detail = null)
            {
                return new FocusTestResult(name, FocusTestOutcome.Pass, detail);
            }

            public static FocusTestResult Fail(string name, string detail)
            {
                return new FocusTestResult(name, FocusTestOutcome.Fail, detail);
            }

            public static FocusTestResult Skip(string name, string detail)
            {
                return new FocusTestResult(name, FocusTestOutcome.Skip, detail);
            }
        }

        /// <summary>One registry entry: a test name and the method that runs it.</summary>
        private sealed class FocusTest
        {
            public string Name { get; private set; }
            public Func<Task<FocusTestResult>> Run { get; private set; }

            public FocusTest(string name, Func<Task<FocusTestResult>> run)
            {
                Name = name;
                Run = run;
            }
        }

        /// <summary>
        /// Dormant entry point. Returns immediately unless
        /// <paramref name="launchArguments"/> contains the "focustest" token
        /// or a flag file named exactly "focustest" exists in the app's
        /// local folder (the msix fallback — a dashboard launch carries no
        /// arguments). Safe to call from MainPage.OnNavigatedTo (before
        /// OnLoaded): the run waits for the shell to be ready before
        /// navigating. Fire-and-forget on the UI thread; the suite writes
        /// one TEST line per test plus START/DONE lines to the log.
        /// </summary>
        public static void RunIfRequested(MainPage shell, string launchArguments)
        {
            if (shell == null) return;
            var tokenArmed = !string.IsNullOrEmpty(launchArguments)
                && launchArguments.IndexOf(LaunchToken, StringComparison.OrdinalIgnoreCase) >= 0;
            var flagArmed = !tokenArmed && HasFlagFile();
            if (!tokenArmed && !flagArmed) return;
            var harness = new FocusTestHarness(shell, tokenArmed ? "launch-token" : "flag-file");
            // Always via the dispatcher: this may be called before the shell
            // is loaded, from any thread. The run itself awaits on the UI
            // thread, so Task.Delay continuations stay on it too.
            shell.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                _ = harness.RunAllTestsAsync();
            });
        }

        /// <summary>
        /// True when a file named exactly "focustest" (no extension) exists
        /// in ApplicationData.Current.LocalFolder. Synchronous System.IO
        /// against the folder's Path (verified against Microsoft Learn:
        /// ApplicationData.LocalFolder and StorageFolder.Path exist in UWP,
        /// UniversalApiContract v1.0, and the app needs no capability to
        /// access its own app folders). Any failure (no path, no access)
        /// means not armed — never throws out of the dormant entry point.
        /// </summary>
        private static bool HasFlagFile()
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                if (folder == null || string.IsNullOrEmpty(folder.Path)) return false;
                return File.Exists(Path.Combine(folder.Path, LaunchToken));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The suite: 47 automated tests in registry order (the two gate
        /// self-checks first, then Phase 1–4, flag-off tests last so the flag
        /// is restored before the suite ends). Every test runs even after a
        /// failure, so one failure does not hide the rest. A throwing test
        /// is a FAIL, never a crash: the suite logs it and continues.
        /// </summary>
        public async Task RunAllTestsAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            var passed = 0;
            var failed = 0;
            var skipped = 0;
            _suiteLogOffset = LogOffset();
            Log.Write("TEST suite START tests=47 armedVia=" + _armedVia);
            var shellReady = await WaitForShellReadyAsync().ConfigureAwait(true);
            if (!shellReady)
            {
                Log.Write("TEST suite FAIL shell-not-ready ms=" + stopwatch.ElapsedMilliseconds);
                return;
            }
            _addonGuardStart = await TakeAddonGuardSnapshotAsync().ConfigureAwait(true);
            SubscribeAddonGuardChangeWatch();
            foreach (var test in BuildTests())
            {
                _addonGuardCurrentTest = test.Name;
                FocusTestResult result;
                try
                {
                    result = await test.Run().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    result = FocusTestResult.Fail(test.Name, "exception " + ex.GetType().Name + ": " + ex.Message);
                }
                if (result == null) result = FocusTestResult.Fail(test.Name, "null result");
                Log.Write("TEST " + result.Name + " " + result.Outcome.ToString().ToUpperInvariant()
                    + (string.IsNullOrEmpty(result.Detail) ? string.Empty : " " + result.Detail));
                switch (result.Outcome)
                {
                    case FocusTestOutcome.Pass: passed++; break;
                    case FocusTestOutcome.Fail: failed++; break;
                    default: skipped++; break;
                }
                CheckAddonGuardPoll();
            }
            _addonGuardCurrentTest = null;
            await VerifyAndRestoreAddonGuardAsync().ConfigureAwait(true);
            UnsubscribeAddonGuardChangeWatch();
            // Defensive flag restore: the flag-off tests restore in their
            // own finally blocks; this is the backstop so the app never
            // leaves the suite with the coordinator switched off.
            if (!FocusCoordinator.UseFocusCoordinator)
            {
                FocusCoordinator.UseFocusCoordinator = true;
                Log.Write("TEST suite flag restored to on");
            }
            Log.Write("TEST suite DONE passed=" + passed + " failed=" + failed + " skipped=" + skipped
                + " ms=" + stopwatch.ElapsedMilliseconds);
        }

        /// <summary>
        /// Addon guard (b): snapshot the installed addons before the suite
        /// runs, so VerifyAndRestoreAddonGuardAsync can warn about and
        /// restore anything a test removed or disabled. The copies are
        /// manual, never live references: MergeAddons mutates its inputs in
        /// place, and SetAddonEnabled/RefreshAddonAsync mutate fields in
        /// place. Never fails the suite: any problem logs an ERROR and the
        /// guard is skipped (null snapshot).
        /// </summary>
        private async Task<List<Addon>> TakeAddonGuardSnapshotAsync()
        {
            try
            {
                if (_shell == null || _shell.Stremio == null)
                {
                    Log.Write("Harness addon guard: WARNING no Stremio service, guard skipped");
                    return null;
                }
                await _shell.Stremio.LoadAddonsAsync().ConfigureAwait(true);
                var start = new List<Addon>();
                foreach (var addon in _shell.Stremio.ListAddons())
                {
                    if (addon == null || string.IsNullOrEmpty(addon.TransportUrl)) continue;
                    start.Add(new Addon
                    {
                        TransportUrl = addon.TransportUrl,
                        Manifest = addon.Manifest,
                        Enabled = addon.Enabled,
                        RefreshedAt = addon.RefreshedAt,
                        Error = addon.Error,
                        Handler = addon.Handler,
                    });
                }
                Log.Write("Harness addon guard: start " + start.Count + " addons");
                return start;
            }
            catch (Exception ex)
            {
                Log.Write("Harness addon guard: ERROR " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Addon guard (b): re-read the installed addons, warn about any
        /// that went missing or changed enabled-state since the snapshot,
        /// and restore them through the existing public API only
        /// (MergeAddons + SetAddonEnabled). Never fails the suite: warnings
        /// stay warnings, and any problem logs an ERROR. An empty start
        /// snapshot with addons at the end is a late load, not a loss — no
        /// warning, no restore. Extra addons that were not in the snapshot
        /// are not a loss either.
        /// </summary>
        private Task VerifyAndRestoreAddonGuardAsync()
        {
            try
            {
                var start = _addonGuardStart;
                if (start == null) return Task.FromResult(0);
                if (_shell == null || _shell.Stremio == null)
                {
                    Log.Write("Harness addon guard: WARNING no Stremio service, guard skipped");
                    return Task.FromResult(0);
                }
                var end = _shell.Stremio.ListAddons();
                Log.Write("Harness addon guard: end " + end.Count + " addons");
                if (start.Count == 0) return Task.FromResult(0);
                var endByUrl = new Dictionary<string, Addon>();
                foreach (var addon in end)
                {
                    if (addon == null || string.IsNullOrEmpty(addon.TransportUrl)) continue;
                    endByUrl[addon.TransportUrl] = addon;
                }
                var missing = new List<Addon>();
                var enabledChanged = new List<Addon>();
                foreach (var addon in start)
                {
                    Addon current;
                    if (!endByUrl.TryGetValue(addon.TransportUrl, out current)) { missing.Add(addon); continue; }
                    if ((addon.Enabled != false) != (current.Enabled != false)) enabledChanged.Add(addon);
                }
                if (missing.Count == 0 && enabledChanged.Count == 0) return Task.FromResult(0);
                if (missing.Count > 0)
                {
                    Log.Write("Harness addon guard: WARNING missing " + missing.Count + " addon(s): " + AddonGuardNames(missing));
                }
                if (enabledChanged.Count > 0)
                {
                    Log.Write("Harness addon guard: WARNING enabled changed for " + enabledChanged.Count + " addon(s): " + AddonGuardNames(enabledChanged));
                }
                if (missing.Count > 0)
                {
                    // Fresh copies for the merge: MergeAddons mutates its
                    // inputs (forces Enabled=true, stamps RefreshedAt), so
                    // the snapshot itself must never be passed in.
                    var copies = new List<Addon>();
                    foreach (var addon in missing)
                    {
                        copies.Add(new Addon
                        {
                            TransportUrl = addon.TransportUrl,
                            Manifest = addon.Manifest,
                            Enabled = addon.Enabled,
                            RefreshedAt = addon.RefreshedAt,
                            Error = addon.Error,
                            Handler = addon.Handler,
                        });
                    }
                    _shell.Stremio.MergeAddons(copies);
                    foreach (var addon in missing)
                    {
                        if (addon.Enabled == false) _shell.Stremio.SetAddonEnabled(addon.TransportUrl, false);
                    }
                }
                foreach (var addon in enabledChanged)
                {
                    _shell.Stremio.SetAddonEnabled(addon.TransportUrl, addon.Enabled != false);
                }
                var after = _shell.Stremio.ListAddons();
                if (missing.Count > 0)
                {
                    Log.Write("Harness addon guard: restored " + missing.Count + " addon(s): " + AddonGuardNames(missing));
                }
                var afterUrls = new HashSet<string>();
                foreach (var addon in after)
                {
                    if (addon != null && !string.IsNullOrEmpty(addon.TransportUrl)) afterUrls.Add(addon.TransportUrl);
                }
                var stillMissing = new List<Addon>();
                foreach (var addon in missing)
                {
                    if (!afterUrls.Contains(addon.TransportUrl)) stillMissing.Add(addon);
                }
                if (stillMissing.Count == 0)
                {
                    Log.Write("Harness addon guard: restore verified: " + after.Count + " addons");
                }
                else
                {
                    Log.Write("Harness addon guard: restore FAILED still missing " + stillMissing.Count + " addon(s): " + AddonGuardNames(stillMissing));
                }
            }
            catch (Exception ex)
            {
                Log.Write("Harness addon guard: ERROR " + ex.GetType().Name + ": " + ex.Message);
            }
            return Task.FromResult(0);
        }

        /// <summary>
        /// Addon guard (c): live change watch. Subscribes to
        /// StremioService.AddonsChanged at suite start (right after the
        /// snapshot) and unsubscribes at suite end (after verification).
        /// Every event logs one diagnostic line with the new count, the
        /// addon names (never a transport URL) and the test running at the
        /// time, so the next log shows WHEN the list changed and WHAT was
        /// running — the end-of-suite guard alone could only say that
        /// something was lost. The handler never throws: it runs inside
        /// production save/merge paths.
        /// </summary>
        private void SubscribeAddonGuardChangeWatch()
        {
            try
            {
                if (_shell == null || _shell.Stremio == null) return;
                _addonGuardLastSeen = AddonGuardKey(_shell.Stremio.ListAddons());
                _shell.Stremio.AddonsChanged += OnAddonGuardAddonsChanged;
            }
            catch (Exception ex)
            {
                Log.Write("Harness addon guard: ERROR subscribe failed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void UnsubscribeAddonGuardChangeWatch()
        {
            try
            {
                if (_shell != null && _shell.Stremio != null) _shell.Stremio.AddonsChanged -= OnAddonGuardAddonsChanged;
            }
            catch (Exception ex)
            {
                Log.Write("Harness addon guard: ERROR unsubscribe failed " + ex.GetType().Name + ": " + ex.Message);
            }
            _addonGuardCurrentTest = null;
        }

        private void OnAddonGuardAddonsChanged(object sender, AddonsChangedEventArgs e)
        {
            try
            {
                var addons = e == null ? null : e.Addons;
                _addonGuardLastSeen = AddonGuardKey(addons);
                var count = addons == null ? 0 : addons.Count;
                var names = addons == null ? string.Empty : AddonGuardNames(addons);
                Log.Write("Harness addon guard: CHANGE event count=" + count
                    + " addons=[" + names + "] test=" + (_addonGuardCurrentTest ?? "(none)"));
            }
            catch (Exception)
            {
                // Never throw out of a production event path.
            }
        }

        /// <summary>
        /// Addon guard (c) backstop: the event only fires if
        /// RaiseAddonsChanged runs. If the list is mutated without raising
        /// the event, the event never fires, so after each test the loop
        /// re-reads ListAddons() and compares it with the last-seen set.
        /// A difference logs one diagnostic line in the same format as the
        /// event. Synchronous, inside the existing test flow — no timer,
        /// no background loop.
        /// </summary>
        private void CheckAddonGuardPoll()
        {
            try
            {
                if (_shell == null || _shell.Stremio == null) return;
                if (_addonGuardLastSeen == null) return;
                var current = _shell.Stremio.ListAddons();
                var key = AddonGuardKey(current);
                if (string.Equals(key, _addonGuardLastSeen, StringComparison.Ordinal)) return;
                _addonGuardLastSeen = key;
                Log.Write("Harness addon guard: CHANGE poll count=" + current.Count
                    + " addons=[" + AddonGuardNames(current) + "] test=" + (_addonGuardCurrentTest ?? "(none)"));
            }
            catch (Exception ex)
            {
                Log.Write("Harness addon guard: ERROR poll failed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Comparison key for the addon list: transport URL +
        /// enabled flag per addon, in order. Never logged — only compared.
        /// Names alone would miss an enabled-state flip and could collide
        /// between addons.</summary>
        private static string AddonGuardKey(IEnumerable<Addon> addons)
        {
            var parts = new List<string>();
            if (addons != null)
            {
                foreach (var addon in addons)
                {
                    if (addon == null) continue;
                    parts.Add((addon.TransportUrl ?? string.Empty) + "|" + (addon.Enabled != false ? "1" : "0"));
                }
            }
            return parts.Count + ":" + string.Join(";", parts);
        }

        /// <summary>Guard log names only: the manifest name, else the host
        /// — never the full transport URL, whose query may carry keys.</summary>
        private static string AddonGuardNames(IEnumerable<Addon> addons)
        {
            var names = new List<string>();
            foreach (var addon in addons) names.Add(StremioService.AddonName(addon));
            return string.Join(", ", names);
        }

        /// <summary>
        /// Registry in run order. Gate self-checks first; Phase 1 (Settings),
        /// Phase 2 (Home/Search/Catalog), Phase 3 (dialogs + Streams),
        /// Phase 4 (OSD — the exit test runs after the other in-player tests
        /// so the player stays open for them); flag-off tests last.
        /// The 2 remaining package tests (gate-dormant-no-flag,
        /// gate-flag-file-removed-disarms) are manual procedures in FOCUS.md
        /// §7 — no in-app suite can observe its own non-execution.
        /// </summary>
        private List<FocusTest> BuildTests()
        {
            return new List<FocusTest>
            {
                new FocusTest("gate-launch-token", TestGateLaunchTokenAsync),
                new FocusTest("gate-flag-file", TestGateFlagFileAsync),
                new FocusTest("launch-hero-play", TestLaunchHeroPlayAsync),
                new FocusTest("settings-arrival-first-row", TestSettingsArrivalFirstRowAsync),
                new FocusTest("settings-arrival-strip-collapsed", TestSettingsArrivalStripCollapsedAsync),
                new FocusTest("settings-down-one-row-per-press", TestSettingsDownOneRowAsync),
                new FocusTest("settings-down-never-sidebar", TestSettingsDownNeverSidebarAsync),
                new FocusTest("settings-back-addons-same-row", TestSettingsBackAddonsSameRowAsync),
                new FocusTest("settings-registry-entry-isolation", TestSettingsRegistryIsolationAsync),
                new FocusTest("settings-back-empty-stack-goes-home", TestSettingsBackEmptyStackGoesHomeAsync),
                new FocusTest("settings-menu-back-collapses-returns-to-page", TestSettingsMenuBackCollapsesAsync),
                new FocusTest("settings-guard-cleared-by-settle", TestSettingsGuardClearedBySettleAsync),
                new FocusTest("home-launch-coordinator-settle", TestHomeLaunchCoordinatorSettleAsync),
                new FocusTest("home-down-hero-to-first-card", TestHomeDownHeroToFirstCardAsync),
                new FocusTest("home-row-to-row-per-row-memory", TestHomeRowMemoryAsync),
                new FocusTest("home-down-never-sidebar-or-skip", TestHomeDownNeverSidebarAsync),
                new FocusTest("home-anchor-pins-focused-card", TestHomeAnchorPinsFocusedCardAsync),
                new FocusTest("home-back-details-meta-key", TestHomeBackDetailsMetaKeyAsync),
                new FocusTest("home-back-continue-cw-key", TestHomeBackContinueCwKeyAsync),
                new FocusTest("home-cold-first-entry-hero-play", TestHomeColdFirstEntryAsync),
                new FocusTest("search-arrival-box", TestSearchArrivalBoxAsync),
                new FocusTest("search-down-results-scroll", TestSearchDownResultsAsync),
                new FocusTest("search-back-result-or-box-when-gone", TestSearchBackResultAsync),
                new FocusTest("catalog-arrival-first-card", TestCatalogArrivalFirstCardAsync),
                new FocusTest("catalog-four-direction-grid-scroll", TestCatalogFourDirectionAsync),
                new FocusTest("catalog-back-opened-card", TestCatalogBackOpenedCardAsync),
                new FocusTest("first-entry-first-card-everywhere", TestFirstEntryEverywhereAsync),
                new FocusTest("dialog-home-continue-cancel-restores", TestDialogHomeContinueCancelAsync),
                new FocusTest("dialog-details-episode-restores", TestDialogDetailsEpisodeAsync),
                new FocusTest("dialog-player-subtitle-restores", TestDialogPlayerSubtitleAsync),
                new FocusTest("dialog-player-delay-restores", TestDialogPlayerDelayAsync),
                new FocusTest("dialog-settings-confirm-restores", TestDialogSettingsConfirmAsync),
                new FocusTest("dialog-streams-debrid-both-buttons-restore", TestDialogStreamsDebridAsync),
                new FocusTest("dialog-navigating-choice-arrival-settle", TestDialogNavigatingChoiceAsync),
                new FocusTest("dialog-nested-outer-only-on-outer-close", TestDialogNestedAsync),
                new FocusTest("streams-filter-row-vanishes-fallback", TestStreamsFilterRowVanishesAsync),
                new FocusTest("dialog-guard-no-regression", TestDialogGuardNoRegressionAsync),
                new FocusTest("osd-hide-parks-back-remembers", TestOsdHideParksBackAsync),
                new FocusTest("osd-show-restores-same-button", TestOsdShowRestoresAsync),
                new FocusTest("osd-seek-explicit-hint-wins", TestOsdSeekHintWinsAsync),
                new FocusTest("osd-hidden-y-dialog-then-restore", TestOsdHiddenYDialogAsync),
                new FocusTest("osd-pause-x-hidden-shows-restores", TestOsdPauseXHiddenAsync),
                new FocusTest("osd-hidden-back-twice-exits", TestOsdHiddenBackTwiceExitsAsync),
                new FocusTest("osd-next-card-not-parked", TestOsdNextCardNotParkedAsync),
                new FocusTest("osd-flag-off-old-behaviour", TestOsdFlagOffAsync),
                new FocusTest("flag-off-phase2-legacy", TestFlagOffPhase2Async),
                new FocusTest("flag-off-phase3-dialogs-legacy", TestFlagOffPhase3Async),
            };
        }

        // ---------- Shared helpers ----------

        private async Task<bool> WaitForShellReadyAsync()
        {
            var waitedMs = 0;
            while (waitedMs < ShellReadyTimeoutMs)
            {
                if (_shell != null && _shell.Navigation != null) return true;
                await Task.Delay(SettlePollIntervalMs).ConfigureAwait(true);
                waitedMs += SettlePollIntervalMs;
            }
            return _shell != null && _shell.Navigation != null;
        }

        private async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
        {
            var elapsedMs = 0;
            while (elapsedMs < timeoutMs)
            {
                bool ok;
                try
                {
                    ok = condition();
                }
                catch (Exception)
                {
                    ok = false;
                }
                if (ok) return true;
                await Task.Delay(SettlePollIntervalMs).ConfigureAwait(true);
                elapsedMs += SettlePollIntervalMs;
            }
            return false;
        }

        private async Task WaitForSettleAsync(int timeoutMs = 8000)
        {
            var coordinator = _shell != null ? _shell.Coordinator : null;
            if (coordinator == null) return;
            await WaitForAsync(() => !coordinator.IsSettling, timeoutMs).ConfigureAwait(true);
        }

        private async Task PressAsync(string action, bool repeat = false, int waitMs = InterPressDelayMs)
        {
            if (_shell != null && !string.IsNullOrEmpty(action)) _shell.SimulateFocusInput(action, repeat);
            await Task.Delay(waitMs).ConfigureAwait(true);
        }

        /// <summary>
        /// Activates the currently focused element the way a real Gamepad A
        /// press would. The seam cannot do this: the router ignores
        /// "select" by design (XAML activates the focused control natively
        /// with a real controller), so a PressAsync("select") is a no-op and
        /// every activation below runs the production handler instead:
        /// - Focused Button (Details PlayButton, Settings ResetAddonsButton,
        ///   Streams filter chip): ButtonAutomationPeer.Invoke(), which runs
        ///   the button's real Click handler. IInvokeProvider is implemented
        ///   by the Button/RepeatButton/HyperlinkButton peers only (Microsoft
        ///   Learn: IInvokeProvider, UWP, winrt-22621) — the same peer the
        ///   harness already uses for dialog choices.
        /// - Catalog card (Home/Search/Catalog rows): the NavigationService
        ///   entry the page's own ItemClick handler calls with the same
        ///   arguments — OpenTitle(card.Meta), or NavigateCatalog with the
        ///   row's Addon/Catalog for a Home View All card (exactly
        ///   HomeViewModel.OpenCatalog). The arrival-focus key the click
        ///   records is already in the registry: the page's GotFocus hook
        ///   records it when the card takes focus, before this runs. That
        ///   covers the registry key only — restore must not depend on the
        ///   click path: the focus-recorded registry key is the single
        ///   source of truth for back restore.
        /// - Continue Watching card: NavigationService.ResumeEntry(entry) —
        ///   exactly HomeViewModel.ResumeEntry. Note it resumes playback
        ///   (Streams, or the Player directly when a stream is remembered),
        ///   never Details.
        /// Returns false when the focused element has no harness-reachable
        /// activation. Streams rows: the focused ListViewItem's ItemClick
        /// calls StreamsViewModel.PlayAsync(row.Entry) on the page's view
        /// model — a public method, and the page sets DataContext =
        /// _viewModel — which opens the player for a playable stream
        /// through NavigationService.OpenPlayer. The harness calls that
        /// same public method on the page's DataContext below, so the
        /// activation is the production handler minus its toast/debrid
        /// branches, which only run when PlayAsync returns a non-null
        /// PlayResult — never for a playable row, the only kind steering
        /// activates. ButtonAutomationPeer.Invoke cannot do this:
        /// ListViewItem peers expose IDragProvider and selection, not
        /// IInvokeProvider (Microsoft Learn: IInvokeProvider remarks;
        /// ListViewItemAutomationPeer GetPattern returns a pattern for
        /// IDragProvider only). Details Similar cards and episode cards
        /// are the same shape (private DetailsViewModel) and still
        /// return false.
        /// </summary>
        private async Task<bool> ActivateFocusedAsync()
        {
            var focused = FocusManager.GetFocusedElement();
            if (focused == null) return false;
            var button = focused as Button;
            if (button != null)
            {
                new ButtonAutomationPeer(button).Invoke();
                await Task.Delay(InterPressDelayMs).ConfigureAwait(true);
                return true;
            }
            var listItem = focused as ListViewItem;
            if (listItem != null)
            {
                // Streams rows only: run the production ItemClick's core —
                // StreamsViewModel.PlayAsync(row.Entry) on the page's
                // DataContext — which opens the player for a playable row.
                // A non-null PlayResult (toast/debrid) means the player did
                // not open; the caller judges that by waiting for the
                // Player page, so this still returns true: the activation
                // itself was performed. Throws (e.g. a failed resolve) are
                // the ItemClick handler's logged-and-swallowed path; here
                // they mean "not activated".
                if (!HasAncestor<StreamsPage>(listItem)) return false;
                var row = listItem.Content as StreamRowItem;
                if (row == null || row.Entry == null) return false;
                var streams = AncestorOfType<StreamsPage>(listItem);
                var viewModel = streams != null ? streams.DataContext as StreamsViewModel : null;
                if (viewModel == null) return false;
                try
                {
                    await viewModel.PlayAsync(row.Entry).ConfigureAwait(true);
                }
                catch (Exception)
                {
                    return false;
                }
                await Task.Delay(InterPressDelayMs).ConfigureAwait(true);
                return true;
            }
            var item = focused as GridViewItem;
            if (item == null) return false;
            // Cards only activate as their own page's ItemClick does. A
            // Details Similar card needs OpenSimilarAsync (private view
            // model) and an episode card needs PlayVideo with the episode's
            // Video (private view model) — both return false, as above.
            if (!HasAncestor<HomePage>(item) && !HasAncestor<SearchPage>(item) && !HasAncestor<CatalogPage>(item)) return false;
            var card = item.Content as CatalogCardItem;
            if (card != null)
            {
                if (_shell == null || _shell.Navigation == null) return false;
                if (card.IsViewAll)
                {
                    var grid = FocusedRowGridView();
                    var row = grid != null ? grid.DataContext as CatalogRowViewModel : null;
                    if (row == null) return false;
                    _shell.Navigation.NavigateCatalog(new CatalogNavigation
                    {
                        TransportUrl = row.Addon.TransportUrl,
                        Type = row.Catalog.Type,
                        Id = row.Catalog.Id,
                    });
                    await Task.Delay(InterPressDelayMs).ConfigureAwait(true);
                    return true;
                }
                if (card.Meta != null)
                {
                    _shell.Navigation.OpenTitle(card.Meta);
                    await Task.Delay(InterPressDelayMs).ConfigureAwait(true);
                    return true;
                }
                return false;
            }
            var entry = item.Content as ProgressEntry;
            if (entry != null)
            {
                if (_shell == null || _shell.Navigation == null) return false;
                _shell.Navigation.ResumeEntry(entry);
                await Task.Delay(InterPressDelayMs).ConfigureAwait(true);
                return true;
            }
            return false;
        }

        private FrameworkElement FocusedElement()
        {
            return FocusManager.GetFocusedElement() as FrameworkElement;
        }

        private string FocusedName()
        {
            var focused = FocusedElement();
            return focused != null && !string.IsNullOrEmpty(focused.Name) ? focused.Name : "(none)";
        }

        private string FocusedTag()
        {
            var button = FocusManager.GetFocusedElement() as Button;
            if (button == null) return "(none)";
            var tag = button.Tag as string;
            return string.IsNullOrEmpty(tag) ? "(none)" : tag;
        }

        private static T AncestorOfType<T>(DependencyObject element) where T : DependencyObject
        {
            for (var node = element != null ? VisualTreeHelper.GetParent(element) : null; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var found = node as T;
                if (found != null) return found;
            }
            return null;
        }

        private static T FindNamedDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            if (root == null) return null;
            var named = root as T;
            if (named != null && named.Name == name) return named;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindNamedDescendant<T>(VisualTreeHelper.GetChild(root, i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static List<T> FindDescendantsOfType<T>(DependencyObject root) where T : DependencyObject
        {
            var found = new List<T>();
            CollectDescendantsOfType(root, found);
            return found;
        }

        private static void CollectDescendantsOfType<T>(DependencyObject root, List<T> found) where T : DependencyObject
        {
            if (root == null) return;
            var match = root as T;
            if (match != null) found.Add(match);
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++) CollectDescendantsOfType(VisualTreeHelper.GetChild(root, i), found);
        }

        private static bool HasAncestor<T>(DependencyObject element) where T : DependencyObject
        {
            return AncestorOfType<T>(element) != null;
        }

        private static T FindPage<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            var found = root as T;
            if (found != null) return found;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = FindPage<T>(VisualTreeHelper.GetChild(root, i));
                if (child != null) return child;
            }
            return null;
        }

        private async Task<T> WaitForPageAsync<T>(int timeoutMs = PageReadyTimeoutMs) where T : FocusablePage
        {
            T page = null;
            var ready = await WaitForAsync(() =>
            {
                page = FindPage<T>(_shell);
                return page != null && page.IsContentReady;
            }, timeoutMs).ConfigureAwait(true);
            return ready ? page : null;
        }

        private async Task<bool> NavigateHomeAndSettleAsync()
        {
            if (_shell == null || _shell.Navigation == null) return false;
            _shell.Navigation.NavigateHome();
            var home = await WaitForPageAsync<HomePage>().ConfigureAwait(true);
            if (home == null) return false;
            await WaitForSettleAsync().ConfigureAwait(true);
            return IsHeroPlayButton(FocusManager.GetFocusedElement())
                || FocusedName() == ManageAddonsButtonName;
        }

        private GridView FocusedRowGridView()
        {
            return AncestorOfType<GridView>(FocusManager.GetFocusedElement() as DependencyObject);
        }

        private string FocusedCardMetaId()
        {
            var container = FocusManager.GetFocusedElement() as GridViewItem;
            if (container == null) return null;
            var card = container.Content as CatalogCardItem;
            return card != null && card.Meta != null ? card.Meta.Id : null;
        }

        /// <summary>Mirrors FocusedCardMetaId for the card's Meta.Type
        /// ('movie' | 'series' | … — Models/Meta.cs). Null for a Continue
        /// card (ProgressEntry, no Meta) and for non-card focus.</summary>
        private string FocusedCardMetaType()
        {
            var container = FocusManager.GetFocusedElement() as GridViewItem;
            if (container == null) return null;
            var card = container.Content as CatalogCardItem;
            return card != null && card.Meta != null ? card.Meta.Type : null;
        }

        private string FocusedContinueContentId()
        {
            var container = FocusManager.GetFocusedElement() as GridViewItem;
            if (container == null) return null;
            var entry = container.Content as ProgressEntry;
            return entry != null ? entry.ContentId : null;
        }

        /// <summary>
        /// Steps Down (bounded, 4 extra presses — the bound proven in
        /// TestHomeBackDetailsMetaKeyAsync) until a card with a Meta id is
        /// focused, and returns that id. The first Home row is Continue
        /// Watching, whose ProgressEntry cards carry no Meta and resume
        /// playback on activation instead of opening Details, so activating
        /// the first card after one Down is wrong. Returns null when no
        /// Meta card appears in the scanned rows — a missing fixture, not
        /// a product failure; callers Skip with that reason.
        /// </summary>
        private async Task<string> ScanForMetaCardAsync()
        {
            var metaId = FocusedCardMetaId();
            for (var i = 0; i < 4 && metaId == null; i++)
            {
                await PressAsync("down").ConfigureAwait(true);
                metaId = FocusedCardMetaId();
            }
            return metaId;
        }

        /// <summary>
        /// Type-filtered Home scan (episode-test Route 2): walks Down
        /// row-by-row (bounded, 12 presses) until a card whose Meta.Type
        /// is "series" is focused. Movie cards and Continue cards
        /// (ProgressEntry, no Meta) are skipped. A Down that leaves the
        /// focused card id unchanged counts as a stall (lazily-loading
        /// rows); two consecutive stalls end the scan. Returns false when
        /// no series card is reached — a missing fixture, not a product
        /// failure; the caller SKIPs.
        /// </summary>
        private async Task<bool> ScanForSeriesCardAsync()
        {
            var stalls = 0;
            for (var i = 0; i < 12; i++)
            {
                if (string.Equals(FocusedCardMetaType(), "series", StringComparison.OrdinalIgnoreCase)) return true;
                var before = FocusedCardMetaId() ?? FocusedContinueContentId();
                await PressAsync("down").ConfigureAwait(true);
                await WaitForSettleAsync().ConfigureAwait(true);
                var after = FocusedCardMetaId() ?? FocusedContinueContentId();
                if (after == null || string.Equals(after, before, StringComparison.Ordinal))
                {
                    stalls++;
                    if (stalls >= 2) return false;
                }
                else stalls = 0;
            }
            return string.Equals(FocusedCardMetaType(), "series", StringComparison.OrdinalIgnoreCase);
        }

        private bool SameCard(object first, object second)
        {
            if (ReferenceEquals(first, second)) return true;
            var firstItem = first as GridViewItem;
            var secondItem = second as GridViewItem;
            if (firstItem == null || secondItem == null) return false;
            return ReferenceEquals(firstItem.Content, secondItem.Content);
        }

        private string LogFilePath()
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                if (folder == null || string.IsNullOrEmpty(folder.Path)) return null;
                return Path.Combine(folder.Path, LogFileName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private long LogOffset()
        {
            try
            {
                var path = LogFilePath();
                if (path == null || !File.Exists(path)) return 0;
                return new FileInfo(path).Length;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private bool LogContainsSince(string text, long offset)
        {
            try
            {
                var path = LogFilePath();
                if (path == null || !File.Exists(path)) return false;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (offset < 0 || offset >= stream.Length) return false;
                    stream.Seek(offset, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream))
                    {
                        return reader.ReadToEnd().IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool IsDialogOpen()
        {
            var coordinator = _shell != null ? _shell.Coordinator : null;
            if (coordinator != null && FocusCoordinator.UseFocusCoordinator) return coordinator.IsDialogOpen;
            return FindOpenDialog() != null;
        }

        private async Task<bool> WaitForDialogOpenAsync(int timeoutMs = 3000)
        {
            return await WaitForAsync(() => IsDialogOpen(), timeoutMs).ConfigureAwait(true);
        }

        private async Task<bool> WaitForDialogClosedAsync(int timeoutMs = 3000)
        {
            return await WaitForAsync(() => !IsDialogOpen(), timeoutMs).ConfigureAwait(true);
        }

        /// <summary>
        /// Finds the open ContentDialog through the popup tree (the dialog
        /// lives under PopupRoot, a separate tree from the page). Returns null
        /// when no dialog is open. Test-only discovery: production code never
        /// walks the popup tree.
        /// </summary>
        private ContentDialog FindOpenDialog()
        {
            try
            {
                var popups = VisualTreeHelper.GetOpenPopups(Window.Current);
                foreach (var popup in popups)
                {
                    var dialog = FindDescendantOfType<ContentDialog>(popup.Child);
                    if (dialog == null && popup.Child is ContentDialog direct) dialog = direct;
                    if (dialog != null) return dialog;
                }
            }
            catch (Exception)
            {
                return null;
            }
            return null;
        }

        private static T FindDescendantOfType<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            var match = root as T;
            if (match != null) return match;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindDescendantOfType<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        private static string ButtonLabel(Button button)
        {
            if (button == null) return string.Empty;
            var text = button.Content as string;
            if (!string.IsNullOrEmpty(text)) return text;
            var labels = FindDescendantsOfType<TextBlock>(button);
            var joined = string.Empty;
            foreach (var label in labels) joined += label.Text;
            return joined;
        }

        /// <summary>
        /// Closes the open dialog with its own Hide(). The awaited ShowAsync
        /// then completes and the production finally block runs (DialogOpen =
        /// false → coordinator restore tick), so the restore under test is the
        /// real path — only the close gesture itself is test-only, because
        /// the synthetic seam cannot raise the native system-back the dialog
        /// listens for. Returns false when no dialog was open to close.
        /// </summary>
        private async Task<bool> CloseOpenDialogAsync()
        {
            var dialog = FindOpenDialog();
            if (dialog == null) return false;
            dialog.Hide();
            return await WaitForDialogClosedAsync().ConfigureAwait(true);
        }

        /// <summary>
        /// Chooses a dialog option by its label, through
        /// ButtonAutomationPeer.Invoke() — the documented UWP way to click a
        /// button programmatically. Used only where the seam cannot drive the
        /// choice (input is gated while a dialog is open). The Click handler
        /// and everything after it (navigation, restore) is production code.
        /// Label matching is by substring, so it works in both UI languages.
        /// </summary>
        private async Task<bool> InvokeDialogButtonAsync(params string[] labelParts)
        {
            var dialog = FindOpenDialog();
            if (dialog == null) return false;
            var buttons = FindDescendantsOfType<Button>(dialog);
            foreach (var button in buttons)
            {
                var label = ButtonLabel(button);
                if (string.IsNullOrEmpty(label)) continue;
                foreach (var part in labelParts)
                {
                    if (!string.IsNullOrEmpty(part)
                        && label.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        new ButtonAutomationPeer(button).Invoke();
                        await Task.Delay(InterPressDelayMs).ConfigureAwait(true);
                        return true;
                    }
                }
            }
            return false;
        }

        private async Task<bool> ReachContinueCardAsync()
        {
            if (!await NavigateHomeAndSettleAsync().ConfigureAwait(true)) return false;
            var home = FindPage<HomePage>(_shell);
            if (home == null) return false;
            var grid = FindNamedDescendant<GridView>(home, "ContinueGridView");
            if (grid == null || grid.Items.Count == 0) return false;
            for (var i = 0; i < 12; i++)
            {
                var row = FocusedRowGridView();
                if (row != null && ReferenceEquals(row, grid)) return true;
                await PressAsync("down").ConfigureAwait(true);
            }
            var finalRow = FocusedRowGridView();
            return finalRow != null && ReferenceEquals(finalRow, grid);
        }

        private async Task<bool> ReachCatalogAsync(string type = "movie", string id = "top")
        {
            if (_shell == null || _shell.Navigation == null) return false;
            _shell.Navigation.NavigateCatalog(new CatalogNavigation
            {
                TransportUrl = CinemetaTransportUrl,
                Type = type,
                Id = id,
            });
            var catalog = await WaitForPageAsync<CatalogPage>().ConfigureAwait(true);
            if (catalog == null) return false;
            await WaitForSettleAsync().ConfigureAwait(true);
            return FocusManager.GetFocusedElement() as GridViewItem != null
                && HasAncestor<CatalogPage>(FocusManager.GetFocusedElement() as DependencyObject);
        }

        /// <summary>True when the catalog page loaded (content ready) but
        /// its grid holds zero cards — the series/top catalog is not
        /// advertised by the addon, a runtime fact, not a product failure.
        /// Only meaningful after a failed ReachCatalogAsync.</summary>
        private bool IsSeriesCatalogEmpty()
        {
            var catalog = FindPage<CatalogPage>(_shell);
            if (catalog == null || !catalog.IsContentReady) return false;
            var grid = FindNamedDescendant<GridView>(catalog, "CatalogGridView");
            return grid != null && grid.Items.Count == 0;
        }

        /// <summary>Meta id of the catalog grid's first card (Items[0]).
        /// Arrival focuses index 0 (CatalogPage.TryFocusArrival calls
        /// FocusFirstCard), so the arrival test compares the focused card
        /// against this instead of accepting any focused card in the page.</summary>
        private string FirstCatalogCardMetaId()
        {
            var grid = FocusedRowGridView();
            if (grid == null || grid.Items.Count == 0) return null;
            var first = grid.Items[0] as CatalogCardItem;
            return first != null && first.Meta != null ? first.Meta.Id : null;
        }

        /// <summary>True when focus is on a ListViewItem whose Content is
        /// a StreamRowItem with IsPlayable true. VisibleRows are normally
        /// playable-only (ShowUnsupported defaults false), so the
        /// IsPlayable check is defence, not the filter.</summary>
        private bool IsFocusedPlayableStreamRow()
        {
            var item = FocusManager.GetFocusedElement() as ListViewItem;
            if (item == null) return false;
            var row = item.Content as StreamRowItem;
            return row != null && row.IsPlayable;
        }

        /// <summary>
        /// Steers focus from the Streams arrival point down to the first
        /// playable stream row. Arrival parks on the "all" filter chip —
        /// StreamsPage.TryFocusArrival targets the chip first, and the chip
        /// exists before any stream loads (ComputeFilterChips always seeds
        /// "all") — while "content ready" fires on the loading-state
        /// render, before any row exists (the ListView is Collapsed while
        /// empty), so a single focus check at settle time can only ever
        /// see the chip. Waits up to PageReadyTimeoutMs for the first row
        /// to appear, then presses Down (bounded, 3) until a playable row
        /// is focused. Returns false, with _lastReachFailure carrying the
        /// exact reason, when the list never fills ("no playable stream")
        /// or focus never lands on a row ("no focused stream row") — the
        /// caller SKIPs with that reason; a timeout never PASSes.
        /// </summary>
        private async Task<bool> SteerStreamsToFirstPlayableRowAsync(StreamsPage streams)
        {
            var list = FindNamedDescendant<ListView>(streams, "StreamsListView");
            if (list == null) { _lastReachFailure = "no playable stream"; return false; }
            var loaded = await WaitForAsync(() => list.Items.Count > 0, PageReadyTimeoutMs).ConfigureAwait(true);
            if (!loaded) { _lastReachFailure = "no playable stream"; return false; }
            for (var i = 0; i < 3; i++)
            {
                if (IsFocusedPlayableStreamRow()) return true;
                await PressAsync("down").ConfigureAwait(true);
                await WaitForSettleAsync().ConfigureAwait(true);
                if (IsFocusedPlayableStreamRow()) return true;
            }
            _lastReachFailure = "no focused stream row";
            return false;
        }

        /// <summary>
        /// Shared Home → Details → Streams preamble (the first half of
        /// TryReachPlayerAsync, without its per-step failure recording
        /// there — this copy records the same _lastReachFailure strings so
        /// its callers SKIP with the real reason). Returns the Streams
        /// page, or null when any step fails. TryReachPlayerAsync keeps
        /// its own inline preamble unchanged; this helper serves the
        /// tests that stop at Streams instead of continuing to the Player.
        /// </summary>
        private async Task<StreamsPage> ReachStreamsAsync()
        {
            if (!await NavigateHomeAndSettleAsync().ConfigureAwait(true)) { _lastReachFailure = "home did not settle"; return null; }
            await PressAsync("down").ConfigureAwait(true);
            if (FocusManager.GetFocusedElement() as GridViewItem == null) { _lastReachFailure = "no card on first Down"; return null; }
            if (await ScanForMetaCardAsync().ConfigureAwait(true) == null) { _lastReachFailure = "no Meta card found in rows"; return null; }
            if (!await ActivateFocusedAsync().ConfigureAwait(true)) { _lastReachFailure = "card activation unavailable in harness"; return null; }
            var details = await WaitForPageAsync<DetailsPage>().ConfigureAwait(true);
            if (details == null) { _lastReachFailure = "Details did not open"; return null; }
            await WaitForSettleAsync().ConfigureAwait(true);
            if (FocusedName() != "PlayButton") { _lastReachFailure = "no Play button on Details"; return null; }
            if (!await ActivateFocusedAsync().ConfigureAwait(true)) { _lastReachFailure = "Play button activation unavailable in harness"; return null; }
            var streams = await WaitForPageAsync<StreamsPage>().ConfigureAwait(true);
            if (streams == null) { _lastReachFailure = "Streams did not open"; return null; }
            return streams;
        }

        /// <summary>
        /// Drives the real UI chain Home → Details → Streams → Player. Each
        /// step is bounded; any step timing out means the fixture (catalog
        /// data, a stream, playback) was absent and the caller SKIPs.
        /// Returns true only when the player is open and focus is inside it.
        /// Every false return records the failing step in
        /// _lastReachFailure (see ReachFailureDetail) so callers skip with
        /// the real reason instead of the old blanket "no playable stream"
        /// — that reason now survives only when the Streams list itself is
        /// empty after settling (no sources / no results for the title).
        /// </summary>
        private async Task<bool> TryReachPlayerAsync()
        {
            _lastReachFailure = null;
            if (FindPage<PlayerPage>(_shell) != null) return true;
            if (!await NavigateHomeAndSettleAsync().ConfigureAwait(true)) { _lastReachFailure = "home did not settle"; return false; }
            await PressAsync("down").ConfigureAwait(true);
            if (FocusManager.GetFocusedElement() as GridViewItem == null) { _lastReachFailure = "no card on first Down"; return false; }
            if (await ScanForMetaCardAsync().ConfigureAwait(true) == null) { _lastReachFailure = "no Meta card found in rows"; return false; }
            if (!await ActivateFocusedAsync().ConfigureAwait(true)) { _lastReachFailure = "card activation unavailable in harness"; return false; }
            var details = await WaitForPageAsync<DetailsPage>().ConfigureAwait(true);
            if (details == null) { _lastReachFailure = "Details did not open"; return false; }
            await WaitForSettleAsync().ConfigureAwait(true);
            if (FocusedName() != "PlayButton") { _lastReachFailure = "no Play button on Details"; return false; }
            if (!await ActivateFocusedAsync().ConfigureAwait(true)) { _lastReachFailure = "Play button activation unavailable in harness"; return false; }
            var streams = await WaitForPageAsync<StreamsPage>().ConfigureAwait(true);
            if (streams == null) { _lastReachFailure = "Streams did not open"; return false; }
            await WaitForSettleAsync().ConfigureAwait(true);
            // Arrival parks on the "all" filter chip, not a row: steer
            // Down to the first playable row (waits for the list to fill
            // first). The helper records "no playable stream" /
            // "no focused stream row" in _lastReachFailure itself.
            if (!await SteerStreamsToFirstPlayableRowAsync(streams).ConfigureAwait(true)) return false;
            if (!await ActivateFocusedAsync().ConfigureAwait(true)) { _lastReachFailure = "stream row activation unavailable in harness"; return false; }
            var player = await WaitForPageAsync<PlayerPage>(PlayerOpenTimeoutMs).ConfigureAwait(true);
            if (player == null) { _lastReachFailure = "Player did not open"; return false; }
            await WaitForSettleAsync().ConfigureAwait(true);
            if (!HasAncestor<PlayerPage>(FocusManager.GetFocusedElement() as DependencyObject)) { _lastReachFailure = "focus not inside Player"; return false; }
            return true;
        }

        /// <summary>Skip detail for a failed TryReachPlayerAsync attempt,
        /// carrying the step it actually failed on.</summary>
        private string ReachFailureDetail()
        {
            return "reach failed: " + (_lastReachFailure ?? "unknown");
        }

        /// <summary>
        /// Steers focus to a named OSD button with D-pad presses only
        /// (Down into the transport row, then a Right sweep and a Left sweep).
        /// Returns false when the button is not reached in the bounded sweep
        /// — the caller SKIPs rather than failing the product for a steering
        /// miss.
        /// </summary>
        private async Task<bool> SteerToOsdButtonAsync(string buttonName)
        {
            if (FocusedName() == buttonName) return true;
            await PressAsync("down").ConfigureAwait(true);
            if (FocusedName() == buttonName) return true;
            for (var i = 0; i < 10; i++)
            {
                await PressAsync("right").ConfigureAwait(true);
                if (FocusedName() == buttonName) return true;
            }
            for (var i = 0; i < 10; i++)
            {
                await PressAsync("left").ConfigureAwait(true);
                if (FocusedName() == buttonName) return true;
            }
            return FocusedName() == buttonName;
        }

        private async Task<bool> WaitForOsdHiddenAsync(int timeoutMs = 8000)
        {
            var offset = LogOffset();
            var hidden = await WaitForAsync(() =>
                LogContainsSince("OSD hidden; parked focus on Back", offset)
                || OsdOpacity() == 0, timeoutMs).ConfigureAwait(true);
            return hidden;
        }

        private double? OsdOpacity()
        {
            var player = FindPage<PlayerPage>(_shell);
            if (player == null) return null;
            var osd = FindNamedDescendant<FrameworkElement>(player, "OsdRoot");
            if (osd == null) return null;
            return osd.Opacity;
        }

        // ---------- Gate tests ----------

        private Task<FocusTestResult> TestGateLaunchTokenAsync()
        {
            if (_armedVia == "launch-token") return Task.FromResult(FocusTestResult.Pass("gate-launch-token", "suite armed via launch token"));
            return Task.FromResult(FocusTestResult.Skip("gate-launch-token", "armed via " + _armedVia));
        }

        private Task<FocusTestResult> TestGateFlagFileAsync()
        {
            if (_armedVia == "flag-file") return Task.FromResult(FocusTestResult.Pass("gate-flag-file", "suite armed via flag file"));
            return Task.FromResult(FocusTestResult.Skip("gate-flag-file", "armed via " + _armedVia));
        }

        // ---------- Phase 1: Settings ----------

        /// <summary>
        /// Phase 0 script, launch → hero Play. Navigates home, waits for the
        /// page's ContentReady plus the arrival settle to finish (bounded
        /// poll, never spins forever), then asserts focus is the hero Play
        /// button, inside the page, with the sidebar guard clear (observed
        /// via the shell proxy: focus is not on a sidebar button and the
        /// strip is not expanded — the guard field itself is private to
        /// MainPage). Returns false instead of logging: the suite writes the
        /// TEST line.
        /// </summary>
        public async Task<bool> RunLaunchHeroPlayAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            var passed = false;
            var settleAttempts = 0;
            object focused = null;
            if (_shell != null)
            {
                var shellReady = await WaitForShellReadyAsync().ConfigureAwait(true);
                if (shellReady)
                {
                    if (FindPage<HomePage>(_shell) == null)
                    {
                        _shell.Navigation.NavigateHome();
                    }
                    var elapsedMs = 0;
                    while (elapsedMs < SettleTimeoutMs)
                    {
                        settleAttempts++;
                        var page = FindPage<HomePage>(_shell);
                        if (page != null && page.IsContentReady)
                        {
                            focused = FocusManager.GetFocusedElement();
                            if (IsHeroPlayButton(focused))
                            {
                                passed = true;
                                break;
                            }
                        }
                        await Task.Delay(SettlePollIntervalMs).ConfigureAwait(true);
                        elapsedMs += SettlePollIntervalMs;
                    }
                    if (passed)
                    {
                        var focusedElement = focused as DependencyObject;
                        bool sidebarFirst;
                        bool sidebarLast;
                        var inSidebar = focusedElement != null && _shell.IsSidebarButton(focusedElement, out sidebarFirst, out sidebarLast);
                        passed = !inSidebar && !_shell.IsSidebarExpanded;
                    }
                }
            }
            return passed;
        }

        private async Task<FocusTestResult> TestLaunchHeroPlayAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            var passed = await RunLaunchHeroPlayAsync().ConfigureAwait(true);
            var detail = "expected=" + ExpectedHeroTagAndType + " actual=" + DescribeTagAndType(FocusManager.GetFocusedElement())
                + " ms=" + stopwatch.ElapsedMilliseconds;
            if (passed) return FocusTestResult.Pass("launch-hero-play", detail);
            return FocusTestResult.Fail("launch-hero-play", detail);
        }

        private async Task<FocusTestResult> TestSettingsArrivalFirstRowAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("settings-arrival-first-row", "no shell");
            _shell.Navigation.NavigateSettingsRoot();
            var page = await WaitForPageAsync<SettingsPage>().ConfigureAwait(true);
            if (page == null) return FocusTestResult.Fail("settings-arrival-first-row", "settings did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            var tag = FocusedTag();
            if (tag == "root|account") return FocusTestResult.Pass("settings-arrival-first-row", "focused=" + tag);
            return FocusTestResult.Fail("settings-arrival-first-row", "focused tag=" + tag + " expected root|account");
        }

        private async Task<FocusTestResult> TestSettingsArrivalStripCollapsedAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("settings-arrival-strip-collapsed", "no shell");
            _shell.Navigation.NavigateSettingsRoot();
            var page = await WaitForPageAsync<SettingsPage>().ConfigureAwait(true);
            if (page == null) return FocusTestResult.Fail("settings-arrival-strip-collapsed", "settings did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (!_shell.IsSidebarExpanded) return FocusTestResult.Pass("settings-arrival-strip-collapsed", "strip collapsed");
            return FocusTestResult.Fail("settings-arrival-strip-collapsed", "strip expanded on arrival");
        }

        private async Task<FocusTestResult> TestSettingsDownOneRowAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("settings-down-one-row-per-press", "no shell");
            _shell.Navigation.NavigateSettingsRoot();
            var page = await WaitForPageAsync<SettingsPage>().ConfigureAwait(true);
            if (page == null) return FocusTestResult.Fail("settings-down-one-row-per-press", "settings did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (FocusedTag() != "root|account") return FocusTestResult.Fail("settings-down-one-row-per-press", "arrival tag=" + FocusedTag());
            await PressAsync("down").ConfigureAwait(true);
            var tag = FocusedTag();
            if (tag == "root|addons") return FocusTestResult.Pass("settings-down-one-row-per-press", "one press account->addons");
            return FocusTestResult.Fail("settings-down-one-row-per-press", "after one Down tag=" + tag + " expected root|addons");
        }

        private async Task<FocusTestResult> TestSettingsDownNeverSidebarAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("settings-down-never-sidebar", "no shell");
            _shell.Navigation.NavigateSettingsRoot();
            var page = await WaitForPageAsync<SettingsPage>().ConfigureAwait(true);
            if (page == null) return FocusTestResult.Fail("settings-down-never-sidebar", "settings did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            for (var i = 0; i < 9; i++)
            {
                var focused = FocusManager.GetFocusedElement() as DependencyObject;
                if (focused == null) return FocusTestResult.Fail("settings-down-never-sidebar", "focus lost at press " + i);
                bool first;
                bool last;
                if (_shell.IsSidebarButton(focused, out first, out last)) return FocusTestResult.Fail("settings-down-never-sidebar", "landed on sidebar at press " + i);
                await PressAsync("down").ConfigureAwait(true);
            }
            var end = FocusManager.GetFocusedElement() as DependencyObject;
            if (end == null) return FocusTestResult.Fail("settings-down-never-sidebar", "focus lost at end");
            bool endFirst;
            bool endLast;
            if (_shell.IsSidebarButton(end, out endFirst, out endLast)) return FocusTestResult.Fail("settings-down-never-sidebar", "landed on sidebar at end");
            return FocusTestResult.Pass("settings-down-never-sidebar", "9 Down presses, never sidebar, end tag=" + FocusedTag());
        }

        /// <summary>
        /// Steers to one root-list row: navigates to the Settings root
        /// (arrival = root|account) and walks Down, then Up, until the row's
        /// tag is focused. The coordinator records the row focus is on when
        /// a section navigation leaves the root page, so a section test must
        /// steer here FIRST — navigating straight to a section would record
        /// whatever row the previous test left focus on, not the row under
        /// test. Returns false when the row is not reached in the bounded
        /// walk.
        /// </summary>
        private async Task<bool> SteerToSettingsRootRowAsync(string tag)
        {
            if (_shell == null || _shell.Navigation == null) return false;
            _shell.Navigation.NavigateSettingsRoot();
            if (await WaitForPageAsync<SettingsPage>().ConfigureAwait(true) == null) return false;
            await WaitForSettleAsync().ConfigureAwait(true);
            for (var i = 0; i < 12; i++)
            {
                if (FocusedTag() == tag) return true;
                await PressAsync("down").ConfigureAwait(true);
            }
            if (FocusedTag() == tag) return true;
            for (var i = 0; i < 12; i++)
            {
                if (FocusedTag() == tag) return true;
                await PressAsync("up").ConfigureAwait(true);
            }
            return FocusedTag() == tag;
        }

        private async Task<FocusTestResult> TestSettingsBackAddonsSameRowAsync()
        {
            if (!await SteerToSettingsRootRowAsync("root|addons").ConfigureAwait(true)) return FocusTestResult.Fail("settings-back-addons-same-row", "could not steer to root|addons");
            _shell.Navigation.NavigateSettings("addons");
            var page = await WaitForPageAsync<SettingsPage>().ConfigureAwait(true);
            if (page == null) return FocusTestResult.Fail("settings-back-addons-same-row", "addons did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            await PressAsync("down").ConfigureAwait(true);
            await PressAsync("back").ConfigureAwait(true);
            var settled = await WaitForAsync(() => FocusedTag() == "root|addons", 5000).ConfigureAwait(true);
            if (settled) return FocusTestResult.Pass("settings-back-addons-same-row", "B returned to root|addons");
            return FocusTestResult.Fail("settings-back-addons-same-row", "after B tag=" + FocusedTag() + " expected root|addons");
        }

        /// <summary>
        /// One registry-isolation leg: steer to the root row, open its
        /// section, move inside it, press B, expect the same root row back.
        /// Returns null on pass, or the failure detail.
        /// </summary>
        private async Task<string> SettingsBackLegAsync(string rootTag, string section)
        {
            if (!await SteerToSettingsRootRowAsync(rootTag).ConfigureAwait(true)) return "could not steer to " + rootTag;
            _shell.Navigation.NavigateSettings(section);
            if (await WaitForPageAsync<SettingsPage>().ConfigureAwait(true) == null) return section + " did not load";
            await WaitForSettleAsync().ConfigureAwait(true);
            await PressAsync("down").ConfigureAwait(true);
            await PressAsync("back").ConfigureAwait(true);
            if (!await WaitForAsync(() => FocusedTag() == rootTag, 5000).ConfigureAwait(true)) return "after B from " + section + " tag=" + FocusedTag() + " expected " + rootTag;
            return null;
        }

        private async Task<FocusTestResult> TestSettingsRegistryIsolationAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("settings-registry-entry-isolation", "no shell");
            var failure = await SettingsBackLegAsync("root|addons", "addons").ConfigureAwait(true);
            if (failure != null) return FocusTestResult.Fail("settings-registry-entry-isolation", failure);
            failure = await SettingsBackLegAsync("root|debrid", "debrid").ConfigureAwait(true);
            if (failure != null) return FocusTestResult.Fail("settings-registry-entry-isolation", failure);
            failure = await SettingsBackLegAsync("root|addons", "addons").ConfigureAwait(true);
            if (failure != null) return FocusTestResult.Fail("settings-registry-entry-isolation", failure);
            return FocusTestResult.Pass("settings-registry-entry-isolation", "addons/debrid/addons entries never overwrote each other");
        }

        private async Task<FocusTestResult> TestSettingsBackEmptyStackGoesHomeAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("settings-back-empty-stack-goes-home", "no shell");
            _shell.Navigation.NavigateSettingsRoot();
            if (await WaitForPageAsync<SettingsPage>().ConfigureAwait(true) == null) return FocusTestResult.Fail("settings-back-empty-stack-goes-home", "settings did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            await PressAsync("back").ConfigureAwait(true);
            var home = await WaitForPageAsync<HomePage>().ConfigureAwait(true);
            if (home == null) return FocusTestResult.Fail("settings-back-empty-stack-goes-home", "B did not reach Home");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (IsHeroPlayButton(FocusManager.GetFocusedElement())) return FocusTestResult.Pass("settings-back-empty-stack-goes-home", "B on root went Home, hero Play focused");
            return FocusTestResult.Fail("settings-back-empty-stack-goes-home", "Home focused=" + FocusedName());
        }

        private async Task<FocusTestResult> TestSettingsMenuBackCollapsesAsync()
        {
            // B closes the strip only by navigating: with back history,
            // B goes back to the root page, the new settle focuses content
            // and ContentFrame.GotFocus collapses the strip. (B on Home
            // with an empty stack shows the exit toast instead and leaves
            // the strip alone — that is the documented rule, not a collapse.)
            if (!await SteerToSettingsRootRowAsync("root|addons").ConfigureAwait(true)) return FocusTestResult.Fail("settings-menu-back-collapses-returns-to-page", "could not steer to root|addons");
            _shell.Navigation.NavigateSettings("addons");
            if (await WaitForPageAsync<SettingsPage>().ConfigureAwait(true) == null) return FocusTestResult.Fail("settings-menu-back-collapses-returns-to-page", "addons did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            var expanded = false;
            // Toward the sidebar = toward the flow-start edge: right in RTL,
            // left in LTR (the strip sits at the start edge).
            var towardSidebar = _shell.FlowDirection == FlowDirection.RightToLeft ? "right" : "left";
            for (var i = 0; i < 3 && !expanded; i++)
            {
                await PressAsync(towardSidebar).ConfigureAwait(true);
                expanded = _shell.IsSidebarExpanded;
            }
            if (!expanded) return FocusTestResult.Fail("settings-menu-back-collapses-returns-to-page", "Press toward the sidebar from the section did not expand the strip");
            await PressAsync("back").ConfigureAwait(true);
            var collapsed = await WaitForAsync(() => !_shell.IsSidebarExpanded && FocusedTag() == "root|addons", 5000).ConfigureAwait(true);
            if (!collapsed) return FocusTestResult.Fail("settings-menu-back-collapses-returns-to-page", "after B expanded=" + _shell.IsSidebarExpanded + " tag=" + FocusedTag());
            if (HasAncestor<SettingsPage>(FocusManager.GetFocusedElement() as DependencyObject)) return FocusTestResult.Pass("settings-menu-back-collapses-returns-to-page", "B went back, strip collapsed, focus back on root|addons");
            return FocusTestResult.Fail("settings-menu-back-collapses-returns-to-page", "focus after B=" + FocusedName());
        }

        private async Task<FocusTestResult> TestSettingsGuardClearedBySettleAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("settings-guard-cleared-by-settle", "no shell");
            var coordinator = _shell.Coordinator;
            if (coordinator == null) return FocusTestResult.Skip("settings-guard-cleared-by-settle", "no coordinator (flag off)");
            _shell.Navigation.NavigateSettingsRoot();
            if (await WaitForPageAsync<SettingsPage>().ConfigureAwait(true) == null) return FocusTestResult.Fail("settings-guard-cleared-by-settle", "settings did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (!coordinator.IsNavigationGuardArmed) return FocusTestResult.Pass("settings-guard-cleared-by-settle", "guard cleared by settle");
            return FocusTestResult.Fail("settings-guard-cleared-by-settle", "guard still armed after settle");
        }

        // ---------- Phase 2: Home / Search / Catalog ----------

        private Task<FocusTestResult> TestHomeLaunchCoordinatorSettleAsync()
        {
            if (LogContainsSince("settled=true", _suiteLogOffset)
                && LogContainsSince("focusName=HeroPlayButton", _suiteLogOffset))
            {
                return Task.FromResult(FocusTestResult.Pass("home-launch-coordinator-settle", "coordinator settle line present"));
            }
            return Task.FromResult(FocusTestResult.Fail("home-launch-coordinator-settle", "no coordinator settle line since suite start"));
        }

        private async Task<FocusTestResult> TestHomeDownHeroToFirstCardAsync()
        {
            if (!await NavigateHomeAndSettleAsync().ConfigureAwait(true)) return FocusTestResult.Fail("home-down-hero-to-first-card", "home did not settle");
            await PressAsync("down").ConfigureAwait(true);
            var focused = FocusManager.GetFocusedElement();
            if (focused is GridViewItem && HasAncestor<HomePage>(focused as DependencyObject)) return FocusTestResult.Pass("home-down-hero-to-first-card", "Down from hero landed on a card");
            return FocusTestResult.Fail("home-down-hero-to-first-card", "after Down focused=" + FocusedName());
        }

        private async Task<FocusTestResult> TestHomeRowMemoryAsync()
        {
            if (!await NavigateHomeAndSettleAsync().ConfigureAwait(true)) return FocusTestResult.Fail("home-row-to-row-per-row-memory", "home did not settle");
            await PressAsync("down").ConfigureAwait(true);
            var first = FocusManager.GetFocusedElement();
            var firstRow = FocusedRowGridView();
            if (first == null || firstRow == null) return FocusTestResult.Fail("home-row-to-row-per-row-memory", "no card on first Down");
            await PressAsync("down").ConfigureAwait(true);
            var secondRow = FocusedRowGridView();
            if (secondRow == null) return FocusTestResult.Fail("home-row-to-row-per-row-memory", "no card on second Down");
            if (ReferenceEquals(firstRow, secondRow)) return FocusTestResult.Fail("home-row-to-row-per-row-memory", "second Down stayed in the same row");
            await PressAsync("up").ConfigureAwait(true);
            if (ReferenceEquals(FocusManager.GetFocusedElement(), first)) return FocusTestResult.Pass("home-row-to-row-per-row-memory", "Up returned to the remembered poster of the row above");
            return FocusTestResult.Fail("home-row-to-row-per-row-memory", "Up did not restore the remembered poster, focused=" + FocusedName());
        }

        private async Task<FocusTestResult> TestHomeDownNeverSidebarAsync()
        {
            if (!await NavigateHomeAndSettleAsync().ConfigureAwait(true)) return FocusTestResult.Fail("home-down-never-sidebar-or-skip", "home did not settle");
            var rowsSeen = 0;
            GridView lastRow = null;
            for (var i = 0; i < 6; i++)
            {
                var focused = FocusManager.GetFocusedElement() as DependencyObject;
                if (focused == null) return FocusTestResult.Fail("home-down-never-sidebar-or-skip", "focus lost at press " + i);
                bool first;
                bool last;
                if (_shell.IsSidebarButton(focused, out first, out last)) return FocusTestResult.Fail("home-down-never-sidebar-or-skip", "landed on sidebar at press " + i);
                var row = FocusedRowGridView();
                if (row != null && !ReferenceEquals(row, lastRow)) { rowsSeen++; lastRow = row; }
                await PressAsync("down").ConfigureAwait(true);
            }
            if (rowsSeen < 2) return FocusTestResult.Fail("home-down-never-sidebar-or-skip", "only " + rowsSeen + " row(s) visited in 6 presses");
            return FocusTestResult.Pass("home-down-never-sidebar-or-skip", "6 Down presses, never sidebar, " + rowsSeen + " rows visited");
        }

        /// <summary>
        /// The pinned focused card (D1): the focused poster itself sits
        /// on the hero edge — the first slot of the row's viewport.
        /// LTR 26..114 (26 epx inset, 88 card);
        /// RTL 716..804 (912 viewport - 108 inset - 88 card).
        /// The k-1 second-slot rule is superseded: the pinning is on the
        /// focused card itself, and the strip moves one pitch (97 epx)
        /// per step to keep it there. Walks a long poster row and
        /// asserts the ring holds the first slot while the strip glides
        /// underneath it — the rect is read twice, once pinned after the
        /// first step in and once after three more steps, and both must sit
        /// on the slot while HorizontalOffset grows between them.
        /// </summary>
        private async Task<FocusTestResult> TestHomeAnchorPinsFocusedCardAsync()
        {
            const double slotLeftRtl = 716;
            const double slotRightRtl = 804;
            const double slotLeftLtr = 26;
            const double slotRightLtr = 114;
            // The anchor deadband is 1 epx (TryComputeRowAnchorTarget
            // returns false at |delta| <= 1), plus sub-pixel rounding on
            // the glide: ±2 epx admits a settled strip and nothing more.
            const double toleranceEpx = 2;
            if (!await NavigateHomeAndSettleAsync().ConfigureAwait(true)) return FocusTestResult.Fail("home-anchor-pins-focused-card", "home did not settle");
            await PressAsync("down").ConfigureAwait(true);
            // Walk down to the first poster row: the Continue row's 176 epx
            // cards have a different pitch, so the slot above only holds
            // for 88 epx posters. Entry is always the row's first card —
            // the walk starts there and never steps sideways first.
            GridView row = null;
            for (var i = 0; i < 6; i++)
            {
                row = FocusedRowGridView();
                var card = FocusManager.GetFocusedElement() as GridViewItem;
                if (row != null && card != null && Math.Abs(card.ActualWidth - 88) <= 2) break;
                row = null;
                await PressAsync("down").ConfigureAwait(true);
            }
            if (row == null) return FocusTestResult.Skip("home-anchor-pins-focused-card", "no poster row reached");
            var viewer = FindDescendantOfType<ScrollViewer>(row);
            if (viewer == null) return FocusTestResult.Skip("home-anchor-pins-focused-card", "no ScrollViewer in the poster row");
            // The walk below moves the strip four pitches (388 epx); a row
            // whose ScrollableWidth cannot cover that cannot prove the pin.
            if (viewer.ScrollableWidth < 400) return FocusTestResult.Skip("home-anchor-pins-focused-card", "poster row too short to move the strip (scrollable=" + viewer.ScrollableWidth + ")");
            var rtl = row.FlowDirection == FlowDirection.RightToLeft;
            var next = rtl ? "left" : "right";
            var expectedLeft = rtl ? slotLeftRtl : slotLeftLtr;
            var expectedRight = rtl ? slotRightRtl : slotRightLtr;
            // One step in: the strip moves one pitch (97 epx) to bring
            // the focused card onto the hero edge, so the ring should
            // already sit on the first slot.
            await PressAsync(next).ConfigureAwait(true);
            if (!await WaitForRowScrollStableAsync().ConfigureAwait(true)) return FocusTestResult.Fail("home-anchor-pins-focused-card", "strip did not settle after the first step");
            double left1, right1, offset1;
            if (!TryFocusedCardPhysicalRect(out left1, out right1, out offset1)) return FocusTestResult.Fail("home-anchor-pins-focused-card", "focused card rect unreadable after the first step");
            // Three more steps: the strip must glide under the ring, which stays put.
            for (var i = 0; i < 3; i++) await PressAsync(next).ConfigureAwait(true);
            if (!await WaitForRowScrollStableAsync().ConfigureAwait(true)) return FocusTestResult.Fail("home-anchor-pins-focused-card", "strip did not settle after the walk");
            double left2, right2, offset2;
            if (!TryFocusedCardPhysicalRect(out left2, out right2, out offset2)) return FocusTestResult.Fail("home-anchor-pins-focused-card", "focused card rect unreadable after the walk");
            if (Math.Abs(left1 - expectedLeft) > toleranceEpx || Math.Abs(right1 - expectedRight) > toleranceEpx) return FocusTestResult.Fail("home-anchor-pins-focused-card", "first step rect=" + left1 + ".." + right1 + " expected " + expectedLeft + ".." + expectedRight);
            if (Math.Abs(left2 - expectedLeft) > toleranceEpx || Math.Abs(right2 - expectedRight) > toleranceEpx) return FocusTestResult.Fail("home-anchor-pins-focused-card", "after walk rect=" + left2 + ".." + right2 + " expected " + expectedLeft + ".." + expectedRight);
            if (offset2 <= offset1) return FocusTestResult.Fail("home-anchor-pins-focused-card", "strip did not move under the ring (offset " + offset1 + "->" + offset2 + ")");
            return FocusTestResult.Pass("home-anchor-pins-focused-card", "ring held " + left2 + ".." + right2 + " while the strip moved " + offset1 + "->" + offset2);
        }

        /// <summary>
        /// The focused card's physical edges inside its row's viewport, the
        /// same measurement the app code uses (HomePage KeepCardInView /
        /// TryComputeRowAnchorTarget): TransformToVisual against the row's
        /// ScrollViewer — found with the harness's own descendant walk, the
        /// same first-ScrollViewer-descendant the app's FindScrollViewer
        /// returns — then flipped to physical edges, because
        /// TransformToVisual reports RTL-origin coordinates in RTL (rect
        /// Left is the start edge there, not the physical left). Also
        /// returns the row's HorizontalOffset. False when the focused
        /// element is not a card, the row cannot be found, or the row has
        /// no ScrollViewer.
        /// </summary>
        private bool TryFocusedCardPhysicalRect(out double physicalLeft, out double physicalRight, out double horizontalOffset)
        {
            physicalLeft = 0;
            physicalRight = 0;
            horizontalOffset = 0;
            var item = FocusManager.GetFocusedElement() as GridViewItem;
            var row = FocusedRowGridView();
            if (item == null || row == null) return false;
            var viewer = FindDescendantOfType<ScrollViewer>(row);
            if (viewer == null) return false;
            var rect = item.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            var rtl = row.FlowDirection == FlowDirection.RightToLeft;
            physicalLeft = rtl ? viewer.ViewportWidth - rect.Right : rect.Left;
            physicalRight = rtl ? viewer.ViewportWidth - rect.Left : rect.Right;
            horizontalOffset = viewer.HorizontalOffset;
            return true;
        }

        /// <summary>
        /// Waits until the focused card's rect and the row's
        /// HorizontalOffset stop moving — two consecutive reads identical
        /// within 0.5 epx — or the bounded wait runs out. PressAsync waits
        /// only 200 ms and WaitForSettleAsync watches IsSettling, not the
        /// scroll, and the anchor glide is exponential, so asserting on a
        /// mid-glide rect would read a position the strip is only passing
        /// through. A read that throws (card not in the tree yet) counts as
        /// not stable yet, not as a failure. Returns false on timeout.
        /// </summary>
        private async Task<bool> WaitForRowScrollStableAsync(int timeoutMs = 5000)
        {
            double lastLeft = double.NaN;
            double lastRight = double.NaN;
            double lastOffset = double.NaN;
            var elapsedMs = 0;
            while (elapsedMs < timeoutMs)
            {
                double left, right, offset;
                var readable = false;
                try
                {
                    readable = TryFocusedCardPhysicalRect(out left, out right, out offset);
                }
                catch (Exception)
                {
                    readable = false;
                    left = 0;
                    right = 0;
                    offset = 0;
                }
                if (readable
                    && !double.IsNaN(lastLeft)
                    && Math.Abs(left - lastLeft) <= 0.5
                    && Math.Abs(right - lastRight) <= 0.5
                    && Math.Abs(offset - lastOffset) <= 0.5) return true;
                if (readable)
                {
                    lastLeft = left;
                    lastRight = right;
                    lastOffset = offset;
                }
                await Task.Delay(SettlePollIntervalMs).ConfigureAwait(true);
                elapsedMs += SettlePollIntervalMs;
            }
            return false;
        }

        private async Task<FocusTestResult> TestHomeBackDetailsMetaKeyAsync()
        {
            if (!await NavigateHomeAndSettleAsync().ConfigureAwait(true)) return FocusTestResult.Fail("home-back-details-meta-key", "home did not settle");
            await PressAsync("down").ConfigureAwait(true);
            // The first row is Continue Watching, whose ProgressEntry cards
            // carry no Meta — step Down (bounded) until a card with a Meta
            // id is focused. No Meta card in the scanned rows is a missing
            // fixture, not a product failure.
            var metaId = await ScanForMetaCardAsync().ConfigureAwait(true);
            if (metaId == null) return FocusTestResult.Skip("home-back-details-meta-key", "no Meta card found in rows");
            await ActivateFocusedAsync().ConfigureAwait(true);
            var details = await WaitForPageAsync<DetailsPage>().ConfigureAwait(true);
            if (details == null) return FocusTestResult.Fail("home-back-details-meta-key", "activation did not open Details");
            await WaitForSettleAsync().ConfigureAwait(true);
            await PressAsync("back").ConfigureAwait(true);
            var home = await WaitForPageAsync<HomePage>().ConfigureAwait(true);
            if (home == null) return FocusTestResult.Fail("home-back-details-meta-key", "B did not return Home");
            await WaitForSettleAsync().ConfigureAwait(true);
            var backId = FocusedCardMetaId();
            if (backId == metaId) return FocusTestResult.Pass("home-back-details-meta-key", "B returned to the exact card " + metaId);
            return FocusTestResult.Fail("home-back-details-meta-key", "after B card=" + (backId ?? "(none)") + " expected " + metaId);
        }

        private async Task<FocusTestResult> TestHomeBackContinueCwKeyAsync()
        {
            var contentId = await ReachContinueCardAsync().ConfigureAwait(true) ? FocusedContinueContentId() : null;
            if (contentId == null) return FocusTestResult.Skip("home-back-continue-cw-key", "no Continue card reached");
            var card = FocusManager.GetFocusedElement();
            await ActivateFocusedAsync().ConfigureAwait(true);
            // ResumeEntry resumes playback — Streams, or the Player directly
            // when a stream is remembered — never Details (see
            // ActivateFocusedAsync). Either destination counts; Back from
            // either returns to Home, the page under test here.
            var resumed = await WaitForAsync(() =>
            {
                var streams = FindPage<StreamsPage>(_shell);
                if (streams != null && streams.IsContentReady) return true;
                var player = FindPage<PlayerPage>(_shell);
                return player != null && player.IsContentReady;
            }, PageReadyTimeoutMs).ConfigureAwait(true);
            if (!resumed) return FocusTestResult.Fail("home-back-continue-cw-key", "resume did not open Streams or the Player");
            await WaitForSettleAsync().ConfigureAwait(true);
            await PressAsync("back").ConfigureAwait(true);
            var home = await WaitForPageAsync<HomePage>().ConfigureAwait(true);
            if (home == null) return FocusTestResult.Fail("home-back-continue-cw-key", "B did not return Home");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (SameCard(FocusManager.GetFocusedElement(), card) && FocusedContinueContentId() == contentId) return FocusTestResult.Pass("home-back-continue-cw-key", "B returned to the exact Continue card");
            return FocusTestResult.Fail("home-back-continue-cw-key", "after B card=" + (FocusedContinueContentId() ?? "(none)") + " expected " + contentId);
        }

        private Task<FocusTestResult> TestHomeColdFirstEntryAsync()
        {
            // Cold-start-only: the registry has recorded keys by the time this
            // runs, so a fresh first entry cannot be reproduced in-suite.
            // Covered by launch-hero-play on a cold launch.
            return Task.FromResult(FocusTestResult.Skip("home-cold-first-entry-hero-play", "cold-start only; covered by launch-hero-play"));
        }

        private async Task<FocusTestResult> TestSearchArrivalBoxAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("search-arrival-box", "no shell");
            _shell.Navigation.NavigateSearch(string.Empty);
            var page = await WaitForPageAsync<SearchPage>().ConfigureAwait(true);
            if (page == null) return FocusTestResult.Fail("search-arrival-box", "search did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            var focused = FocusManager.GetFocusedElement();
            if (focused is TextBox && HasAncestor<SearchPage>(focused as DependencyObject)) return FocusTestResult.Pass("search-arrival-box", "arrival on the search box");
            return FocusTestResult.Fail("search-arrival-box", "arrival focused=" + FocusedName());
        }

        private async Task<FocusTestResult> TestSearchDownResultsAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("search-down-results-scroll", "no shell");
            _shell.Navigation.NavigateSearch("the");
            var page = await WaitForPageAsync<SearchPage>().ConfigureAwait(true);
            if (page == null) return FocusTestResult.Fail("search-down-results-scroll", "search did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            await Task.Delay(3000).ConfigureAwait(true);
            await PressAsync("down").ConfigureAwait(true);
            var first = FocusManager.GetFocusedElement();
            if (!(first is GridViewItem) || !HasAncestor<SearchPage>(first as DependencyObject)) return FocusTestResult.Skip("search-down-results-scroll", "no results for the query");
            await PressAsync("down").ConfigureAwait(true);
            var second = FocusManager.GetFocusedElement();
            if (second is GridViewItem && !ReferenceEquals(first, second)) return FocusTestResult.Pass("search-down-results-scroll", "Down moved through results one row per press");
            return FocusTestResult.Fail("search-down-results-scroll", "second Down did not move to another result");
        }

        private async Task<FocusTestResult> TestSearchBackResultAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("search-back-result-or-box-when-gone", "no shell");
            _shell.Navigation.NavigateSearch("the");
            var page = await WaitForPageAsync<SearchPage>().ConfigureAwait(true);
            if (page == null) return FocusTestResult.Fail("search-back-result-or-box-when-gone", "search did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            await Task.Delay(3000).ConfigureAwait(true);
            await PressAsync("down").ConfigureAwait(true);
            var metaId = FocusedCardMetaId();
            if (metaId == null) return FocusTestResult.Skip("search-back-result-or-box-when-gone", "no results for the query");
            await ActivateFocusedAsync().ConfigureAwait(true);
            var details = await WaitForPageAsync<DetailsPage>().ConfigureAwait(true);
            if (details == null) return FocusTestResult.Fail("search-back-result-or-box-when-gone", "activation did not open Details");
            await WaitForSettleAsync().ConfigureAwait(true);
            await PressAsync("back").ConfigureAwait(true);
            var search = await WaitForPageAsync<SearchPage>().ConfigureAwait(true);
            if (search == null) return FocusTestResult.Fail("search-back-result-or-box-when-gone", "B did not return to Search");
            await WaitForSettleAsync().ConfigureAwait(true);
            var backId = FocusedCardMetaId();
            if (backId == metaId) return FocusTestResult.Pass("search-back-result-or-box-when-gone", "B returned to the exact result");
            if (FocusManager.GetFocusedElement() is TextBox) return FocusTestResult.Pass("search-back-result-or-box-when-gone", "result gone, B fell back to the box");
            return FocusTestResult.Fail("search-back-result-or-box-when-gone", "after B result=" + (backId ?? "(none)") + " expected " + metaId);
        }

        private async Task<FocusTestResult> TestCatalogArrivalFirstCardAsync()
        {
            if (!await ReachCatalogAsync().ConfigureAwait(true)) return FocusTestResult.Skip("catalog-arrival-first-card", "catalog did not load");
            var firstId = FirstCatalogCardMetaId();
            if (firstId == null) return FocusTestResult.Fail("catalog-arrival-first-card", "first card has no Meta id");
            var focusedId = FocusedCardMetaId();
            if (focusedId != firstId) return FocusTestResult.Fail("catalog-arrival-first-card", "arrival on card=" + (focusedId ?? "(none)") + " expected first card " + firstId);
            return FocusTestResult.Pass("catalog-arrival-first-card", "arrival on the first card");
        }

        private async Task<FocusTestResult> TestCatalogFourDirectionAsync()
        {
            if (!await ReachCatalogAsync().ConfigureAwait(true)) return FocusTestResult.Skip("catalog-four-direction-grid-scroll", "catalog did not load");
            var start = FocusManager.GetFocusedElement();
            await PressAsync("right").ConfigureAwait(true);
            var afterRight = FocusManager.GetFocusedElement();
            if (ReferenceEquals(start, afterRight)) return FocusTestResult.Fail("catalog-four-direction-grid-scroll", "Right did not move");
            await PressAsync("down").ConfigureAwait(true);
            var afterDown = FocusManager.GetFocusedElement();
            if (ReferenceEquals(afterRight, afterDown)) return FocusTestResult.Fail("catalog-four-direction-grid-scroll", "Down did not move");
            await PressAsync("left").ConfigureAwait(true);
            await PressAsync("up").ConfigureAwait(true);
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return FocusTestResult.Fail("catalog-four-direction-grid-scroll", "focus lost after Left/Up");
            bool first;
            bool last;
            if (_shell.IsSidebarButton(focused, out first, out last)) return FocusTestResult.Fail("catalog-four-direction-grid-scroll", "four-direction walk landed on the sidebar");
            if (!HasAncestor<CatalogPage>(focused)) return FocusTestResult.Fail("catalog-four-direction-grid-scroll", "four-direction walk left the catalog");
            return FocusTestResult.Pass("catalog-four-direction-grid-scroll", "Right/Down/Left/Up stayed in the grid");
        }

        private async Task<FocusTestResult> TestCatalogBackOpenedCardAsync()
        {
            if (!await ReachCatalogAsync().ConfigureAwait(true)) return FocusTestResult.Skip("catalog-back-opened-card", "catalog did not load");
            var metaId = FocusedCardMetaId();
            if (metaId == null) return FocusTestResult.Fail("catalog-back-opened-card", "first card has no Meta id");
            await ActivateFocusedAsync().ConfigureAwait(true);
            var details = await WaitForPageAsync<DetailsPage>().ConfigureAwait(true);
            if (details == null) return FocusTestResult.Fail("catalog-back-opened-card", "activation did not open Details");
            await WaitForSettleAsync().ConfigureAwait(true);
            await PressAsync("back").ConfigureAwait(true);
            var catalog = await WaitForPageAsync<CatalogPage>().ConfigureAwait(true);
            if (catalog == null) return FocusTestResult.Fail("catalog-back-opened-card", "B did not return to the catalog");
            await WaitForSettleAsync().ConfigureAwait(true);
            var backId = FocusedCardMetaId();
            if (backId == metaId) return FocusTestResult.Pass("catalog-back-opened-card", "B returned to the opened card");
            return FocusTestResult.Fail("catalog-back-opened-card", "after B card=" + (backId ?? "(none)") + " expected " + metaId);
        }

        private async Task<FocusTestResult> TestFirstEntryEverywhereAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("first-entry-first-card-everywhere", "no shell");
            _shell.Navigation.NavigateSearch(string.Empty);
            var search = await WaitForPageAsync<SearchPage>().ConfigureAwait(true);
            if (search == null) return FocusTestResult.Fail("first-entry-first-card-everywhere", "search did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (!(FocusManager.GetFocusedElement() is TextBox)) return FocusTestResult.Fail("first-entry-first-card-everywhere", "search first entry was not the box");
            if (!await ReachCatalogAsync().ConfigureAwait(true)) return FocusTestResult.Skip("first-entry-first-card-everywhere", "catalog did not load (search half passed)");
            return FocusTestResult.Pass("first-entry-first-card-everywhere", "search box + catalog first card; empty-library half is cold-start only");
        }

        // ---------- Phase 3: dialogs + Streams ----------

        private async Task<FocusTestResult> TestDialogHomeContinueCancelAsync()
        {
            var contentId = await ReachContinueCardAsync().ConfigureAwait(true) ? FocusedContinueContentId() : null;
            if (contentId == null) return FocusTestResult.Skip("dialog-home-continue-cancel-restores", "no Continue card reached");
            var card = FocusManager.GetFocusedElement();
            await PressAsync("y").ConfigureAwait(true);
            if (!await WaitForDialogOpenAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-home-continue-cancel-restores", "Y did not open the Continue menu");
            if (!await CloseOpenDialogAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-home-continue-cancel-restores", "dialog did not close");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (SameCard(FocusManager.GetFocusedElement(), card) && FocusedContinueContentId() == contentId) return FocusTestResult.Pass("dialog-home-continue-cancel-restores", "cancel restored the invoking card");
            return FocusTestResult.Fail("dialog-home-continue-cancel-restores", "after close card=" + (FocusedContinueContentId() ?? "(none)"));
        }

        /// <summary>
        /// Opens Details for the focused card and, only when the loaded
        /// title actually has episodes (Route 3 gate:
        /// DetailsViewModel.HasEpisodes, read after the page is content
        /// ready — Meta is assigned before LoadCompleted fires), runs the
        /// episode-menu dialog body below. Returns null only when the
        /// title has no episodes, so the caller presses Back (from Details)
        /// and tries the next candidate; otherwise the body's
        /// Pass/Fail/Skip. A Details page that never opens is a Fail, as
        /// before — never a silent try-next (Back from the wrong page
        /// would leave the catalog). The body itself is unchanged: the
        /// dialog-close-restore expectation is untouched.
        /// </summary>
        private async Task<FocusTestResult> TryEpisodeDialogForFocusedCardAsync()
        {
            await ActivateFocusedAsync().ConfigureAwait(true);
            var details = await WaitForPageAsync<DetailsPage>().ConfigureAwait(true);
            if (details == null) return FocusTestResult.Fail("dialog-details-episode-restores", "activation did not open Details");
            await WaitForSettleAsync().ConfigureAwait(true);
            var viewModel = details.DataContext as DetailsViewModel;
            if (viewModel == null || !viewModel.HasEpisodes) return null;
            object episodeCard = null;
            var opened = false;
            for (var i = 0; i < 12 && !opened; i++)
            {
                // Y opens a menu only on a card (a GridViewItem). On the
                // Play button or a section header it does nothing, so walk
                // Down past non-cards instead of pressing Y on them.
                var focused = FocusManager.GetFocusedElement();
                if (!(focused is GridViewItem))
                {
                    await PressAsync("down").ConfigureAwait(true);
                    continue;
                }
                episodeCard = focused;
                await PressAsync("y").ConfigureAwait(true);
                opened = await WaitForDialogOpenAsync(1000).ConfigureAwait(true);
                if (!opened) await PressAsync("down").ConfigureAwait(true);
            }
            if (!opened) return FocusTestResult.Skip("dialog-details-episode-restores", "no episode menu found (title may have no episodes)");
            if (!await CloseOpenDialogAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-details-episode-restores", "dialog did not close");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (SameCard(FocusManager.GetFocusedElement(), episodeCard)) return FocusTestResult.Pass("dialog-details-episode-restores", "cancel restored the episode card");
            return FocusTestResult.Fail("dialog-details-episode-restores", "after close focused=" + FocusedName());
        }

        private async Task<FocusTestResult> TestDialogDetailsEpisodeAsync()
        {
            // The old test scanned Home for the first Meta card, which is
            // always a movie (the first Home row after Continue is
            // Cinemeta movie|top), so it could never reach an episode menu
            // and SKIPPED in every log. Three routes, ≤3 titles total:
            // Route 1 (deterministic): the Cinemeta series catalog, whose
            // cards are series by construction. Route 2 (fallback): a
            // type-filtered Home scan for a series card. Route 3 (gate):
            // after opening Details, HasEpisodes decides — a title without
            // episodes backs out and the next candidate is tried.
            const string name = "dialog-details-episode-restores";
            var attempts = 0;
            string route1Skip = null;
            var route2Ran = false;
            if (await ReachCatalogAsync("series", "top").ConfigureAwait(true))
            {
                for (var i = 0; i < 3 && attempts < 3; i++)
                {
                    if (!string.Equals(FocusedCardMetaType(), "series", StringComparison.OrdinalIgnoreCase)) break;
                    attempts++;
                    var result = await TryEpisodeDialogForFocusedCardAsync().ConfigureAwait(true);
                    if (result != null) return result;
                    await PressAsync("back").ConfigureAwait(true);
                    await WaitForSettleAsync().ConfigureAwait(true);
                    await PressAsync("down").ConfigureAwait(true);
                    await WaitForSettleAsync().ConfigureAwait(true);
                    if (FocusManager.GetFocusedElement() as GridViewItem == null) break;
                }
            }
            else if (IsSeriesCatalogEmpty())
            {
                // Leg skipped, not a product failure: series/top is not
                // advertised by the addon. Fall through to Route 2.
                route1Skip = "no series catalog (series/top not advertised)";
            }
            if (attempts < 3)
            {
                if (!await NavigateHomeAndSettleAsync().ConfigureAwait(true))
                {
                    // Nothing was ever attempted and Home itself did not
                    // settle: today's behaviour (Fail), unless the only
                    // route that ran already established the catalog is
                    // not advertised — then the honest outcome is that
                    // leg's SKIP, not a product failure.
                    if (attempts == 0 && route1Skip != null) return FocusTestResult.Skip(name, route1Skip);
                    if (attempts == 0) return FocusTestResult.Fail(name, "home did not settle");
                    return FocusTestResult.Skip(name, "no episode menu found (title may have no episodes)");
                }
                await PressAsync("down").ConfigureAwait(true);
                route2Ran = true;
                while (attempts < 3 && await ScanForSeriesCardAsync().ConfigureAwait(true))
                {
                    attempts++;
                    var result = await TryEpisodeDialogForFocusedCardAsync().ConfigureAwait(true);
                    if (result != null) return result;
                    await PressAsync("back").ConfigureAwait(true);
                    await WaitForSettleAsync().ConfigureAwait(true);
                    await PressAsync("down").ConfigureAwait(true);
                    await WaitForSettleAsync().ConfigureAwait(true);
                }
            }
            // No route produced a series title with episodes. When Route 2
            // never ran (Home did not settle) and the series catalog is
            // not advertised, that leg's reason is the informative one;
            // otherwise the exact SKIP as before.
            if (attempts == 0 && route1Skip != null && !route2Ran) return FocusTestResult.Skip(name, route1Skip);
            return FocusTestResult.Skip(name, "no episode menu found (title may have no episodes)");
        }

        private async Task<FocusTestResult> TestDialogPlayerSubtitleAsync()
        {
            if (!await TryReachPlayerAsync().ConfigureAwait(true)) return FocusTestResult.Skip("dialog-player-subtitle-restores", ReachFailureDetail());
            if (!await SteerToOsdButtonAsync("SpeedButton").ConfigureAwait(true)) return FocusTestResult.Skip("dialog-player-subtitle-restores", "could not steer to SpeedButton");
            var invoker = FocusedName();
            await PressAsync("y").ConfigureAwait(true);
            if (!await WaitForDialogOpenAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-player-subtitle-restores", "Y did not open the subtitle menu");
            if (!await CloseOpenDialogAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-player-subtitle-restores", "dialog did not close");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (FocusedName() == invoker) return FocusTestResult.Pass("dialog-player-subtitle-restores", "close restored the invoker " + invoker);
            return FocusTestResult.Fail("dialog-player-subtitle-restores", "after close focused=" + FocusedName() + " expected " + invoker);
        }

        private async Task<FocusTestResult> TestDialogPlayerDelayAsync()
        {
            if (!await TryReachPlayerAsync().ConfigureAwait(true)) return FocusTestResult.Skip("dialog-player-delay-restores", ReachFailureDetail());
            if (!await SteerToOsdButtonAsync("SpeedButton").ConfigureAwait(true)) return FocusTestResult.Skip("dialog-player-delay-restores", "could not steer to SpeedButton");
            var invoker = FocusedName();
            await PressAsync("y").ConfigureAwait(true);
            if (!await WaitForDialogOpenAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-player-delay-restores", "Y did not open the subtitle menu");
            if (!await InvokeDialogButtonAsync("elay", "تأخير").ConfigureAwait(true)) return FocusTestResult.Skip("dialog-player-delay-restores", "no delay row in the subtitle menu");
            if (!await WaitForDialogOpenAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-player-delay-restores", "delay menu did not open");
            if (!await CloseOpenDialogAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-player-delay-restores", "delay dialog did not close");
            await WaitForSettleAsync().ConfigureAwait(true);
            // The coordinator's restore skips when focus is already on a
            // valid target in the current page ("Dialog restore skipped:
            // focus is already on a valid target"), so closing the delay
            // dialog leaves focus on its invoker — the same rule the
            // subtitle test asserts, not a fallback to the arrival target.
            if (FocusedName() == invoker) return FocusTestResult.Pass("dialog-player-delay-restores", "close restored the invoker " + invoker);
            return FocusTestResult.Fail("dialog-player-delay-restores", "after close focused=" + FocusedName() + " expected " + invoker);
        }

        // The dialog under test is now the install-addon's text-entry dialog (RequestTextAsync; Hide/cancel/empty confirm change no state) — the Reset confirm dialog is never opened by this test; installed addons are protected by the suite-level addon guard in RunAllTestsAsync instead.
        private async Task<FocusTestResult> TestDialogSettingsConfirmAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("dialog-settings-confirm-restores", "no shell");
            _shell.Navigation.NavigateSettings("addons");
            if (await WaitForPageAsync<SettingsPage>().ConfigureAwait(true) == null) return FocusTestResult.Fail("dialog-settings-confirm-restores", "addons did not load");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (FocusedName() != "InstallAddonButton") return FocusTestResult.Fail("dialog-settings-confirm-restores", "arrival focused=" + FocusedName() + " expected InstallAddonButton");
            await ActivateFocusedAsync().ConfigureAwait(true);
            if (!await WaitForDialogOpenAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-settings-confirm-restores", "activation did not open the install dialog");
            if (!await CloseOpenDialogAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-settings-confirm-restores", "dialog did not close");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (FocusedName() == "InstallAddonButton") return FocusTestResult.Pass("dialog-settings-confirm-restores", "close restored the Install button");
            return FocusTestResult.Fail("dialog-settings-confirm-restores", "after close focused=" + FocusedName());
        }

        private Task<FocusTestResult> TestDialogStreamsDebridAsync()
        {
            // The debrid invalid-key dialog needs a torrent stream resolved
            // against an invalid Debrid key — a fixture the suite cannot
            // construct (no Debrid account with an invalid key, no forced
            // torrent resolve). The reachable half — Streams arrival focus —
            // is covered by streams-filter-row-vanishes-fallback. The dialog
            // itself stays a manual device check (Phase 3 list, step 6).
            return Task.FromResult(FocusTestResult.Skip("dialog-streams-debrid-both-buttons-restore", "debrid dialog needs invalid-key + torrent fixture (manual check)"));
        }

        private async Task<FocusTestResult> TestDialogNavigatingChoiceAsync()
        {
            var contentId = await ReachContinueCardAsync().ConfigureAwait(true) ? FocusedContinueContentId() : null;
            if (contentId == null) return FocusTestResult.Skip("dialog-navigating-choice-arrival-settle", "no Continue card reached");
            await PressAsync("y").ConfigureAwait(true);
            if (!await WaitForDialogOpenAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-navigating-choice-arrival-settle", "Y did not open the Continue menu");
            // The first button in the menu is always "Go to details".
            var dialog = FindOpenDialog();
            if (dialog == null) return FocusTestResult.Fail("dialog-navigating-choice-arrival-settle", "dialog vanished");
            var buttons = FindDescendantsOfType<Button>(dialog);
            if (buttons.Count == 0) return FocusTestResult.Fail("dialog-navigating-choice-arrival-settle", "dialog has no buttons");
            new ButtonAutomationPeer(buttons[0]).Invoke();
            var details = await WaitForPageAsync<DetailsPage>().ConfigureAwait(true);
            if (details == null) return FocusTestResult.Fail("dialog-navigating-choice-arrival-settle", "choice did not navigate to Details");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (HasAncestor<DetailsPage>(FocusManager.GetFocusedElement() as DependencyObject)) return FocusTestResult.Pass("dialog-navigating-choice-arrival-settle", "choice navigated, arrival settled inside Details");
            return FocusTestResult.Fail("dialog-navigating-choice-arrival-settle", "after choice focused=" + FocusedName());
        }

        private async Task<FocusTestResult> TestDialogNestedAsync()
        {
            if (!await TryReachPlayerAsync().ConfigureAwait(true)) return FocusTestResult.Skip("dialog-nested-outer-only-on-outer-close", ReachFailureDetail());
            if (!await SteerToOsdButtonAsync("SpeedButton").ConfigureAwait(true)) return FocusTestResult.Skip("dialog-nested-outer-only-on-outer-close", "could not steer to SpeedButton");
            await PressAsync("y").ConfigureAwait(true);
            if (!await WaitForDialogOpenAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-nested-outer-only-on-outer-close", "Y did not open the subtitle menu");
            if (!await InvokeDialogButtonAsync("elay", "تأخير").ConfigureAwait(true)) return FocusTestResult.Skip("dialog-nested-outer-only-on-outer-close", "no delay row in the subtitle menu");
            if (!await WaitForDialogOpenAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-nested-outer-only-on-outer-close", "delay menu did not open");
            // While the inner (delay) dialog is open, the outer invoker must
            // NOT have been restored: focus stays inside the inner dialog.
            var innerFocused = FocusManager.GetFocusedElement();
            if (innerFocused == null || FindOpenDialog() == null) return FocusTestResult.Fail("dialog-nested-outer-only-on-outer-close", "no focus inside the inner dialog");
            if (!HasAncestor<ContentDialog>(innerFocused as DependencyObject)) return FocusTestResult.Fail("dialog-nested-outer-only-on-outer-close", "focus left the inner dialog early, focused=" + FocusedName());
            if (!await CloseOpenDialogAsync().ConfigureAwait(true)) return FocusTestResult.Fail("dialog-nested-outer-only-on-outer-close", "inner dialog did not close");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (FocusedName() == "BackButton") return FocusTestResult.Pass("dialog-nested-outer-only-on-outer-close", "inner close landed on the arrival target, never inside the closed outer dialog");
            return FocusTestResult.Fail("dialog-nested-outer-only-on-outer-close", "after inner close focused=" + FocusedName() + " expected BackButton");
        }

        /// <summary>True when the focused element is a Button whose
        /// ancestor chain contains the FiltersPanel — the structural chip
        /// check. The old check tested FocusedTag().StartsWith("chip:"),
        /// which can never be true: FocusedTag() returns the raw
        /// Button.Tag, and chip Tags are chip.Key ("all" or an addon
        /// manifest URL) — the "chip:" prefix exists only in the
        /// coordinator's arrival-focus key, never in Button.Tag.</summary>
        private static bool IsFocusedFilterChip(StackPanel filtersPanel)
        {
            var button = FocusManager.GetFocusedElement() as Button;
            if (button == null || filtersPanel == null) return false;
            for (var node = VisualTreeHelper.GetParent(button); node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, filtersPanel)) return true;
            }
            return false;
        }

        private async Task<FocusTestResult> TestStreamsFilterRowVanishesAsync()
        {
            // Reach Streams through the shared preamble, then steer to the
            // first playable row: arrival parks on the "all" filter chip,
            // not a row, so the old test's assumption (arrival focus is a
            // row) was wrong in the same way as the reach chain's.
            var streams = await ReachStreamsAsync().ConfigureAwait(true);
            if (streams == null) return FocusTestResult.Skip("streams-filter-row-vanishes-fallback", ReachFailureDetail());
            await WaitForSettleAsync().ConfigureAwait(true);
            if (!await SteerStreamsToFirstPlayableRowAsync(streams).ConfigureAwait(true)) return FocusTestResult.Skip("streams-filter-row-vanishes-fallback", ReachFailureDetail());
            // Move up toward the filter chips in a bounded sweep; if a
            // chip is reached, select it to change the filter, then focus
            // must land on a neighbour, a chip, or the arrival target —
            // never vanish. No chip in the sweep is a steering miss (Up
            // row→chip has never been exercised in any log), so SKIP.
            var filtersPanel = FindNamedDescendant<StackPanel>(streams, "FiltersPanel");
            for (var i = 0; i < 2 && !IsFocusedFilterChip(filtersPanel); i++)
            {
                await PressAsync("up").ConfigureAwait(true);
                await WaitForSettleAsync().ConfigureAwait(true);
            }
            if (!IsFocusedFilterChip(filtersPanel))
            {
                return FocusTestResult.Skip("streams-filter-row-vanishes-fallback", "filter chips not reached from the rows");
            }
            await ActivateFocusedAsync().ConfigureAwait(true);
            await Task.Delay(1000).ConfigureAwait(true);
            var after = FocusManager.GetFocusedElement() as DependencyObject;
            if (after == null) return FocusTestResult.Fail("streams-filter-row-vanishes-fallback", "focus vanished after the filter change");
            if (!HasAncestor<StreamsPage>(after)) return FocusTestResult.Fail("streams-filter-row-vanishes-fallback", "focus left Streams after the filter change");
            return FocusTestResult.Pass("streams-filter-row-vanishes-fallback", "focus survived the filter change on " + FocusedName());
        }

        private async Task<FocusTestResult> TestDialogGuardNoRegressionAsync()
        {
            if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("dialog-guard-no-regression", "no shell");
            _shell.Navigation.NavigateHome();
            await PressAsync("down").ConfigureAwait(true);
            await PressAsync("down").ConfigureAwait(true);
            var home = await WaitForPageAsync<HomePage>().ConfigureAwait(true);
            if (home == null) return FocusTestResult.Fail("dialog-guard-no-regression", "Home did not load after rapid presses");
            await WaitForSettleAsync().ConfigureAwait(true);
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return FocusTestResult.Fail("dialog-guard-no-regression", "focus lost after rapid presses during load");
            bool first;
            bool last;
            if (_shell.IsSidebarButton(focused, out first, out last)) return FocusTestResult.Fail("dialog-guard-no-regression", "rapid presses stuck focus on the sidebar");
            if (!HasAncestor<HomePage>(focused)) return FocusTestResult.Fail("dialog-guard-no-regression", "focus outside Home after rapid presses");
            if (_shell.IsSidebarExpanded) return FocusTestResult.Fail("dialog-guard-no-regression", "strip expanded after rapid presses during load");
            return FocusTestResult.Pass("dialog-guard-no-regression", "rapid D-pad during load never stuck focus, ended on " + FocusedName());
        }

        // ---------- Phase 4: Player OSD ----------

        private async Task<FocusTestResult> TestOsdHideParksBackAsync()
        {
            if (!await TryReachPlayerAsync().ConfigureAwait(true)) return FocusTestResult.Skip("osd-hide-parks-back-remembers", ReachFailureDetail());
            var offset = LogOffset();
            var hidden = await WaitForOsdHiddenAsync().ConfigureAwait(true);
            if (!hidden) return FocusTestResult.Fail("osd-hide-parks-back-remembers", "OSD did not hide within the wait");
            var parkedLog = LogContainsSince("OSD hidden; parked focus on Back", offset);
            if (FocusedName() == "BackButton" && parkedLog) return FocusTestResult.Pass("osd-hide-parks-back-remembers", "OSD hid, focus parked on Back, log confirms");
            if (FocusedName() == "BackButton") return FocusTestResult.Pass("osd-hide-parks-back-remembers", "OSD hid, focus parked on Back (log line not found)");
            return FocusTestResult.Fail("osd-hide-parks-back-remembers", "after hide focused=" + FocusedName() + " expected BackButton");
        }

        private async Task<FocusTestResult> TestOsdShowRestoresAsync()
        {
            if (!await TryReachPlayerAsync().ConfigureAwait(true)) return FocusTestResult.Skip("osd-show-restores-same-button", ReachFailureDetail());
            if (!await SteerToOsdButtonAsync("SpeedButton").ConfigureAwait(true)) return FocusTestResult.Skip("osd-show-restores-same-button", "could not steer to SpeedButton");
            var offset = LogOffset();
            await WaitForOsdHiddenAsync().ConfigureAwait(true);
            // Show with Down, not Right: while the OSD is hidden Right
            // means "scrub right" — HandleAction calls
            // ShowControls("seek"), whose unconditional "seek" hint yanks
            // focus from the just-restored SpeedButton onto SeekBar (the
            // restore itself works; the sibling seek-hint test asserts
            // that Right lands on SeekBar). Down shows the OSD with the
            // "play" hint instead, whose never-yank rule keeps the
            // restored button, so this test measures the restore alone.
            await PressAsync("down").ConfigureAwait(true);
            var restored = await WaitForAsync(() => FocusedName() == "SpeedButton", 3000).ConfigureAwait(true);
            if (restored)
            {
                var logged = LogContainsSince("OSD shown; focus restored to SpeedButton", offset);
                return FocusTestResult.Pass("osd-show-restores-same-button", "OSD showed, focus back on SpeedButton" + (logged ? ", log confirms" : ""));
            }
            return FocusTestResult.Fail("osd-show-restores-same-button", "after show focused=" + FocusedName() + " expected SpeedButton");
        }

        private async Task<FocusTestResult> TestOsdSeekHintWinsAsync()
        {
            if (!await TryReachPlayerAsync().ConfigureAwait(true)) return FocusTestResult.Skip("osd-seek-explicit-hint-wins", ReachFailureDetail());
            if (!await SteerToOsdButtonAsync("SeekBar").ConfigureAwait(true)) return FocusTestResult.Skip("osd-seek-explicit-hint-wins", "could not steer to SeekBar");
            await WaitForOsdHiddenAsync().ConfigureAwait(true);
            await PressAsync("right").ConfigureAwait(true);
            var onSeek = await WaitForAsync(() => FocusedName() == "SeekBar", 3000).ConfigureAwait(true);
            if (onSeek) return FocusTestResult.Pass("osd-seek-explicit-hint-wins", "explicit hint kept focus on the seek bar");
            return FocusTestResult.Fail("osd-seek-explicit-hint-wins", "after Right focused=" + FocusedName() + " expected SeekBar");
        }

        private async Task<FocusTestResult> TestOsdHiddenYDialogAsync()
        {
            if (!await TryReachPlayerAsync().ConfigureAwait(true)) return FocusTestResult.Skip("osd-hidden-y-dialog-then-restore", ReachFailureDetail());
            if (!await SteerToOsdButtonAsync("SpeedButton").ConfigureAwait(true)) return FocusTestResult.Skip("osd-hidden-y-dialog-then-restore", "could not steer to SpeedButton");
            await WaitForOsdHiddenAsync().ConfigureAwait(true);
            await PressAsync("y").ConfigureAwait(true);
            if (!await WaitForDialogOpenAsync().ConfigureAwait(true)) return FocusTestResult.Fail("osd-hidden-y-dialog-then-restore", "Y did not open the subtitle menu while hidden");
            if (!await CloseOpenDialogAsync().ConfigureAwait(true)) return FocusTestResult.Fail("osd-hidden-y-dialog-then-restore", "dialog did not close");
            await WaitForSettleAsync().ConfigureAwait(true);
            if (FocusedName() != "BackButton") return FocusTestResult.Fail("osd-hidden-y-dialog-then-restore", "after dialog close focused=" + FocusedName() + " expected BackButton");
            await PressAsync("down").ConfigureAwait(true);
            var restored = await WaitForAsync(() => FocusedName() == "SpeedButton", 3000).ConfigureAwait(true);
            if (restored) return FocusTestResult.Pass("osd-hidden-y-dialog-then-restore", "dialog closed to Back, direction restored SpeedButton");
            return FocusTestResult.Fail("osd-hidden-y-dialog-then-restore", "after direction focused=" + FocusedName() + " expected SpeedButton");
        }

        private async Task<FocusTestResult> TestOsdPauseXHiddenAsync()
        {
            if (!await TryReachPlayerAsync().ConfigureAwait(true)) return FocusTestResult.Skip("osd-pause-x-hidden-shows-restores", ReachFailureDetail());
            if (!await SteerToOsdButtonAsync("SpeedButton").ConfigureAwait(true)) return FocusTestResult.Skip("osd-pause-x-hidden-shows-restores", "could not steer to SpeedButton");
            await WaitForOsdHiddenAsync().ConfigureAwait(true);
            await PressAsync("x").ConfigureAwait(true);
            var restored = await WaitForAsync(() => FocusedName() == "SpeedButton", 3000).ConfigureAwait(true);
            if (restored) return FocusTestResult.Pass("osd-pause-x-hidden-shows-restores", "X paused, OSD showed, focus back on SpeedButton");
            return FocusTestResult.Fail("osd-pause-x-hidden-shows-restores", "after X focused=" + FocusedName() + " expected SpeedButton");
        }

        private async Task<FocusTestResult> TestOsdHiddenBackTwiceExitsAsync()
        {
            if (!await TryReachPlayerAsync().ConfigureAwait(true)) return FocusTestResult.Skip("osd-hidden-back-twice-exits", ReachFailureDetail());
            await WaitForOsdHiddenAsync().ConfigureAwait(true);
            await PressAsync("back").ConfigureAwait(true);
            await Task.Delay(500).ConfigureAwait(true);
            await PressAsync("back").ConfigureAwait(true);
            var left = await WaitForAsync(() => FindPage<PlayerPage>(_shell) == null, 5000).ConfigureAwait(true);
            if (left) return FocusTestResult.Pass("osd-hidden-back-twice-exits", "second Back while hidden left the player");
            return FocusTestResult.Fail("osd-hidden-back-twice-exits", "still in the player after two Back presses");
        }

        private Task<FocusTestResult> TestOsdNextCardNotParkedAsync()
        {
            // Needs playback near the end of an episode so the Next card is
            // showing — a timing fixture the suite cannot construct. Manual
            // device check (Phase 4 list, step 5).
            return Task.FromResult(FocusTestResult.Skip("osd-next-card-not-parked", "needs near-end playback for the Next card (manual check)"));
        }

        private async Task<FocusTestResult> TestOsdFlagOffAsync()
        {
            FocusCoordinator.UseFocusCoordinator = false;
            try
            {
                if (!await TryReachPlayerAsync().ConfigureAwait(true)) return FocusTestResult.Skip("osd-flag-off-old-behaviour", ReachFailureDetail());
                if (!await SteerToOsdButtonAsync("SpeedButton").ConfigureAwait(true)) return FocusTestResult.Skip("osd-flag-off-old-behaviour", "could not steer to SpeedButton");
                await Task.Delay(5000).ConfigureAwait(true);
                var opacity = OsdOpacity();
                if (opacity.HasValue && opacity.Value == 0 && FocusedName() == "SpeedButton")
                {
                    return FocusTestResult.Pass("osd-flag-off-old-behaviour", "flag off: OSD hid, focus stayed on the hidden SpeedButton");
                }
                return FocusTestResult.Fail("osd-flag-off-old-behaviour", "flag off: opacity=" + (opacity.HasValue ? opacity.Value.ToString() : "(none)") + " focused=" + FocusedName());
            }
            finally
            {
                FocusCoordinator.UseFocusCoordinator = true;
                await NavigateHomeAndSettleAsync().ConfigureAwait(true);
            }
        }

        // ---------- Flag-off sanity ----------

        private async Task<FocusTestResult> TestFlagOffPhase2Async()
        {
            FocusCoordinator.UseFocusCoordinator = false;
            try
            {
                if (_shell == null || _shell.Navigation == null) return FocusTestResult.Fail("flag-off-phase2-legacy", "no shell");
                _shell.Navigation.NavigateHome();
                var home = await WaitForPageAsync<HomePage>().ConfigureAwait(true);
                if (home == null) return FocusTestResult.Fail("flag-off-phase2-legacy", "home did not load with flag off");
                await Task.Delay(2000).ConfigureAwait(true);
                if (!IsHeroPlayButton(FocusManager.GetFocusedElement())) return FocusTestResult.Fail("flag-off-phase2-legacy", "flag off: home arrival focused=" + FocusedName());
                _shell.Navigation.NavigateSettingsRoot();
                var settings = await WaitForPageAsync<SettingsPage>().ConfigureAwait(true);
                if (settings == null) return FocusTestResult.Fail("flag-off-phase2-legacy", "settings did not load with flag off");
                await Task.Delay(2000).ConfigureAwait(true);
                if (FocusedTag() != "root|account") return FocusTestResult.Fail("flag-off-phase2-legacy", "flag off: settings arrival tag=" + FocusedTag());
                return FocusTestResult.Pass("flag-off-phase2-legacy", "flag off: legacy settle still lands on hero Play and the first settings row");
            }
            finally
            {
                FocusCoordinator.UseFocusCoordinator = true;
                await NavigateHomeAndSettleAsync().ConfigureAwait(true);
            }
        }

        private async Task<FocusTestResult> TestFlagOffPhase3Async()
        {
            FocusCoordinator.UseFocusCoordinator = false;
            try
            {
                var contentId = await ReachContinueCardAsync().ConfigureAwait(true) ? FocusedContinueContentId() : null;
                if (contentId == null) return FocusTestResult.Skip("flag-off-phase3-dialogs-legacy", "no Continue card reached");
                var card = FocusManager.GetFocusedElement();
                await PressAsync("y").ConfigureAwait(true);
                var opened = await WaitForAsync(() => FindOpenDialog() != null, 3000).ConfigureAwait(true);
                if (!opened) return FocusTestResult.Fail("flag-off-phase3-dialogs-legacy", "Y did not open the Continue menu with flag off");
                if (!await CloseOpenDialogAsync().ConfigureAwait(true)) return FocusTestResult.Fail("flag-off-phase3-dialogs-legacy", "dialog did not close with flag off");
                await Task.Delay(1000).ConfigureAwait(true);
                if (SameCard(FocusManager.GetFocusedElement(), card) && FocusedContinueContentId() == contentId) return FocusTestResult.Pass("flag-off-phase3-dialogs-legacy", "flag off: legacy restore returned to the invoking card");
                return FocusTestResult.Fail("flag-off-phase3-dialogs-legacy", "flag off: after close card=" + (FocusedContinueContentId() ?? "(none)"));
            }
            finally
            {
                FocusCoordinator.UseFocusCoordinator = true;
                await NavigateHomeAndSettleAsync().ConfigureAwait(true);
            }
        }

        /// <summary>One scripted step: an optional input action, a wait, then a focus assertion.</summary>
        public sealed class FocusTestStep
        {
            /// <summary>Controller action ("up"/"down"/"left"/"right"/"select"/"back"/...); null or empty means no input, just wait and assert.</summary>
            public string Action { get; set; }

            /// <summary>True for a held-button repeat (arrives every 110 ms on the controller); false for a single press.</summary>
            public bool Repeat { get; set; }

            /// <summary>Milliseconds to wait after the input (default 200 ms: past the 150 ms dedupe) before asserting.</summary>
            public int WaitMs { get; set; }

            /// <summary>Name of the element expected to hold focus after the wait; null means no focus assertion for this step.</summary>
            public string ExpectedFocusedName { get; set; }
        }

        /// <summary>
        /// Runs a table of steps in order on the UI thread. Each step drives
        /// one input through the pinned MainPage seam (or none), waits the
        /// step's realistic inter-press delay, then asserts the focused
        /// element's Name when the step names one. Returns false at the first
        /// failed assertion (later steps still run: the loop continues so one
        /// failure does not hide the rest of the script's behaviour).
        /// </summary>
        public async Task<bool> RunStepsAsync(IReadOnlyList<FocusTestStep> steps)
        {
            if (_shell == null || steps == null) return false;
            var passed = true;
            foreach (var step in steps)
            {
                if (!string.IsNullOrEmpty(step.Action))
                {
                    _shell.SimulateFocusInput(step.Action, step.Repeat);
                }
                var waitMs = step.WaitMs > 0 ? step.WaitMs : InterPressDelayMs;
                await Task.Delay(waitMs).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(step.ExpectedFocusedName) && !IsFocusedElementNamed(step.ExpectedFocusedName))
                {
                    passed = false;
                }
            }
            return passed;
        }

        private bool IsFocusedElementNamed(string name)
        {
            var focused = FocusManager.GetFocusedElement() as FrameworkElement;
            return focused != null && focused.Name == name;
        }

        /// <summary>True when the focused element is the hero Play button: a Button named HeroPlayButton inside the HomePage.</summary>
        private bool IsHeroPlayButton(object focused)
        {
            var button = focused as Button;
            if (button == null || button.Name != HeroPlayButtonName) return false;
            return HasAncestor<HomePage>(button);
        }

        /// <summary>Tag/Type of the focused element for the result line. Single-character tags are Segoe glyphs, logged as their code point.</summary>
        private static string DescribeTagAndType(object focused)
        {
            var type = focused != null ? focused.GetType().Name : "null";
            var button = focused as Button;
            if (button == null) return "null/" + type;
            var tag = button.Tag as string;
            if (string.IsNullOrEmpty(tag)) return "(none)/" + type;
            if (tag.Length == 1) return "U+" + ((int)tag[0]).ToString("X4") + "/" + type;
            return tag + "/" + type;
        }
    }
}
