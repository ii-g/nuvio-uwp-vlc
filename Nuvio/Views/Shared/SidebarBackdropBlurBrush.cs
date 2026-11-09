using System;
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
    /// Frosted-glass brush for the sidebar: a custom Composition backdrop blur.
    /// A GaussianBlurEffect blurs the app content behind the strip (the home
    /// backdrop and rows, which extend under the overlay sidebar), and a dark
    /// tint is composited over the blur so the white icons and labels keep full
    /// contrast. This replaces AcrylicBrush by Mohammed's call: the custom blur
    /// gives full control of the blur amount and the tint, and unlike
    /// HostBackdrop acrylic it is a documented Xbox-compatible path (backdrop
    /// sampling runs in the compositor; HostBackdrop would render only the
    /// fallback solid on Xbox).
    ///
    /// Values: BlurAmount 20 DIPs (clearly frosted, still cheap on the
    /// compositor), tint bg-elevated #16171D at 80% alpha (0xCC) — the same
    /// dark tint the acrylic version used. If the effect cannot be built
    /// (bad description, no compositor), the brush falls back to the old
    /// solid #111218 background and logs, so the app can never crash on this
    /// brush at startup.
    /// </summary>
    internal sealed class SidebarBackdropBlurBrush : XamlCompositionBrushBase
    {
        private const float BlurAmount = 20f;

        // bg-elevated (#16171D) at 80% opacity.
        private static readonly Color Tint = Color.FromArgb(0xCC, 0x16, 0x17, 0x1D);

        // Solid fallback: the sidebar's old background.
        private static readonly Color Fallback = Color.FromArgb(0xFF, 0x11, 0x12, 0x18);

        private CompositionBackdropBrush _backdropBrush;
        private CompositionEffectBrush _effectBrush;

        protected override void OnConnected()
        {
            base.OnConnected();
            Compositor compositor = null;
            try
            {
                compositor = Window.Current.Compositor;
                _backdropBrush = compositor.CreateBackdropBrush();
                // CreateEffectFactory takes an IGraphicsEffect graph, not an
                // effect-description string (no string overload exists — that
                // was the CS1503 at (55,62)), so the graph is built with
                // Win2D effect objects. Every source is an IGraphicsEffect:
                // the backdrop enters through a CompositionEffectSourceParameter
                // named "Backdrop", and the tint is a ColorSourceEffect.
                var blur = new GaussianBlurEffect
                {
                    Name = "Blur",
                    Source = new CompositionEffectSourceParameter("Backdrop"),
                    BlurAmount = BlurAmount,
                    BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Speed
                };
                var tint = new ColorSourceEffect
                {
                    Name = "Tint",
                    Color = Tint
                };
                var composite = new CompositeEffect
                {
                    Name = "Out",
                    Mode = CanvasComposite.SourceOver,
                    Sources = { tint, blur }
                };
                var factory = compositor.CreateEffectFactory(composite);
                _effectBrush = factory.CreateBrush();
                _effectBrush.SetSourceParameter("Backdrop", _backdropBrush);
                CompositionBrush = _effectBrush;
            }
            catch (Exception ex)
            {
                // Never crash startup on the blur: fall back to the solid color.
                Log.Write("SidebarBackdropBlurBrush failed, using solid fallback: " + ex.Message);
                DisposeBrushes();
                if (compositor != null)
                    CompositionBrush = compositor.CreateColorBrush(Fallback);
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
