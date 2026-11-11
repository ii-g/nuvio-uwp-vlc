using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Composition;
using Nuvio.Diagnostics;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace Nuvio.Views.Shared
{
    /// <summary>
    /// The focus frame, markodenic edition: a bright white slice of a conic
    /// gradient that travels around the 3 epx ring, once per 3 s.
    ///
    /// Technique (from markodenic.com/snippets/css/animated-border): three
    /// layers. The ring is a band with the middle cut out (CSS does it with
    /// a two-layer mask, content-box vs full-box, mask-composite exclude).
    /// The light is a square filled with a conic gradient that is bright
    /// for a short slice and transparent everywhere else (the demo's short
    /// version: conic-gradient(from var(--angle), transparent 80%, #fff)),
    /// rotating one full turn. The square is scaled ~1.4x so it still covers
    /// the corners while it turns.
    ///
    /// UWP mapping: the conic layer is a static image, rendered once (a
    /// 256x256 white wedge, 72 degrees wide with a cosine falloff, peak
    /// alpha 255) and cached as a shared CompositionDrawingSurface, built
    /// synchronously on the UI thread at first attach — never
    /// re-evaluated per frame. One SpriteVisual per ring paints that image
    /// (CompositionSurfaceBrush, Fill) and rotates it with a SINGLE cheap
    /// ScalarKeyFrameAnimation on RotationAngle (0 to 2pi over 3 s, Forever,
    /// linear). The visual sits inside a ContainerVisual clipped to the
    /// ring band (a rounded-rect ring geometry: outer rect minus the rect
    /// inset by the 3 epx thickness, radii R and R-3), so the middle stays
    /// covered by the content and only the band shows the light — the
    /// clip is static, only the child rotates. The light host's Opacity is
    /// driven by an expression on the FocusRing Border's Opacity, so the
    /// existing VisualState storyboards (which animate the Border's Opacity
    /// 0/1) still own visibility, and the 1.06 focus scale is untouched.
    /// The Border keeps its static #80FFFFFF ring brush underneath: a
    /// constant faint frame the slice travels on, and the fallback if any
    /// step here fails.
    ///
    /// Cost: one static image shared by every ring, one scalar animation
    /// per ring, no per-stop gradient animations, no dependent (XAML)
    /// animations, no DelayTime of any kind. The rotation starts once at
    /// attach and is never stopped, restarted, or retimed by focus. Any
    /// failure leaves the static ring in place and logs; the page never
    /// crashes for a frame light.
    ///
    /// Insertion mirrors TextGlow: the light host is a child of the
    /// Border's parent ContainerVisual (the frame host Grid on cards, the
    /// RootGrid on buttons), inserted at the top so it paints above the
    /// content but only inside the ring clip, and placement is glued to
    /// the Border with expression animations (Offset/Size/TransformMatrix).
    /// If the parent is not ready at attach, LayoutUpdated retries until
    /// the insert lands. LayoutUpdated and SizeChanged also re-attempt
    /// Attach itself if it was ever missed, so a ring has no single
    /// trigger that can leave it lightless for the session.
    /// </summary>
    public static class RotatingFrameLight
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(RotatingFrameLight),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        private const double RotationDurationMs = 3000;
        private const float RingThickness = 3f;

        // The conic image: 256x256, white, bright for a 72 degree slice
        // (the demo's transparent 80% / white 20%) with a cosine falloff.
        private const int ConicSize = 256;
        private const double SliceHalfAngleRad = Math.PI * 36.0 / 180.0;

        private sealed class LightState
        {
            public Visual HostVisual;
            public ContainerVisual Container;
            public ContainerVisual LightHost;
            public SpriteVisual Light;
            public CompositionSurfaceBrush LightBrush;
            public bool IsInserted;
            public bool ClipReady;
            public int ClipAttempts;
            public double LastWidth = -1;
            public double LastHeight = -1;
            public double LastRadius = -1;
            public double AttemptedWidth = -1;
            public double AttemptedHeight = -1;
            public double AttemptedRadius = -1;
            public Vector2 LastHostSize = new Vector2(-1, -1);
            public float LastOpacity = -1f;
            public bool OffsetTracked;
            public bool SizeTracked;
            public bool TransformTracked;
            public bool OpacityTracked;
        }

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached(
                "State",
                typeof(LightState),
                typeof(RotatingFrameLight),
                new PropertyMetadata(null));

        private static LightState GetState(DependencyObject obj) => (LightState)obj.GetValue(StateProperty);
        private static void SetState(DependencyObject obj, LightState value) => obj.SetValue(StateProperty, value);

        // The conic image, built once synchronously on the UI thread and
        // shared by every ring. _conicSurfaceAttempted records that a build
        // was tried, so a broken device logs once instead of on every attach.
        private static readonly object SurfaceLock = new object();
        private static CompositionDrawingSurface _conicSurface;
        private static bool _conicSurfaceAttempted;

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is Border border)) return;
            if ((bool)e.NewValue)
            {
                border.Loaded += OnLoaded;
                border.Unloaded += OnUnloaded;
                border.SizeChanged += OnSizeChanged;
                border.LayoutUpdated += OnLayoutUpdated;
                Log.Write("RotatingFrameLight enabled");
                if (border.IsLoaded) Attach(border);
            }
            else
            {
                border.Loaded -= OnLoaded;
                border.Unloaded -= OnUnloaded;
                border.SizeChanged -= OnSizeChanged;
                border.LayoutUpdated -= OnLayoutUpdated;
                Detach(border);
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border border) Attach(border);
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border border) Detach(border);
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Border border)
            {
                var state = GetState(border);
                if (state == null)
                {
                    // Attach may have been missed: retry here — Attach is
                    // guarded, so repeated attempts are harmless.
                    Attach(border);
                    state = GetState(border);
                }
                if (state != null) SyncPlacement(border, state);
            }
        }

        private static void OnLayoutUpdated(object sender, object e)
        {
            if (!(sender is Border border)) return;
            var state = GetState(border);
            if (state == null)
            {
                // Attach may have been missed: retry here — Attach is
                // guarded, so repeated attempts are harmless.
                Attach(border);
                state = GetState(border);
                if (state == null) return;
            }
            if (!state.IsInserted)
            {
                EnsureInserted(border, state);
                if (!state.IsInserted) return;
            }
            SyncPlacement(border, state);
        }

        private static void Attach(Border border)
        {
            if (GetState(border) != null) return;
            try
            {
                var hostVisual = ElementCompositionPreview.GetElementVisual(border);
                var compositor = hostVisual.Compositor;
                var state = new LightState { HostVisual = hostVisual };
                SetState(border, state);
                Log.Write("RotatingFrameLight attached");

                var lightHost = compositor.CreateContainerVisual();
                var light = compositor.CreateSpriteVisual();
                var brush = compositor.CreateSurfaceBrush();
                brush.Stretch = CompositionStretch.Fill;
                light.Brush = brush;
                light.RotationAxis = new Vector3(0, 0, 1);
                lightHost.Children.InsertAtTop(light);
                state.LightHost = lightHost;
                state.Light = light;
                state.LightBrush = brush;

                // The one animation: a single scalar sweep of RotationAngle,
                // 0 to 2pi over 3 s, forever. Started once here, never
                // stopped, restarted, or retimed by focus.
                try
                {
                    var rotation = compositor.CreateScalarKeyFrameAnimation();
                    rotation.Duration = TimeSpan.FromMilliseconds(RotationDurationMs);
                    rotation.IterationBehavior = AnimationIterationBehavior.Forever;
                    var linear = compositor.CreateLinearEasingFunction();
                    rotation.InsertKeyFrame(0f, 0f, linear);
                    rotation.InsertKeyFrame(1f, (float)(Math.PI * 2));
                    light.StartAnimation("RotationAngle", rotation);
                }
                catch (Exception ex)
                {
                    Log.Write("RotatingFrameLight rotation failed: " + ex.Message);
                }

                // Visibility follows the Border: the VisualState storyboards
                // animate its Opacity, and the light fades with it. Seed it
                // from the host so the light starts in the right state even
                // if the expression below fails to start.
                lightHost.Opacity = hostVisual.Opacity;
                state.OpacityTracked = TryStartExpression(lightHost, "Opacity", hostVisual, "host.Opacity");

                // Seed the placement and glue it to the host with
                // expressions, so the light tracks layout moves and the
                // 1.06 focus scale (it lives inside the scaled subtree).
                SyncPlacement(border, state);
                EnsureInserted(border, state);

                // The conic surface is built synchronously, once, on the UI
                // thread and shared by every ring. If the build fails, the
                // static ring stays and the failure is logged; the light
                // simply never appears for any ring.
                var surface = GetOrBuildConicSurface(compositor);
                if (surface != null)
                {
                    state.LightBrush.Surface = surface;
                    Log.Write("RotatingFrameLight surface applied");
                }
            }
            catch (Exception ex)
            {
                // A frame light must never take the page down: the static
                // ring stays, and the light simply never appears.
                Log.Write("RotatingFrameLight attach failed: " + ex.Message);
                SetState(border, null);
            }
        }

        private static void EnsureInserted(Border border, LightState state)
        {
            if (state.IsInserted || state.LightHost == null || state.HostVisual == null) return;
            try
            {
                var parent = state.HostVisual.Parent as ContainerVisual;
                if (parent == null)
                {
                    // Not parented yet: LayoutUpdated retries.
                    return;
                }
                state.Container = parent;
                // Never insert a visual that is already parented elsewhere
                // (a previous attempt that left it attached).
                if (state.LightHost.Parent is ContainerVisual oldParent && oldParent != parent)
                {
                    oldParent.Children.Remove(state.LightHost);
                }
                if (state.LightHost.Parent == null)
                {
                    parent.Children.InsertAtTop(state.LightHost);
                }
                state.IsInserted = true;
            }
            catch (Exception ex)
            {
                // Not inserted yet: LayoutUpdated retries on the next pass.
                Log.Write("RotatingFrameLight insert failed: " + ex.Message);
            }
        }

        private static void SyncPlacement(Border border, LightState state)
        {
            if (state.LightHost == null || state.HostVisual == null) return;
            try
            {
                var host = state.HostVisual;
                if (!state.OffsetTracked)
                {
                    state.OffsetTracked = TryStartExpression(state.LightHost, "Offset", host, "host.Offset");
                    if (!state.OffsetTracked) state.LightHost.Offset = host.Offset;
                }
                if (!state.SizeTracked)
                {
                    state.SizeTracked = TryStartExpression(state.LightHost, "Size", host, "host.Size");
                    if (!state.SizeTracked) state.LightHost.Size = host.Size;
                }
                if (!state.TransformTracked)
                {
                    state.TransformTracked = TryStartExpression(state.LightHost, "TransformMatrix", host, "host.TransformMatrix");
                    if (!state.TransformTracked) state.LightHost.TransformMatrix = host.TransformMatrix;
                }

                // Fallback for an Opacity expression that failed to start:
                // copy the host opacity directly, only when it changed.
                if (!state.OpacityTracked)
                {
                    float opacity = host.Opacity;
                    if (opacity != state.LastOpacity)
                    {
                        state.LightHost.Opacity = opacity;
                        state.LastOpacity = opacity;
                    }
                }

                // Fallback for a Size expression that failed to start: copy
                // the host size directly, only when it changed.
                if (!state.SizeTracked)
                {
                    var size = host.Size;
                    if (size != state.LastHostSize)
                    {
                        state.LightHost.Size = size;
                        state.LastHostSize = size;
                    }
                }

                // Size-dependent parts: the rotating child's size and center
                // point, and the ring clip. Rebuilt only when the size or
                // the corner radius actually changed since the last good
                // build; a failed build gets a fresh retry budget only for
                // a new size, so a broken Win2D device cannot spam the log
                // on every layout pass.
                double w = border.ActualWidth;
                double h = border.ActualHeight;
                double r = border.CornerRadius.TopLeft;
                if (w <= 0 || h <= 0) return;
                if (w != state.LastWidth || h != state.LastHeight || r != state.LastRadius)
                {
                    if (w != state.LastWidth || h != state.LastHeight)
                    {
                        if (state.Light != null)
                        {
                            state.Light.Size = new Vector2((float)w, (float)h);
                            state.Light.CenterPoint = new Vector3((float)(w / 2), (float)(h / 2), 0);
                        }
                    }
                    if (w != state.AttemptedWidth || h != state.AttemptedHeight || r != state.AttemptedRadius)
                    {
                        state.ClipAttempts = 0;
                        state.AttemptedWidth = w;
                        state.AttemptedHeight = h;
                        state.AttemptedRadius = r;
                    }
                    if (state.ClipAttempts < 3)
                    {
                        state.ClipAttempts++;
                        if (RebuildClip(state, w, h, r))
                        {
                            state.ClipReady = true;
                            state.LastWidth = w;
                            state.LastHeight = h;
                            state.LastRadius = r;
                            state.LightHost.IsVisible = true;
                        }
                        else if (state.ClipAttempts >= 3)
                        {
                            // The clip will not build (no Win2D device):
                            // keep the light hidden so the conic image
                            // never washes over the content; the static
                            // ring stays.
                            state.LightHost.IsVisible = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("RotatingFrameLight sync failed: " + ex.Message);
            }
        }

        private static bool RebuildClip(LightState state, double w, double h, double r)
        {
            try
            {
                if (w <= 2 * RingThickness || h <= 2 * RingThickness) return false;
                var compositor = state.LightHost.Compositor;
                var device = CanvasDevice.GetSharedDevice();
                float fr = (float)r;
                float inner = Math.Max(0f, fr - RingThickness);
                var outer = CanvasGeometry.CreateRoundedRectangle(device, 0f, 0f, (float)w, (float)h, fr, fr);
                var innerRect = CanvasGeometry.CreateRoundedRectangle(device, RingThickness, RingThickness,
                    (float)(w - 2 * RingThickness), (float)(h - 2 * RingThickness), inner, inner);
                var ring = outer.CombineWith(innerRect, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
                var clip = compositor.CreateGeometricClip();
                clip.Geometry = compositor.CreatePathGeometry(new CompositionPath(ring));
                state.LightHost.Clip = clip;
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("RotatingFrameLight clip failed: " + ex.Message);
                return false;
            }
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
                Log.Write("RotatingFrameLight expression failed for " + property + ": " + ex.Message);
                return false;
            }
        }

        // The conic wedge pixels, kept exactly as verified: a 256x256 white
        // image, bright for a 72 degree slice centered on the top with a
        // cosine falloff, transparent everywhere else. Pixels are BGRA
        // premultiplied (white with alpha a is B=G=R=A=a).
        private static byte[] RenderConicPixels()
        {
            var pixels = new byte[ConicSize * ConicSize * 4];
            double half = SliceHalfAngleRad;
            for (int y = 0; y < ConicSize; y++)
            {
                for (int x = 0; x < ConicSize; x++)
                {
                    float dx = x - (ConicSize - 1) / 2f;
                    float dy = y - (ConicSize - 1) / 2f;
                    // 0 at the top, clockwise positive (CSS conic 0deg).
                    double angle = Math.Atan2(dx, -dy);
                    double ad = Math.Abs(angle);
                    byte a = 0;
                    if (ad < half)
                    {
                        double alpha = 0.5 * (1.0 + Math.Cos(ad * Math.PI / half));
                        a = (byte)(alpha * 255.0);
                    }
                    int i = (y * ConicSize + x) * 4;
                    pixels[i] = a;
                    pixels[i + 1] = a;
                    pixels[i + 2] = a;
                    pixels[i + 3] = a;
                }
            }
            return pixels;
        }

        // Builds the shared conic surface SYNCHRONOUSLY on the UI thread at
        // first attach: pixels -> CanvasBitmap -> CompositionDrawingSurface
        // (CanvasComposition drawing session). No async pipeline, no event,
        // no unverified load — the caller knows immediately whether the
        // surface exists. Returns null (and logs once) on failure; the
        // static ring stays and the light simply never appears.
        private static CompositionDrawingSurface GetOrBuildConicSurface(Compositor compositor)
        {
            lock (SurfaceLock)
            {
                if (_conicSurface != null) return _conicSurface;
                if (_conicSurfaceAttempted) return null;
                _conicSurfaceAttempted = true;
            }
            try
            {
                var device = CanvasDevice.GetSharedDevice();
                var pixels = RenderConicPixels();
                using (var bitmap = CanvasBitmap.CreateFromBytes(device, pixels, ConicSize, ConicSize,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f))
                {
                    var surface = CanvasComposition.CreateDrawingSurface(compositor,
                        new Size(ConicSize, ConicSize),
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        DirectXAlphaMode.Premultiplied);
                    using (var session = CanvasComposition.CreateDrawingSession(surface))
                    {
                        session.Clear(Colors.Transparent);
                        session.DrawImage(bitmap);
                    }
                    lock (SurfaceLock)
                    {
                        _conicSurface = surface;
                    }
                    return surface;
                }
            }
            catch (Exception ex)
            {
                Log.Write("RotatingFrameLight surface build failed: " + ex.Message);
                return null;
            }
        }

        private static void Detach(Border border)
        {
            var state = GetState(border);
            if (state == null) return;
            SetState(border, null);
            try
            {
                if (state.Light != null)
                {
                    state.Light.StopAnimation("RotationAngle");
                }
                if (state.LightHost != null)
                {
                    state.LightHost.StopAnimation("Opacity");
                    state.LightHost.StopAnimation("Offset");
                    state.LightHost.StopAnimation("Size");
                    state.LightHost.StopAnimation("TransformMatrix");
                    if (state.LightHost.Parent is ContainerVisual currentParent)
                    {
                        currentParent.Children.Remove(state.LightHost);
                    }
                }
                state.LightBrush?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Write("RotatingFrameLight detach failed: " + ex.Message);
            }
        }
    }
}
