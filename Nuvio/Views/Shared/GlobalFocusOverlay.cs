using System;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;
using Nuvio.Diagnostics;
using Nuvio.Views;

namespace Nuvio.Views.Shared
{
    /// <summary>
    /// Global animated focus overlay: one ring for the whole shell (sidebar, cards,
    /// hero buttons, chips, search field, any interactive element), instead of a
    /// focus visual per template. The shell hosts a single instance on top of the
    /// page root (MainPage.xaml, last child of RootGrid); it follows
    /// FocusManager.GotFocus on its own, so no page wires anything per element.
    ///
    /// Motion: Composition animations on the ring's Visual (Offset +
    /// Opacity) with a cubic-bezier ease-out (0.22, 1, 0.36, 1), ~200 ms.
    /// The ring's Size is layout-driven (Width/Height on the Grid and the
    /// Rectangle): a XAML element's content is drawn at its layout size,
    /// so a Composition Size animation on the element Visual can never
    /// make a 0x0 ring visible — and any layout pass resets the animated
    /// Visual.Size to 0 (that is why build 268 showed no ring anywhere).
    /// Size and RadiusX/RadiusY have no Composition animation type, so
    /// they morph together through a bounded CompositionTarget.Rendering
    /// loop with the same curve and duration, which detaches itself when
    /// done. Every retarget starts from the ring's CURRENT values (the
    /// live layout size and radius), so a fast focus change at the 110 ms
    /// repeat rate glides from mid-flight instead of jumping or restarting
    /// from the target.
    ///
    /// Look (dark theme, TV/game-UI language): a glassy white ring (~90%
    /// white, 2 epx) with a faint white wash (12%) and a soft white DropShadow
    /// glow around it (BlurRadius 10, ~50% white at 0.7 opacity — the old
    /// 14 epx / 35% / 0.55 glow was too faint to see on the TV, so the ring
    /// read as a plain thin line). The app's Accent (#FF3C7BFF) is deliberately NOT the
    /// ring colour: it already means progress bars and selected chips, and
    /// the old blue accent bar on the sidebar was removed for clashing with
    /// the frosted strip; white is the ring language the old card ring used.
    ///
    /// Radius adapts to the focused element: the art's 6 epx corners on cards
    /// (ring 6), the button's own CornerRadius (sidebar 14, hero pills 14,
    /// chips 11, default buttons 2), and a pill (height / 2) for the search
    /// field. The ring sits ON the target's edge on every side (Pad 0): its
    /// 2 epx stroke straddles the element's hard edge, so the two read as
    /// one line instead of a double.
    ///
    /// Visibility rule: the ring shows for gamepad/keyboard focus
    /// (FocusState.Keyboard — D-pad arrives as Keyboard in UWP) and for
    /// programmatic moves once real input has been seen (every focus move in
    /// this app is Programmatic: the router, FocusFirst, FocusSidebar). It
    /// stays hidden for pointer focus (mouse/touch: FocusState.Pointer) and
    /// for the framework's parked launch focus before any input — the same
    /// gate the sidebar expansion uses. It also hides when focus leaves the
    /// interactive tree (Frame, GridView, dialogs in Popups — their bounds
    /// cannot be mapped into the shell) or when the app loses activation.
    ///
    /// Following: the ring re-targets (120 ms) on the focused element's
    /// SizeChanged — event-driven, no per-frame polling — so it tracks the
    /// sidebar's 220 ms expand/collapse (whose buttons change width as the
    /// strip moves). On the enclosing ScrollViewer's ViewChanged it does NOT
    /// re-animate: during a smooth scroll the card moves every frame, and
    /// restarting a 120 ms ease-out each frame made the ring chase with a
    /// lag and then jump to catch up (the shake) — instead it snaps its
    /// offset to the card's exact live position, riding 1:1 with no lag.
    /// MainPage also calls Refresh() when the expand/collapse storyboard
    /// completes. A window resize re-targets through the host's
    /// SizeChanged; the layout size survives layout passes (Width/Height
    /// are explicit values), so the ring never resets to 0.
    ///
    /// Coordinates are physical: every rect is measured with TransformToVisual
    /// against the shell root, the same rule the scroller uses (TransformToVisual
    /// reports RTL-origin rects, so no flow-direction math is needed here).
    /// </summary>
    // NOTE: Border is sealed in UWP (CS0509), and a plain Visual has no
    // Shadow in UWP either (CS1061: Visual.Shadow is WinUI 3 only). The
    // ring is therefore a Rectangle (Shape) hosted in this 0x0 Grid: the
    // Rectangle draws the ring, and a SpriteVisual child of it carries the
    // DropShadow, masked by the Rectangle's own alpha (Shape.GetAlphaMask)
    // so the glow hugs the outline instead of washing the ring's
    // transparent interior. The Grid draws nothing itself. ContentControl
    // was rejected: its default template does not guarantee the ring's
    // Fill/Stroke render, and as a Control it would be tab-focusable by
    // default.
    public sealed class GlobalFocusOverlay : Grid
    {
        private readonly Rectangle _ringRect;
        // --- Appearance -------------------------------------------------
        private static readonly Color RingColor = Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF);
        private const double RingThickness = 2.0;
        // Wash inside the ring: 12% white (was 8% — invisible on the dark
        // UI, so the ring read as a plain thin line instead of glassy white).
        private static readonly Color FillColor = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
        // The ring sits ON the target's edge, not outside it: the 2 epx
        // stroke is centred on the rect boundary, so Pad 0 puts the stroke
        // straddling the element's own hard edge (one line). Pad 2 left a
        // 2 epx gap whose element edge read as a second, parallel line —
        // the "double" in the build 269 photo and video.
        private const double Pad = 0.0;
        private const double CardRadius = 8.0;

