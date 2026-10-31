using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Openings
{
    /// <summary>
    /// Walls / floors / roofs / ceilings / beams the user selected before Place Openings — in this model,
    /// or in a link (picked with Tab). Together with selected MEP elements they form pairs that get an
    /// opening even where they do not touch (see <see cref="IntersectionFinder"/>).
    /// </summary>
    public static class SelectedHosts
    {
        public static List<HostItem> Read(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            List<HostSource> sources = HostSource.Collect(doc);
            HostSource current = sources.First(s => s.Instance == null);
            List<HostItem> hosts = new List<HostItem>();

            void Add(HostSource src, Element e)
            {
                if (src == null || e == null || !IsHost(e)) return;
                HostItem h = IntersectionFinder.MakeHost(src, e);
                if (h != null && hosts.All(x => x.Key != h.Key)) hosts.Add(h);
            }

            foreach (ElementId id in uidoc.Selection.GetElementIds())
                Add(current, doc.GetElement(id));

            foreach (Reference r in LinkedReferences(uidoc.Selection))
            {
                if (r.LinkedElementId == ElementId.InvalidElementId) continue;
                HostSource src = sources.FirstOrDefault(s => s.Instance != null && s.Instance.Id == r.ElementId);
                Add(src, src?.Doc.GetElement(r.LinkedElementId));
            }
            return hosts;
        }

        private static bool IsHost(Element e)
        {
            if (e is Wall w && w.WallType.Kind == WallKind.Curtain) return false;
            long cat = e.Category?.Id.GetValue() ?? 0;
            return cat == (long)BuiltInCategory.OST_Walls || cat == (long)BuiltInCategory.OST_Floors ||
                   cat == (long)BuiltInCategory.OST_Roofs || cat == (long)BuiltInCategory.OST_Ceilings ||
                   cat == (long)BuiltInCategory.OST_StructuralFraming;
        }

        /// <summary>The selection as references, linked elements included; empty on Revit 2022.</summary>
        private static IEnumerable<Reference> LinkedReferences(Selection sel)
        {
#if NETFRAMEWORK
            // Selection.GetReferences exists from Revit 2023; this half is compiled against the 2022 API.
            MethodInfo m = typeof(Selection).GetMethod("GetReferences", Type.EmptyTypes);
            return m?.Invoke(sel, null) as IEnumerable<Reference> ?? Enumerable.Empty<Reference>();
#else
            return sel.GetReferences();
#endif
        }
    }
}
