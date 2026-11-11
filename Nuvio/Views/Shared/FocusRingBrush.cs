using System;
using System.Numerics;
using Nuvio.Diagnostics;
using Windows.UI.Composition;
using Windows.UI.Xaml;
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
    /// </summary>
    internal sealed class FocusRingBrush : XamlCompositionBrushBase
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
