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
    /// backdrop and rows, which extend under the overlay sidebar), and a tint
    /// is composited over the blur. This replaces AcrylicBrush by Mohammed's
    /// call: the custom blur gives full control of the blur amount and the
    /// tint, and unlike HostBackdrop acrylic it is a documented Xbox-compatible
    /// path (backdrop sampling runs in the compositor; HostBackdrop would
    /// render only the fallback solid on Xbox).
    ///
    /// Composite order matters: in a CompositeEffect the first source is the
    /// destination (bottom), so the old order { tint, blur } put the opaque
    /// blur over the tint and the tint had no effect at all. The strip's
    /// brightness tracked the hero art one to one (dark slide about 71-87,
    /// bright slide about 115-122 at the strip's start edge), which is the
    /// change Mohammed saw when moving focus: the focus move un-pauses the
    /// hero auto-rotate, the slide flips dark to bright, and the bright art
    /// leaks through the live blur at the strip's start edge. Nothing in the
    /// sidebar itself changes on a focus move: no opacity animation on the
    /// strip, and the button focus states are per-button only.
    ///
    /// The tint is therefore pinned at the bright second state (after the
    /// focus move): a light warm gray at 75% alpha over the blur, so the
    /// strip reads light with its natural streak at the start edge and barely
    /// moves with the slides (predicted start-edge about 110 on a dark slide
    /// and 122 on a bright one, against 71 to 122 before; the streak
    /// survives at about a quarter of its blur strength). The blur is still
    /// live underneath, so the frosted look stays. If the effect cannot be
    /// built (bad description, no compositor), the brush falls back to the
    /// same light color solid and logs, so the app can never crash on this
    /// brush at startup.
    ///
    /// Values: BlurAmount 20 DIPs (clearly frosted, still cheap on the
    /// compositor), tint #7C7A7E at 75% alpha (0xBF).
    /// </summary>
    internal sealed class SidebarBackdropBlurBrush : XamlCompositionBrushBase
    {
        private const float BlurAmount = 20f;

        // Pinned light tint (124, 122, 126) at 75% opacity: the bright
        // post-focus-move state, composited OVER the blur (see the class
        // comment: the old order left the tint under the opaque blur, where
        // it did nothing).
        private static readonly Color Tint = Color.FromArgb(0xBF, 0x7C, 0x7A, 0x7E);

        // Solid fallback: the same pinned light color, opaque.
        private static readonly Color Fallback = Color.FromArgb(0xFF, 0x7C, 0x7A, 0x7E);

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
                    // Tint over the blur: the first source is the destination
                    // (bottom), so the old order { tint, blur } buried the
                    // tint under the opaque blur and it had no effect.
                    Sources = { blur, tint }
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
