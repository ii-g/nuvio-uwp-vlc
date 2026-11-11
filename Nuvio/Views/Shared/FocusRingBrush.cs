using System;
using System.Diagnostics;
using System.Numerics;
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
    /// alpha alternating with 10% valleys, period 1.0) built as a
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
    /// The shine is one continuous global motion and never restarts.
    /// Every ring starts its 7 stop animations once, in OnConnected,
    /// at the current global phase (a static stopwatch, elapsed %
    /// 3000 ms), and they run Forever. Focus changes never create,
    /// stop, or retime an animation: the ring's visibility is driven
    /// by the XAML VisualState Opacity storyboard alone. An earlier
    /// design started the shine on focus gain and stopped it on focus
    /// loss; every start re-created the animations and sought the
    /// global phase, and a broken seek reset the sweep to the base
    /// offsets on every focus move — during fast navigation the shine
    /// kept restarting and barely advanced, reading as a sudden
    /// slowdown. Starting once per connection at the global phase
    /// removes that whole failure mode. The only re-time is a size
    /// change (Loaded/SizeChanged), which rebuilds the animations
    /// from the current global phase with the same math.
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
        // peaks (#D9FFFFFF), odd stops are valleys (#1AFFFFFF).
        private static readonly float[] StopOffsets = { -1f, -0.5f, 0f, 0.5f, 1f, 1.5f, 2f };

        private static readonly Windows.UI.Color Peak =
            Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);

        private static readonly Windows.UI.Color Valley =
            Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

        // Global phase clock: one continuous sweep shared by every
        // ring. Every ring starts its animations at the current global
        // phase (elapsed % 3000 ms), so all rings sweep in step and no
        // ring ever restarts from the base offsets.
        private static readonly Stopwatch _phaseClock = Stopwatch.StartNew();

        private Compositor _compositor;
        private CompositionLinearGradientBrush _gradientBrush;
        private CompositionColorGradientStop[] _stops;

        // Fraction of each loop spent on the first half-offset (one
        // horizontal edge). Default 0.5 reproduces the old two-keyframe
        // timing exactly until a live size is known.
        private float _horizontalFraction = 0.5f;

        /// <summary>
        /// Attached property for a FocusRing Border: when true, the
        /// shine travels at constant speed along the ring's perimeter,
        /// timed from the Border's live size (see the class comment).
        /// Hooks Loaded and SizeChanged for the size only. Focus never
        /// touches the animations: the shine starts once in OnConnected
        /// and runs forever, and the ring's visibility is driven by
        /// the XAML VisualState Opacity storyboard.
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
                    // Set after load (e.g. in code): Loaded already
                    // fired, so sync the size now instead of waiting.
                    if (border.IsLoaded)
                    {
                        SyncRingSize(border);
                    }
                }
                else
                {
                    border.Loaded -= RingBorder_Loaded;
                    border.SizeChanged -= RingBorder_SizeChanged;
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
            SyncRingSize((Border)sender);
        }

        private static void RingBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            SyncRingSize((Border)sender);
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
        /// Reconfigures the shine timing from the ring's live size.
        /// Fraction = W/(W+H): the share of each loop spent on the
        /// first half-offset (one horizontal edge), so the spot covers
        /// the horizontal and vertical edges at the same speed. Only
        /// reconfigures when the fraction actually changed. Called on
        /// Loaded/SizeChanged, never on focus: a size change rebuilds
        /// the animations from the current global phase, a focus
        /// change never touches them.
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
                // Start the shine once, here, at the current global
                // phase: the sweep runs Forever from this moment and
                // is never stopped, restarted, or retimed by focus.
                // The ring is Opacity 0 until focused, so an unfocused
                // ring animates invisibly — the cost of one continuous
                // global motion with no restart machinery.
                RetimeAnimations();
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
            // Explicit keyframe shift for phase continuity: the sweep
            // is one continuous motion shared by every ring, so a ring
            // that (re)connects or re-times resumes mid-sweep instead
            // of jumping back to the base offsets. The value function
            // over one loop is piecewise linear with a kink at
            // _horizontalFraction (from -> from+0.5 over the first
            // segment, from+0.5 -> from+1 over the second), and the
            // gradient pattern repeats every 1.0, so a loop restart is
            // a seamless -1.0 jump. Shifting the keyframes to start at
            // the current global phase p keeps the same slopes and the
            // same loop — only the starting point moves. The easing on
            // a keyframe governs the segment that follows it, and the
            // interpolation must be linear by construction, not by
            // relying on the default.
            float fraction = _horizontalFraction;
            float p = (float)((_phaseClock.ElapsedMilliseconds % AnimationDurationMs) / AnimationDurationMs);
            if (p < 0f) p = 0f;
            if (p > 1f) p = 1f;
            // Value progress at the current phase: the offset's
            // position within one loop, 0..1.
            float progress;
            if (p <= fraction)
            {
                progress = 0.5f * (p / fraction);
            }
            else
            {
                progress = 0.5f + 0.5f * ((p - fraction) / (1f - fraction));
            }
            var linear = _compositor.CreateLinearEasingFunction();
            try
            {
                // Trace the same increasing value function from the
                // current phase: (0, from+progress), then the kink and
                // the loop boundary in whichever order they fall, then
                // (1, from+1+progress). The loop restart jumps -1.0,
                // which is invisible because the gradient repeats
                // every 1.0.
                var frames = new System.Collections.Generic.List<System.Tuple<float, float>>();
                frames.Add(System.Tuple.Create(0f, from + progress));
                if (p <= fraction)
                {
                    frames.Add(System.Tuple.Create(fraction - p, from + 0.5f));
                    frames.Add(System.Tuple.Create(1f - p, from + 1f));
                }
                else
                {
                    frames.Add(System.Tuple.Create(1f - p, from + 1f));
                    frames.Add(System.Tuple.Create(1f + fraction - p, from + 1.5f));
                }
                frames.Add(System.Tuple.Create(1f, from + 1f + progress));
                // Merge keyframes that coincide (p at 0, at the
                // fraction, or at the loop boundary): they carry the
                // same value, so keeping one is exact.
                var merged = new System.Collections.Generic.List<System.Tuple<float, float>>();
                foreach (var frame in frames)
                {
                    if (merged.Count > 0 && frame.Item1 - merged[merged.Count - 1].Item1 < 0.001f) continue;
                    merged.Add(frame);
                }
                for (int i = 0; i < merged.Count; i++)
                {
                    if (i == merged.Count - 1)
                    {
                        animation.InsertKeyFrame(merged[i].Item1, merged[i].Item2);
                    }
                    else
                    {
                        animation.InsertKeyFrame(merged[i].Item1, merged[i].Item2, linear);
                    }
                }
            }
            catch (Exception ex)
            {
                // Never lose the shine on a phase-shift failure: fall
                // back to starting from the base offsets.
                Log.Write("FocusRingBrush phase shift failed: " + ex.Message);
                try
                {
                    animation = _compositor.CreateScalarKeyFrameAnimation();
                    animation.Duration = TimeSpan.FromMilliseconds(AnimationDurationMs);
                    animation.IterationBehavior = AnimationIterationBehavior.Forever;
                    animation.InsertKeyFrame(0f, from, linear);
                    animation.InsertKeyFrame(fraction, from + 0.5f, linear);
                    animation.InsertKeyFrame(1f, from + 1f);
                }
                catch (Exception inner)
                {
                    Log.Write("FocusRingBrush base start failed: " + inner.Message);
                    return;
                }
            }
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