        // --- Motion ------------------------------------------------------
        private const double MoveDurationMs = 200;
        private const double FollowDurationMs = 120;
        private const double ShowDurationMs = 150;
        private const double HideDurationMs = 120;
        private const double BezierX1 = 0.22;
        private const double BezierY1 = 1.0;
        private const double BezierX2 = 0.36;
        private const double BezierY2 = 1.0;

        private Compositor _compositor;
        private Visual _visual;
        private CubicBezierEasingFunction _ease;
        private DropShadow _shadow;
        private SpriteVisual _shadowSprite;
        private bool _maskRefreshQueued;
        private FrameworkElement _host;
        private bool _attached;
        private bool _visible;
        private bool _userInputSeen;

        private UIElement _currentElement;
        private ScrollViewer _currentScroller;
        private Rect _targetRect;
        private double _targetRadius;

        // Size + radius morph state (Rendering loop): the ring's size is
        // layout-driven, so both interpolate here on the same curve.
        private bool _morphAnimating;
        private Vector2 _morphFromSize;
        private Vector2 _morphToSize;
        private double _radiusFrom;
        private double _radiusTo;
        private DateTime _morphStart;
        private double _morphDurationMs;

        public GlobalFocusOverlay()
        {
            // A Rectangle (Shape), not a Border: only Shape/Image/TextBlock
            // expose GetAlphaMask, and the glow's DropShadow needs the
            // ring's own alpha as its mask (see EnsureShadow).
            _ringRect = new Rectangle
            {
                Fill = new SolidColorBrush(FillColor),
                Stroke = new SolidColorBrush(RingColor),
                StrokeThickness = RingThickness,
                RadiusX = CardRadius,
                RadiusY = CardRadius,
                Opacity = 0,
                IsHitTestVisible = false,
            };
            Children.Add(_ringRect);
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            Width = 0;
            Height = 0;
            IsHitTestVisible = false;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>
        /// Arms the programmatic-focus gate from the shell's own input gate
        /// (MainPage calls this wherever it sets _userInputSeen: the
        /// controller poll and PreviewKeyDown). The overlay also arms itself
        /// from CoreWindow.KeyDown, so either path is enough.
        /// </summary>
        public void MarkUserInputSeen()
        {
            if (_userInputSeen) return;
            _userInputSeen = true;
            TryShowCurrentFocus();
        }

        /// <summary>Re-resolves the current element and re-targets the ring
        /// (sidebar expand/collapse completion, window resize).</summary>
        public void Refresh()
        {
            if (_visual == null) return;
            if (_currentElement != null && _visible) RetargetCurrent(FollowDurationMs);
            else TryShowCurrentFocus();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_attached) return;
            _attached = true;
            _visual = ElementCompositionPreview.GetElementVisual(_ringRect);
            _compositor = _visual.Compositor;
            _ease = _compositor.CreateCubicBezierEasingFunction(
                new Vector2((float)BezierX1, (float)BezierY1),
                new Vector2((float)BezierX2, (float)BezierY2));
            EnsureShadow();
            _host = VisualTreeHelper.GetParent(this) as FrameworkElement
                ?? Window.Current?.Content as FrameworkElement;
            if (_host != null) _host.SizeChanged += OnHostSizeChanged;
            FocusManager.GotFocus += OnFocusManagerGotFocus;
            FocusManager.LostFocus += OnFocusManagerLostFocus;
            var window = Window.Current;
            if (window != null)
            {
                window.Activated += OnWindowActivated;
                window.SizeChanged += OnWindowSizeChanged;
                if (window.CoreWindow != null) window.CoreWindow.KeyDown += OnCoreWindowKeyDown;
            }
            TryShowCurrentFocus();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_attached) return;
            _attached = false;
            DetachFollowerSubscriptions();
            StopMorphAnimation();
            Windows.UI.Xaml.Media.CompositionTarget.Rendering -= OnMaskRendering;
            _maskRefreshQueued = false;
            if (_host != null) _host.SizeChanged -= OnHostSizeChanged;
            FocusManager.GotFocus -= OnFocusManagerGotFocus;
            FocusManager.LostFocus -= OnFocusManagerLostFocus;
            var window = Window.Current;
            if (window != null)
            {
                window.Activated -= OnWindowActivated;
                window.SizeChanged -= OnWindowSizeChanged;
                if (window.CoreWindow != null) window.CoreWindow.KeyDown -= OnCoreWindowKeyDown;
            }
        }

        private void EnsureShadow()
        {
            if (_compositor == null) return;
            try
            {
                // Soft white halo around the ring (no offset: the glow hugs
                // the ring on every side). Strengthened in the build 269
                // fix: BlurRadius 10 (was 14), ~50% white (was 35%) at
                // 0.7 opacity (was 0.55) — the old glow was too faint to
                // see on the TV at all, so the ring read as a plain thin
                // line instead of glassy glowing white. Still a soft blur,
                // not a hard second line: the peak is ~35% white and it
                // falls off over 10 epx. If the device cannot build it, the
                // ring still renders — the glow is decoration, not the cue.
                //
                // UWP has no Visual.Shadow (that is WinUI 3, CS1061): the
                // shadow lives on a SpriteVisual (SpriteVisual.Shadow,
                // since 14393). The sprite is the ring Rectangle's element
                // child visual, so it inherits the ring Visual's animated
                // Offset/Opacity — but NOT its Size, so SetLayoutSize feeds
                // the sprite the ring's live layout size on every morph
                // tick (and on snap).
                // The sprite carries no Brush: it draws nothing, and the
                // shadow's shape comes from DropShadow.Mask, fed by the
                // ring Rectangle's own alpha (Shape.GetAlphaMask, since
                // 14393). A filled-rectangle mask would wash the ring's
                // transparent interior white — the Toolkit's attached-shadow
                // docs note a plain DropShadow "shows through transparent
                // element" — so the ring-shaped mask is what keeps the
                // glow a halo around the outline instead of a haze over
                // the focused card.
                var shadow = _compositor.CreateDropShadow();
                shadow.BlurRadius = 10;
                shadow.Offset = new Vector3(0, 0, 0);
                shadow.Color = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
                shadow.Opacity = 0.7f;
                _shadow = shadow;
                _shadowSprite = _compositor.CreateSpriteVisual();
                _shadowSprite.Offset = new Vector3(0, 0, 0);
                _shadowSprite.Size = Vector2.Zero;
                _shadowSprite.Shadow = shadow;
                RefreshShadowMask();
                // A hand-in visual draws on top of the element, but the
                // sprite has no Brush, so it paints nothing — only its
                // shadow (from the mask) renders.
                ElementCompositionPreview.SetElementChildVisual(_ringRect, _shadowSprite);
            }
            catch (Exception)
            {
                // No glow on this device; the ring still renders.
                _shadow = null;
                _shadowSprite = null;
            }
        }

        private void RefreshShadowMask()
        {
            if (_shadow == null || _ringRect == null) return;
            try
            {
                // The mask is a snapshot of the last rendered shape, so
                // Retarget re-fetches it on every move. The mask brush
                // carries the shape's alignment/stretch (Learn), so a
                // one-move-old mask is exact for the overwhelmingly common
                // same-size moves and only distorts invisibly under the
                // 14 epx blur on size changes. At first load the ring is
                // 0x0 and the mask is empty; QueueMaskRefresh below fetches
                // it once the ring has rendered at a real size.
                _shadow.Mask = _ringRect.GetAlphaMask();
            }
            catch (Exception)
            {
                // Keep the previous mask (or none); the ring still renders.
            }
        }

        private void QueueMaskRefresh()
        {
            if (_maskRefreshQueued) return;
            _maskRefreshQueued = true;
            Windows.UI.Xaml.Media.CompositionTarget.Rendering += OnMaskRendering;
        }

        private void OnMaskRendering(object sender, object e)
        {
            Windows.UI.Xaml.Media.CompositionTarget.Rendering -= OnMaskRendering;
            _maskRefreshQueued = false;
            RefreshShadowMask();
        }

        private void OnFocusManagerGotFocus(object sender, FocusManagerGotFocusEventArgs e)
        {
            ShowForElement(e.NewFocusedElement as UIElement);
        }

        private async void OnFocusManagerLostFocus(object sender, FocusManagerLostFocusEventArgs e)
        {
            // UWP's FocusManagerLostFocusEventArgs has no NewFocusedElement
            // (only OldFocusedElement + CorrelationId — that member is a
            // WinUI 3 / LosingFocusEventArgs one), so the next element
            // cannot be read from the args. Defer one dispatcher tick
            // instead: the GotFocus of the next element in the same move
            // runs first and re-shows the ring, so this hides only on a
            // real loss of focus (no flicker between two focused
            // elements).
            var dispatcher = Window.Current?.Dispatcher;
            if (dispatcher == null) return;
            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (FocusManager.GetFocusedElement() == null) Hide();
            });
        }

        private void OnCoreWindowKeyDown(CoreWindow sender, KeyEventArgs e)
        {
            // Any key is real input: arm the programmatic-focus gate. The
            // first key also reveals the ring on the element that already
            // holds the parked launch focus (usually the sidebar's Home
            // button), since the user is driving now.
            MarkUserInputSeen();
        }

        private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
        {
            if (e.WindowActivationState == CoreWindowActivationState.Deactivated) Hide();
            else TryShowCurrentFocus();
        }

        private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            Refresh();
        }

        private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
        {
            Refresh();
        }

        private void OnScrollerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            // During a smooth scroll the focused card moves every frame.
            // Re-animating a 120 ms ease-out to the new rect on every
            // ViewChanged made the ring chase the card with a lag and then
            // jump to catch up when the scroll stopped (the shake in the
            // build 269 video). The size does not change while scrolling,
            // so snap the offset to the card's exact live position instead:
            // the ring rides the card 1:1 with no lag. Only if the size or
            // radius changed (sidebar expanding mid-scroll) fall back to
            // the animated retarget. The shadow sprite is the ring's child
            // visual, so it inherits the snapped offset for free.
            if (_currentElement == null || !_visible || _visual == null) return;
            Rect rect;
            double radius;
            if (!TryResolveTarget(_currentElement, out rect, out radius)) { Hide(); return; }
            if (Math.Abs(rect.Width - _targetRect.Width) < 0.5
                && Math.Abs(rect.Height - _targetRect.Height) < 0.5
                && Math.Abs(radius - _targetRadius) < 0.5)
            {
                _targetRect = rect;
                _targetRadius = radius;
                _visual.StopAnimation("Offset");
                _visual.Offset = new Vector3((float)rect.X, (float)rect.Y, 0);
                return;
            }
            RetargetCurrent(FollowDurationMs);
        }

        private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e)
        {
            RetargetCurrent(FollowDurationMs);
        }

        private void TryShowCurrentFocus()
        {
            ShowForElement(FocusManager.GetFocusedElement() as UIElement);
        }

        private void ShowForElement(UIElement element)
        {
            if (element == null) { Hide(); return; }
            // Pointer focus (mouse/touch) never shows the ring: the pointer
            // has its own hover states, and a ring chasing clicks would
            // fight them.
            // FocusState lives on Control in UWP, not on UIElement.
            // (Every focusable element is a Control — Focus() is a
            // Control method — so the cast succeeds for anything that
            // actually holds focus; the fallback is defensive only.)
            var focusState = (element as Control)?.FocusState ?? FocusState.Unfocused;
            if (focusState == FocusState.Pointer) { Hide(); return; }
            if (focusState == FocusState.Unfocused) { Hide(); return; }
            // Every move in this app is Programmatic (router, FocusFirst,
            // FocusSidebar), so the gate is the only thing separating the
            // framework's parked launch focus from a real D-pad move.
            if (focusState == FocusState.Programmatic && !_userInputSeen) { Hide(); return; }
            ShowFor(element, MoveDurationMs);
        }

        private void ShowFor(UIElement element, double durationMs)
        {
            if (_visual == null || _compositor == null) return;
            Rect rect;
            double radius;
            if (!TryResolveTarget(element, out rect, out radius)) { Hide(); return; }
            DetachFollowerSubscriptions();
            _currentElement = element;
            AttachFollowerSubscriptions(element);
            var wasVisible = _visible;
            Retarget(rect, radius, durationMs);
            _visible = true;
            // One line per show transition (not per move): the rect the ring
            // was given and the layout size it actually got — the pair that
            // diagnoses a future invisible-ring report at a glance.
            if (!wasVisible) Log.Write("FocusOverlay: show " + element.GetType().Name
                + " rect=" + rect + " layout=" + Width + "x" + Height);
        }

        private void RetargetCurrent(double durationMs)
        {
            if (_currentElement == null || !_visible) return;
            Rect rect;
            double radius;
            if (!TryResolveTarget(_currentElement, out rect, out radius)) { Hide(); return; }
            Retarget(rect, radius, durationMs);
        }

        private void Hide()
        {
            DetachFollowerSubscriptions();
            _currentElement = null;
            if (!_visible) return;
            _visible = false;
            Log.Write("FocusOverlay: hide");
            // Keep the last rect: a quick re-show glides from where the ring
            // already is instead of growing from the origin.
            AnimateOpacity(0, HideDurationMs);
        }

        private void Retarget(Rect rect, double radius, double durationMs)
        {
            if (_visual == null || _compositor == null) return;
            var targetOffset = new Vector3((float)rect.X, (float)rect.Y, 0);
            var targetSize = new Vector2((float)rect.Width, (float)rect.Height);
            // The ring's size is layout-driven: the snap condition is the
            // layout size (Width/Height), never _visual.Size. A Composition
            // Size animation on the element Visual cannot draw the ring —
            // the Rectangle's Fill/Stroke are rasterised at its layout
            // size, which was 0x0, so build 268 showed no ring anywhere —
            // and any layout pass resets the animated Visual.Size to 0.
            var currentSize = new Vector2((float)Width, (float)Height);
            if (currentSize == Vector2.Zero)
            {
                // First placement: snap into place and fade in only —
                // growing from the origin would read as a jump.
                _visual.Offset = targetOffset;
                SetLayoutSize(targetSize);
                _ringRect.RadiusX = radius;
                _ringRect.RadiusY = radius;
                _targetRect = rect;
                _targetRadius = radius;
                RefreshShadowMask();
                QueueMaskRefresh();
                AnimateOpacity(1, ShowDurationMs);
                return;
            }
            // A scroll settle can re-fire with the same rect; restarting the
            // animations for it would stutter the glide.
            if (_visible
                && Math.Abs(rect.X - _targetRect.X) < 0.5
                && Math.Abs(rect.Y - _targetRect.Y) < 0.5
                && Math.Abs(rect.Width - _targetRect.Width) < 0.5
                && Math.Abs(rect.Height - _targetRect.Height) < 0.5
                && Math.Abs(radius - _targetRadius) < 0.5)
            {
                return;
            }
            _targetRect = rect;
            _targetRadius = radius;
            RefreshShadowMask();
            // Stop the running animations first: the property keeps its live
            // animated value, which becomes the start keyframe below — so a
            // retarget at the 110 ms repeat rate continues from mid-flight
            // instead of jumping back to an old endpoint.
            _visual.StopAnimation("Offset");
            _visual.StopAnimation("Opacity");
            var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.InsertKeyFrame(0f, _visual.Offset);
            offsetAnimation.InsertKeyFrame(1f, targetOffset, _ease);
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(durationMs);
            _visual.StartAnimation("Offset", offsetAnimation);
            AnimateOpacity(1, ShowDurationMs);
            // Size and radius morph through the bounded Rendering loop:
            // Width/Height are layout values, interpolated on the same
            // cubic-bezier curve and duration. No Composition Size
            // animation on the element Visual — layout owns that size.
            AnimateMorph(currentSize, targetSize, _ringRect.RadiusX, radius, durationMs);
        }

        /// <summary>Drives the ring's size through layout (Width/Height on
        /// the Grid and the Rectangle) and feeds the same live size to the
        /// shadow sprite, which is a pure Composition visual and takes its
        /// size from here, not from _visual.Size.</summary>
        private void SetLayoutSize(Vector2 size)
        {
            Width = size.X;
            Height = size.Y;
            _ringRect.Width = size.X;
            _ringRect.Height = size.Y;
            if (_shadowSprite != null) _shadowSprite.Size = size;
        }

        private void AnimateOpacity(float to, double durationMs)
        {
            if (_visual == null || _compositor == null) return;
            var current = _visual.Opacity;
            if (Math.Abs(current - to) < 0.01f) return;
            _visual.StopAnimation("Opacity");
            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, current);
            animation.InsertKeyFrame(1f, to, _ease);
            animation.Duration = TimeSpan.FromMilliseconds(durationMs);
            _visual.StartAnimation("Opacity", animation);
        }

        private void AttachFollowerSubscriptions(UIElement element)
        {
            var scroller = FindEnclosingScrollViewer(element);
            if (scroller != null)
            {
                _currentScroller = scroller;
                scroller.ViewChanged += OnScrollerViewChanged;
            }
            var frameworkElement = element as FrameworkElement;
            if (frameworkElement != null) frameworkElement.SizeChanged += OnTargetSizeChanged;
        }

        private void DetachFollowerSubscriptions()
        {
            if (_currentScroller != null)
            {
                _currentScroller.ViewChanged -= OnScrollerViewChanged;
                _currentScroller = null;
            }
            var frameworkElement = _currentElement as FrameworkElement;
            if (frameworkElement != null) frameworkElement.SizeChanged -= OnTargetSizeChanged;
        }

        private static ScrollViewer FindEnclosingScrollViewer(UIElement element)
        {
            for (var node = element as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var scroller = node as ScrollViewer;
                if (scroller != null) return scroller;
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Target resolution: physical rect + radius for the focused element.
        // ------------------------------------------------------------------

        private bool TryResolveTarget(UIElement element, out Rect rect, out double radius)
        {
            rect = default(Rect);
            radius = CardRadius;
            if (element == null || !IsInteractiveElement(element)) return false;
            // 1. A card: the ring wraps the art only (web: .card.focused
            // .card-art), never the title below it.
            GridViewItem cardItem = null;
            for (var node = element as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
            {
                cardItem = node as GridViewItem;
                if (cardItem != null) break;
            }
            if (cardItem != null && ResolveCardTarget(cardItem, out rect, out radius)) return true;
            // 2. A text field: the ring wraps the field row (magnifier + box).
            var textBox = element as TextBox;
            var passwordBox = element as PasswordBox;
            if (textBox != null && ResolveTextBoxTarget(textBox, out rect, out radius)) return true;
            if (passwordBox != null && ResolveTextBoxTarget(passwordBox, out rect, out radius)) return true;
            // 3. Anything else: the nearest rounded Border/Control around it.
            return ResolveRoundedTarget(element, out rect, out radius);
        }

        private static bool IsInteractiveElement(UIElement element)
        {
            for (var node = element as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is ButtonBase || node is TextBox || node is PasswordBox
                    || node is GridViewItem || node is ComboBox || node is Slider) return true;
                // The frame, the page and the tree root are not interactive:
                // focus parked on them (or inside a Popup, whose walk never
                // reaches the page) hides the ring.
                if (node is Page || node is Frame) return false;
            }
            return false;
        }

        private bool ResolveCardTarget(GridViewItem item, out Rect rect, out double radius)
        {
            rect = default(Rect);
            radius = CardRadius;
            // The template root carries the focus zoom (ScaleTransform 1.06
            // over 180 ms on the template RootGrid, RenderTransformOrigin
            // 0.5,0.5), but GotFocus fires as the zoom STARTS — so measuring
            // the live scaled bounds gives a ~1.0 rect while the art settles
            // at 1.06: the ring would sit ~2-3 epx inside the settled art
            // (the art's edges end up outside the ring), reading as a
            // double line, and a different size on every move as the
            // measured scale varies with input timing (the shake). Target
            // the SETTLED 1.06 art instead: the live bounds' centre is the
            // image of the layout centre for any uniform scale, so the
            // settled rect is the same centre with the layout size x 1.06.
            // The card then zooms into a ring that is already exactly
            // right, and re-measuring it later returns the identical rect.
            FrameworkElement templateRoot = null;
            var count = VisualTreeHelper.GetChildrenCount(item);
            for (var i = 0; i < count; i++)
            {
                templateRoot = VisualTreeHelper.GetChild(item, i) as FrameworkElement;
                if (templateRoot != null) break;
            }
            if (templateRoot == null) return false;
            Rect bounds;
            if (!TryGetBounds(templateRoot, out bounds)) return false;
            if (templateRoot.ActualHeight <= 0 || bounds.Height <= 0) return false;
            // Art height by the same rule as the old ring (CardArtHeightConverter:
            // landscape 90, square 104, compact 102, large 156, extraLarge 180,
            // default 132, Continue cards 99). The zoom is uniform, so the art
            // occupies the top artHeight * 1.06 of the settled item.
            double artHeight = bounds.Height;
            try
            {
                var converter = new CardArtHeightConverter();
                var value = converter.Convert(item.Content, typeof(double), null, string.Empty);
                artHeight = Convert.ToDouble(value);
            }
            catch
            {
                // Keep the whole item height as the fallback.
            }
            const double settledZoom = 1.06;
            var centerX = bounds.X + bounds.Width / 2;
            var centerY = bounds.Y + bounds.Height / 2;
            var settledWidth = templateRoot.ActualWidth * settledZoom;
            var settledHeight = templateRoot.ActualHeight * settledZoom;
            rect = Inflate(new Rect(centerX - settledWidth / 2,
                                    centerY - settledHeight / 2,
                                    settledWidth, artHeight * settledZoom), Pad);
            radius = 6 + Pad;
            return true;
        }

        private bool ResolveTextBoxTarget(FrameworkElement box, out Rect rect, out double radius)
        {
            rect = default(Rect);
            radius = CardRadius;
            Rect bounds;
            if (!TryGetBounds(box, out bounds)) return false;
            // The field row holds the magnifier and the box side by side:
            // union the same-row siblings so the ring wraps the whole field,
            // not only the text area.
            var parent = VisualTreeHelper.GetParent(box) as Grid;
            if (parent != null)
            {
                var row = Grid.GetRow(box);
                var count = VisualTreeHelper.GetChildrenCount(parent);
                var union = bounds;
                var found = false;
                for (var i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i) as FrameworkElement;
                    if (child == null || child == box) continue;
                    if (Grid.GetRow(child) != row) continue;
                    Rect childBounds;
                    if (TryGetBounds(child, out childBounds))
                    {
                        union = Union(union, childBounds);
                        found = true;
                    }
                }
                if (found) bounds = union;
            }
            rect = Inflate(bounds, Pad);
            radius = rect.Height / 2;
            return true;
        }

        private bool ResolveRoundedTarget(UIElement element, out Rect rect, out double radius)
        {
            rect = default(Rect);
            radius = CardRadius;
            Rect elementBounds;
            if (!TryGetBounds(element, out elementBounds)) return false;
            // Nearest Border/Control with a CornerRadius, not much larger than
            // the focused element itself (so a rounded page panel never
            // swallows a small button). For buttons the candidate is the
            // button itself: sidebar 14, hero pills 14, chips 11, default 2.
            for (var node = element as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
            {
                double corner = 0;
                FrameworkElement candidate = null;
                var border = node as Border;
                if (border != null && border.CornerRadius.TopLeft > 0)
                {
                    corner = border.CornerRadius.TopLeft;
                    candidate = border;
                }
                else
                {
                    var control = node as Control;
                    if (control != null && control.CornerRadius.TopLeft > 0)
                    {
                        corner = control.CornerRadius.TopLeft;
                        candidate = control;
                    }
                }
                if (candidate == null) continue;
                Rect candidateBounds;
                if (!TryGetBounds(candidate, out candidateBounds)) return false;
                if (candidateBounds.Width > elementBounds.Width + 24
                    || candidateBounds.Height > elementBounds.Height + 24) continue;
                rect = Inflate(candidateBounds, Pad);
                radius = corner + Pad;
                return true;
            }
            rect = Inflate(elementBounds, Pad);
            radius = CardRadius;
            return true;
        }

        private bool TryGetBounds(UIElement element, out Rect bounds)
        {
            bounds = default(Rect);
            var frameworkElement = element as FrameworkElement;
            if (_host == null || frameworkElement == null) return false;
            if (frameworkElement.ActualWidth <= 0 || frameworkElement.ActualHeight <= 0) return false;
            try
            {
                // Cross-tree elements (dialogs live in Popups, not under the
                // shell root) throw here — the ring hides instead of landing
                // at a bogus rect.
                var transform = frameworkElement.TransformToVisual(_host);
                bounds = transform.TransformBounds(new Rect(0, 0, frameworkElement.ActualWidth, frameworkElement.ActualHeight));
            }
            catch
            {
                return false;
            }
            return !(double.IsNaN(bounds.X) || double.IsNaN(bounds.Y)
                || bounds.Width <= 0 || bounds.Height <= 0);
        }

        private static Rect Inflate(Rect rect, double pad)
        {
            return new Rect(rect.X - pad, rect.Y - pad, rect.Width + pad * 2, rect.Height + pad * 2);
        }

        private static Rect Union(Rect a, Rect b)
        {
            var x = Math.Min(a.X, b.X);
            var y = Math.Min(a.Y, b.Y);
            var right = Math.Max(a.X + a.Width, b.X + b.Width);
            var bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
            return new Rect(x, y, right - x, bottom - y);
        }

        // ------------------------------------------------------------------
        // Size + radius morph: neither has a Composition animation type on
        // a XAML element (Size belongs to layout; RadiusX/RadiusY are plain
        // properties), so both interpolate on the same cubic-bezier curve
        // through a bounded Rendering loop that detaches itself when the
        // morph finishes (or when a new retarget restarts it from the live
        // size and radius). Setting Width/Height every tick is a layout
        // pass per frame, but the subtree is one Grid + one Rectangle, so
        // the cost is negligible for a 200 ms glide.
        // ------------------------------------------------------------------

        private void AnimateMorph(Vector2 sizeFrom, Vector2 sizeTo, double radiusFrom, double radiusTo, double durationMs)
        {
            var sizeDelta = Math.Abs(sizeTo.X - sizeFrom.X) + Math.Abs(sizeTo.Y - sizeFrom.Y);
            if (sizeDelta < 1 && Math.Abs(radiusFrom - radiusTo) < 0.5)
            {
                SetLayoutSize(sizeTo);
                _ringRect.RadiusX = radiusTo;
                _ringRect.RadiusY = radiusTo;
                return;
            }
            _morphFromSize = sizeFrom;
            _morphToSize = sizeTo;
            _radiusFrom = radiusFrom;
            _radiusTo = radiusTo;
            _morphStart = DateTime.UtcNow;
            _morphDurationMs = durationMs;
            if (!_morphAnimating)
            {
                _morphAnimating = true;
                Windows.UI.Xaml.Media.CompositionTarget.Rendering += OnMorphRendering;
            }
        }

        private void OnMorphRendering(object sender, object e)
        {
            var elapsed = (DateTime.UtcNow - _morphStart).TotalMilliseconds;
            var t = elapsed / _morphDurationMs;
            if (t >= 1)
            {
                SetLayoutSize(_morphToSize);
                _ringRect.RadiusX = _radiusTo;
                _ringRect.RadiusY = _radiusTo;
                StopMorphAnimation();
                // The mask was fetched from the pre-move shape: re-fetch it
                // now that the ring sits at its settled size, so the glow
                // hugs the final outline exactly.
                RefreshShadowMask();
                return;
            }
            var eased = EvaluateCubicBezier(Math.Max(0, t), BezierX1, BezierY1, BezierX2, BezierY2);
            var width = _morphFromSize.X + (_morphToSize.X - _morphFromSize.X) * eased;
            var height = _morphFromSize.Y + (_morphToSize.Y - _morphFromSize.Y) * eased;
            SetLayoutSize(new Vector2((float)width, (float)height));
            var radius = _radiusFrom + (_radiusTo - _radiusFrom) * eased;
            _ringRect.RadiusX = radius;
            _ringRect.RadiusY = radius;
        }

        private void StopMorphAnimation()
        {
            if (!_morphAnimating) return;
            _morphAnimating = false;
            Windows.UI.Xaml.Media.CompositionTarget.Rendering -= OnMorphRendering;
        }

        private static double EvaluateCubicBezier(double t, double x1, double y1, double x2, double y2)
        {
            // Newton-Raphson on the x curve (5 iterations), then the y curve.
            var u = t;
            for (var i = 0; i < 5; i++)
            {
                var x = Cubic(u, x1, x2) - t;
                var dx = CubicDerivative(u, x1, x2);
                if (Math.Abs(dx) < 1e-6) break;
                u -= x / dx;
                if (u < 0) u = 0;
                else if (u > 1) u = 1;
            }
            return Cubic(u, y1, y2);
        }

        private static double Cubic(double t, double a, double b)
        {
            var u = 1 - t;
            return 3 * u * u * t * a + 3 * u * t * t * b + t * t * t;
        }

        private static double CubicDerivative(double t, double a, double b)
        {
            var u = 1 - t;
            return 3 * u * u * a + 6 * u * t * (b - a) + 3 * t * t * (1 - b);
        }
    }
}
