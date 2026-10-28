using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>What the floor probe hit: the top of the floor and which element it is.</summary>
    public class FloorHit
    {
        public double Z;
        public string Describe;   // "<model> : <category> <id> — <type>"
    }

    /// <summary>
    /// Finds the floor under a wall opening: step along the duct past both wall faces
    /// and shoot a ray straight down to the nearest floor or roof of the ticked models.
    /// Same approach as Ceiling Attach's FloorFinder, but looking down.
    /// </summary>
    public class FloorProbe
    {
        private const string ViewName = "_Openings_SearchView";

        private readonly Document _doc;
        private readonly ReferenceIntersector _intersector;
        private readonly double _offset;

        // Only the models ticked in the window count: a floor in an unticked link is ignored.
        private readonly HashSet<ElementId> _links = new HashSet<ElementId>();
        private readonly bool _currentModel;

        /// <summary>Must be created inside an open transaction (it may create the search view).</summary>
        public FloorProbe(Document doc, double offsetPastFace, IEnumerable<HostSource> sources)
        {
            _doc = doc;
            _offset = offsetPastFace;
            foreach (HostSource src in sources)
            {
                if (src.Instance != null) _links.Add(src.Instance.Id);
                else _currentModel = true;
            }
            View3D view = GetOrCreateView(doc);
            // Roofs too: a wall opening above a roof terrace stands on the roof, not on the floor below it.
            ElementFilter filter = new LogicalOrFilter(new List<ElementFilter>
            {
                new ElementClassFilter(typeof(Floor)),
                new ElementClassFilter(typeof(RoofBase))
            });
            _intersector = new ReferenceIntersector(filter, FindReferenceTarget.Face, view)
            {
                FindReferencesInRevitLinks = true
            };
        }

        /// <summary>
        /// Top of the floor below the opening, or null if nothing was hit on either side.
        /// When the two sides stand on different floors, the higher one wins (the opening's
        /// height above the floor is quoted from the side closer to it).
        ///
        /// The ray starts at the opening's centre height, not its bottom edge: an opening on a duct
        /// lying on the floor reaches into the slab by its margin, and a ray from there would miss
        /// that floor. Only upward-facing faces count, so a start point that lands inside a slab
        /// (a duct tight under a thicker or stepped slab above) does not take that slab's underside
        /// for the floor.
        /// </summary>
        public FloorHit FloorUnder(OpeningPlan plan)
        {
            XYZ wallNormal = plan.Frame.Y;
            XYZ dir = plan.ProbeAxis;
            double cos = Math.Abs(dir.DotProduct(wallNormal));
            if (cos < 0.2) { dir = wallNormal; cos = 1; }

            // Distance along the duct from the centre to just past each wall face.
            double reach = plan.Depth / 2 / cos + _offset;

            FloorHit best = null;
            for (int side = -1; side <= 1; side += 2)
            {
                XYZ origin = plan.Center + dir * (side * reach);
                ReferenceWithContext hit = _intersector.Find(origin, -XYZ.BasisZ)
                    .Where(h => h.Proximity > 1e-6 && IsTicked(h.GetReference()))
                    .OrderBy(h => h.Proximity)
                    .FirstOrDefault(h => FacesUp(h.GetReference()));
                if (hit == null) continue;

                double z = origin.Z - hit.Proximity;
                if (best == null || z > best.Z) best = new FloorHit { Z = z, Describe = Describe(hit.GetReference()) };
            }
            return best;
        }

        private bool IsTicked(Reference r) =>
            r.LinkedElementId != ElementId.InvalidElementId ? _links.Contains(r.ElementId) : _currentModel;

        /// <summary>Whether the hit face looks up (a floor top); true when the face cannot be read.</summary>
        private bool FacesUp(Reference r)
        {
            try
            {
                Face face;
                Transform toHost = Transform.Identity;
                if (r.LinkedElementId != ElementId.InvalidElementId)
                {
                    RevitLinkInstance link = _doc.GetElement(r.ElementId) as RevitLinkInstance;
                    Document linkDoc = link?.GetLinkDocument();
                    if (linkDoc == null) return true;
                    face = linkDoc.GetElement(r.LinkedElementId)?.GetGeometryObjectFromReference(r.CreateReferenceInLink()) as Face;
                    toHost = link.GetTotalTransform();
                }
                else
                    face = _doc.GetElement(r.ElementId)?.GetGeometryObjectFromReference(r) as Face;
                if (face == null) return true;

                IntersectionResult ir = face.Project(toHost.Inverse.OfPoint(r.GlobalPoint));
                if (ir == null) return true;
                return toHost.OfVector(face.ComputeNormal(ir.UVPoint)).Z > 0.1;
            }
            catch { return true; }
        }

        private string Describe(Reference r)
        {
            string source = "Current model";
            Element e;
            if (r.LinkedElementId != ElementId.InvalidElementId)
            {
                Document linkDoc = (_doc.GetElement(r.ElementId) as RevitLinkInstance)?.GetLinkDocument();
                if (linkDoc == null) return "?";
                source = linkDoc.Title;
                e = linkDoc.GetElement(r.LinkedElementId);
            }
            else
                e = _doc.GetElement(r.ElementId);

            if (e == null) return source + " : ?";
            string type = e.Document.GetElement(e.GetTypeId())?.Name;
            return $"{source} : {e.Category?.Name} {e.Id.GetValue()}" + (type != null ? $" — {type}" : "");
        }

        private static View3D GetOrCreateView(Document doc)
        {
            View3D view = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == ViewName);

            if (view == null)
            {
                ViewFamilyType vft = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .First(v => v.ViewFamily == ViewFamily.ThreeDimensional);

                view = View3D.CreateIsometric(doc, vft.Id);
                view.Name = ViewName;
                view.DetailLevel = ViewDetailLevel.Fine;
                view.ViewTemplateId = ElementId.InvalidElementId;

                foreach (Category cat in doc.Settings.Categories)
                {
                    try { view.SetCategoryHidden(cat.Id, true); }
                    catch { }
                }
                UnhideCategory(doc, view, BuiltInCategory.OST_Floors);
                UnhideCategory(doc, view, BuiltInCategory.OST_RvtLinks);
            }
            // Views made before roofs were probed have them hidden.
            UnhideCategory(doc, view, BuiltInCategory.OST_Roofs);

            if (view.ViewTemplateId != ElementId.InvalidElementId)
                view.ViewTemplateId = ElementId.InvalidElementId;
            if (view.IsSectionBoxActive)
                view.IsSectionBoxActive = false;

            doc.Regenerate();
            return view;
        }

        private static void UnhideCategory(Document doc, View3D view, BuiltInCategory bic)
        {
            Category cat = doc.Settings.Categories.get_Item(bic);
            if (cat != null)
                view.SetCategoryHidden(cat.Id, false);
        }
    }
}
