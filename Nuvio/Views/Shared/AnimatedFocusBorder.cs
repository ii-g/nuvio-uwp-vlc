// AnimatedFocusBorder — focus-following beam.
//
// One shared beam effect that circles the element with keyboard focus,
// wherever it is (hero buttons, cards, sidebar buttons). It is a visual
// observer only: it reads focus state (FocusManager.GetFocusedElement)
// and subscribes to FocusManager.GotFocus/LostFocus so it can retarget
// promptly, but it never moves focus — it never calls Focus(), and never
// touches FocusCoordinator, NativeInputRouter or any page. The white
// focus ring is untouched and keeps working on its own.
//
// Why segments instead of one rotating ring image:
// A rigid transform cannot move a long beam along a rectangular perimeter.
// Rotating one ring image by 90 degrees maps the 88-wide top edge onto a
// vertical line 22 DIP outside the card's right edge, so the beam would
// detach from the card and sweep across it like a spinning frame. Instead
// each beam is eleven short straight segments (16 DIP long, spaced 12 DIP
// apart, so a beam spans ~132 DIP = 22.5% of a 176x114 card's perimeter,
// the size the beam was tuned against). Every segment is a tiny
// SpriteVisual sharing ONE surface (a horizontal white bar with a stepped
// glow baked in) and follows the rounded-rectangle perimeter with two
// infinite linear keyframe animations (Offset and rotation), both 5
// seconds, driven entirely by the compositor. No timer, no render loop,
// no per-frame UI-thread work; the surface is drawn once (at 2x
// supersample, so the thin core stays smooth when segments rotate) and
// never redrawn, and the keyframes are rebuilt only when the target
// changes size. Moving the target only moves the shared container
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
using Nuvio.Views;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Display;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
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
        // Eleven segments spaced 12 DIP apart span 132 DIP, so on the 176x114
        // card the beam was tuned against each beam ends where the reference
        // puts it (22.5% of that card's ~568 DIP perimeter).
        private const double Beam1StartFraction = 0.025;
        private const double Beam2StartFraction = 0.525;
        // The stroke is centred on the card edge, so the beam root covers the
        // target rect expanded by this much per side. The value cancels out
        // (the path is built +Expansion and the root is offset -Expansion),
        // so the centreline stays exactly on the target's edge whatever the
        // value; it only needs to stay positive.
        private const double ExpansionDip = 3.0;
        // One segment: a 16 DIP bar on a 24x20 DIP surface (4 DIP margin each side,
        // 8 DIP above/below for the glow). Eleven segments spaced 12 DIP apart span
        // 132 DIP = 22.5% of a 176x114 card's ~568 DIP perimeter.
        private const double SegmentSurfaceWidthDip = 24.0;
        private const double SegmentSurfaceHeightDip = 20.0;
        private const double SegmentBarLengthDip = 16.0;
        private const double SegmentSpacingDip = 12.0;
        private const int SegmentsPerBeam = 11;
        private const int KeyframeCount = 64;
        // The surface is drawn at (RawPixelsPerViewPixel x SupersampleFactor) px
        // per DIP and downsampled by the brush onto the 24x20 DIP visual, so the
        // thin core stays smooth when segments rotate (a 1x surface showed
        // stair-stepped edges on device, build 605 video).
        private const double SupersampleFactor = 2.0;

        private Canvas _host;
        private UIElement _target;
        private DependencyObject _shellRoot;
        private Compositor _compositor;
        private CompositionGraphicsDevice _graphicsDevice;
        private CompositionDrawingSurface _beamSurface;
        private CompositionSurfaceBrush _beamBrush;
        private ContainerVisual _beamRoot;
        private readonly SpriteVisual[] _segments = new SpriteVisual[SegmentsPerBeam * 2];
        private double _lastRectWidth = -1;
        private double _lastRectHeight = -1;
        private bool _layoutSubscribed;
        private bool _focusSubscribed;
        private bool _animationsStarted;
        private bool _attachLogged;
        private bool _syncFailureLogged;
        private bool _surfaceDrawn;
        private bool _surfaceStepLogged;

        /// <summary>Attaches the beam to the window's focused element and follows focus from then on. Hides when nothing has focus, when focus is on a bare container, or when focus is inside a dialog/popup tree. Never moves focus.</summary>
        public bool TryAttach(Canvas host)
        {
            if (!IsEnabled) return false;
            try
            {
                _host = host;
                // Subscribe before creating the surface: if creation throws,
                // the next layout pass still retries through SyncTarget
                // instead of the beam dying for the whole session.
                if (!_layoutSubscribed)
                {
                    _host.LayoutUpdated += OnHostLayoutUpdated;
                    _layoutSubscribed = true;
                }
                if (!_focusSubscribed)
                {
                    FocusManager.GotFocus += (sender, args) => { SyncTarget(); };
                    FocusManager.LostFocus += (sender, args) => { SyncTarget(); };
                    _focusSubscribed = true;
                }
                EnsureCompositorAndSurface();
                AttachBeamRoot();
                _shellRoot = FindVisualRoot(_host);
                return SyncTarget();
            }
            catch (Exception ex)
            {
                Log.Write("AnimatedFocusBorder: attach failed: " + ex.Message + " (hr=0x" + ex.HResult.ToString("X8") + ")");
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
                if (_beamRoot == null || !_surfaceDrawn)
                {
                    EnsureCompositorAndSurface();
                    AttachBeamRoot();
                }
                // The shell root is found in TryAttach, but a retry after a
                // failed attach reaches here with it still null.
                if (_shellRoot == null && _host != null) _shellRoot = FindVisualRoot(_host);
                // Re-read the current focus on every sync: the target may
                // have moved (focus events), been unloaded (page navigation
                // destroys pages), or never existed yet (before the settle).
                // The beam follows focus honestly — it never picks a target
                // itself, and there is no fallback to any card.
                DependencyObject focused = FocusManager.GetFocusedElement() as DependencyObject;
                UIElement focusedElement = focused as UIElement;
                if (!ReferenceEquals(_target, focusedElement))
                {
                    if (!IsBeamTarget(focused))
                    {
                        _target = null;
                        HideBeam();
                        return false;
                    }
                    _target = focusedElement;
                    _lastRectWidth = -1;
                    _lastRectHeight = -1;
                    _attachLogged = false;
                }
                if (_target == null)
                {
                    HideBeam();
                    return false;
                }
                if (!(_target is FrameworkElement loadedTarget && loadedTarget.IsLoaded))
                {
                    HideBeam();
                    return false;
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
                    Log.Write("AnimatedFocusBorder: attached to " + DescribeTarget(_target) + " rect " + (int)rect.X + "," + (int)rect.Y + " " + (int)rect.Width + "x" + (int)rect.Height + " DIP");
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
            // The surface may exist while the draw never ran (CreateDrawingSession
            // threw E_INVALIDARG on the first Xbox attempt): retry the draw until
            // it succeeds instead of attaching an invisible beam.
            if (_beamSurface != null && _surfaceDrawn && _beamBrush != null) return;
            // Diagnostic: name the failing step — the first device log only
            // carried the generic E_INVALIDARG message.
            string step = "GetElementVisual";
            try
            {
            if (_beamSurface == null)
            {
            _compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
            step = "GetSharedDevice";
            CanvasDevice canvasDevice = CanvasDevice.GetSharedDevice();
            step = "CreateCompositionGraphicsDevice";
            _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(_compositor, canvasDevice);
            _graphicsDevice.RenderingDeviceReplaced += OnRenderingDeviceReplaced;
            step = "RawPixelsPerViewPixel";
            double scale = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
            int pixelWidth = Math.Max(1, (int)Math.Round(SegmentSurfaceWidthDip * scale * SupersampleFactor));
            int pixelHeight = Math.Max(1, (int)Math.Round(SegmentSurfaceHeightDip * scale * SupersampleFactor));
            step = "CreateDrawingSurface";
            _beamSurface = _graphicsDevice.CreateDrawingSurface(new Size(pixelWidth, pixelHeight), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
            Log.Write("AnimatedFocusBorder: beam surface created " + pixelWidth + "x" + pixelHeight + " px (scale " + scale + ", supersample " + SupersampleFactor + ")");
            }
            if (!_surfaceDrawn)
            {
            // Draw with the plain no-rect session: the rect+DPI overload
            // failed with E_INVALIDARG on the first Xbox attempt, and this
            // is the form the Win2D samples use. Geometry is scaled to
            // pixels explicitly (drawScale = display scale x supersample,
            // so the surface is drawn at 2x and downsampled by the brush
            // onto the 24x20 DIP visual). Bar: 7 DIP faint, 5 DIP mid,
            // 4 DIP bright core, so no shadow object is needed per segment.
            step = "CreateDrawingSession";
            double drawScale = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel * SupersampleFactor;
            using (CanvasDrawingSession ds = CanvasComposition.CreateDrawingSession(_beamSurface))
            {
                ds.Clear(Colors.Transparent);
                float barX0 = (float)((SegmentSurfaceWidthDip - SegmentBarLengthDip) / 2.0 * drawScale);
                float barX1 = (float)((SegmentSurfaceWidthDip + SegmentBarLengthDip) / 2.0 * drawScale);
                float barY = (float)(SegmentSurfaceHeightDip / 2.0 * drawScale);
                var strokeStyle = new CanvasStrokeStyle();
                ds.DrawLine(new Vector2(barX0, barY), new Vector2(barX1, barY), Color.FromArgb(26, 255, 255, 255), (float)(7.0 * drawScale), strokeStyle);
                ds.DrawLine(new Vector2(barX0, barY), new Vector2(barX1, barY), Color.FromArgb(56, 255, 255, 255), (float)(5.0 * drawScale), strokeStyle);
                ds.DrawLine(new Vector2(barX0, barY), new Vector2(barX1, barY), Colors.White, (float)(4.0 * drawScale), strokeStyle);
            }
            step = "CreateSurfaceBrush";
            _beamBrush = _compositor.CreateSurfaceBrush(_beamSurface);
            _beamBrush.Stretch = CompositionStretch.Fill;
            foreach (SpriteVisual segment in _segments)
            {
                if (segment != null) segment.Brush = _beamBrush;
            }
            _surfaceDrawn = true;
            _surfaceStepLogged = false;
            }
            }
            catch (Exception ex)
            {
                if (!_surfaceStepLogged)
                {
                    _surfaceStepLogged = true;
                    Log.Write("AnimatedFocusBorder: surface step '" + step + "' failed: " + ex.Message + " (hr=0x" + ex.HResult.ToString("X8") + ")");
                }
                throw;
            }
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
                _surfaceDrawn = false;
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

        /// <summary>True when the focused element is one the beam may circle: a real focus target (same rule as FocusablePage.IsRealFocusTarget), a FrameworkElement (the rect comes from ActualWidth/ActualHeight), and inside the shell visual tree.</summary>
        private bool IsBeamTarget(DependencyObject focused)
        {
            if (focused == null) return false;
            // A plain ItemsControl (not a ListViewBase) is focusable in UWP
            // but draws no ring, so the beam must not circle it either.
            if (!FocusablePage.IsRealFocusTarget(focused)) return false;
            if (!(focused is FrameworkElement)) return false;
            // Dialogs and popups live in their own visual tree (PopupRoot),
            // so walking up from a dialog element never reaches the shell
            // root. The beam stays on the shell content only — and that
            // includes the sidebar buttons, which are in the shell tree.
            return IsInShellTree(focused);
        }

        private bool IsInShellTree(DependencyObject element)
        {
            if (_shellRoot == null) return false;
            DependencyObject node = element;
            while (node != null)
            {
                if (ReferenceEquals(node, _shellRoot)) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        private static DependencyObject FindVisualRoot(DependencyObject element)
        {
            if (element == null) return null;
            DependencyObject root = element;
            DependencyObject parent = VisualTreeHelper.GetParent(element);
            while (parent != null)
            {
                root = parent;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return root;
        }

        private static string DescribeTarget(UIElement target)
        {
            string type = target.GetType().Name;
            FrameworkElement fe = target as FrameworkElement;
            if (fe != null && !string.IsNullOrEmpty(fe.Name)) return type + " '" + fe.Name + "'";
            if (fe != null && fe.Tag is string tag && !string.IsNullOrEmpty(tag)) return type + " '" + tag + "'";
            return type;
        }

        private Rect GetTargetRect(UIElement element)
        {
            var frameworkElement = (FrameworkElement)element;
            return element.TransformToVisual(_host).TransformBounds(new Rect(0, 0, frameworkElement.ActualWidth, frameworkElement.ActualHeight));
        }

        private void RebuildAnimations(double targetWidth, double targetHeight)
        {
            try
            {
                RoundedRectPath path = BuildRoundedRectPath(targetWidth, targetHeight);
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
                    // Tail segment (j = 0) is dim, head segment (j = 10) is full white.
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
                    Log.Write("AnimatedFocusBorder: beam animations started (" + _segments.Length + " segments, 5.0s cycle)");
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

        private RoundedRectPath BuildRoundedRectPath(double targetWidth, double targetHeight)
        {
            // Ring centreline rect in beam-root coordinates: the stroke is centred
            // on the target edge, so the centreline runs exactly along the target's bounds.
            double x0 = ExpansionDip;
            double y0 = ExpansionDip;
            double x1 = ExpansionDip + targetWidth;
            double y1 = ExpansionDip + targetHeight;
            double r = CornerRadiusDip;
            double cx = ExpansionDip + targetWidth / 2.0;

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
