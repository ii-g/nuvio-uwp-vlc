using System;
using System.Diagnostics;
using System.Numerics;
using Nuvio.Diagnostics;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
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
    /// The shine runs only on the focused ring. It used to start in
    /// OnConnected and run Forever on every realized ring — hundreds of
    /// perpetual animations (Home ~350-700, Catalog ~250-630) of which
    /// exactly one was ever visible, since every ring is Opacity 0
    /// until focused and Composition does not pause animations on
    /// transparent visuals. That baseline saturated the compositor on
    /// Xbox Developer Mode, so the shine itself slowed down and input
    /// response lagged, then recovered when the transient load cleared.
    /// Now the UniformSpeed hookup subscribes the ring to its focusable
    /// ancestor's GotFocus/LostFocus: the shine starts on focus gain
    /// and stops on focus loss, so the perpetual load is 7 animations
    /// (the single focused ring) instead of hundreds. Unfocused rings
    /// are invisible anyway, so nothing on screen changes. A ring with
    /// no focusable ancestor falls back to animating always, so the
    /// shine still works rather than silently disappearing.
    ///
    /// The shine is one continuous sweep and is never restarted from
    /// the base offsets: a ring that starts on focus gain seeks to the
    /// current global phase (a static stopwatch, elapsed % 3000 ms) via
    /// a negative DelayTime, so the sweep keeps its phase when focus
    /// moves from one element to the next.
    ///
    /// Uniform perimeter speed: the gradient is diagonal across a
    /// rectangle, so with plain linear offset travel the bright spot
    /// moves at 2W per unit offset on the horizontal edges and 2H per
    /// unit offset on the vertical ones — on a 2:3 card it is 1.5x
    /// faster down the sides than across the top and bottom, reading
    /// as "slow then fast". Setting the UniformSpeed attached property
    /// on a FocusRing Border fixes this from the Border's live size:
    /// each loop spends W/(W+H) of its time on the first half-offset
    /// (one horizontal edge) and H/(W+H) on the second (one vertical
    /// edge), so the spot covers both edges at the same speed. The
    /// fraction is re-read on Loaded and SizeChanged only — never on
    /// focus — and the reconfiguration is a size-change event, not a
    /// restart. Keyframes use an explicit linear easing function so
    /// the interpolation is linear by construction.
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

        // Global phase clock: one continuous sweep shared by every
        // ring. A ring that starts on focus gain seeks to the current
        // global phase instead of restarting from the base offsets.
        private static readonly Stopwatch _phaseClock = Stopwatch.StartNew();

        private Compositor _compositor;
        private CompositionLinearGradientBrush _gradientBrush;
        private CompositionColorGradientStop[] _stops;

        // Fraction of each loop spent on the first half-offset (one
        // horizontal edge). Default 0.5 reproduces the old two-keyframe
        // timing exactly until a live size is known.
        private float _horizontalFraction = 0.5f;

        // True while the shine should be running (this ring's owner has
        // focus, or no focusable owner was found). Set by StartShine /
        // StopShine; OnConnected honours it so the shine starts even if
        // focus was gained before the brush connected.
        private bool _shineRequested;

        // Attached: the focusable ancestor (GridViewItem/Button) whose
        // focus drives this ring's shine. Stored on the Border so
        // Unloaded can unsubscribe from it.
        private static readonly DependencyProperty FocusOwnerProperty =
            DependencyProperty.RegisterAttached(
                "FocusOwner",
                typeof(Control),
                typeof(FocusRingBrush),
                new PropertyMetadata(null));

        private static Control GetFocusOwner(DependencyObject obj)
        {
            return (Control)obj.GetValue(FocusOwnerProperty);
        }

        private static void SetFocusOwner(DependencyObject obj, Control value)
        {
            obj.SetValue(FocusOwnerProperty, value);
        }

        /// <summary>
        /// Attached property for a FocusRing Border: when true, the
        /// shine travels at constant speed along the ring's perimeter,
        /// timed from the Border's live size (see the class comment),
        /// and runs only while the ring's focusable ancestor has focus.
        /// Hooks Loaded and SizeChanged for the size, and the ancestor's
        /// GotFocus/LostFocus for the shine — never a restart on focus,
        /// the sweep resumes at the global phase.
        /// </summary>
        public static readonly DependencyProperty UniformSpeedProperty =
            DependencyProperty.RegisterAttached(
                "UniformSpeed",
                typeof(bool),
                typeof(FocusRingBrush),
                new PropertyMetadata(false, OnUniformSpeedChanged));

        public static bool GetUniformSpeed(DependencyObject obj)
        {
            return (bool)obj.GetValue(UniformSpeedProperty);
        }

        public static void SetUniformSpeed(DependencyObject obj, bool value)
        {
            obj.SetValue(UniformSpeedProperty, value);
        }

        private static void OnUniformSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            try
            {
                var border = d as Border;
                if (border == null) return;
                if ((bool)e.NewValue)
                {
                    border.Loaded += RingBorder_Loaded;
                    border.SizeChanged += RingBorder_SizeChanged;
                    border.Unloaded += RingBorder_Unloaded;
                    // Set after load (e.g. in code): Loaded already
                    // fired, so hook up now instead of waiting.
                    if (border.IsLoaded)
                    {
                        SyncRing(border);
                    }
                }
                else
                {
                    border.Loaded -= RingBorder_Loaded;
                    border.SizeChanged -= RingBorder_SizeChanged;
                    border.Unloaded -= RingBorder_Unloaded;
                    UnhookFocus(border);
                }
            }
            catch (Exception ex)
            {
                // Never crash page load on a shine hookup failure.
                Log.Write("FocusRingBrush UniformSpeed hookup failed: " + ex.Message);
            }
        }

        private static void RingBorder_Loaded(object sender, RoutedEventArgs e)
        {
            SyncRing((Border)sender);
        }

        private static void RingBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            SyncRingSize((Border)sender);
        }

        private static void RingBorder_Unloaded(object sender, RoutedEventArgs e)
        {
            // Unsubscribe focus only. Loaded/SizeChanged/Unloaded stay
            // attached: a recycled container reloads the same Border,
            // and Loaded must re-run the focus hookup then. The
            // handlers are static methods, so keeping them attached
            // holds no object alive.
            UnhookFocus((Border)sender);
        }

        // Size sync plus focus hookup on load.
        private static void SyncRing(Border border)
        {
            SyncRingSize(border);
            HookFocus(border);
        }

        private static void SyncRingSize(Border border)
        {
            try
            {
                if (border.BorderBrush is FocusRingBrush brush)
                {
                    brush.ApplySize(border.ActualWidth, border.ActualHeight);
                }
            }
            catch (Exception ex)
            {
                Log.Write("FocusRingBrush size sync failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Subscribes this ring's shine to its focusable ancestor's
        /// focus. Only the focused ring animates (see the class
        /// comment). If no focusable ancestor is found the shine runs
        /// always — today's behaviour — rather than silently
        /// disappearing.
        /// </summary>
        private static void HookFocus(Border border)
        {
            try
            {
                var owner = FindFocusableAncestor(border);
                SetFocusOwner(border, owner);
                if (owner == null)
                {
                    StartShine(border);
                    return;
                }
                owner.GotFocus -= FocusOwner_GotFocus;
                owner.GotFocus += FocusOwner_GotFocus;
                owner.LostFocus -= FocusOwner_LostFocus;
                owner.LostFocus += FocusOwner_LostFocus;
                // Focus restore on Back (and initial focus): if the
                // owner is already focused, start now at the global
                // phase instead of waiting for the next focus move.
                if (FocusManager.GetFocusedElement() == owner)
                {
                    StartShine(border);
                }
                else
                {
                    StopShine(border);
                }
            }
            catch (Exception ex)
            {
                Log.Write("FocusRingBrush focus hookup failed: " + ex.Message);
            }
        }

        private static void UnhookFocus(Border border)
        {
            try
            {
                var owner = GetFocusOwner(border);
                if (owner == null) return;
                owner.GotFocus -= FocusOwner_GotFocus;
                owner.LostFocus -= FocusOwner_LostFocus;
                SetFocusOwner(border, null);
            }
            catch (Exception ex)
            {
                Log.Write("FocusRingBrush focus unhook failed: " + ex.Message);
            }
        }

        private static void FocusOwner_GotFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                var border = FindRingBorder(sender as DependencyObject);
                if (border != null) StartShine(border);
            }
            catch (Exception ex)
            {
                Log.Write("FocusRingBrush focus gain failed: " + ex.Message);
            }
        }

        private static void FocusOwner_LostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                var border = FindRingBorder(sender as DependencyObject);
                if (border != null) StopShine(border);
            }
            catch (Exception ex)
            {
                Log.Write("FocusRingBrush focus loss failed: " + ex.Message);
            }
        }

        // First GridViewItem or Button above the ring Border: the
        // templated control that owns the focus. Null when the ring has
        // no focusable ancestor (then the shine runs always).
        private static Control FindFocusableAncestor(DependencyObject start)
        {
            DependencyObject current = VisualTreeHelper.GetParent(start);
            while (current != null)
            {
                if (current is GridViewItem || current is Button)
                {
                    return (Control)current;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // The single FocusRing Border inside an owner's template (the
        // Border whose BorderBrush is a FocusRingBrush). Focus changes
        // are user-driven and infrequent, so a small tree walk is fine.
        private static Border FindRingBorder(DependencyObject root)
        {
            if (root == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Border border && border.BorderBrush is FocusRingBrush)
                {
                    return border;
                }
                var found = FindRingBorder(child);
                if (found != null) return found;
            }
            return null;
        }

        private static void StartShine(Border border)
        {
            try
            {
                if (border.BorderBrush is FocusRingBrush brush)
                {
                    brush.StartShine();
                }
            }
            catch (Exception ex)
            {
                Log.Write("FocusRingBrush shine start failed: " + ex.Message);
            }
        }

        private static void StopShine(Border border)
        {
            try
            {
                if (border.BorderBrush is FocusRingBrush brush)
                {
                    brush.StopShine();
                }
            }
            catch (Exception ex)
            {
                Log.Write("FocusRingBrush shine stop failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Start (or re-seek) the shine at the global phase. Idempotent:
        /// RetimeAnimations stops first, so calling it twice is safe.
        /// </summary>
        private void StartShine()
        {
            _shineRequested = true;
            RetimeAnimations();
        }

        /// <summary>
        /// Stop the shine and park the stops at their base offsets. The
        /// ring is Opacity 0 while unfocused, so nothing visible changes.
        /// </summary>
        private void StopShine()
        {
            _shineRequested = false;
            if (_stops == null) return;
            try
            {
                for (int i = 0; i < _stops.Length; i++)
                {
                    _stops[i].StopAnimation("Offset");
                    _stops[i].Offset = StopOffsets[i];
                }
            }
            catch (Exception ex)
            {
                Log.Write("FocusRingBrush shine stop failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Reconfigures the shine timing from the ring's live size.
        /// Fraction = W/(W+H): the share of each loop spent on the
        /// first half-offset (one horizontal edge), so the spot covers
        /// the horizontal and vertical edges at the same speed. Only
        /// reconfigures when the fraction actually changed, and only
        /// restarts the stop animations when the brush is already
        /// connected — otherwise OnConnected picks up the stored
        /// fraction. Called on Loaded/SizeChanged, never on focus.
        /// </summary>
        public void ApplySize(double width, double height)
        {
            if (width <= 0 || height <= 0) return;
            float fraction = (float)(width / (width + height));
            if (fraction < 0.001f) fraction = 0.001f;
            if (fraction > 0.999f) fraction = 0.999f;
            if (Math.Abs(fraction - _horizontalFraction) < 0.001f) return;
            _horizontalFraction = fraction;
            RetimeAnimations();
        }

        private void RetimeAnimations()
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
                Log.Write("FocusRingBrush retime failed: " + ex.Message);
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
                }
                CompositionBrush = _gradientBrush;
                // Do NOT start the stop animations here: the shine runs
                // only on the focused ring (see HookFocus), so the stops
                // stay at their base offsets until focus gain starts
                // them at the global phase. If focus was gained before
                // the brush connected, honour the pending request now.
                if (_shineRequested)
                {
                    RetimeAnimations();
                }
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
            // Seek to the global phase: the sweep is one continuous
            // motion shared by every ring, so a ring starting on focus
            // gain resumes mid-sweep instead of jumping back to the
            // base offsets. A negative DelayTime starts the animation
            // as if it had already been running for that long.
            double phaseMs = _phaseClock.ElapsedMilliseconds % AnimationDurationMs;
            try
            {
                animation.DelayTime = TimeSpan.FromMilliseconds(-phaseMs);
            }
            catch (Exception ex)
            {
                // Negative delay unsupported: start from the base
                // offsets instead of losing the shine entirely.
                Log.Write("FocusRingBrush phase seek failed: " + ex.Message);
            }
            // Three keyframes: the offset covers one horizontal edge
            // in the first half-offset and one vertical edge in the
            // second, so the first segment takes W/(W+H) of the loop
            // and the second H/(W+H) — the spot's perimeter speed is
            // the same on both. Explicit linear easing on both
            // segments: the easing on a keyframe governs the segment
            // that follows it, and the interpolation must be linear
            // by construction, not by relying on the default.
            // The pattern repeats every 1.0, so the loop restart is
            // seamless.
            var linear = _compositor.CreateLinearEasingFunction();
            animation.InsertKeyFrame(0f, from, linear);
            animation.InsertKeyFrame(_horizontalFraction, from + 0.5f, linear);
            animation.InsertKeyFrame(1f, from + 1f);
            stop.StartAnimation("Offset", animation);
        }

        protected override void OnDisconnected()
        {
            base.OnDisconnected();
            _shineRequested = false;
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
