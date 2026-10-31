using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>
    /// Openings the user placed by hand: instances of the rectangular / round opening family chosen in
    /// Options (any type of that family) that do not carry this add-in's key yet, and copies of opening families
    /// (a copy carries its original's key and ID). Each becomes one of the
    /// add-in's openings — Opening ID, size / elevation / host data, and a key with the host face it stands
    /// on and the MEP elements crossing it — so the list, runs, Lock, Edit, Section and the tracker treat it
    /// like an opening the add-in placed. The family itself (position, size) is not touched.
    ///
    /// It gets Opening Lock, as anything changed by hand does: a run over the view keeps its size, a run on
    /// selected elements recomputes it.
    /// </summary>
    /// <summary>What one pass of <see cref="OpeningAdopter.AdoptAll"/> did, for the palette's status line.</summary>
    public class AdoptResult
    {
        public List<ElementId> Adopted = new List<ElementId>();
        public int Failed;
        public string Error;          // the first failure's message
        public bool NoFamilies;       // no opening family chosen in Options (or not loaded): nothing can be adopted

        public string Note =>
            NoFamilies ? "Openings placed by hand are picked up once their family is chosen in Options → Families."
            : Failed > 0 ? $"{Failed} opening(s) placed by hand could not be picked up: {Error}"
            : Adopted.Count > 0 ? $"Picked up {Adopted.Count} opening(s) placed by hand."
            : null;
    }

    public static class OpeningAdopter
    {
        /// <summary>API context, no transaction open.</summary>
        public static AdoptResult AdoptAll(Document doc, OpeningSettings s)
        {
            AdoptResult result = new AdoptResult();
            if (doc == null || s == null || doc.IsFamilyDocument || doc.IsReadOnly || doc.IsModifiable) return result;

            FamilySymbol rect = FamilyCatalog.Find(doc, s.RectFamily), round = FamilyCatalog.Find(doc, s.RoundFamily);
            HashSet<string> families = new HashSet<string>();
            if (rect != null) families.Add(rect.FamilyName);
            if (round != null) families.Add(round.FamilyName);
            if (families.Count == 0)
            {
                result.NoFamilies = true;
                return result;
            }

            List<FamilyInstance> found = Candidates(doc, families);
            if (found.Count == 0) return result;

            SharedParamHelper.EnsureParameters(doc);
            List<HostSource> sources = HostSource.Collect(doc)
                .Where(src => s.HostSources.Count == 0 ? src.Instance != null : s.HostSources.Contains(src.Title))
                .ToList();

            using (new OwnChanges())   // the add-in's own writing: no Lock from the tracker (it sets Lock itself)
            using (Transaction t = new Transaction(doc, "Adopt openings placed by hand"))
            {
                t.Start();
                OpeningPlacer placer = new OpeningPlacer(doc, ExistingOpening.Collect(doc), null, false, null, s.Mode);
                // Made before the per-opening sub-transactions: its search view must not be rolled back with one of them.
                FloorProbe probe = found.Any(fi => Math.Abs(fi.GetTransform().BasisZ.Z) < 0.5)
                    ? new FloorProbe(doc, s.FloorProbeOffset, sources) : null;

                foreach (FamilyInstance fi in found)
                {
                    using (SubTransaction st = new SubTransaction(doc))
                    {
                        st.Start();
                        try
                        {
                            Adopt(doc, fi, s, rect?.FamilyName, round?.FamilyName, families, placer, probe);
                            st.Commit();
                        }
                        catch (Exception ex)
                        {
                            st.RollBack();   // left as it is; the next refresh tries again
                            result.Failed++;
                            if (result.Error == null) result.Error = $"{fi.Symbol?.FamilyName} {fi.Id.GetValue()} — {ex.Message}";
                        }
                    }
                }
                if (placer.Touched.Count > 0) t.Commit(); else t.RollBack();
                result.Adopted.AddRange(placer.Touched);
                return result;
            }
        }

        /// <summary>
        /// Instances of the opening families that are not openings yet, and copies of opening families
        /// (Copy / Paste / Array): a copy carries its original's key and Opening ID, so it is told apart by
        /// the ID — the opening with the lowest ElementId keeps it, the others are adopted afresh.
        /// </summary>
        private static List<FamilyInstance> Candidates(Document doc, HashSet<string> families)
        {
            bool OfFamilies(FamilyInstance fi) =>
                fi.SuperComponent == null && families.Contains(fi.Symbol?.FamilyName) && OpeningUpdater.Editable(doc, fi.Id);

            List<FamilyInstance> list = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_GenericModel).Cast<FamilyInstance>()
                .Where(fi => OfFamilies(fi) && !OpeningElements.IsOpening(fi))
                .ToList();

            IEnumerable<FamilyInstance> copies = OpeningElements.Collect(doc)
                .Select(e => (e, id: e.get_Parameter(OpeningParams.OpeningId.Guid)))
                .Where(x => x.id != null && x.id.HasValue)
                .GroupBy(x => x.id.AsInteger())
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.OrderBy(x => x.e.Id.GetValue()).Skip(1).Select(x => x.e))
                .OfType<FamilyInstance>()
                .Where(OfFamilies);
            list.AddRange(copies);
            return list;
        }

        private static void Adopt(Document doc, FamilyInstance fi, OpeningSettings s, string rectFamily, string roundFamily,
                                  HashSet<string> families, OpeningPlacer placer, FloorProbe probe)
        {
            // The family stands on the host face: Z out of the face, X along the width, cutting Depth into the host.
            Transform t = fi.GetTransform();
            HostKind kind = Math.Abs(t.BasisZ.Z) < 0.5 ? HostKind.Wall : HostKind.Floor;
            Frame frame = Frame.Horizontal(t.BasisX);
            LocalBox box = BoxOf(fi, t);

            // Round: an instance of the round family — when one family serves both, the one with a diameter.
            bool round = fi.Symbol.FamilyName == roundFamily &&
                         (roundFamily != rectFamily || Mapped(fi, s.MapDiameter) != null);
            double spanX = box == null ? 0 : box.MaxX - box.MinX;
            double spanY = box == null ? 0 : box.MaxY - box.MinY;
            double spanZ = box == null ? 0 : box.MaxZ - box.MinZ;
            double depth = Mapped(fi, round ? s.MapRoundDepth : s.MapDepth) ?? spanZ;
            double width, height;
            if (round)
                width = height = Mapped(fi, s.MapDiameter) ?? Math.Max(spanX, spanY);
            else
            {
                width = Mapped(fi, s.MapWidth) ?? spanX;
                height = Mapped(fi, s.MapHeight) ?? spanY;
            }

            OpeningPlan plan = new OpeningPlan
            {
                Kind = kind,
                Round = round,
                Center = t.Origin - t.BasisZ * (depth / 2),
                Frame = frame,
                Width = width,
                Height = height,
                Diameter = round ? width : 0,
                Depth = depth,
                ProbeAxis = frame.Y
            };

            string hostKey = HostOf(doc, fi, out string hostInfo);
            plan.HostKey = hostKey;

            // The MEP elements crossing it: with them in the key, runs match it like a placed opening.
            List<long> meps = hostKey == null || depth <= 0 || width <= 0 || height <= 0
                ? new List<long>()
                : CrossingMep(doc, OpeningPlacer.BuildSolid(plan), s, families);
            OpeningElements.SetKey(fi, ExistingOpening.KeyOf(hostKey ?? "", meps, frame.X,
                meps.Count > 0 ? MepFingerprint.Of(doc, meps) : null));

            FloorHit floor = kind == HostKind.Wall ? probe?.FloorUnder(plan) : null;
            placer.Adopt(fi, plan, floor, hostInfo, meps);
        }

        /// <summary>A mapped family length parameter, or null when it is not mapped / not there.</summary>
        private static double? Mapped(FamilyInstance fi, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            Parameter p = fi.LookupParameter(name);
            return p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 1e-9
                ? p.AsDouble() : (double?)null;
        }

        /// <summary>Fallback size: the instance's bounding box in its own axes (X, Y, Z of the family).</summary>
        private static LocalBox BoxOf(FamilyInstance fi, Transform t)
        {
            BoundingBoxXYZ bb = fi.get_BoundingBox(null);
            if (bb == null) return null;
            LocalBox box = new LocalBox();
            for (int i = 0; i < 8; i++)
            {
                XYZ p = new XYZ((i & 1) == 0 ? bb.Min.X : bb.Max.X, (i & 2) == 0 ? bb.Min.Y : bb.Max.Y, (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                box.Add(p.DotProduct(t.BasisX), p.DotProduct(t.BasisY), p.DotProduct(t.BasisZ));
            }
            return box.IsEmpty ? null : box;
        }

        /// <summary>
        /// HostItem.Key of the element whose face the family stands on ("link instance id:element id", 0 for the
        /// current model) and its description as runs write it; null when it is not on a host face.
        /// </summary>
        private static string HostOf(Document doc, FamilyInstance fi, out string info)
        {
            info = "";
            Reference face = null;
            try { face = fi.HostFace; } catch { /* not hosted on a face */ }

            Element host = null;
            string source = "Current model";
            long linkId = 0;
            if (face != null && face.LinkedElementId != ElementId.InvalidElementId &&
                doc.GetElement(face.ElementId) is RevitLinkInstance link && link.GetLinkDocument() is Document linkDoc)
            {
                host = linkDoc.GetElement(face.LinkedElementId);
                source = linkDoc.Title;
                linkId = link.Id.GetValue();
            }
            else if (fi.Host != null && !(fi.Host is RevitLinkInstance))
                host = fi.Host;
            if (host == null) return null;

            info = $"{source} : {host.Category?.Name} {host.Id.GetValue()}";
            return linkId + ":" + host.Id.GetValue();
        }

        /// <summary>Elements of the ticked MEP categories whose body crosses the opening's volume.</summary>
        private static List<long> CrossingMep(Document doc, Solid opening, OpeningSettings s, HashSet<string> families)
        {
            List<long> result = new List<long>();
            List<BuiltInCategory> cats = new List<BuiltInCategory>();
            foreach (string n in s.MepCategories)
                if (Enum.TryParse(n, out BuiltInCategory bic)) cats.Add(bic);
            if (cats.Count == 0) return result;

            BoundingBoxXYZ bb = opening.GetBoundingBox();
            XYZ a = bb.Transform.OfPoint(bb.Min), b = bb.Transform.OfPoint(bb.Max);
            Outline outline = new Outline(new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
                                          new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));

            foreach (Element el in new FilteredElementCollector(doc)
                         .WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType()
                         .WherePasses(new BoundingBoxIntersectsFilter(outline)))
            {
                // Openings are generic models too (and so may be other hand-placed ones): never their own MEP.
                if (OpeningElements.IsOpening(el)) continue;
                if (el is FamilyInstance other && families.Contains(other.Symbol?.FamilyName)) continue;

                foreach (Solid ms in GeometryUtils.GetSolids(el, null))
                {
                    Solid inter;
                    try { inter = BooleanOperationsUtils.ExecuteBooleanOperation(ms, opening, BooleanOperationsType.Intersect); }
                    catch { continue; }
                    if (inter != null && inter.Volume > 1e-6)
                    {
                        result.Add(el.Id.GetValue());
                        break;
                    }
                }
            }
            return result;
        }
    }
}
