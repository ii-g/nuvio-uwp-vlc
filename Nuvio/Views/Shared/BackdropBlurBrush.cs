using System;
using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Nuvio.Diagnostics;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Nuvio.Views.Shared
{
    /// <summary>
    /// Uniform backdrop blur: a Composition GaussianBlur over the app
    /// content behind the rectangle, with an animatable blur amount.
    /// The page drives it with SetBlurAmount from its focus handler
    /// (hero 8, row 20, 200ms). No gradient mask: the blur is uniform
    /// over the whole backdrop, and the gradient tint lives in a
    /// separate rectangle above this one. If the effect cannot be
    /// built, the brush renders nothing (the backdrop simply shows
    /// unblurred) and logs, so it can never crash startup.
    /// </summary>
    internal sealed class BackdropBlurBrush : XamlCompositionBrushBase
    {
        private const double AnimationDurationMs = 200;

        // easeOutCubic (1-(1-t)^3) as a cubic-bezier, matching the
        // XAML CubicEase EaseOut the tint animation uses.
        private static readonly Vector2 EaseOutControl1 = new Vector2(0.215f, 0.61f);
        private static readonly Vector2 EaseOutControl2 = new Vector2(0.355f, 1f);

        private Compositor _compositor;
        private CompositionBackdropBrush _backdropBrush;
        private CompositionEffectBrush _effectBrush;

        // Hero default: the XAML default state is hero focus.
        private float _blurAmount = 8f;

        /// <summary>Sets the blur amount, animating over 200ms when
        /// asked. Safe to call before the brush is connected: the value
        /// is recorded and applied on connect.</summary>
        public void SetBlurAmount(float amount, bool animate = true)
        {
            if (amount == _blurAmount && _effectBrush != null) return;
            _blurAmount = amount;
            if (_effectBrush == null || _compositor == null) return;
            try
            {
                if (animate)
                {
                    var animation = _compositor.CreateScalarKeyFrameAnimation();
                    animation.Duration = TimeSpan.FromMilliseconds(AnimationDurationMs);
                    // Final keyframe only: the animation starts from the live
                    // value, so a new animation retargets smoothly.
                    animation.InsertKeyFrame(1.0f, amount,
                        _compositor.CreateCubicBezierEasingFunction(EaseOutControl1, EaseOutControl2));
                    _effectBrush.StartAnimation("Blur.BlurAmount", animation);
                }
                else
                {
                    _effectBrush.Properties.InsertScalar("Blur.BlurAmount", amount);
                }
            }
            catch (Exception ex)
            {
                // Never crash on the blur animation: fall back to setting
                // the value directly, and if even that fails keep the last
                // good amount and log.
                Log.Write("BackdropBlurBrush animation failed: " + ex.Message);
                try { _effectBrush.Properties.InsertScalar("Blur.BlurAmount", amount); }
                catch (Exception inner) { Log.Write("BackdropBlurBrush set failed: " + inner.Message); }
            }
        }

        protected override void OnConnected()
        {
            base.OnConnected();
            _compositor = null;
            try
            {
                _compositor = Window.Current.Compositor;
                _backdropBrush = _compositor.CreateBackdropBrush();
                // CreateEffectFactory takes an IGraphicsEffect graph, not an
                // effect-description string (no string overload exists), so
                // the graph is built with Win2D effect objects. The backdrop
                // enters through a CompositionEffectSourceParameter named
                // "Backdrop". Blur only, no tint and no composite.
                var blur = new GaussianBlurEffect
                {
                    Name = "Blur",
                    Source = new CompositionEffectSourceParameter("Backdrop"),
                    BlurAmount = _blurAmount,
                    BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Speed
                };
                var factory = _compositor.CreateEffectFactory(blur, new[] { "Blur.BlurAmount" });
                _effectBrush = factory.CreateBrush();
                _effectBrush.SetSourceParameter("Backdrop", _backdropBrush);
                // Seed the animatable property so the first animation
                // retargets from the live value.
                _effectBrush.Properties.InsertScalar("Blur.BlurAmount", _blurAmount);
                CompositionBrush = _effectBrush;
            }
            catch (Exception ex)
            {
                // Never crash startup on the blur: the backdrop images
                // simply show unblurred.
                Log.Write("BackdropBlurBrush failed, no blur: " + ex.Message);
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
        }
    }
}
