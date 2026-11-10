using System;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media.Imaging;

namespace Nuvio.Views.Shared
{
    /// <summary>
    /// Soft, omnidirectional glow behind white text and icons so they read
    /// on light fanart (variant D, chosen by Mohammed: blur 3 epx, black 90%).
    ///
    /// Why Composition, not a XAML duplicate: UWP has no text-shadow
    /// property, and the earlier 1 epx offset duplicate only darkened one
    /// side of the glyphs. A Composition DropShadow blurred by 3 epx spreads
    /// the dark around the glyphs in every direction.
    ///
    /// Why a child SpriteVisual, not Visual.Shadow: in UWP the base Visual
    /// class has NO Shadow property — only SpriteVisual (and LayerVisual)
    /// do — so the shadow lives on a transparent child SpriteVisual whose
    /// DropShadow mask is the element's own alpha. For TextBlock the mask
    /// is TextBlock.GetAlphaMask(); FontIcon has no GetAlphaMask, so its
    /// mask is a one-time RenderTargetBitmap snapshot of the rendered glyph
    /// (the icons never change, so one snapshot is enough). If any step
    /// fails the element simply gets no glow — the app never crashes for
    /// a shadow.
    ///
    /// The glow is inside the element's own visual subtree, so it follows
    /// the text automatically: translation (x:Uid), the collapse/expand
    /// opacity fade, and the icon jiggle transform all move the glow with
    /// the glyphs. The shadow's opacity is additionally driven by an
    /// expression on the host visual's Opacity, so the glow fades out with
    /// a sidebar label and never lingers as a ghost while the label is
    /// hidden. The mask is re-taken when the text or the size changes, so
    /// the glow follows the hero carousel and rows loading.
    /// </summary>
    public static class TextGlow
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(TextGlow),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        private const float BlurRadius = 3f;
        private const float Opacity = 0.9f;

        private sealed class GlowState
        {
            public DropShadow Shadow;
            public SpriteVisual Visual;
            public long TextChangedToken = -1;
        }

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached(
                "State",
                typeof(GlowState),
                typeof(TextGlow),
                new PropertyMetadata(null));

