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
    /// The tint is gone: the reference (expanded menu over the Verity hero,
    /// focus on Library, after the focus move) reads about 115-122 at the
    /// start edge, 75-105 across the body, std 36-44 — the hero clearly
    /// visible, frosted, through the menu, brightest at the start edge
    /// where the white hero title bleeds through. Those tones ARE the pure
    /// blur: the reference was recorded when the tint sat buried under the
    /// opaque blur and had no effect, so the strip was blur only. The 75%
    /// light tint of build 251 flattened the strip to a near-opaque light
    /// panel (about 168, std about 20), so it is removed — the effect is
    /// the Gaussian blur alone. If the effect cannot be built (bad
    /// description, no compositor), the brush falls back to a mid-grey
    /// solid near the reference average and logs, so the app can never
    /// crash on this brush at startup.
    ///
    /// Stability is not the tint's job: HomePage freezes the hero
    /// auto-rotate while the sidebar is expanded (MainPage
    /// .IsSidebarExpanded), so the backdrop behind the strip cannot change
    /// while the menu is open, and the tones stay exactly as they were
    /// when the menu opened. No artificial streak layer is painted here
    /// (banned since 43766ac): the bright band at the start edge is the
    /// real blurred hero title bleeding through the live blur.
    ///
    /// Values: BlurAmount 20 DIPs (clearly frosted, still cheap on the
    /// compositor), no tint.
    /// </summary>
    internal sealed class SidebarBackdropBlurBrush : XamlCompositionBrushBase
    {
        private const float BlurAmount = 20f;

        // No tint: the strip is the bare blur (see the class comment — the
        // reference tones are the pure blur, and any tint only flattens
        // them; build 251 proved it at 75% alpha).

        // Solid fallback (effect build failed): a mid grey near the
        // reference menu's average (about 96-101), opaque.
        private static readonly Color Fallback = Color.FromArgb(0xFF, 0x60, 0x61, 0x63);

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
                // Win2D effect objects. The backdrop enters through a
                // CompositionEffectSourceParameter named "Backdrop".
                // Blur only, no tint and no composite: the reference tones
                // are the pure blur (see the class comment), and the
                // CompositeEffect/ColorSourceEffect are gone with the tint.
                var blur = new GaussianBlurEffect
                {
                    Name = "Blur",
                    Source = new CompositionEffectSourceParameter("Backdrop"),
                    BlurAmount = BlurAmount,
                    BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Speed
                };
                var factory = compositor.CreateEffectFactory(blur);
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
