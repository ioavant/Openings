using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>
    /// Elements of a linked document that show in a view of the host document.
    ///
    /// Revit 2024 added the (document, view, link) collector that answers this exactly.
    /// The net8 build (2025+) calls it directly. The net48 build is compiled against the
    /// 2022 API: it finds that constructor by reflection on Revit 2024 and, on 2022/2023,
    /// approximates the view by its visible volume (section box, crop box, view range).
    /// </summary>
    public static class LinkViewCollector
    {
        // Large enough to mean "unbounded", small enough to survive a link transform.
        private const double Far = 1e5;

#if NETFRAMEWORK
        private static readonly ConstructorInfo LinkViewCtor = typeof(FilteredElementCollector).GetConstructor(
            new[] { typeof(Document), typeof(ElementId), typeof(ElementId) });
#endif

        public static FilteredElementCollector Collect(View view, RevitLinkInstance link, Document linkDoc)
        {
#if NETFRAMEWORK
            if (LinkViewCtor != null)
                return (FilteredElementCollector)LinkViewCtor.Invoke(new object[] { view.Document, view.Id, link.Id });
            return CollectApproximate(view, link, linkDoc);
#else
            return new FilteredElementCollector(view.Document, view.Id, link.Id);
#endif
        }

        /// <summary>Revit 2022/2023: link elements whose box touches the view's visible volume.</summary>
        private static FilteredElementCollector CollectApproximate(View view, RevitLinkInstance link, Document linkDoc)
        {
            FilteredElementCollector collector = new FilteredElementCollector(linkDoc);
            List<XYZ> corners = VisibleCorners(view);
            if (corners == null) return collector;

            Transform toLink = link.GetTotalTransform().Inverse;
            XYZ min = null, max = null;
            foreach (XYZ c in corners)
            {
                XYZ p = toLink.OfPoint(c);
                min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
            return collector.WherePasses(new BoundingBoxIntersectsFilter(new Outline(min, max)));
        }

        /// <summary>Corners of the volume the view shows, in host model coordinates; null = unbounded.</summary>
        private static List<XYZ> VisibleCorners(View view)
        {
            if (view is View3D v3)
                return v3.IsSectionBoxActive ? BoxCorners(v3.GetSectionBox()) : null;

            if (view is ViewPlan plan)
            {
                double minX = -Far, minY = -Far, maxX = Far, maxY = Far;
                if (plan.CropBoxActive)
                {
                    List<XYZ> crop = BoxCorners(plan.CropBox);
                    minX = maxX = crop[0].X;
                    minY = maxY = crop[0].Y;
                    foreach (XYZ c in crop)
                    {
                        minX = Math.Min(minX, c.X); maxX = Math.Max(maxX, c.X);
                        minY = Math.Min(minY, c.Y); maxY = Math.Max(maxY, c.Y);
                    }
                }
                PlanViewRange range = plan.GetViewRange();
                double minZ = RangeElevation(plan, range, PlanViewPlane.ViewDepthPlane, -Far);
                double maxZ = RangeElevation(plan, range, PlanViewPlane.TopClipPlane, Far);
                if (minZ > maxZ) { double t = minZ; minZ = maxZ; maxZ = t; }   // ceiling plans look up
                return new List<XYZ> { new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ) };
            }

            // Sections, elevations: the crop box includes the far clip depth.
            return view.CropBoxActive ? BoxCorners(view.CropBox) : null;
        }

        private static double RangeElevation(ViewPlan plan, PlanViewRange range, PlanViewPlane plane, double unbounded)
        {
            ElementId levelId = range.GetLevelId(plane);
            if (plan.Document.GetElement(levelId) is Level level)
                return level.Elevation + range.GetOffset(plane);
            if (levelId == PlanViewRange.Current && plan.GenLevel != null)
                return plan.GenLevel.Elevation + range.GetOffset(plane);
            return unbounded;   // Unlimited, Level Above / Below
        }

        private static List<XYZ> BoxCorners(BoundingBoxXYZ box)
        {
            List<XYZ> corners = new List<XYZ>(8);
            for (int i = 0; i < 8; i++)
            {
                XYZ local = new XYZ(
                    (i & 1) == 0 ? box.Min.X : box.Max.X,
                    (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                    (i & 4) == 0 ? box.Min.Z : box.Max.Z);
                corners.Add(box.Transform.OfPoint(local));
            }
            return corners;
        }
    }
}
