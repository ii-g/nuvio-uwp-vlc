using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nuvio.Diagnostics;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Nuvio.Views.Shared
{
    /// <summary>
    /// Focus ring shine, compositor edition. The ring used to be a XAML
    /// LinearGradientBrush whose 7 stops were slid by DoubleAnimations
    /// with EnableDependentAnimation: dependent animations run on the UI
    /// thread, and a Forever one on the focused element kept the UI
    /// thread busy every frame — on Xbox Developer Mode, whose resource
    /// budget is smaller than retail mode, that starved input dispatch
    /// and the scroll slide, so the whole app lagged (~1 s per move).
    /// This brush is the same gradient (diagonal, white peaks at 85%
    /// alpha alternating with 15% valleys, period 1.0) built as a
    /// CompositionLinearGradientBrush, with each stop's Offset driven by
    /// a Forever ScalarKeyFrameAnimation on the compositor — the same
    /// mechanism BackdropFadeBrush already uses for its fade stops.
    /// The UI thread pays nothing per frame, so Developer Mode stays
    /// smooth, and the shine keeps visibly traveling. The ring's static
    /// appearance is unchanged: the Border still owns thickness (3),
    /// corner radius, Margin -1 and Opacity, and the VisualState
    /// storyboards still animate Opacity/Scale (both compositor).
    /// On failure the brush falls back to a static solid ring instead
    /// of crashing the page.
    ///
    /// Restart on focus: the old storyboard ran its stop animations
    /// only in the Focused/PointerFocused states, so the sweep
    /// restarted from the same base offsets every time an element
    /// gained focus — the bright spot always started at the same
    /// corner and swept. The compositor animations instead start in
    /// OnConnected and run Forever, so without a restart the shine sat
    /// at a random clock phase when focus arrived and the sweep felt
    /// slow and different (same 3 s speed, different phase). Setting
    /// the RestartOnFocus attached property on a FocusRing Border
    /// replays the old restart: when the focusable control that owns
    /// the template gains focus, the brush stops and re-starts all 7
    /// stop animations from the base offsets. That is a one-off
    /// compositor action, not per-frame work, so the lag fix stands.
    ///
    /// The owner type must stay public: the runtime XAML type system
    /// resolves the attached property through this type, and an
    /// internal owner breaks the assignment at page load
    /// (XamlParseException, app crash on startup). Per-Border
    /// subscription state lives in a ConditionalWeakTable, never in
    /// an attached DependencyProperty: a private nested class cannot
    /// be a DP property type, and registering one breaks the type
    /// initializer the first time the property is touched. Any
    /// subscription failure is swallowed and logged so page load can
    /// never crash on the ring again.
    /// </summary>
    public sealed class FocusRingBrush : XamlCompositionBrushBase
    {
        private const double AnimationDurationMs = 3000;

        // Same 7-stop gradient as the old XAML ring: even stops are
        // peaks (#D9FFFFFF), odd stops are valleys (#26FFFFFF).
        private static readonly float[] StopOffsets = { -1f, -0.5f, 0f, 0.5f, 1f, 1.5f, 2f };

        private static readonly Windows.UI.Color Peak =
            Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);

        private static readonly Windows.UI.Color Valley =
            Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);

        private Compositor _compositor;
        private CompositionLinearGradientBrush _gradientBrush;
        private CompositionColorGradientStop[] _stops;

        /// <summary>
        /// Attached property for a FocusRing Border: when true, the
        /// shine sweep restarts from the base offsets whenever the
        /// focusable control that owns the template gains focus.
        /// </summary>
        public static readonly DependencyProperty RestartOnFocusProperty =
            DependencyProperty.RegisterAttached(
                "RestartOnFocus",
                typeof(bool),
                typeof(FocusRingBrush),
                new PropertyMetadata(false, OnRestartOnFocusChanged));

        public static bool GetRestartOnFocus(DependencyObject obj)
        {
            return (bool)obj.GetValue(RestartOnFocusProperty);
        }

        public static void SetRestartOnFocus(DependencyObject obj, bool value)
        {
            obj.SetValue(RestartOnFocusProperty, value);
        }

        // Per-Border subscription state, unhooked on Unload so recycled
        // GridView items never leak a GotFocus handler. Kept in a
        // ConditionalWeakTable, not an attached DependencyProperty:
        // the subscription type is private, and a private type cannot
        // be a DP property type (registering one breaks the type
        // initializer on first touch of this class).
        private static readonly ConditionalWeakTable<Border, FocusRestartSubscription> RestartSubscriptions =
            new ConditionalWeakTable<Border, FocusRestartSubscription>();

        private static void OnRestartOnFocusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var border = d as Border;
            if (border == null) return;
            if ((bool)e.NewValue)
            {
                border.Loaded += RingBorder_Loaded;
                border.Unloaded += RingBorder_Unloaded;
            }
            else
            {
                border.Loaded -= RingBorder_Loaded;
                border.Unloaded -= RingBorder_Unloaded;
                DetachRestart(border);
            }
        }

        private static void RingBorder_Loaded(object sender, RoutedEventArgs e)
        {
            // Never let the shine subscription crash page load: a
            // failure here must stay a logged no-op, not an exception
            // escaping into XAML parsing.
            try
            {
                AttachRestart((Border)sender);
            }
            catch (Exception ex)
            {
                Log.Write("FocusRingBrush attach failed: " + ex.Message);
            }
        }

        private static void RingBorder_Unloaded(object sender, RoutedEventArgs e)
        {
            DetachRestart((Border)sender);
        }

        private static void AttachRestart(Border border)
        {
            DetachRestart(border);
            var owner = FindFocusableOwner(border);
            if (owner == null) return;
            var subscription = new FocusRestartSubscription(border, owner);
            RestartSubscriptions.Add(border, subscription);
            owner.GotFocus += subscription.OnOwnerGotFocus;
            // Focus may already be here (restored before load): restart
            // now so the first sweep starts from the base corner.
            if (owner.FocusState != FocusState.Unfocused)
            {
                subscription.RestartRing();
            }
        }

        private static void DetachRestart(Border border)
        {
            if (!RestartSubscriptions.TryGetValue(border, out var subscription)) return;
            subscription.Detach();
            RestartSubscriptions.Remove(border);
        }

        private static Control FindFocusableOwner(Border border)
        {
            // Walk the visual tree to the first Control ancestor.
            // For a template part this is the templated control
            // itself (GridViewItem for cards/episodes, Button for
            // hero/details/sidebar); for DataTemplate-hosted rings
            // it is the nearest focusable container. Note: the
            // owner cannot be read from a template-parent property
            // here (that property exists only in WPF, not UWP) —
            // the visual-tree walk is the only way to resolve it.
            DependencyObject current = VisualTreeHelper.GetParent(border);
            while (current != null)
            {
                if (current is Control control)
                {
                    return control;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private sealed class FocusRestartSubscription
        {
            private readonly Border _ring;
            private readonly Control _owner;

            public FocusRestartSubscription(Border ring, Control owner)
            {
                _ring = ring;
                _owner = owner;
            }

            public void Detach()
            {
                _owner.GotFocus -= OnOwnerGotFocus;
            }

            public void OnOwnerGotFocus(object sender, RoutedEventArgs e)
            {
                RestartRing();
            }

            public void RestartRing()
            {
                if (_ring.BorderBrush is FocusRingBrush brush)
                {
                    brush.Restart();
                }
            }
        }

        /// <summary>
        /// Restarts the shine sweep from the base offsets: stops and
        /// re-starts all 7 stop animations, so the bright spot starts
        /// at the same corner and sweeps, exactly like the old
        /// Focused-state storyboard did. One-off compositor work;
        /// no UI-thread cost per frame.
        /// </summary>
        public void Restart()
        {
            if (_stops == null || _compositor == null) return;
            try
            {
                for (int i = 0; i < _stops.Length; i++)
                {
                    _stops[i].StopAnimation("Offset");
                    StartShineAnimation(_stops[i], StopOffsets[i]);
                }
            }
            catch (Exception ex)
            {
                Log.Write("FocusRingBrush restart failed: " + ex.Message);
            }
        }

        protected override void OnConnected()
        {
            base.OnConnected();
            _compositor = null;
            try
            {
                _compositor = Window.Current.Compositor;
                _gradientBrush = _compositor.CreateLinearGradientBrush();
                // Relative mapping: (0,0)-(1,1) spans the Border's
                // bounds, exactly like the old XAML brush.
                _gradientBrush.MappingMode = CompositionMappingMode.Relative;
                _gradientBrush.StartPoint = new Vector2(0f, 0f);
                _gradientBrush.EndPoint = new Vector2(1f, 1f);
                _stops = new CompositionColorGradientStop[StopOffsets.Length];
                for (int i = 0; i < StopOffsets.Length; i++)
                {
                    var stop = _compositor.CreateColorGradientStop(
                        StopOffsets[i], (i % 2 == 0) ? Peak : Valley);
                    _gradientBrush.ColorStops.Add(stop);
                    _stops[i] = stop;
                    StartShineAnimation(stop, StopOffsets[i]);
                }
                CompositionBrush = _gradientBrush;
            }
            catch (Exception ex)
            {
                // Never crash on the ring: fall back to a static solid
                // ring, and if even that fails the ring is invisible.
                Log.Write("FocusRingBrush failed: " + ex.Message);
                DisposeBrushes();
                TryFallback();
            }
        }

        private void StartShineAnimation(CompositionColorGradientStop stop, float from)
        {
            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(AnimationDurationMs);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            // Linear travel: +1.0 per loop. The pattern repeats every
            // 1.0, so the loop restart is seamless.
            animation.InsertKeyFrame(0f, from);
            animation.InsertKeyFrame(1f, from + 1f);
            stop.StartAnimation("Offset", animation);
        }

        protected override void OnDisconnected()
        {
            base.OnDisconnected();
            CompositionBrush = null;
            DisposeBrushes();
        }

        private void TryFallback()
        {
            try
            {
                if (_compositor == null) return;
                CompositionBrush = _compositor.CreateColorBrush(
                    Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            }
            catch (Exception ex)
            {
                Log.Write("FocusRingBrush fallback failed: " + ex.Message);
                CompositionBrush = null;
            }
        }

        private void DisposeBrushes()
        {
            _gradientBrush?.Dispose();
            _gradientBrush = null;
            _stops = null;
        }
    }
}
