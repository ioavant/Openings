using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Openings
{
    /// <summary>How an opening stands in the model. The logic around it (matching, locks, deletions) is the same.</summary>
    public enum OpeningMode
    {
        DirectShape,   // Generic Model DirectShape: any shape
        Family         // face-based family instance on the wall / slab face: rectangles and circles only
    }

    /// <summary>
    /// This add-in's openings, whichever way they are represented: recognising them and reading /
    /// writing their key ("host=…;mep=…;x=…;geo=…"). A DirectShape keeps the key in its
    /// ApplicationDataId; a family instance in an Extensible Storage entity, which also marks it as ours.
    /// </summary>
    public static class OpeningElements
    {
        private static readonly Guid SchemaGuid = new Guid("9E3A61F4-7C2B-4D85-A0E6-52B1C7D84F39");
        private const string Field = "Key";

        private static Schema GetSchema()
        {
            Schema s = Schema.Lookup(SchemaGuid);
            if (s != null) return s;
            SchemaBuilder b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName("VixeldorfOpeningsKey");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField(Field, typeof(string));
            return b.Finish();
        }

        private static Entity KeyEntity(Element e)
        {
            Schema s = Schema.Lookup(SchemaGuid);
            if (s == null) return null;
            Entity en = e.GetEntity(s);
            return en != null && en.IsValid() ? en : null;
        }

        public static bool IsOpening(Element e) =>
            e is DirectShape ds ? ds.ApplicationId == OpeningParams.AppId
                                : e is FamilyInstance && KeyEntity(e) != null;

        public static OpeningMode ModeOf(Element e) => e is FamilyInstance ? OpeningMode.Family : OpeningMode.DirectShape;

        public static string GetKey(Element e) =>
            e is DirectShape ds ? ds.ApplicationDataId : KeyEntity(e)?.Get<string>(Field);

        /// <summary>Inside a transaction.</summary>
        public static void SetKey(Element e, string key)
        {
            if (e is DirectShape ds) { ds.ApplicationDataId = key; return; }
            Entity en = new Entity(GetSchema());
            en.Set(Field, key ?? "");
            e.SetEntity(en);
        }

        public static List<Element> Collect(Document doc) => Collect(doc, null);

        /// <param name="viewId">Only those visible in this view; null for the whole project.</param>
        public static List<Element> Collect(Document doc, ElementId viewId)
        {
            FilteredElementCollector Of() => viewId == null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, viewId);

            List<Element> list = Of().OfClass(typeof(DirectShape)).Cast<DirectShape>()
                .Where(ds => ds.ApplicationId == OpeningParams.AppId).Cast<Element>().ToList();
            if (Schema.Lookup(SchemaGuid) != null)
                list.AddRange(Of().OfClass(typeof(FamilyInstance)).WherePasses(new ExtensibleStorageFilter(SchemaGuid)));
            return list;
        }

        /// <summary>
        /// Middle of the opening (halfway through the host). A family instance stands on the host face
        /// with its Z out of the face and cuts Opening Depth into the host, so the middle is half of that behind it.
        /// </summary>
        public static XYZ CenterOf(Element e)
        {
            if (e is FamilyInstance fi)
            {
                Transform t = fi.GetTransform();
                Parameter d = fi.get_Parameter(OpeningParams.Depth.Guid);
                double depth = d != null && d.HasValue ? d.AsDouble() : 0;
                return t.Origin - t.BasisZ * (depth / 2);
            }
            BoundingBoxXYZ bb = e.get_BoundingBox(null);
            return bb == null ? null : (bb.Min + bb.Max) / 2;
        }

        /// <summary>
        /// Data of one opening element copied to the one replacing it (Opening … parameters, Comments, Mark,
        /// key), so the opening keeps its ID, Lock and whatever the user wrote on it.
        /// </summary>
        public static void CopyData(Element from, Element to)
        {
            foreach (OpeningParam op in OpeningParams.All)
                CopyParam(from.get_Parameter(op.Guid), to.get_Parameter(op.Guid));
            CopyParam(from.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), to.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS));
            CopyParam(from.get_Parameter(BuiltInParameter.ALL_MODEL_MARK), to.get_Parameter(BuiltInParameter.ALL_MODEL_MARK));
            SetKey(to, GetKey(from));
        }

        private static void CopyParam(Parameter src, Parameter dst)
        {
            if (src == null || dst == null || dst.IsReadOnly || src.StorageType != dst.StorageType) return;
            if (!src.HasValue) { if (dst.HasValue) dst.ClearValue(); return; }
            switch (src.StorageType)
            {
                case StorageType.String: dst.Set(src.AsString() ?? ""); break;
                case StorageType.Double: dst.Set(src.AsDouble()); break;
                case StorageType.Integer: dst.Set(src.AsInteger()); break;
            }
        }
    }

    /// <summary>A loaded face-based Generic Model family type openings can be placed as.</summary>
    public class FamilyChoice
    {
        public string Label;                                  // "Family : Type"
        public List<string> Parameters = new List<string>();  // its settable instance length parameters
    }

    /// <summary>
    /// The loaded family types openings can be placed as. Families are never opened or changed: the
    /// parameter names offered for the mapping come from an instance already in the project; until
    /// there is one, the names are typed in (defaults Width / Height / Depth / Diameter).
    /// </summary>
    public static class FamilyCatalog
    {
        public static string LabelOf(FamilySymbol s) => s.FamilyName + " : " + s.Name;

        private static IEnumerable<FamilySymbol> Candidates(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_GenericModel)
                .Cast<FamilySymbol>()
                .Where(s => s.Family != null && !s.Family.IsInPlace &&
                            s.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased);

        public static FamilySymbol Find(Document doc, string label) =>
            string.IsNullOrEmpty(label) ? null : Candidates(doc).FirstOrDefault(s => LabelOf(s) == label);

        /// <summary>API context.</summary>
        public static List<FamilyChoice> Collect(Document doc) =>
            Candidates(doc)
                .Select(s => new FamilyChoice { Label = LabelOf(s), Parameters = ParamsOf(doc, s) })
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>Settable length instance parameters, read off a placed instance of the family (none placed: empty).</summary>
        private static List<string> ParamsOf(Document doc, FamilySymbol sym)
        {
            HashSet<Guid> ours = new HashSet<Guid>(OpeningParams.All.Select(p => p.Guid));
            IEnumerable<Parameter> Settable(Element e) => e.Parameters.Cast<Parameter>()
                .Where(p => !p.IsReadOnly && p.StorageType == StorageType.Double &&
                            p.Definition.GetDataType() == SpecTypeId.Length &&
                            !(p.IsShared && ours.Contains(p.GUID)));

            // Instance parameters are only visible on an instance; type parameters cannot be set per opening.
            Element instance = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_GenericModel)
                .FirstOrDefault(e => (e as FamilyInstance)?.Symbol?.Family?.Id == sym.Family.Id);
            if (instance == null) return new List<string>();

            return Settable(instance).Select(p => p.Definition.Name).Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// Fitting a plan to what a family can be: a rectangle or a circle. Free shapes (ShaftMaster shafts,
    /// edited floor profiles) within the tolerance of their bounding rectangle or circle become that.
    /// </summary>
    public static class FamilyFit
    {
        /// <summary>The plan as a family places it, or null when the shape is too far from both.</summary>
        public static OpeningPlan Fit(OpeningPlan plan, double tolerance)
        {
            if (!plan.IsFree) return plan;
            if (plan.Kind != HostKind.Floor) return null;

            List<XYZ> pts = Sample(plan.FreeProfile, Math.Max(tolerance / 2, 5 / 304.8));
            if (pts.Count < 3) return null;

            // Rectangle: along the frame's X or any straight side; the one that hugs the shape best.
            List<XYZ> dirs = new List<XYZ> { plan.Frame.X };
            dirs.AddRange(plan.FreeProfile.OfType<Line>().Where(l => l.Length > tolerance).Select(l => l.Direction));
            Frame bestFrame = null; LocalBox bestBox = null; double rectDev = double.MaxValue;
            foreach (XYZ d in dirs)
            {
                Frame f = Frame.Horizontal(d);
                LocalBox box = LocalBox.Of(pts, f);
                double dev = pts.Max(p =>
                {
                    double x = p.DotProduct(f.X), y = p.DotProduct(f.Y);
                    return Math.Min(Math.Min(x - box.MinX, box.MaxX - x), Math.Min(y - box.MinY, box.MaxY - y));
                });
                if (dev < rectDev) { rectDev = dev; bestFrame = f; bestBox = box; }
            }

            // Circle around the middle of the shape, through its farthest point.
            XYZ c = new XYZ(pts.Average(p => p.X), pts.Average(p => p.Y), 0);
            double r = pts.Max(p => Dist2D(p, c));
            double circleDev = pts.Max(p => r - Dist2D(p, c));

            if (Math.Min(rectDev, circleDev) > tolerance) return null;

            OpeningPlan fitted = plan.Clone();
            fitted.FreeProfile = null;
            if (rectDev <= circleDev)
            {
                XYZ mid = bestBox.CenterIn(bestFrame);
                fitted.Round = false;
                fitted.Frame = bestFrame;
                fitted.Center = new XYZ(mid.X, mid.Y, plan.Center.Z);
                fitted.Width = bestBox.MaxX - bestBox.MinX;
                fitted.Height = bestBox.MaxY - bestBox.MinY;
            }
            else
            {
                fitted.Round = true;
                fitted.Center = new XYZ(c.X, c.Y, plan.Center.Z);
                fitted.Diameter = fitted.Width = fitted.Height = 2 * r;
            }
            return fitted;
        }

        private static double Dist2D(XYZ a, XYZ b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        // Evenly along every curve: a square's corners alone would sit on a circle too.
        private static List<XYZ> Sample(CurveLoop loop, double step)
        {
            List<XYZ> pts = new List<XYZ>();
            foreach (Curve curve in loop)
            {
                int n = Math.Max(4, (int)Math.Ceiling(curve.Length / step));
                for (int i = 0; i < n; i++) pts.Add(curve.Evaluate((double)i / n, true));
            }
            return pts;
        }
    }

    /// <summary>
    /// Puts an opening plan into the model as a DirectShape or as a face-based family instance, updating
    /// the element it already had where it can: a DirectShape gets the new solid; a family instance on the
    /// same host face is moved, turned and resized. Otherwise a new element takes over the old one's data
    /// (<see cref="OpeningElements.CopyData"/>) and the old one is deleted.
    ///
    /// The family is hosted on the host's face — a wall's +Y side, a slab's top — with its insertion point
    /// at the middle of the opening on that face, X along the opening's width, cutting Depth into the host.
    /// </summary>
    public class OpeningWriter
    {
        private readonly Document _doc;
        private readonly OpeningSettings _s;
        private FamilySymbol _rect, _round;
        private bool _resolved;

        public HashSet<string> MissingFamilyParams { get; } = new HashSet<string>();

        public OpeningWriter(Document doc, OpeningSettings settings)
        {
            _doc = doc;
            _s = settings;
        }

        private void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            _rect = FamilyCatalog.Find(_doc, _s.RectFamily);
            _round = FamilyCatalog.Find(_doc, _s.RoundFamily);
        }

        /// <summary>Why openings cannot be placed as families, or null.</summary>
        public string FamilyProblem
        {
            get
            {
                Resolve();
                if (_rect == null && _round == null)
                    return "Choose the rectangular and round opening families in Options (they must be loaded in this project), " +
                           "or switch to Generic Model (DirectShape).";
                return null;
            }
        }

        /// <summary>
        /// Inside a transaction. Returns the element that now holds the opening: <paramref name="current"/>
        /// updated, or a new one replacing it. <paramref name="used"/> is the plan as placed (a free shape fitted
        /// to a rectangle / circle for a family); <paramref name="note"/> says why a family was asked for but a
        /// DirectShape was made.
        /// </summary>
        public Element Write(OpeningPlan plan, Element current, OpeningMode want, out OpeningPlan used, out string note)
        {
            note = null;
            used = plan;
            if (want == OpeningMode.Family)
            {
                Resolve();
                OpeningPlan fitted = FamilyFit.Fit(plan, _s.FamilyTolerance);
                FamilySymbol sym = fitted == null ? null : fitted.Round ? _round : _rect;
                if (fitted == null) note = "free shape (not a rectangle or circle within the tolerance)";
                else if (sym == null) note = $"no {(fitted.Round ? "round" : "rectangular")} family chosen in Options";
                else
                {
                    FamilyInstance fi = WriteFamily(fitted, current as FamilyInstance, sym, out note);
                    if (fi != null)
                    {
                        used = fitted;
                        return Replace(current, fi);
                    }
                }
            }
            return Replace(current, WriteDirectShape(plan, current as DirectShape));
        }

        private Element Replace(Element current, Element now)
        {
            if (current != null && current.IsValidObject && current.Id != now.Id)
            {
                OpeningElements.CopyData(current, now);
                _doc.Delete(current.Id);
            }
            return now;
        }

        private DirectShape WriteDirectShape(OpeningPlan plan, DirectShape ds)
        {
            Solid solid = OpeningPlacer.BuildSolid(plan);
            if (ds == null)
            {
                ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
                ds.ApplicationId = OpeningParams.AppId;
            }
            ds.SetShape(new List<GeometryObject> { solid });
            ds.Name = $"Opening – {(plan.Kind == HostKind.Wall ? "Wall" : "Floor")} – {(plan.IsFree ? "Free" : plan.Round ? "Round" : "Rectangular")}";
            return ds;
        }

        // ── Family ────────────────────────────────────────────────────────────

        private FamilyInstance WriteFamily(OpeningPlan plan, FamilyInstance fi, FamilySymbol sym, out string note)
        {
            note = null;
            if (!FindFace(plan, out Reference face, out XYZ origin, out XYZ normal))
            {
                note = "no face of the host found at the opening";
                return null;
            }
            XYZ refDir = plan.Frame.X - normal * plan.Frame.X.DotProduct(normal);
            if (refDir.GetLength() < 1e-6) { note = "the opening is not square to the host face"; return null; }
            refDir = refDir.Normalize();

            bool created = false;
            try
            {
                if (fi != null && fi.Symbol.Id == sym.Id && SameFace(fi, face))
                    MoveTo(fi, origin, refDir, normal);
                else
                {
                    if (!sym.IsActive) { sym.Activate(); _doc.Regenerate(); }
                    fi = _doc.Create.NewFamilyInstance(face, origin, refDir, sym);
                    created = true;
                }
                SetSize(fi, plan);
                return fi;
            }
            catch (Exception ex)
            {
                if (created && fi.IsValidObject) _doc.Delete(fi.Id);
                note = ex.Message;
                return null;
            }
        }

        private void MoveTo(FamilyInstance fi, XYZ origin, XYZ refDir, XYZ normal)
        {
            double angle = fi.HandOrientation.AngleOnPlaneTo(refDir, normal);
            if (fi.Location is LocationPoint lp)
            {
                XYZ d = origin - lp.Point;
                if (d.GetLength() > 1e-9) ElementTransformUtils.MoveElement(_doc, fi.Id, d);
            }
            if (angle > 1e-6 && angle < 2 * Math.PI - 1e-6)
                ElementTransformUtils.RotateElement(_doc, fi.Id, Line.CreateUnbound(origin, normal), angle);
        }

        private bool SameFace(FamilyInstance fi, Reference face)
        {
            try
            {
                Reference host = fi.HostFace;
                return host != null && host.ConvertToStableRepresentation(_doc) == face.ConvertToStableRepresentation(_doc);
            }
            catch { return false; }
        }

        private void SetSize(FamilyInstance fi, OpeningPlan plan)
        {
            if (plan.Round)
            {
                SetMapped(fi, _s.MapDiameter, plan.Diameter);
                SetMapped(fi, _s.MapRoundDepth, plan.Depth);
            }
            else
            {
                SetMapped(fi, _s.MapWidth, plan.Width);
                SetMapped(fi, _s.MapHeight, plan.Height);
                SetMapped(fi, _s.MapDepth, plan.Depth);
            }
        }

        private void SetMapped(FamilyInstance fi, string name, double value)
        {
            if (string.IsNullOrWhiteSpace(name)) return;   // not mapped: the family keeps its own value
            Parameter p = fi.LookupParameter(name);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double)
            {
                MissingFamilyParams.Add($"{fi.Symbol.FamilyName}: {name}");
                return;
            }
            p.Set(value);
        }

        /// <summary>
        /// The host's planar face across the opening's direction (wall: ±Y, slab: ±Z) that the opening's
        /// middle projects onto, in host coordinates. Always the same side when there is one — wall +Y,
        /// slab top — so a re-run finds the face the instance is already on.
        /// </summary>
        private bool FindFace(OpeningPlan plan, out Reference face, out XYZ origin, out XYZ normal)
        {
            face = null; origin = normal = null;
            if (!ResolveHost(plan.HostKey, out Element host, out RevitLinkInstance link, out Transform t)) return false;

            XYZ dir = plan.Kind == HostKind.Wall ? plan.Frame.Y : XYZ.BasisZ;
            XYZ local = t.Inverse.OfPoint(plan.Center);
            double best = double.MaxValue;
            foreach (PlanarFace pf in PlanarFaces(host))
            {
                if (pf.Reference == null) continue;
                XYZ n = t.OfVector(pf.FaceNormal);
                double along = n.DotProduct(dir);
                if (Math.Abs(along) < 0.999) continue;
                IntersectionResult r = pf.Project(local);
                if (r == null) continue;   // outside this face (another part of the host, or a hole in it)

                double score = r.Distance + (along > 0 ? 0 : 1e6);
                if (score >= best) continue;
                best = score;
                normal = n;
                origin = t.OfPoint(r.XYZPoint);
                face = link != null ? pf.Reference.CreateLinkReference(link) : pf.Reference;
            }
            return face != null;
        }

        /// <summary>"linkInstanceId:elementId" (HostItem.Key) → the element and its link -> host transform.</summary>
        private bool ResolveHost(string key, out Element host, out RevitLinkInstance link, out Transform t)
        {
            host = null; link = null; t = Transform.Identity;
            string[] parts = (key ?? "").Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], out long linkId) || !long.TryParse(parts[1], out long id))
                return false;
            if (linkId == 0)
                host = _doc.GetElement(ElementIdExtensions.FromValue(id));
            else
            {
                link = _doc.GetElement(ElementIdExtensions.FromValue(linkId)) as RevitLinkInstance;
                Document linkDoc = link?.GetLinkDocument();
                if (linkDoc == null) return false;
                host = linkDoc.GetElement(ElementIdExtensions.FromValue(id));
                t = link.GetTotalTransform();
            }
            return host != null;
        }

        private static List<PlanarFace> PlanarFaces(Element e)
        {
            List<PlanarFace> faces = new List<PlanarFace>();
            GeometryElement geom = e.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine });
            if (geom != null) Collect(geom, faces);
            return faces;
        }

        private static void Collect(GeometryElement geom, List<PlanarFace> into)
        {
            foreach (GeometryObject obj in geom)
            {
                if (obj is Solid s && s.Volume > 1e-9)
                {
                    foreach (Face f in s.Faces)
                        if (f is PlanarFace pf) into.Add(pf);
                }
                else if (obj is GeometryInstance gi)
                    Collect(gi.GetInstanceGeometry(), into);
            }
        }
    }
}
