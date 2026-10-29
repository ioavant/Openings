using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Openings
{
    /// <summary>
    /// Temporary section through a wall opening, looking at the wall face: the opening with a margin
    /// around it, down to the floor below when that is known. Named "_Openings_Section …"; the
    /// previous ones are removed when the next is made and when the palette closes (never the
    /// active view, which Revit does not allow to delete).
    /// </summary>
    public static class OpeningSection
    {
        public const string Prefix = "_Openings_Section";

        private static readonly double Around = 1000 / 304.8;    // margin around the opening
        private static readonly double Beyond = 300 / 304.8;     // past each wall face

        /// <summary>API context, outside a transaction: makes the section and opens it.</summary>
        public static void Show(UIDocument uidoc, WallOpening w)
        {
            Document doc = uidoc.Document;
            ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Section);
            if (vft == null)
            {
                TaskDialog.Show("Openings", "The project has no section view type.");
                return;
            }

            double halfW = (w.Round ? w.Diameter : w.Width) / 2 + Around;
            double halfH = (w.Round ? w.Diameter : w.Height) / 2;
            double below = halfH + Around;
            Parameter fromFloor = w.Element.get_Parameter(OpeningParams.CenterFromFloor.Guid);
            if (fromFloor != null && fromFloor.HasValue)
                below = Math.Max(below, fromFloor.AsDouble() + 300 / 304.8);   // show the floor the height is measured from

            // Section box in the opening's frame: X along the wall, Y up, Z out of the wall face.
            Transform t = Transform.Identity;
            t.Origin = w.Center;
            t.BasisX = w.Frame.X;
            t.BasisY = XYZ.BasisZ;
            t.BasisZ = w.Frame.X.CrossProduct(XYZ.BasisZ);
            BoundingBoxXYZ box = new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(-halfW, -below, -(w.Depth / 2 + Beyond)),
                Max = new XYZ(halfW, halfH + Around, w.Depth / 2 + Beyond)
            };

            ViewSection view;
            using (Transaction tr = new Transaction(doc, "Openings: section"))
            {
                tr.Start();
                DeleteOld(doc, uidoc.ActiveView?.Id);

                view = ViewSection.CreateSection(doc, vft.Id, box);
                view.ViewTemplateId = ElementId.InvalidElementId;
                view.Scale = 50;
                view.DetailLevel = ViewDetailLevel.Fine;
                view.Name = UniqueName(doc, $"{Prefix} {(w.OpeningId.HasValue ? "#" + w.OpeningId : w.Element.Id.GetValue().ToString())}");
                tr.Commit();
            }

            uidoc.ActiveView = view;
            List<ElementId> sel = new List<ElementId> { w.Element.Id };
            uidoc.Selection.SetElementIds(sel);
        }

        /// <summary>API context, outside a transaction: removes the temporary sections that can be removed.</summary>
        public static void Cleanup(Document doc, ElementId activeViewId)
        {
            if (!new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>()
                    .Any(v => IsTemporary(v) && v.Id != activeViewId))
                return;
            using (Transaction tr = new Transaction(doc, "Openings: remove temporary sections"))
            {
                tr.Start();
                DeleteOld(doc, activeViewId);
                tr.Commit();
            }
        }

        private static void DeleteOld(Document doc, ElementId activeViewId)
        {
            List<ElementId> old = new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>()
                .Where(v => IsTemporary(v) && v.Id != activeViewId)
                .Select(v => v.Id).ToList();
            if (old.Count > 0) doc.Delete(old);
        }

        private static bool IsTemporary(View v) => !v.IsTemplate && v.Name.StartsWith(Prefix);

        private static string UniqueName(Document doc, string name)
        {
            HashSet<string> taken = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View))
                .Cast<View>().Select(v => v.Name));
            string n = name;
            for (int i = 2; taken.Contains(n); i++) n = $"{name} ({i})";
            return n;
        }
    }
}
