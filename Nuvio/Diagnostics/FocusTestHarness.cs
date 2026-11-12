using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Nuvio.Views;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Nuvio.Diagnostics
{
    /// <summary>
    /// Phase 0 scripted focus-test engine (Focus Manager programme, stage 0).
    ///
    /// Drives input ONLY through the pinned MainPage seam
    /// <see cref="MainPage.SimulateFocusInput(string, bool)"/> — the integration
    /// worker creates that seam on MainPage, and it must invoke the identical code
    /// path the ControllerBridge poll callback uses (router Handle, then the
    /// settling-aware takeover decision). This harness never calls the router or
    /// FocusManager directly for input, and never sets focus itself except
    /// through the seam.
    ///
    /// DORMANCY: the harness never runs unless the launch arguments contain
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

        private const string HeroPlayButtonName = "HeroPlayButton";
        private const string ExpectedHeroTagAndType = "hero/Button";

        private readonly MainPage _shell;

        public FocusTestHarness(MainPage shell)
        {
            _shell = shell;
        }

        /// <summary>
        /// Dormant entry point. Returns immediately unless
        /// <paramref name="launchArguments"/> contains the "focustest" token
        /// or a flag file named exactly "focustest" exists in the app's
        /// local folder (the msix fallback — a dashboard launch carries no
        /// arguments). Safe to call from MainPage.OnNavigatedTo (before
        /// OnLoaded): the run waits for the shell to be ready before
        /// navigating. Fire-and-forget on the UI thread; the single TEST
        /// result line is written to the log.
        /// </summary>
        public static void RunIfRequested(MainPage shell, string launchArguments)
        {
            if (shell == null) return;
            var tokenArmed = !string.IsNullOrEmpty(launchArguments)
                && launchArguments.IndexOf(LaunchToken, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!tokenArmed && !HasFlagFile()) return;
            var harness = new FocusTestHarness(shell);
            // Always via the dispatcher: this may be called before the shell
            // is loaded, from any thread. The run itself awaits on the UI
            // thread, so Task.Delay continuations stay on it too.
            shell.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                _ = harness.RunLaunchHeroPlayAsync();
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
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                if (folder == null || string.IsNullOrEmpty(folder.Path)) return false;
                return System.IO.File.Exists(System.IO.Path.Combine(folder.Path, LaunchToken));
            }
            catch (Exception)
            {
                return false;
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

        /// <summary>
        /// Phase 0 script, launch → hero Play. Navigates home, waits for the
        /// page's ContentReady plus the arrival settle to finish (bounded
        /// poll, never spins forever), then asserts focus is the hero Play
        /// button, inside the page, with the sidebar guard clear (observed
        /// via the shell proxy: focus is not on a sidebar button and the
        /// strip is not expanded — the guard field itself is private to
        /// MainPage). Writes exactly one result line to the log.
        /// </summary>
        public async Task RunLaunchHeroPlayAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            var passed = false;
            var settleAttempts = 0;
            object focused = null;
            if (_shell != null)
            {
                // 1. Wait for the shell to finish loading (NavigationService
                //    is created in MainPage.OnLoaded, after this harness may
                //    have been armed from OnNavigatedTo).
                var shellReady = false;
                var shellWaitedMs = 0;
                while (shellWaitedMs < ShellReadyTimeoutMs)
                {
                    if (_shell.Navigation != null)
                    {
                        shellReady = true;
                        break;
                    }
                    await Task.Delay(SettlePollIntervalMs).ConfigureAwait(true);
                    shellWaitedMs += SettlePollIntervalMs;
                }
                if (shellReady)
                {
                    // 2. Navigate home (a no-op re-navigation when OnLoaded
                    //    already landed there; required when the shell was
                    //    showing another page).
                    if (FindPage<HomePage>(_shell) == null)
                    {
                        _shell.Navigation.NavigateHome();
                    }
                    // 3. Wait for the settle: page present, content ready,
                    //    focus on the hero Play button. Bounded: the loop
                    //    exits on timeout with a FAIL line, never spins.
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
                    // 4. Sidebar guard proxy: focus must not sit on a
                    //    sidebar button and the strip must not be expanded.
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
            var focusedType = focused != null ? focused.GetType().Name : "null";
            var actualTagAndType = DescribeTagAndType(focused);
            Log.Write("TEST launch-hero-play " + (passed ? "PASS" : "FAIL")
                + " expected=" + ExpectedHeroTagAndType
                + " actual=" + actualTagAndType
                + " focused=" + focusedType
                + " settleAttempts=" + settleAttempts
                + " ms=" + stopwatch.ElapsedMilliseconds);
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

        private static bool HasAncestor<T>(DependencyObject element) where T : DependencyObject
        {
            for (var node = VisualTreeHelper.GetParent(element); node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is T) return true;
            }
            return false;
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
