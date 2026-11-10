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
    ///
    /// Insertion is resolved from the Composition tree itself, not guessed
    /// from the XAML tree: the glow must be a sibling of the host visual,
    /// directly below it, and the only reliable way to find that sibling
    /// slot is the host visual's own Composition parent (Visual.Parent),
    /// falling back to the XAML ancestors via GetElementVisual cast to
    /// ContainerVisual. The first behind-text version took the XAML
    /// parent's GetElementVisual "as ContainerVisual" and silently
    /// skipped the glow when the cast failed — which is what happened on
    /// Xbox (build 314/315: no glow anywhere, no crash) — and it never
    /// retried, so one failed InsertBelow left the element glowless
    /// forever. This version retries the insert on every layout pass
    /// until it lands, and falls back to InsertAtBottom (no sibling
    /// needed) when InsertBelow cannot place below the host.
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
            // The container the glow is inserted into, resolved at insert
            // time from the Composition tree (see ResolveContainer) —
            // never assumed from the XAML parent. Children
            // (VisualCollection) lives on ContainerVisual only, not on
            // the base Visual class, and GetElementVisual returns a base
            // Visual handout, so taking the XAML parent's visual "as
            // ContainerVisual" can be null: the old code then skipped
            // the glow silently, which is what happened on Xbox
            // (build 314/315: no glow anywhere, no crash).
            public ContainerVisual Container;
            public Visual HostVisual;
            public bool IsInserted;
            // Correction from the host's placement to the container's
            // space, accumulated over every level between the host and
            // the container (zero / identity when the container is the
            // host's direct parent, which is the normal case).
            public Vector3 OffsetCorrection;
            public Matrix4x4 TransformCorrection;
            public bool HasCorrection;
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
                // The container is resolved lazily in EnsureInserted,
                // from the Composition tree itself: at attach time the
                // host visual may not have been parented yet.
                var state = new GlowState
                {
                    HostVisual = ElementCompositionPreview.GetElementVisual(element),
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
            if (state.IsInserted || state.Visual == null || state.HostVisual == null) return;
            try
            {
                // Resolve the container fresh on every attempt: at attach
                // time the host visual may not have been parented yet,
                // and the resolution result can change as the tree
                // settles. Re-resolving also keeps the correction in step
                // with the container.
                if (!ResolveContainer(element, state, out var container, out var offsetCorrection, out var transformCorrection))
                {
                    System.Diagnostics.Debug.WriteLine("TextGlow: no container visual found for " + element.GetType().Name);
                    return;
                }
                var correctionChanged = state.Container != container
                    || state.OffsetCorrection != offsetCorrection
                    || state.TransformCorrection != transformCorrection;
                state.Container = container;
                state.OffsetCorrection = offsetCorrection;
                state.TransformCorrection = transformCorrection;
                state.HasCorrection = offsetCorrection != Vector3.Zero || transformCorrection != Matrix4x4.Identity;
                if (correctionChanged)
                {
                    // The glow tracks the host through expressions that
                    // embed the correction: restart them against the new
                    // container.
                    StartTrackingExpressions(element, state);
                }
                // Never insert a visual that is already parented
                // elsewhere (a previous attempt that left it attached).
                if (state.Visual.Parent is ContainerVisual oldParent && oldParent != container)
                {
                    oldParent.Children.Remove(state.Visual);
                }
                try
                {
                    container.Children.InsertBelow(state.Visual, state.HostVisual);
                }
                catch
                {
                    // The host visual is not (yet) a child of this
                    // container: InsertBelow needs the sibling in the
                    // collection. The glow still belongs below the text,
                    // so put it at the bottom of the container instead —
                    // no sibling needed, same visual result for these
                    // containers (each holds only the text, or
                    // non-overlapping siblings).
                    container.Children.InsertAtBottom(state.Visual);
                }
                state.IsInserted = true;
            }
            catch (Exception ex)
            {
                // Not inserted yet (element still unparented, container
                // gone): SyncTracked retries on the next layout pass,
                // and the visibility callback retries on the next change
                // to Visible.
                System.Diagnostics.Debug.WriteLine("TextGlow: insert failed: " + ex.Message);
            }
        }

        // Finds the ContainerVisual the glow must live in: a sibling of
        // the host visual, directly below it. The Composition tree is
        // the ground truth — the host's Offset is relative to exactly
        // its Composition parent, so no correction is needed when that
        // parent is a container. If levels have to be skipped, their
        // placement is accumulated into the correction (going up one
        // level with placement (offset o, matrix m) maps a point p to
        // p * m + o).
        private static bool ResolveContainer(FrameworkElement element, GlowState state, out ContainerVisual container, out Vector3 offsetCorrection, out Matrix4x4 transformCorrection)
        {
            container = null;
            offsetCorrection = Vector3.Zero;
            transformCorrection = Matrix4x4.Identity;
            var hostVisual = state.HostVisual;
            if (hostVisual == null) return false;

            // 1) The host visual's own Composition parent chain.
            var composedOffset = hostVisual.Offset;
            var restMatrix = Matrix4x4.Identity;
            var current = hostVisual.Parent;
            for (var depth = 0; current != null && depth < 64; depth++)
            {
                if (current is ContainerVisual found)
                {
                    container = found;
                    offsetCorrection = composedOffset - Vector3.Transform(hostVisual.Offset, restMatrix);
                    transformCorrection = restMatrix;
                    return true;
                }
                composedOffset = Vector3.Transform(composedOffset, current.TransformMatrix) + current.Offset;
                restMatrix = Matrix4x4.Multiply(restMatrix, current.TransformMatrix);
                current = current.Parent;
            }

            // 2) Fallback: the XAML ancestors. GetElementVisual returns
            // a base Visual handout, which is not guaranteed to be a
            // ContainerVisual; cast it and walk up until one of the
            // ancestors yields a container. (GetContainerVisual is a
            // WinUI 3-only API and does not exist on UWP's
            // ElementCompositionPreview, so it cannot be used here.)
            composedOffset = hostVisual.Offset;
            restMatrix = Matrix4x4.Identity;
            var ancestor = VisualTreeHelper.GetParent(element) as UIElement;
            for (var depth = 0; ancestor != null && depth < 64; depth++)
            {
                var ancestorContainer = TryGetContainerVisual(ancestor);
                if (ancestorContainer != null)
                {
                    container = ancestorContainer;
                    offsetCorrection = composedOffset - Vector3.Transform(hostVisual.Offset, restMatrix);
                    transformCorrection = restMatrix;
                    return true;
                }
                try
                {
                    var ancestorVisual = ElementCompositionPreview.GetElementVisual(ancestor);
                    composedOffset = Vector3.Transform(composedOffset, ancestorVisual.TransformMatrix) + ancestorVisual.Offset;
                    restMatrix = Matrix4x4.Multiply(restMatrix, ancestorVisual.TransformMatrix);
                }
                catch
                {
                    break;
                }
                ancestor = VisualTreeHelper.GetParent(ancestor) as UIElement;
            }

            return false;
        }

        private static ContainerVisual TryGetContainerVisual(UIElement element)
        {
            try
            {
                var asContainer = ElementCompositionPreview.GetElementVisual(element) as ContainerVisual;
                if (asContainer != null) return asContainer;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: element visual failed: " + ex.Message);
            }
            return null;
        }

        private static void StartTrackingExpressions(FrameworkElement element, GlowState state)
        {
            var shadowVisual = state.Visual;
            var hostVisual = state.HostVisual;
            if (shadowVisual == null || hostVisual == null) return;
            // Seed the placement from the host plus the correction, then
            // glue it to the host with expression animations: the glow
            // tracks layout moves, size changes and the icon jiggle
            // transform. When the container is the host's direct parent
            // the correction is zero / identity and these are exactly
            // the plain host expressions.
            shadowVisual.Offset = Vector3.Transform(hostVisual.Offset, state.TransformCorrection) + state.OffsetCorrection;
            shadowVisual.Size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
            shadowVisual.TransformMatrix = Matrix4x4.Multiply(hostVisual.TransformMatrix, state.TransformCorrection);
            state.LastOffset = shadowVisual.Offset;
            state.LastSize = shadowVisual.Size;
            state.LastTransform = shadowVisual.TransformMatrix;

            if (state.HasCorrection)
            {
                state.OffsetTracked = TryStartCorrectedOffsetExpression(shadowVisual, hostVisual, state.OffsetCorrection, state.TransformCorrection);
                state.SizeTracked = TryStartExpression(shadowVisual, "Size", hostVisual, "host.Size");
                state.TransformTracked = TryStartCorrectedTransformExpression(shadowVisual, hostVisual, state.TransformCorrection);
            }
            else
            {
                state.OffsetTracked = TryStartExpression(shadowVisual, "Offset", hostVisual, "host.Offset");
                state.SizeTracked = TryStartExpression(shadowVisual, "Size", hostVisual, "host.Size");
                state.TransformTracked = TryStartExpression(shadowVisual, "TransformMatrix", hostVisual, "host.TransformMatrix");
            }
        }

        private static bool TryStartCorrectedOffsetExpression(Visual target, Visual host, Vector3 offsetCorrection, Matrix4x4 transformCorrection)
        {
            try
            {
                var animation = target.Compositor.CreateExpressionAnimation("host.Offset * restMatrix + restOffset");
                animation.SetReferenceParameter("host", host);
                animation.SetMatrix4x4Parameter("restMatrix", transformCorrection);
                animation.SetVector3Parameter("restOffset", offsetCorrection);
                target.StartAnimation("Offset", animation);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: corrected offset expression failed: " + ex.Message);
                return false;
            }
        }

        private static bool TryStartCorrectedTransformExpression(Visual target, Visual host, Matrix4x4 transformCorrection)
        {
            try
            {
                var animation = target.Compositor.CreateExpressionAnimation("host.TransformMatrix * restMatrix");
                animation.SetReferenceParameter("host", host);
                animation.SetMatrix4x4Parameter("restMatrix", transformCorrection);
                target.StartAnimation("TransformMatrix", animation);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TextGlow: corrected transform expression failed: " + ex.Message);
                return false;
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
            shadowVisual.IsVisible = element.Visibility == Visibility.Visible;
            state.Shadow = shadow;
            state.Visual = shadowVisual;
            // Seed the placement from the host, then glue it to the host
            // with expression animations: the glow tracks layout moves,
            // size changes and the icon jiggle transform.
            StartTrackingExpressions(element, state);
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
                // Retry a never-completed insert: the host visual may not
                // have been parented when Attach ran, and nothing else
                // retries — a single failed InsertBelow used to leave the
                // element glowless forever (build 314/315).
                if (!state.IsInserted)
                {
                    EnsureInserted(element, state);
                    if (!state.IsInserted) return;
                }
                if (!state.OffsetTracked)
                {
                    var offset = Vector3.Transform(state.HostVisual.Offset, state.TransformCorrection) + state.OffsetCorrection;
                    if (offset != state.LastOffset)
                    {
                        state.Visual.Offset = offset;
                        state.LastOffset = offset;
                    }
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
                if (!state.TransformTracked)
                {
                    var transform = Matrix4x4.Multiply(state.HostVisual.TransformMatrix, state.TransformCorrection);
                    if (transform != state.LastTransform)
                    {
                        state.Visual.TransformMatrix = transform;
                        state.LastTransform = transform;
                    }
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
                        state.Container?.Children.Remove(state.Visual);
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
