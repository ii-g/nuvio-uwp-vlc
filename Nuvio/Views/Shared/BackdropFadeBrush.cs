using System;
using System.Numerics;
using Nuvio.Diagnostics;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Nuvio.Views.Shared
{
    /// <summary>
    /// Backdrop fade (variant D): the backdrop image itself dissolves
    /// downward into the solid dark app background through a vertical
    /// gradient, with no blur and no dark tint overlay. The rectangle
    /// this brush fills sits above BackdropA/BackdropB; a plain
    /// CompositionLinearGradientBrush — a veil from transparent to the
    /// solid shell background (#0D0E13) — is drawn over the images, so
    /// the image dissolves into the background. The veil's alpha is
    /// (1 - mask), where mask is the old effect's gradient mask
    /// (opaque above the fade start, gone by the fade end), so the
    /// composite is identical to the old one by construction:
    /// image*(1-alpha) + solid*alpha = image*mask + solid*(1-mask).
    /// The old implementation sampled the backdrop live every frame
    /// with a CompositionBackdropBrush + AlphaMaskEffect +
    /// CompositeEffect chain; that fullscreen live effect saturated
    /// the compositor on Xbox Developer Mode, so the focus shine
    /// slowed down and input lagged whenever the hero tick spiked it.
    /// The veil is a trivial gradient fill with the same crossfade,
    /// same gradient and same offsets — the same look, no live
    /// sampling, no effect chain. The mask applies only to the
    /// backdrop images — hero text, buttons, posters and rows sit
    /// above this rectangle and are untouched.
    /// The page drives it with SetRowState from its focus handler:
    /// hero keeps the image visible longer (opaque to 45%, gone by
    /// 90%), any row focus fades it earlier (opaque to 25%, gone by
    /// 70%), 200ms between states. If the brush cannot be built, it
    /// renders nothing (the backdrop simply shows unfaded) and logs,
    /// so it can never crash startup.
    /// </summary>
    internal sealed class BackdropFadeBrush : XamlCompositionBrushBase
    {
        private const double AnimationDurationMs = 200;

        // easeOutCubic (1-(1-t)^3) as a cubic-bezier, matching the
        // XAML CubicEase EaseOut the rest of the page uses.
        private static readonly Vector2 EaseOutControl1 = new Vector2(0.215f, 0.61f);
        private static readonly Vector2 EaseOutControl2 = new Vector2(0.355f, 1f);

        // Solid background the image dissolves into: the shell
        // background behind the page.
        private static readonly Windows.UI.Color BackgroundColor =
            Windows.UI.Color.FromArgb(0xFF, 0x0D, 0x0E, 0x13);

        // Hero default: the XAML default state is hero focus.
        private const float FadeStartHero = 0.45f;
        private const float FadeEndHero = 0.90f;
        private const float FadeStartRow = 0.25f;
        private const float FadeEndRow = 0.70f;

        private Compositor _compositor;
        private CompositionLinearGradientBrush _gradientBrush;
        private CompositionColorGradientStop _startStop;
        private CompositionColorGradientStop _endStop;

        private bool _isRow;
        private float _fadeStart = FadeStartHero;
        private float _fadeEnd = FadeEndHero;

        /// <summary>Moves the fade between the hero and row states,
        /// animating over 200ms when asked. Safe to call before the
        /// brush is connected: the values are recorded and applied on
        /// connect.</summary>
        public void SetRowState(bool isRow, bool animate = true)
        {
            float start = isRow ? FadeStartRow : FadeStartHero;
            float end = isRow ? FadeEndRow : FadeEndHero;
            if (_isRow == isRow && _startStop != null) return;
            _isRow = isRow;
            _fadeStart = start;
            _fadeEnd = end;
            if (_startStop == null || _endStop == null || _compositor == null) return;
            try
            {
                if (animate)
                {
                    AnimateStopOffset(_startStop, start);
                    AnimateStopOffset(_endStop, end);
                }
                else
                {
                    _startStop.Offset = start;
                    _endStop.Offset = end;
                }
            }
            catch (Exception ex)
            {
                // Never crash on the fade animation: fall back to setting
                // the values directly, and if even that fails keep the
                // last good state and log.
                Log.Write("BackdropFadeBrush animation failed: " + ex.Message);
                try
                {
                    _startStop.Offset = start;
                    _endStop.Offset = end;
                }
                catch (Exception inner) { Log.Write("BackdropFadeBrush set failed: " + inner.Message); }
            }
        }

        private void AnimateStopOffset(CompositionColorGradientStop stop, float target)
        {
            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(AnimationDurationMs);
            // Final keyframe only: the animation starts from the live
            // value, so a new animation retargets smoothly.
            animation.InsertKeyFrame(1.0f, target,
                _compositor.CreateCubicBezierEasingFunction(EaseOutControl1, EaseOutControl2));
            stop.StartAnimation("Offset", animation);
        }

        protected override void OnConnected()
        {
            base.OnConnected();
            _compositor = null;
            try
            {
                _compositor = Window.Current.Compositor;

                // Gradient veil: transparent above the fade start,
                // fading to the solid background at the fade end. The
                // veil's alpha is (1 - mask), so compositing it over
                // the backdrop images gives exactly the old effect's
                // result — image*mask + solid*(1-mask) — with no live
                // backdrop sampling and no effect chain. The gradient
                // stops are animated directly on the stop objects, so
                // no animatable properties need declaring.
                _gradientBrush = _compositor.CreateLinearGradientBrush();
                _gradientBrush.StartPoint = new Vector2(0f, 0f);
                _gradientBrush.EndPoint = new Vector2(0f, 1f);
                var transparent = Windows.UI.Color.FromArgb(0x00, 0x0D, 0x0E, 0x13);
                _gradientBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0f, transparent));
                _startStop = _compositor.CreateColorGradientStop(_fadeStart, transparent);
                _gradientBrush.ColorStops.Add(_startStop);
                _endStop = _compositor.CreateColorGradientStop(_fadeEnd, BackgroundColor);
                _gradientBrush.ColorStops.Add(_endStop);
                _gradientBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1f, BackgroundColor));
                CompositionBrush = _gradientBrush;
            }
            catch (Exception ex)
            {
                // Never crash startup on the fade: the backdrop images
                // simply show unfaded.
                Log.Write("BackdropFadeBrush failed, no fade: " + ex.Message);
                DisposeBrushes();
                CompositionBrush = null;
            }
        }

        protected override void OnDisconnected()
        {
            base.OnDisconnected();
            CompositionBrush = null;
            DisposeBrushes();
        }

        private void DisposeBrushes()
        {
            _gradientBrush?.Dispose();
            _gradientBrush = null;
            _startStop = null;
            _endStop = null;
        }
    }
}