        private static GlowState GetState(DependencyObject obj) => (GlowState)obj.GetValue(StateProperty);
        private static void SetState(DependencyObject obj, GlowState value) => obj.SetValue(StateProperty, value);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement element)) return;
            if ((bool)e.NewValue)
            {
                element.Loaded += OnLoaded;
                element.Unloaded += OnUnloaded;
                element.SizeChanged += OnSizeChanged;
                if (element.IsLoaded) Attach(element);
            }
            else
            {
                element.Loaded -= OnLoaded;
                element.Unloaded -= OnUnloaded;
                element.SizeChanged -= OnSizeChanged;
                Detach(element);
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element) Attach(element);
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element) Detach(element);
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // New text (hero carousel rotation, rows loading) usually changes
            // the size: re-take the mask and keep the shadow visual sized.
            if (!(sender is FrameworkElement element)) return;
            var state = GetState(element);
            if (state?.Visual == null) return;
            try
            {
                state.Visual.Size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: resize failed: " + ex.Message);
            }
            RefreshMask(element);
        }

        private static void Attach(FrameworkElement element)
        {
            if (GetState(element) != null) return;
            try
            {
                if (element is TextBlock textBlock)
                {
                    var mask = textBlock.GetAlphaMask();
                    CreateShadow(element, mask);
                    // Text can change without changing size (same-length
                    // replacement), so track the property as well as size.
                    var state = GetState(element);
                    if (state != null)
                    {
                        state.TextChangedToken = element.RegisterPropertyChangedCallback(
                            TextBlock.TextProperty,
                            (s, dp) => RefreshMask(s as FrameworkElement));
                    }
                }
                else if (element is FontIcon)
                {
                    // No GetAlphaMask on FontIcon: snapshot the rendered glyph
                    // once (the icons never change) and attach when ready.
                    AttachIconAsync(element);
                }
            }
            catch (Exception ex)
            {
                // A glow must never take the page down: leave the text plain.
                System.Diagnostics.Debug.WriteLine("TextGlow: attach failed for " + element.GetType().Name + ": " + ex.Message);
            }
        }

        private static async void AttachIconAsync(FrameworkElement element)
        {
            CompositionBrush mask = null;
            try
            {
                mask = await CreateIconMaskAsync((FontIcon)element);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: icon mask failed: " + ex.Message);
            }
            if (mask == null) return;
            if (!element.IsLoaded || !GetIsEnabled(element)) return;
            if (GetState(element) != null) return;
            try
            {
                CreateShadow(element, mask);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: icon attach failed: " + ex.Message);
            }
        }

        private static void CreateShadow(FrameworkElement element, CompositionBrush mask)
        {
            var hostVisual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = hostVisual.Compositor;

            var shadow = compositor.CreateDropShadow();
            shadow.BlurRadius = BlurRadius;
            shadow.Color = Colors.Black;
            shadow.Opacity = Opacity;
            shadow.Offset = new Vector3(0, 0, 0);
            shadow.Mask = mask;

            // The glow must fade with the element (sidebar labels fade to 0
            // when the strip collapses). Drive the shadow opacity from the
            // host visual's opacity so no ghost glow survives the fade, even
            // if the compositor does not multiply it by itself.
            try
            {
                var opacityAnimation = compositor.CreateExpressionAnimation("host.Opacity * 0.9");
                opacityAnimation.SetReferenceParameter("host", hostVisual);
                shadow.StartAnimation("Opacity", opacityAnimation);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: opacity expression failed: " + ex.Message);
            }

            var shadowVisual = compositor.CreateSpriteVisual();
            shadowVisual.Shadow = shadow;
            shadowVisual.Size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);

            try
            {
                var sizeAnimation = compositor.CreateExpressionAnimation("host.Size");
                sizeAnimation.SetReferenceParameter("host", hostVisual);
                shadowVisual.StartAnimation("Size", sizeAnimation);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: size expression failed: " + ex.Message);
            }

            ElementCompositionPreview.SetElementChildVisual(element, shadowVisual);
            SetState(element, new GlowState { Shadow = shadow, Visual = shadowVisual });
        }

        private static void RefreshMask(FrameworkElement element)
        {
            if (element == null) return;
            var state = GetState(element);
            if (state?.Shadow == null) return;
            if (!(element is TextBlock textBlock)) return;
            try
            {
                state.Shadow.Mask = textBlock.GetAlphaMask();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: mask refresh failed: " + ex.Message);
            }
        }

        private static async Task<CompositionBrush> CreateIconMaskAsync(FontIcon icon)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(icon).Compositor;

            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(icon);
            var pixels = await bitmap.GetPixelsAsync();

            var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight,
                96.0,
                96.0,
                pixels.ToArray());
            await encoder.FlushAsync();
            stream.Seek(0);

            var surface = LoadedImageSurface.StartLoadFromStream(stream);
            surface.LoadCompleted += (s, e) => stream.Dispose();
            var brush = compositor.CreateSurfaceBrush(surface);
            brush.Stretch = CompositionStretch.Fill;
            return brush;
        }

        private static void Detach(FrameworkElement element)
        {
            var state = GetState(element);
            if (state == null) return;
            SetState(element, null);
            try
            {
                if (state.TextChangedToken >= 0)
                {
                    element.UnregisterPropertyChangedCallback(TextBlock.TextProperty, state.TextChangedToken);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: unregister failed: " + ex.Message);
            }
            try
            {
                if (state.Visual != null)
                {
                    state.Visual.StopAnimation("Size");
                    if (ElementCompositionPreview.GetElementChildVisual(element) == state.Visual)
                    {
                        ElementCompositionPreview.SetElementChildVisual(element, null);
                    }
                }
                state.Shadow?.StopAnimation("Opacity");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: detach failed: " + ex.Message);
            }
        }
    }
}
