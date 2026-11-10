using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Nuvio.Views
{
    /// <summary>
    /// Focus-driven scrolling for the M2 pages: the smooth scroller + row anchor
    /// HomePage grew in M1, ported verbatim from HomePage.xaml.cs (web
    /// js/core/focus.js smoothScrollTo, tau 105 ms) into one shared helper, so
    /// Search/Catalog get the same glide and the same anchor without touching
    /// HomePage — and without re-implementing the hard-won lessons per page:
    /// TransformToVisual reports RTL-origin rects, the row pins at ~16% of the
    /// page only when the row changes, and the card anchors to the row's start
    /// edge (right in RTL) instead of a BringIntoView ratio.
    ///
    /// Two shapes:
    /// - Row pages (Search): AnchorFocusedRowElement pins the focused card to the
    ///   row's start edge and the row to ~16% of the page, exactly like Home.
    /// - Wrapping grids (Catalog): KeepGridItemInView scrolls the minimal amount
    ///   to reveal the focused card (web scrollIntoView for the grid).
    /// </summary>
    internal sealed class FocusScrollHelper
    {
        private const double SmoothTauMs = 105;
        private const double SmoothSnapEpsilon = 0.6;

        /// <summary>Per-ScrollViewer glide state (web focus.js pendingScrolls).</summary>
        private sealed class SmoothScrollState
        {
            public ScrollViewer Viewer;
            public double? TargetH;
            public double? TargetV;
            public DateTime LastTime;
            public bool Running;
        }

        private readonly Dictionary<ScrollViewer, SmoothScrollState> _smoothScrolls = new Dictionary<ScrollViewer, SmoothScrollState>();
        private bool _smoothScrollHandlerAttached;
        private GridView _lastAnchoredRow;

        /// <summary>Stops every running glide and detaches the render loop (call on page leave).</summary>
        public void Stop()
        {
            foreach (var state in _smoothScrolls.Values.ToArray()) state.Running = false;
            _smoothScrolls.Clear();
            if (_smoothScrollHandlerAttached)
            {
                _smoothScrollHandlerAttached = false;
                CompositionTarget.Rendering -= OnSmoothScrollRendering;
            }
        }

        // ---------------------------------------------------------------------------
        // Smooth scrolling — a port of web/js/core/focus.js smoothScrollTo.
        // The web feels calm because each strip glides continuously toward a destination
        // that is retargeted every frame (exponential approach, tau 105 ms, no animation
        // queue). One loop per ScrollViewer, driven by CompositionTarget.Rendering;
        // retargeting only moves the destination, never restarts the glide.
        // ---------------------------------------------------------------------------

        /// <summary>Glides a ScrollViewer toward the given offsets (web smoothScrollTo).
        /// Retargeting only moves the destination; a running glide is never restarted.</summary>
        public void SmoothScrollTo(ScrollViewer viewer, double? targetH, double? targetV)
        {
            if (viewer == null) return;
            SmoothScrollState state;
            if (!_smoothScrolls.TryGetValue(viewer, out state))
            {
                state = new SmoothScrollState { Viewer = viewer };
                _smoothScrolls[viewer] = state;
            }
            if (targetH.HasValue) state.TargetH = Math.Max(0, Math.Min(viewer.ScrollableWidth, targetH.Value));
            if (targetV.HasValue) state.TargetV = Math.Max(0, Math.Min(viewer.ScrollableHeight, targetV.Value));
            state.LastTime = DateTime.UtcNow;
            if (!state.Running)
            {
                state.Running = true;
                if (!_smoothScrollHandlerAttached)
                {
                    _smoothScrollHandlerAttached = true;
                    CompositionTarget.Rendering += OnSmoothScrollRendering;
                }
            }
        }

        private void OnSmoothScrollRendering(object sender, object e)
        {
            var now = DateTime.UtcNow;
            var anyRunning = false;
            foreach (var pair in _smoothScrolls.ToArray())
            {
                var viewer = pair.Key;
                var state = pair.Value;
                if (viewer == null || !viewer.IsLoaded)
                {
                    state.Running = false;
                    if (viewer != null) _smoothScrolls.Remove(viewer);
                    continue;
                }
                // Web: elapsed clamped to [1, 32] ms per frame.
                var elapsedMs = Math.Min(32, Math.Max(1, (now - state.LastTime).TotalMilliseconds));
                state.LastTime = now;
                var active = false;
                if (state.TargetH.HasValue)
                {
                    var current = viewer.HorizontalOffset;
                    var distance = state.TargetH.Value - current;
                    if (Math.Abs(distance) < SmoothSnapEpsilon)
                    {
                        viewer.ChangeView(state.TargetH.Value, null, null, true);
                        state.TargetH = null;
                    }
                    else
                    {
                        var next = current + distance * (1 - Math.Exp(-elapsedMs / SmoothTauMs));
                        viewer.ChangeView(next, null, null, true);
                        active = true;
                    }
                }
                if (state.TargetV.HasValue)
                {
                    var current = viewer.VerticalOffset;
                    var distance = state.TargetV.Value - current;
                    if (Math.Abs(distance) < SmoothSnapEpsilon)
                    {
                        viewer.ChangeView(null, state.TargetV.Value, null, true);
                        state.TargetV = null;
                    }
                    else
                    {
                        var next = current + distance * (1 - Math.Exp(-elapsedMs / SmoothTauMs));
                        viewer.ChangeView(null, next, null, true);
                        active = true;
                    }
                }
                if (!active)
                {
                    state.Running = false;
                    _smoothScrolls.Remove(viewer);
                }
                else
                {
                    anyRunning = true;
                }
            }
            if (!anyRunning && _smoothScrollHandlerAttached)
            {
                _smoothScrollHandlerAttached = false;
                CompositionTarget.Rendering -= OnSmoothScrollRendering;
            }
        }

        /// <summary>
        /// Row-page anchor (HomePage.AnchorFocusedElement, minus hero/backdrop): the
        /// focused card stays at the row's start edge while the strip glides beneath
        /// it, and the focused row sits ~16% down the page. Entering a row from
        /// elsewhere only keeps the card inside the padded viewport instead of
        /// yanking the strip to the anchor, and the 16% pin applies only when the
        /// focused row changes — re-aligning on every horizontal move made the page
        /// bob while the row glided (the "two scrolls" feeling).
        /// </summary>
        public void AnchorFocusedRowElement(DependencyObject focused, ScrollViewer pageScroller)
        {
            var element = focused as UIElement;
            if (element == null) return;
            var row = FindAncestorGridView(focused);
            var item = FindAncestorGridViewItem(focused);
            var rowChanged = row == null || !ReferenceEquals(row, _lastAnchoredRow);
            if (row != null && item != null)
            {
                if (rowChanged) KeepCardInView(row, item);
                else AnchorRow(row, item);
            }
            if (rowChanged)
            {
                _lastAnchoredRow = row;
                if (row != null && pageScroller != null)
                {
                    AnchorPageVertical(row, pageScroller);
                }
            }
        }

        /// <summary>
        /// Minimal vertical scroll-into-view for a card of a wrapping grid (the
        /// catalog grid): scrolls only when the card sticks out past the padding,
        /// never yanks it to an anchor. Vertical edges are the same in both
        /// directions (only the horizontal axis has an RTL origin), so no
        /// physical-edge flip is needed here.
        /// </summary>
        public void KeepGridItemInView(GridView grid, GridViewItem item)
        {
            var viewer = FindScrollViewer(grid);
            if (viewer == null || item == null) return;
            var rect = item.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            const double pad = 12;
            var viewportHeight = viewer.ViewportHeight;
            if (viewportHeight <= 0) return;
            double? target = null;
            if (rect.Top < pad) target = viewer.VerticalOffset + (rect.Top - pad);
            else if (rect.Bottom > viewportHeight - pad) target = viewer.VerticalOffset + (rect.Bottom - (viewportHeight - pad));
            if (target.HasValue) SmoothScrollTo(viewer, null, target.Value);
        }

        /// <summary>Guards the row's flow end: a target offset must never leave the
        /// last card half-cut at the end edge. If the last item would be partially
        /// visible at the target, the target is nudged to the nearer offset where
        /// it is fully visible (its edge flush with the viewport edge — the end
        /// padding is 0 by Mohammed's call in build 241: the 68 epx end pad only
        /// disfigured the last poster) or fully hidden (its edge at the viewport
        /// edge). Skipped when the last container is not realized (then it is far
        /// off-screen, not partial) or the row does not scroll horizontally (the
        /// vertical catalog grid).</summary>
        private double SnapRowEnd(GridView row, ScrollViewer viewer, double target)
        {
            if (viewer.ScrollableWidth <= 0) return target;
            if (row.Items.Count == 0) return target;
            var last = row.ContainerFromIndex(row.Items.Count - 1) as GridViewItem;
            if (last == null) return target;
            var rect = last.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, last.ActualWidth, last.ActualHeight));
            var rtl = row.FlowDirection == FlowDirection.RightToLeft;
            var physicalLeft = rtl ? viewer.ViewportWidth - rect.Right : rect.Left;
            var physicalRight = rtl ? viewer.ViewportWidth - rect.Left : rect.Right;
            // The last item's edges at the target offset: in RTL the offset grows as
            // content moves right, in LTR as it moves left.
            var shift = rtl ? target - viewer.HorizontalOffset : viewer.HorizontalOffset - target;
            var lastLeft = physicalLeft + shift;
            var lastRight = physicalRight + shift;
            if (lastLeft >= 0 || lastRight <= 0) return target;
            // Build 241: no end padding at all — the last card ends flush with the
            // viewport edge (lastLeft 0) or hidden whole (lastRight 0), never
            // half-cut behind a gap.
            const double endPad = 0;
            var toVisible = endPad - lastLeft;
            var toHidden = -lastRight;
            var nudge = Math.Abs(toVisible) <= Math.Abs(toHidden) ? toVisible : toHidden;
            var snapped = rtl ? target + nudge : target - nudge;
            return Math.Max(0, Math.Min(viewer.ScrollableWidth, snapped));
        }

        /// <summary>Tidies a row that is already where it should be: if its last card
        /// is half-cut at the end edge from an earlier offset, scrolls it to the
        /// nearer fully-visible or fully-hidden offset (SnapRowEnd).</summary>
        private void TidyRowEnd(GridView row, ScrollViewer viewer)
        {
            var tidied = SnapRowEnd(row, viewer, viewer.HorizontalOffset);
            if (Math.Abs(tidied - viewer.HorizontalOffset) > 1) SmoothScrollTo(viewer, tidied, null);
        }

        /// <summary>Keeps a newly entered row's focused card inside the padded viewport
        /// (web scrollIntoView's row part): scrolls only when the card sticks out past
        /// the row padding, never yanks it to the anchor. The anchor is for moves
        /// inside the row (AnchorRow). The pads are 0 epx at the flow end (build 241:
        /// no end padding — the last card ends flush or hidden whole) and 6 epx at
        /// the flow start (the page's 68 epx start inset sits outside the track).
        /// The offset signs mirror AnchorRow: in RTL the offset grows as content
        /// moves right, in LTR as it moves left. The end is also tidied (SnapRowEnd)
        /// so a leftover offset never leaves the last card half-cut.</summary>
        private void KeepCardInView(GridView row, GridViewItem item)
        {
            var viewer = FindScrollViewer(row);
            if (viewer == null || item == null) return;
            var rect = item.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            var rtl = row.FlowDirection == FlowDirection.RightToLeft;
            // Physical edges (see AnchorRow): TransformToVisual is RTL-origin in RTL,
            // so rect.Left/Right are the start/end edges there, not the physical ones.
            var physicalLeft = rtl ? viewer.ViewportWidth - rect.Right : rect.Left;
            var physicalRight = rtl ? viewer.ViewportWidth - rect.Left : rect.Right;
            const double startPad = 6;
            const double endPad = 0;
            double? target = null;
            if (rtl)
            {
                if (physicalLeft < endPad) target = viewer.HorizontalOffset + (endPad - physicalLeft);
                else if (physicalRight > viewer.ViewportWidth - startPad) target = viewer.HorizontalOffset + ((viewer.ViewportWidth - startPad) - physicalRight);
            }
            else
            {
                if (physicalLeft < startPad) target = viewer.HorizontalOffset + (physicalLeft - startPad);
                else if (physicalRight > viewer.ViewportWidth - endPad) target = viewer.HorizontalOffset + (physicalRight - (viewer.ViewportWidth - endPad));
            }
            if (target.HasValue)
            {
                SmoothScrollTo(viewer, SnapRowEnd(row, viewer, target.Value), null);
            }
            else
            {
                // The focused card is already inside, but the row's end may still be
                // half-cut from an earlier offset: tidy it without moving the card.
                TidyRowEnd(row, viewer);
            }
        }

        /// <summary>Anchors a focused card to the row's flow-start edge (web
        /// focusInHorizontalRow): right in RTL, left in LTR, one card-padding (6 epx)
        /// inside the edge — the same place the first card rests. The offset is computed
        /// from the card's on-screen rect, so no ratio is guessed: BringIntoView ratios
        /// are fractions of the flow extent, which is why 0.99 pinned the card to the
        /// flow end (the left edge in RTL) on build 162 instead of the start.</summary>
        private void AnchorRow(GridView row, GridViewItem item)
        {
            var viewer = FindScrollViewer(row);
            if (viewer == null || item == null) return;
            var rect = item.TransformToVisual(viewer).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            var rtl = row.FlowDirection == FlowDirection.RightToLeft;
            // The row's own padding: the anchor sits where the first card rests (the web's
            // scroll-padding), and the 1.06 focus zoom only overflows ~2.6 epx past it.
            // TransformToVisual reports RTL-origin coordinates (x measured from the
            // flow-start edge): build 180 proved it — sidebarX=0 for a strip visibly at
            // 912..960 — so in RTL rect.Left is the card's START edge (physical right)
            // and rect.Right its END edge (physical left), the reverse of the physical
            // reading the previous anchor used. That pinned the focused card to the flow
            // END (physical left) and threw the row several cards off per press, which is
            // why the scroll felt broken, the first card showed as a sliver at the start
            // edge, and the target container could virtualize away mid-row (falling back
            // to a second XYFocus move). Flip to physical edges so the anchor reads the
            // same in both directions.
            var physicalLeft = rtl ? viewer.ViewportWidth - rect.Right : rect.Left;
            var physicalRight = rtl ? viewer.ViewportWidth - rect.Left : rect.Right;
            const double startInset = 6;
            double target;
            if (rtl)
            {
                // Start edge is the physical right; in RTL the offset grows as the content
                // moves right, so the item's right edge moves right with it.
                var anchor = viewer.ViewportWidth - startInset;
                var delta = anchor - physicalRight;
                if (Math.Abs(delta) <= 1)
                {
                    TidyRowEnd(row, viewer);
                    return;
                }
                target = viewer.HorizontalOffset + delta;
            }
            else
            {
                var anchor = startInset;
                var delta = physicalLeft - anchor;
                if (Math.Abs(delta) <= 1)
                {
                    TidyRowEnd(row, viewer);
                    return;
                }
                target = viewer.HorizontalOffset + delta;
            }
            SmoothScrollTo(viewer, SnapRowEnd(row, viewer, target), null);
        }

        /// <summary>Anchors the current row's block at 16% of the page height (web
        /// scrollIntoView), only when the row changes.</summary>
        private void AnchorPageVertical(GridView row, ScrollViewer scroller)
        {
            var section = FindRowSection(row);
            if (section == null) return;
            var rect = section.TransformToVisual(scroller).TransformBounds(new Rect(0, 0, section.ActualWidth, section.ActualHeight));
            var viewportHeight = scroller.ViewportHeight;
            if (viewportHeight <= 0) return;
            var anchor = viewportHeight * 0.16;
            var delta = rect.Top - anchor;
            if (Math.Abs(delta) <= 2) return;
            SmoothScrollTo(scroller, null, scroller.VerticalOffset + delta);
        }

        private static FrameworkElement FindRowSection(GridView row)
        {
            // The row's block is the DataTemplate root (header + GridView): the first
            // StackPanel above the GridView. Falls back to the row itself.
            for (var node = VisualTreeHelper.GetParent(row); node != null; node = VisualTreeHelper.GetParent(node))
            {
                var panel = node as StackPanel;
                if (panel != null) return panel;
                if (node is ItemsControl) break;
            }
            return row;
        }

        public static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var viewer = child as ScrollViewer;
                if (viewer != null) return viewer;
                viewer = FindScrollViewer(child);
                if (viewer != null) return viewer;
            }
            return null;
        }

        public static GridView FindAncestorGridView(DependencyObject element)
        {
            for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var grid = node as GridView;
                if (grid != null) return grid;
            }
            return null;
        }

        public static GridViewItem FindAncestorGridViewItem(DependencyObject element)
        {
            for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var item = node as GridViewItem;
                if (item != null) return item;
            }
            return null;
        }

        public static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
        {
            for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node == ancestor) return true;
            }
            return false;
        }
    }
}
