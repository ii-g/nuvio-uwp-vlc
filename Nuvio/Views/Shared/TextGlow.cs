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
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Nuvio.Views.Shared
{
    /// <summary>
    /// Soft, omnidirectional glow BEHIND white text, icons and the hero logo
    /// so they read on light fanart (variant D, chosen by Mohammed: blur 3
    /// epx, black 90%).
    ///
    /// Why Composition, not a XAML duplicate: UWP has no text-shadow
    /// property, and the earlier 1 epx offset duplicate only darkened one
    /// side of the glyphs. A Composition DropShadow blurred by 3 epx spreads
    /// the dark around the glyphs in every direction.
    ///
    /// Why behind, not a child: the first version attached the shadow as a
    /// child of the element's own Visual (SetElementChildVisual), and in
    /// Composition children paint ABOVE their parent's content — the dark
    /// blur covered the white glyphs (build 310 photo). The shadow now
    /// lives on a transparent SpriteVisual inserted into the element's
    /// PARENT visual directly BELOW the element's own visual
    /// (InsertBelow), so the glyphs paint over the glow. Its Offset, Size
    /// and TransformMatrix are driven by expression animations on the host
    /// visual, so the glow tracks layout moves (hero rotation, rows
    /// loading), scrolling (it moves with the parent) and the sidebar icon
    /// jiggle transform; a LayoutUpdated sync covers any property whose
    /// expression failed to start.
    ///
    /// Masks: TextBlock.GetAlphaMask() for text; FontIcon has no
    /// GetAlphaMask, so its mask is a one-time RenderTargetBitmap snapshot
    /// of the rendered glyph (the icons never change). Image has no
    /// GetAlphaMask either: its mask is loaded straight from the source
    /// URI (LoadedImageSurface.StartLoadFromUri) when the source is a
    /// BitmapImage with a UriSource — the Image element sizes itself to
    /// the uniform-fitted logo, so the full image fills the element box
    /// exactly — and re-taken whenever Image.Source changes (the hero logo
    /// changes every slide); other sources fall back to the snapshot path.
    /// If any step fails the element simply gets no glow — the app never
    /// crashes for a shadow.
    ///
    /// The glow fades with the element: the shadow's opacity is driven by
    /// an expression on the host visual's Opacity, so it fades out with a
    /// sidebar label and never lingers as a ghost while the label is
    /// hidden. The shadow is also hidden while the element itself is
    /// Collapsed (the hero logo has no logo on some slides): the shadow
    /// lives in the parent, outside the collapsed element, so its
    /// visibility is tracked explicitly. The mask is re-taken when the
    /// text or the image source changes, so the glow follows the hero
    /// carousel and rows loading.
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
            // Children (VisualCollection) lives on ContainerVisual only,
            // not on the base Visual class — touching it on a Visual is
            // CS1061 (build 313). Store the parent as a ContainerVisual
            // (null when the parent visual is not a container); the null
            // guards in EnsureInserted and Detach then skip the glow
            // instead of crashing.
            public ContainerVisual ParentVisual;
            public Visual HostVisual;
            public bool IsInserted;
            public long TextChangedToken = -1;
            public long SourceChangedToken = -1;
            public long VisibilityChangedToken = -1;
            public int MaskGeneration;
            public bool OffsetTracked;
            public bool SizeTracked;
            public bool TransformTracked;
            public Vector3 LastOffset;
            public Vector2 LastSize;
            public Matrix4x4 LastTransform;
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
                element.LayoutUpdated += OnLayoutUpdated;
                if (element.IsLoaded) Attach(element);
            }
            else
            {
                element.Loaded -= OnLoaded;
                element.Unloaded -= OnUnloaded;
                element.SizeChanged -= OnSizeChanged;
                element.LayoutUpdated -= OnLayoutUpdated;
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

        private static void OnLayoutUpdated(object sender, object e)
        {
            if (sender is FrameworkElement element) SyncTracked(element);
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!(sender is FrameworkElement element)) return;
            RefreshMask(element);
            SyncTracked(element);
        }

        private static void Attach(FrameworkElement element)
        {
            if (GetState(element) != null) return;
            if (!(element is TextBlock) && !(element is FontIcon) && !(element is Image)) return;
            try
            {
                var parent = VisualTreeHelper.GetParent(element) as UIElement;
                if (parent == null)
                {
                    System.Diagnostics.Debug.WriteLine("TextGlow: no visual parent for " + element.GetType().Name);
                    return;
                }
                var state = new GlowState
                {
                    HostVisual = ElementCompositionPreview.GetElementVisual(element),
                    ParentVisual = ElementCompositionPreview.GetElementVisual(parent) as ContainerVisual,
                };
                SetState(element, state);

                // The shadow lives in the parent, outside the element, so
                // it does not hide itself when the element collapses (the
                // hero logo has no logo on some slides): track visibility
                // explicitly.
                state.VisibilityChangedToken = element.RegisterPropertyChangedCallback(
                    UIElement.VisibilityProperty,
                    (s, dp) => OnVisibilityChanged(s as FrameworkElement));

                if (element is TextBlock textBlock)
                {
                    CreateShadow(element, textBlock.GetAlphaMask());
                    // Text can change without changing size (same-length
                    // replacement), so track the property as well as size.
                    state.TextChangedToken = element.RegisterPropertyChangedCallback(
                        TextBlock.TextProperty,
                        (s, dp) => RefreshMask(s as FrameworkElement));
                }
                else if (element is FontIcon)
                {
                    // No GetAlphaMask on FontIcon: snapshot the rendered glyph
                    // once (the icons never change) and attach when ready.
                    AttachIconAsync(element);
                }
                else if (element is Image image)
                {
                    state.SourceChangedToken = element.RegisterPropertyChangedCallback(
                        Image.SourceProperty,
                        (s, dp) => RefreshImageMaskAsync(s as FrameworkElement));
                    image.ImageOpened += OnImageOpened;
                    RefreshImageMaskAsync(element);
                }
            }
            catch (Exception ex)
            {
                // A glow must never take the page down: leave the text plain.
                System.Diagnostics.Debug.WriteLine("TextGlow: attach failed for " + element.GetType().Name + ": " + ex.Message);
                SetState(element, null);
            }
        }

        private static void OnImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element) RefreshImageMaskAsync(element);
        }

        private static void OnVisibilityChanged(FrameworkElement element)
        {
            if (element == null) return;
            var state = GetState(element);
            if (state?.Visual == null) return;
            try
            {
                if (element.Visibility == Visibility.Visible)
                {
                    EnsureInserted(element, state);
                    state.Visual.IsVisible = true;
                }
                else
                {
                    state.Visual.IsVisible = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: visibility sync failed: " + ex.Message);
            }
        }

        private static void EnsureInserted(FrameworkElement element, GlowState state)
        {
            if (state.IsInserted || state.Visual == null || state.ParentVisual == null || state.HostVisual == null) return;
            try
            {
                state.ParentVisual.Children.InsertBelow(state.Visual, state.HostVisual);
                state.IsInserted = true;
            }
            catch (Exception ex)
            {
                // The host visual is not in the parent yet (element still
                // collapsed): the visibility callback retries on the next
                // change to Visible.
                System.Diagnostics.Debug.WriteLine("TextGlow: insert failed: " + ex.Message);
            }
        }

        private static async void AttachIconAsync(FrameworkElement element)
        {
            CompositionBrush mask = null;
            try
            {
                mask = await CreateSnapshotMaskAsync(element);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: icon mask failed: " + ex.Message);
            }
            if (mask == null) return;
            if (!element.IsLoaded || !GetIsEnabled(element)) return;
            if (GetState(element)?.Visual != null) return;
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
            var state = GetState(element);
            if (state == null || state.Visual != null) return;
            var hostVisual = state.HostVisual;
            var parentVisual = state.ParentVisual;
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
            // Seed the placement from the host, then glue it to the host
            // with expression animations: the glow tracks layout moves,
            // size changes and the icon jiggle transform.
            shadowVisual.Offset = hostVisual.Offset;
            shadowVisual.Size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
            shadowVisual.TransformMatrix = hostVisual.TransformMatrix;
            shadowVisual.IsVisible = element.Visibility == Visibility.Visible;
            state.LastOffset = shadowVisual.Offset;
            state.LastSize = shadowVisual.Size;
            state.LastTransform = shadowVisual.TransformMatrix;

            state.OffsetTracked = TryStartExpression(shadowVisual, "Offset", hostVisual, "host.Offset");
            state.SizeTracked = TryStartExpression(shadowVisual, "Size", hostVisual, "host.Size");
            state.TransformTracked = TryStartExpression(shadowVisual, "TransformMatrix", hostVisual, "host.TransformMatrix");

            state.Shadow = shadow;
            state.Visual = shadowVisual;
            EnsureInserted(element, state);
        }

        private static bool TryStartExpression(Visual target, string property, Visual host, string expression)
        {
            try
            {
                var animation = target.Compositor.CreateExpressionAnimation(expression);
                animation.SetReferenceParameter("host", host);
                target.StartAnimation(property, animation);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: expression failed for " + property + ": " + ex.Message);
                return false;
            }
        }

        private static void SyncTracked(FrameworkElement element)
        {
            // Fallback for any placement property whose expression failed to
            // start: copy the host's current values, but only when they
            // changed, so the per-layout-pass cost stays negligible.
            var state = GetState(element);
            if (state?.Visual == null || state.HostVisual == null) return;
            try
            {
                if (!state.OffsetTracked && state.HostVisual.Offset != state.LastOffset)
                {
                    state.Visual.Offset = state.HostVisual.Offset;
                    state.LastOffset = state.HostVisual.Offset;
                }
                if (!state.SizeTracked)
                {
                    var size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
                    if (size != state.LastSize)
                    {
                        state.Visual.Size = size;
                        state.LastSize = size;
                    }
                }
                if (!state.TransformTracked && state.HostVisual.TransformMatrix != state.LastTransform)
                {
                    state.Visual.TransformMatrix = state.HostVisual.TransformMatrix;
                    state.LastTransform = state.HostVisual.TransformMatrix;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: sync failed: " + ex.Message);
            }
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

        private static async void RefreshImageMaskAsync(FrameworkElement element)
        {
            var state = GetState(element);
            if (state == null || !(element is Image image)) return;
            var generation = ++state.MaskGeneration;
            CompositionBrush mask = null;
            try
            {
                mask = await CreateImageMaskAsync(image);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: image mask failed: " + ex.Message);
            }
            if (mask == null) return;
            // The slide may have changed again while the mask was loading:
            // only the newest mask may win, and only while still attached.
            if (!element.IsLoaded || !GetIsEnabled(element)) return;
            var current = GetState(element);
            if (current != state || current.MaskGeneration != generation) return;
            try
            {
                if (current.Visual == null)
                {
                    CreateShadow(element, mask);
                }
                else if (current.Shadow != null)
                {
                    current.Shadow.Mask = mask;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: image mask apply failed: " + ex.Message);
            }
        }

        private static async Task<CompositionBrush> CreateImageMaskAsync(Image image)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(image).Compositor;
            // Prefer loading the surface straight from the source URI: the
            // Image element sizes itself to the uniform-fitted logo, so the
            // full image fills the element box exactly (no letterboxing).
            if (image.Source is BitmapImage bitmap && bitmap.UriSource != null)
            {
                var surface = LoadedImageSurface.StartLoadFromUri(bitmap.UriSource);
                var brush = compositor.CreateSurfaceBrush(surface);
                brush.Stretch = CompositionStretch.Fill;
                return brush;
            }
            // Other sources (stream-backed BitmapImage, WriteableBitmap):
            // snapshot the rendered element like a FontIcon.
            if (image.Source == null) return null;
            return await CreateSnapshotMaskAsync(image);
        }

        private static async Task<CompositionBrush> CreateSnapshotMaskAsync(FrameworkElement element)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;

            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element);
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
                if (state.SourceChangedToken >= 0)
                {
                    element.UnregisterPropertyChangedCallback(Image.SourceProperty, state.SourceChangedToken);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: unregister failed: " + ex.Message);
            }
            try
            {
                if (state.VisibilityChangedToken >= 0)
                {
                    element.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, state.VisibilityChangedToken);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: unregister failed: " + ex.Message);
            }
            if (element is Image image)
            {
                image.ImageOpened -= OnImageOpened;
            }
            try
            {
                if (state.Visual != null)
                {
                    state.Visual.StopAnimation("Size");
                    state.Visual.StopAnimation("Offset");
                    state.Visual.StopAnimation("TransformMatrix");
                    if (state.IsInserted)
                    {
                        state.ParentVisual?.Children.Remove(state.Visual);
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
