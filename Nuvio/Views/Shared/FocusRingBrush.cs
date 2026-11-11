using System;
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
    /// The shine runs continuously and is never restarted: the
    /// animations start in OnConnected and run Forever, so the sweep
    /// keeps its phase when focus moves from one element to the next —
    /// there is deliberately no restart on focus gain. The sweep
    /// travels +1.0 per 3 s loop.
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
        /// Hooks Loaded and SizeChanged only — never focus — so the
        /// shine stays continuous and uninterrupted.
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
                }
                else
                {
                    border.Loaded -= RingBorder_Loaded;
                    border.SizeChanged -= RingBorder_SizeChanged;
                    border.Unloaded -= RingBorder_Unloaded;
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

        private static void RingBorder_Unloaded(object sender, RoutedEventArgs e)
        {
            var border = (Border)sender;
            border.Loaded -= RingBorder_Loaded;
            border.SizeChanged -= RingBorder_SizeChanged;
            border.Unloaded -= RingBorder_Unloaded;
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
