// AnimatedFocusBorder — proof of concept (Phase 2a).
//
// One shared beam effect on the FIRST card of the first Home row only.
// Rendering proof only: it proves a beam can be drawn and moved by the
// compositor on Xbox. It owns no focus logic — it never reads focus state,
// never calls Focus(), and never touches FocusCoordinator, NativeInputRouter
// or any page.
//
// Why segments instead of one rotating ring image:
// A rigid transform cannot move a long beam along a rectangular perimeter.
// Rotating one ring image by 90 degrees maps the 88-wide top edge onto a
// vertical line 22 DIP outside the card's right edge, so the beam would
// detach from the card and sweep across it like a spinning frame. Instead
// each beam is eight short straight segments (16 DIP long, spaced 12 DIP
// apart, so a beam spans ~96 DIP = 22.5% of a poster's perimeter). Every
// segment is a tiny SpriteVisual sharing ONE surface (a horizontal white
// bar with a stepped glow baked in) and follows the rounded-rectangle
// perimeter with two infinite linear keyframe animations (Offset and
// rotation), both 5 seconds, driven entirely by the compositor. No timer,
// no render loop, no per-frame UI-thread work; the surface is drawn once
// and never redrawn, and the keyframes are rebuilt only when the target
// card changes size. Moving the card only moves the shared container
// visual's Offset.
//
// Kill switch: set IsEnabled to false and the beam hides; the white ring
// is untouched and keeps working on its own. Any failure hides the beam
// and logs one line — it can never crash the app or disturb focus.

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Composition;
using Nuvio.Diagnostics;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Display;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace Nuvio.Views.Shared
{
    internal sealed class AnimatedFocusBorder
    {
        /// <summary>Kill switch. False by default; MainPage enables it with one line for the PoC. Flip off to restore the white ring only.</summary>
        internal static bool IsEnabled = false;

        // Beam shape, from the reference: dual white beams, 4 DIP stroke, rounded-rect ring, corner radius 7 DIP, 5 second cycle.
        private const double CornerRadiusDip = 7.0;
        private const double CycleSeconds = 5.0;
        // Beam starts, from the reference: beam 1 spans 2.5% -> 25% and beam 2
        // spans 52.5% -> 75% of the perimeter, measured from top-centre clockwise.
        // Eight segments spaced 12 DIP apart span 96 DIP = 22.5% of the perimeter,
        // so each beam ends where the reference puts it.
        private const double Beam1StartFraction = 0.025;
        private const double Beam2StartFraction = 0.525;
        // The stroke is centred on the card edge, so the beam root covers the card
        // rect expanded by this much per side (2 DIP half-stroke + 1 DIP safety).
        private const double ExpansionDip = 3.0;
        // One segment: a 16 DIP bar on a 24x20 DIP surface (4 DIP margin each side,
        // 8 DIP above/below for the glow). Eight segments spaced 12 DIP apart span
        // 96 DIP = 22.5% of a poster's ~428 DIP perimeter.
        private const double SegmentSurfaceWidthDip = 24.0;
        private const double SegmentSurfaceHeightDip = 20.0;
        private const double SegmentBarLengthDip = 16.0;
        private const double SegmentSpacingDip = 12.0;
        private const int SegmentsPerBeam = 8;
        private const int KeyframeCount = 64;

        private Frame _frame;
        private Canvas _host;
        private UIElement _target;
        private Compositor _compositor;
        private CompositionGraphicsDevice _graphicsDevice;
        private CompositionDrawingSurface _beamSurface;
        private CompositionSurfaceBrush _beamBrush;
        private ContainerVisual _beamRoot;
        private readonly SpriteVisual[] _segments = new SpriteVisual[SegmentsPerBeam * 2];
        private double _lastRectWidth = -1;
        private double _lastRectHeight = -1;
        private bool _layoutSubscribed;
        private bool _animationsStarted;
        private bool _attachLogged;
        private bool _syncFailureLogged;

        /// <summary>Attaches the beam to the first realized card of the first GridView on the frame's current page. Retries on the next layout pass until a card exists. Never attaches to more than one element.</summary>
        public bool TryAttachFirstCard(Frame frame, Canvas host)
        {
            if (!IsEnabled) return false;
            try
            {
                _frame = frame;
                _host = host;
                EnsureCompositorAndSurface();
                AttachBeamRoot();
                if (!_layoutSubscribed)
                {
                    _host.LayoutUpdated += OnHostLayoutUpdated;
                    _layoutSubscribed = true;
                }
                return SyncTarget();
            }
            catch (Exception ex)
            {
                Log.Write("AnimatedFocusBorder: attach failed: " + ex.Message);
                HideBeam();
                return false;
            }
        }

        private void OnHostLayoutUpdated(object sender, object e)
        {
            SyncTarget();
        }

        private bool SyncTarget()
        {
            if (!IsEnabled)
            {
                HideBeam();
                return false;
            }
            try
            {
                if (_beamRoot == null)
                {
                    EnsureCompositorAndSurface();
                    AttachBeamRoot();
                }
                if (_target == null || !(_target is FrameworkElement loadedTarget && loadedTarget.IsLoaded))
                {
                    HideBeam();
                    _target = FindFirstCard();
                    if (_target == null) return false;
                    _lastRectWidth = -1;
                    _lastRectHeight = -1;
                    _attachLogged = false;
                }
                Rect rect = GetTargetRect(_target);
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    HideBeam();
                    return false;
                }
                _beamRoot.Offset = new Vector3((float)(rect.X - ExpansionDip), (float)(rect.Y - ExpansionDip), 0);
                _beamRoot.IsVisible = true;
                if (Math.Abs(rect.Width - _lastRectWidth) > 0.5 || Math.Abs(rect.Height - _lastRectHeight) > 0.5)
                {
                    _lastRectWidth = rect.Width;
                    _lastRectHeight = rect.Height;
                    RebuildAnimations(rect.Width, rect.Height);
                }
                if (!_attachLogged)
                {
                    _attachLogged = true;
                    Log.Write("AnimatedFocusBorder: attached to first card rect " + (int)rect.X + "," + (int)rect.Y + " " + (int)rect.Width + "x" + (int)rect.Height + " DIP");
                }
                _syncFailureLogged = false;
                return true;
            }
            catch (Exception ex)
            {
                if (!_syncFailureLogged)
                {
                    _syncFailureLogged = true;
                    Log.Write("AnimatedFocusBorder: sync failed: " + ex.Message);
                }
                HideBeam();
                return false;
            }
        }

        private void HideBeam()
        {
            if (_beamRoot != null) _beamRoot.IsVisible = false;
        }

        private void EnsureCompositorAndSurface()
        {
            if (_beamSurface != null) return;
            _compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
            CanvasDevice canvasDevice = CanvasDevice.GetSharedDevice();
            _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(_compositor, canvasDevice);
            _graphicsDevice.RenderingDeviceReplaced += OnRenderingDeviceReplaced;
            double scale = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
            int pixelWidth = Math.Max(1, (int)Math.Round(SegmentSurfaceWidthDip * scale));
            int pixelHeight = Math.Max(1, (int)Math.Round(SegmentSurfaceHeightDip * scale));
            _beamSurface = _graphicsDevice.CreateDrawingSurface(new Size(pixelWidth, pixelHeight), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
            // Draw once, in DIPs: a horizontal bar with a stepped glow baked in
            // (12 DIP faint, 8 DIP mid, 4 DIP bright core) so no shadow object
            // is needed per segment.
            float dpi = (float)(96.0 * scale);
            float barX0 = (float)((SegmentSurfaceWidthDip - SegmentBarLengthDip) / 2.0);
            float barX1 = (float)(SegmentSurfaceWidthDip - (SegmentSurfaceWidthDip - SegmentBarLengthDip) / 2.0);
            float barY = (float)(SegmentSurfaceHeightDip / 2.0);
            var strokeStyle = new CanvasStrokeStyle();
            // The session rect is in DIPs at the session DPI and must match the
            // surface size: pixels = DIPs x dpi / 96. (Pixel dimensions here
            // would double-count the scale on a 200% Xbox display.)
            using (CanvasDrawingSession ds = CanvasComposition.CreateDrawingSession(_beamSurface, new Rect(0, 0, SegmentSurfaceWidthDip, SegmentSurfaceHeightDip), dpi))
            {
                ds.DrawLine(new Vector2(barX0, barY), new Vector2(barX1, barY), Color.FromArgb(26, 255, 255, 255), 12.0f, strokeStyle);
                ds.DrawLine(new Vector2(barX0, barY), new Vector2(barX1, barY), Color.FromArgb(56, 255, 255, 255), 8.0f, strokeStyle);
                ds.DrawLine(new Vector2(barX0, barY), new Vector2(barX1, barY), Colors.White, 4.0f, strokeStyle);
            }
            _beamBrush = _compositor.CreateSurfaceBrush(_beamSurface);
            _beamBrush.Stretch = CompositionStretch.Fill;
            Log.Write("AnimatedFocusBorder: beam surface created " + pixelWidth + "x" + pixelHeight + " px (scale " + scale + ")");
        }

        private void AttachBeamRoot()
        {
            if (_beamRoot != null) return;
            _beamRoot = _compositor.CreateContainerVisual();
            _beamRoot.IsVisible = false;
            ElementCompositionPreview.SetElementChildVisual(_host, _beamRoot);
            for (int i = 0; i < _segments.Length; i++)
            {
                SpriteVisual segment = _compositor.CreateSpriteVisual();
                segment.Size = new Vector2((float)SegmentSurfaceWidthDip, (float)SegmentSurfaceHeightDip);
                segment.CenterPoint = new Vector3((float)(SegmentSurfaceWidthDip / 2.0), (float)(SegmentSurfaceHeightDip / 2.0), 0);
                segment.Brush = _beamBrush;
                segment.Opacity = 0f;
                segment.IsVisible = false;
                _beamRoot.Children.InsertAtTop(segment);
                _segments[i] = segment;
            }
        }

        private void OnRenderingDeviceReplaced(CompositionGraphicsDevice sender, object args)
        {
            try
            {
                Log.Write("AnimatedFocusBorder: rendering device replaced; recreating surface");
                _beamSurface = null;
                _beamBrush = null;
                EnsureCompositorAndSurface();
                if (_beamRoot != null)
                {
                    foreach (SpriteVisual segment in _segments)
                    {
                        if (segment != null) segment.Brush = _beamBrush;
                    }
                }
                _lastRectWidth = -1;
                _lastRectHeight = -1;
            }
            catch (Exception ex)
            {
                Log.Write("AnimatedFocusBorder: device-replaced recovery failed: " + ex.Message);
                HideBeam();
            }
        }

        private UIElement FindFirstCard()
        {
            Page page = _frame == null ? null : _frame.Content as Page;
            if (page == null) return null;
            // Pre-order walk, document order: the first GridView (in the tree) with
            // a realized first item wins. A GridView with no realized item is
            // skipped, and the walk continues into its children.
            var stack = new Stack<DependencyObject>();
            stack.Push(page);
            while (stack.Count > 0)
            {
                DependencyObject node = stack.Pop();
                GridView gridView = node as GridView;
                if (gridView != null)
                {
                    DependencyObject container = gridView.ContainerFromIndex(0);
                    UIElement item = container as UIElement;
                    if (item != null) return item;
                }
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = count - 1; i >= 0; i--)
                {
                    stack.Push(VisualTreeHelper.GetChild(node, i));
                }
            }
            return null;
        }

        private Rect GetTargetRect(UIElement element)
        {
            var frameworkElement = (FrameworkElement)element;
            return element.TransformToVisual(_host).TransformBounds(new Rect(0, 0, frameworkElement.ActualWidth, frameworkElement.ActualHeight));
        }

        private void RebuildAnimations(double cardWidth, double cardHeight)
        {
            try
            {
                RoundedRectPath path = BuildRoundedRectPath(cardWidth, cardHeight);
                double perimeter = path.Perimeter;
                if (perimeter <= 0) return;

                for (int s = 0; s < _segments.Length; s++)
                {
                    SpriteVisual segment = _segments[s];
                    if (segment == null) continue;
                    int beam = s / SegmentsPerBeam;
                    int j = s % SegmentsPerBeam;
                    double beamStart = (beam == 0 ? Beam1StartFraction : Beam2StartFraction) * perimeter;
                    double baseDistance = beamStart + (j + 0.5) * SegmentSpacingDip;
                    // Tail segment (j = 0) is dim, head segment (j = 7) is full white.
                    segment.Opacity = (float)(0.12 + 0.88 * j / (SegmentsPerBeam - 1));
                    segment.IsVisible = true;

                    var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
                    var rotationAnimation = _compositor.CreateScalarKeyFrameAnimation();
                    offsetAnimation.Duration = TimeSpan.FromSeconds(CycleSeconds);
                    rotationAnimation.Duration = TimeSpan.FromSeconds(CycleSeconds);
                    offsetAnimation.IterationBehavior = AnimationIterationBehavior.Forever;
                    rotationAnimation.IterationBehavior = AnimationIterationBehavior.Forever;

                    double previousAngle = double.NaN;
                    for (int k = 0; k <= KeyframeCount; k++)
                    {
                        float t = (float)k / KeyframeCount;
                        double distance = (baseDistance + t * perimeter) % perimeter;
                        Vector2 point;
                        double rawAngle;
                        SamplePath(path, distance, out point, out rawAngle);
                        // Unwrap the tangent angle so it rises monotonically 0 -> 360
                        // across the loop; otherwise the last keyframe would snap the
                        // rotation backwards instead of continuing forwards.
                        double angle;
                        if (double.IsNaN(previousAngle))
                        {
                            angle = rawAngle;
                        }
                        else
                        {
                            angle = rawAngle;
                            while (angle < previousAngle - 180.0) angle += 360.0;
                            while (angle > previousAngle + 180.0) angle -= 360.0;
                        }
                        previousAngle = angle;
                        offsetAnimation.InsertKeyFrame(t, new Vector3(point.X - (float)(SegmentSurfaceWidthDip / 2.0), point.Y - (float)(SegmentSurfaceHeightDip / 2.0), 0));
                        rotationAnimation.InsertKeyFrame(t, (float)angle);
                    }

                    // Starting a new animation on a property replaces the running one.
                    segment.StartAnimation("Offset", offsetAnimation);
                    segment.StartAnimation("RotationAngleInDegrees", rotationAnimation);
                }

                if (!_animationsStarted)
                {
                    _animationsStarted = true;
                    Log.Write("AnimatedFocusBorder: beam animations started (16 segments, 5.0s cycle)");
                }
            }
            catch (Exception ex)
            {
                Log.Write("AnimatedFocusBorder: rebuild failed: " + ex.Message);
                HideBeam();
            }
        }

        // Rounded-rectangle perimeter in pure C# math: 4 straight edges + 4
        // quarter-circle corners, sampled as a polyline starting at top-centre
        // and going clockwise (conic-gradient convention: 0% = top, clockwise).
        private sealed class RoundedRectPath
        {
            public List<Vector2> Points = new List<Vector2>();
            public List<double> Cumulative = new List<double>();
            public double Perimeter;
        }

        private RoundedRectPath BuildRoundedRectPath(double cardWidth, double cardHeight)
        {
            // Ring centreline rect in beam-root coordinates: the stroke is centred
            // on the card edge, so the centreline runs exactly along the card's bounds.
            double x0 = ExpansionDip;
            double y0 = ExpansionDip;
            double x1 = ExpansionDip + cardWidth;
            double y1 = ExpansionDip + cardHeight;
            double r = CornerRadiusDip;
            double cx = ExpansionDip + cardWidth / 2.0;

            var points = new List<Vector2>();
            // Top edge, right half: top-centre -> top-right corner start.
            AddPoint(points, cx, y0);
            AddPoint(points, x1 - r, y0);
            // Top-right corner: centre (x1-r, y0+r), -90deg -> 0deg.
            AddArc(points, x1 - r, y0 + r, -90, 0, r);
            // Right edge.
            AddPoint(points, x1, y1 - r);
            // Bottom-right corner: centre (x1-r, y1-r), 0deg -> 90deg.
            AddArc(points, x1 - r, y1 - r, 0, 90, r);
            // Bottom edge.
            AddPoint(points, x0 + r, y1);
            // Bottom-left corner: centre (x0+r, y1-r), 90deg -> 180deg.
            AddArc(points, x0 + r, y1 - r, 90, 180, r);
            // Left edge.
            AddPoint(points, x0, y0 + r);
            // Top-left corner: centre (x0+r, y0+r), 180deg -> 270deg.
            AddArc(points, x0 + r, y0 + r, 180, 270, r);
            // Top edge, left half: back to the start (top-centre).
            AddPoint(points, cx, y0);

            var path = new RoundedRectPath();
            path.Points = points;
            double total = 0;
            path.Cumulative.Add(0);
            for (int i = 1; i < points.Count; i++)
            {
                total += Vector2.Distance(points[i - 1], points[i]);
                path.Cumulative.Add(total);
            }
            path.Perimeter = total;
            return path;
        }

        private static void AddPoint(List<Vector2> points, double x, double y)
        {
            var p = new Vector2((float)x, (float)y);
            int n = points.Count;
            if (n > 0)
            {
                Vector2 last = points[n - 1];
                if (Math.Abs(last.X - p.X) < 0.001 && Math.Abs(last.Y - p.Y) < 0.001) return;
            }
            points.Add(p);
        }

        private static void AddArc(List<Vector2> points, double centerX, double centerY, double startDegrees, double endDegrees, double radius)
        {
            const double stepDegrees = 15.0;
            for (double a = startDegrees + stepDegrees; a < endDegrees; a += stepDegrees)
            {
                AddPoint(points, centerX + radius * Math.Cos(a * Math.PI / 180.0), centerY + radius * Math.Sin(a * Math.PI / 180.0));
            }
            AddPoint(points, centerX + radius * Math.Cos(endDegrees * Math.PI / 180.0), centerY + radius * Math.Sin(endDegrees * Math.PI / 180.0));
        }

        private static void SamplePath(RoundedRectPath path, double distance, out Vector2 point, out double angleDegrees)
        {
            var points = path.Points;
            var cumulative = path.Cumulative;
            int i = 0;
            while (i < points.Count - 2 && cumulative[i + 1] < distance)
            {
                i++;
            }
            double segmentLength = cumulative[i + 1] - cumulative[i];
            double f = segmentLength > 0 ? (distance - cumulative[i]) / segmentLength : 0;
            if (f < 0) f = 0;
            if (f > 1) f = 1;
            Vector2 a = points[i];
            Vector2 b = points[i + 1];
            point = new Vector2((float)(a.X + (b.X - a.X) * f), (float)(a.Y + (b.Y - a.Y) * f));
            angleDegrees = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180.0 / Math.PI;
            if (angleDegrees < 0) angleDegrees += 360.0;
        }
    }
}
