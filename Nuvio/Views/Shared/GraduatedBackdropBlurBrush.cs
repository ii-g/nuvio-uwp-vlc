using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Nuvio.Diagnostics;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Nuvio.Views.Shared
{
    /// <summary>
    /// Graduated blur for backdrops: one shared definition, built in
    /// Composition. The brush wraps a CompositionMaskBrush whose source
    /// is the same GaussianBlur effect the frosted sidebar uses
    /// (BlurAmount 12 over the app content behind it) and whose mask
    /// is a vertical CompositionLinearGradientBrush — transparent where
    /// the backdrop stays sharp, opaque where it is fully blurred.
    /// A page only puts this brush on its blur rectangle and drives it
    /// with SetRowState from its focus handler; no page keeps a local
    /// copy of the states or the animation.
    ///
    /// Why Composition, not XAML: UWP has no OpacityMask on UIElement
    /// (that is a WPF-only property — build 297 failed with WMC0011
    /// for exactly that), so the graduation cannot be an XAML mask.
    ///
    /// States: hero (clear to 0.30, fully blurred from 0.80) and row
    /// (clear to 0.30, fully blurred from 0.60 — the blur never
    /// reaches the top). ~200ms
    /// with an ease-out cubic-bezier matching the XAML CubicEase
    /// EaseOut (1-(1-t)^3); the animation carries only the final
    /// keyframe, so a new animation retargets from the current value
    /// and fast focus repeats stay smooth. The state guard means a
    /// repeat call for the same state is a no-op.
    ///
    /// The brush is shared across pages by design: only one page is
    /// on screen at a time, and a page re-applies its state from its
    /// focus handler when it comes back (HomePage's OnGotFocus does).
    /// If the mask cannot be built, the brush falls back to the plain
    /// uniform blur (graduation lost, blur kept); it never crashes
    /// startup on the blur.
    ///
    /// Values: BlurAmount 12 DIPs (lighter than the sidebar's 20), no tint.
    /// Min version 22000 is far above CompositionMaskBrush /
    /// CompositionLinearGradientBrush (both 17134), so no
    /// ApiInformation guard is needed.
    /// </summary>
    internal sealed class GraduatedBackdropBlurBrush : XamlCompositionBrushBase
    {
        private const float BlurAmount = 12f;

        // Hero state: clear to 0.30, fully blurred from 0.80.
        private const double HeroClearOffset = 0.30;
        private const double HeroFullOffset = 0.80;

        // Row state: clear to 0.30, fully blurred from 0.60 —
        // the blur never reaches the top.
        private const double RowClearOffset = 0.30;
        private const double RowFullOffset = 0.60;

        private const double AnimationDurationMs = 200;

        // easeOutCubic (1-(1-t)^3) as a cubic-bezier, matching the
        // XAML CubicEase EaseOut the old mask animation used.
        private static readonly Vector2 EaseOutControl1 = new Vector2(0.215f, 0.61f);
        private static readonly Vector2 EaseOutControl2 = new Vector2(0.355f, 1f);

        private Compositor _compositor;
        private CompositionBackdropBrush _backdropBrush;
        private CompositionEffectBrush _effectBrush;
        private CompositionLinearGradientBrush _gradientBrush;
        private CompositionMaskBrush _maskBrush;
        private CompositionColorGradientStop _clearStop;
        private CompositionColorGradientStop _fullStop;

        private bool _isRowState;

        /// <summary>Switches the graduation between the hero state
        /// (false) and the row state (true), animating both stop
        /// offsets over ~200ms. Safe to call before the brush is
        /// connected: the state is recorded and applied on connect.
        /// </summary>
        public void SetRowState(bool rowState)
        {
            if (_isRowState == rowState) return;
            _isRowState = rowState;
            if (_clearStop == null || _fullStop == null) return;
            AnimateStop(_clearStop, rowState ? RowClearOffset : HeroClearOffset);
            AnimateStop(_fullStop, rowState ? RowFullOffset : HeroFullOffset);
        }

        private void AnimateStop(CompositionColorGradientStop stop, double target)
        {
            var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(AnimationDurationMs);
            // Final keyframe only: the animation starts from the live
            // value, so a new animation retargets smoothly.
            animation.InsertKeyFrame(1.0f, (float)target,
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
                // CreateEffectFactory takes an IGraphicsEffect graph, not an
                // effect-description string (no string overload exists — that
                // was the CS1503 at (55,62) in the sidebar brush), so the
                // graph is built with Win2D effect objects. The backdrop
                // enters through a CompositionEffectSourceParameter named
                // "Backdrop". Blur only, no tint and no composite.
                var blur = new GaussianBlurEffect
                {
                    Name = "Blur",
                    Source = new CompositionEffectSourceParameter("Backdrop"),
                    BlurAmount = BlurAmount,
                    BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Speed
                };
                var factory = _compositor.CreateEffectFactory(blur);
                _effectBrush = factory.CreateBrush();
                _effectBrush.SetSourceParameter("Backdrop", _backdropBrush);
                try
                {
                    // The graduation: a vertical alpha ramp. Only the
                    // alpha matters for the mask — transparent black
                    // above, opaque black below.
                    _gradientBrush = _compositor.CreateLinearGradientBrush();
                    _gradientBrush.StartPoint = new Vector2(0, 0);
                    _gradientBrush.EndPoint = new Vector2(0, 1);
                    _gradientBrush.MappingMode = CompositionMappingMode.Relative;
                    _clearStop = _compositor.CreateColorGradientStop(
                        (float)HeroClearOffset, Color.FromArgb(0, 0, 0, 0));
                    _fullStop = _compositor.CreateColorGradientStop(
                        (float)HeroFullOffset, Color.FromArgb(255, 0, 0, 0));
                    _gradientBrush.ColorStops.Add(_clearStop);
                    _gradientBrush.ColorStops.Add(_fullStop);
                    if (_isRowState)
                    {
                        // SetRowState was called before connect: apply
                        // the recorded state at once, no animation.
                        _clearStop.Offset = (float)RowClearOffset;
                        _fullStop.Offset = (float)RowFullOffset;
                    }
                    _maskBrush = _compositor.CreateMaskBrush();
                    _maskBrush.Source = _effectBrush;
                    _maskBrush.Mask = _gradientBrush;
                    CompositionBrush = _maskBrush;
                }
                catch (Exception ex)
                {
                    // Graduation failed: keep the plain uniform blur
                    // rather than losing the blur (or the backdrop).
                    Log.Write("GraduatedBackdropBlurBrush mask failed, using uniform blur: " + ex.Message);
                    CompositionBrush = _effectBrush;
                }
            }
            catch (Exception ex)
            {
                // Never crash startup on the blur: the backdrop images
                // simply show unblurred.
                Log.Write("GraduatedBackdropBlurBrush failed, no blur: " + ex.Message);
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
            _maskBrush?.Dispose();
            _maskBrush = null;
            _gradientBrush?.Dispose();
            _gradientBrush = null;
            _clearStop = null;
            _fullStop = null;
            _effectBrush?.Dispose();
            _effectBrush = null;
            _backdropBrush?.Dispose();
            _backdropBrush = null;
        }
    }
}
