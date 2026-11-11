using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Nuvio.Diagnostics;
using Windows.Graphics.Effects;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Nuvio.Views.Shared
{
    /// <summary>
    /// Backdrop fade (variant D): the backdrop image itself dissolves
    /// downward into the solid dark app background through a vertical
    /// gradient alpha mask, with no blur and no dark tint overlay. The
    /// rectangle this brush fills sits above BackdropA/BackdropB; a
    /// CompositionBackdropBrush samples the images behind it, an
    /// AlphaMaskEffect fades them with a linear gradient, and a
    /// CompositeEffect lays the faded image over the solid shell
    /// background (#0D0E13) so the image dissolves into it. The mask
    /// applies only to the backdrop images — hero text, buttons,
    /// posters and rows sit above this rectangle and are untouched.
    /// The page drives it with SetRowState from its focus handler:
    /// hero keeps the image visible longer (opaque to 45%, gone by
    /// 90%), any row focus fades it earlier (opaque to 25%, gone by
    /// 70%), 200ms between states. If the effect cannot be built, the
    /// brush renders nothing (the backdrop simply shows unfaded) and
    /// logs, so it can never crash startup.
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
        private CompositionBackdropBrush _backdropBrush;
        private CompositionEffectBrush _effectBrush;
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
                _backdropBrush = _compositor.CreateBackdropBrush();

                // Gradient mask: fully opaque above the fade start,
                // fading to fully transparent at the fade end. The
                // mask's alpha channel is what AlphaMaskEffect reads.
                _gradientBrush = _compositor.CreateLinearGradientBrush();
                _gradientBrush.StartPoint = new Vector2(0f, 0f);
                _gradientBrush.EndPoint = new Vector2(0f, 1f);
                var opaque = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
                var clear = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);
                _gradientBrush.Stops.Add(_compositor.CreateColorGradientStop(0f, opaque));
                _startStop = _compositor.CreateColorGradientStop(_fadeStart, opaque);
                _gradientBrush.Stops.Add(_startStop);
                _endStop = _compositor.CreateColorGradientStop(_fadeEnd, clear);
                _gradientBrush.Stops.Add(_endStop);
                _gradientBrush.Stops.Add(_compositor.CreateColorGradientStop(1f, clear));

                // Fade the sampled backdrop with the gradient, then lay
                // it over the solid background so the image dissolves
                // into it instead of showing through to the images
                // behind this rectangle.
                var fade = new AlphaMaskEffect
                {
                    Name = "Fade",
                    Source = new CompositionEffectSourceParameter("Backdrop"),
                    AlphaMask = _gradientBrush
                };
                var solid = new ColorSourceEffect
                {
                    Name = "Base",
                    Color = BackgroundColor
                };
                var composite = new CompositeEffect
                {
                    Mode = CanvasComposite.SourceOver,
                    Sources = new IGraphicsEffectSource[] { fade, solid }
                };
                // The gradient stops are animated directly on the stop
                // objects, not through the effect brush, so no
                // animatable properties need declaring here.
                var factory = _compositor.CreateEffectFactory(composite);
                _effectBrush = factory.CreateBrush();
                _effectBrush.SetSourceParameter("Backdrop", _backdropBrush);
                CompositionBrush = _effectBrush;
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
            _effectBrush?.Dispose();
            _effectBrush = null;
            _backdropBrush?.Dispose();
            _backdropBrush = null;
            _gradientBrush?.Dispose();
            _gradientBrush = null;
            _startStop = null;
            _endStop = null;
        }
    }
}
