using System;
using Nuvio.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Nuvio.Views
{
    /// <summary>
    /// Base class for every hosted page (Fix 2: focus infrastructure). Pages
    /// derive from this instead of Page directly so arrival focus, the
    /// entry-keyed back registry and dialog restore share one implementation:
    /// the router owns settling, this class only provides targets, keys and a
    /// readiness signal. It never sets focus itself on load and never touches
    /// the sidebar guard.
    ///
    /// NOTE for the page migration: the XAML root of each derived page must
    /// name this class (e.g. &lt;views:FocusablePage ...&gt;) so the generated
    /// partial agrees with the code-behind base — a XAML root left as
    /// &lt;Page&gt; while the code-behind says FocusablePage fails with CS0263
    /// (partial declarations specify different base classes).
    /// </summary>
    internal abstract class FocusablePage : Page, IArrivalFocus
    {
        /// <summary>
        /// Raised once, when the page's first render/content is ready for
        /// focus (e.g. the Home hero data has loaded). The router's settle
        /// subscribes one-shot; pages with async content call
        /// RaiseContentReady when their arrival target exists. Sync pages
        /// never need to raise it — the settle's synchronous first attempt
        /// already covers them.
        /// </summary>
        internal event Action ContentReady;

        /// <summary>True once RaiseContentReady has run (or the page was ready at navigate time).</summary>
        internal bool IsContentReady { get; private set; }

        private bool _contentReadyRaised;
        private object _navigationParameter;

        /// <summary>The navigation parameter this page was opened with (stored from OnNavigatedTo).</summary>
        internal object NavigationParameter { get { return _navigationParameter; } }

        /// <summary>
        /// Stable identity of this navigation entry: page type + parameter
        /// identity (see NativeInputRouter.EntryIdentity). Computed in
        /// OnNavigatedTo; the back registry is keyed by this, not by page
        /// type, so the Settings root and a Settings section never overwrite
        /// each other. Empty until the first OnNavigatedTo.
        /// </summary>
        internal string EntryKey { get; private set; }

        protected FocusablePage()
        {
            // Single shared arrival recorder: every focus change in the page
            // records the focused control under this entry. This generalizes
            // the old per-page OnGotFocus copies, which the migration deletes.
            GotFocus += OnGotFocusRecordArrival;
        }

        /// <summary>
        /// Marks the page's content ready and raises ContentReady once.
        /// A second call is a no-op: ContentReady fires at most once per
        /// page instance.
        /// </summary>
        protected void RaiseContentReady()
        {
            if (_contentReadyRaised) return;
            _contentReadyRaised = true;
            IsContentReady = true;
            var handler = ContentReady;
            if (handler != null) handler();
        }

        /// <summary>Default arrival target: none. Pages override with their existing TryFocusArrival body.</summary>
        public virtual bool TryFocusArrival()
        {
            return false;
        }

        /// <summary>Default keyed arrival target: none. Pages override with their existing TryFocusKey body.</summary>
        public virtual bool TryFocusKey(string key)
        {
            return false;
        }

        /// <summary>
        /// Stable row key for the row holding <paramref name="focused"/>, or
        /// null when the focused element is not in a remembered row. HomePage
        /// overrides this to return the row key of a GridViewItem ancestor;
        /// the base default is null (no remembered rows).
        /// </summary>
        protected virtual string RowKeyForElement(DependencyObject focused)
        {
            return null;
        }

        /// <summary>
        /// THE single dialog-restore implementation (replaces the per-page
        /// duplicates): back to the element that opened the dialog when it
        /// can still take focus (Control cast first — Focus lives on
        /// Control, not UIElement), else the page's arrival target.
        /// </summary>
        protected bool RestoreDialogInvoker(UIElement invoker)
        {
            var control = invoker as Control;
            if (control != null && control.Focus(FocusState.Programmatic)) return true;
            return TryFocusArrival();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _navigationParameter = e.Parameter;
            EntryKey = GetType().FullName + "|" + NativeInputRouter.EntryIdentity(e.Parameter);
        }

        private void OnGotFocusRecordArrival(object sender, RoutedEventArgs e)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null || string.IsNullOrEmpty(EntryKey)) return;
            for (var node = focused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var button = node as Button;
                if (button == null) continue;
                var tag = button.Tag as string;
                // Hero / OSD buttons carry their Segoe MDL2 glyph in Tag
                // (a single private-use character) — that is an icon, not an
                // arrival identity. Recording it would overwrite the real
                // key (e.g. "hero", "video:&lt;id&gt;") with a glyph the
                // page's TryFocusKey cannot resolve. Single-character tags
                // are never arrival keys in this app; skip them.
                if (!string.IsNullOrEmpty(tag) && tag.Length > 1)
                {
                    // Single funnel (Phase 2): MainPage.RecordArrivalFocusKey
                    // writes the router's registry (the rollback path every
                    // page's legacy settle reads) and, when the flag is on,
                    // the coordinator's registry too — same key format, same
                    // Tag discipline. RowKeyForElement is evaluated once.
                    var rowKey = RowKeyForElement(focused);
                    var shell = MainPage.Current;
                    if (shell != null)
                    {
                        shell.RecordArrivalFocusKey(EntryKey, tag, rowKey);
                    }
                }
                return;
            }
        }
    }
}
