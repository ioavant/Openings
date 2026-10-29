using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>
    /// Writes the openings of a run: which existing opening each plan updates (or keeps, when locked /
    /// pinned), its geometry through <see cref="OpeningWriter"/> — a Generic Model DirectShape built
    /// straight in model coordinates (as ShaftMaster does with shafts), or a face-based family — and
    /// its parameters.
    /// </summary>
    public class OpeningPlacer
    {
        public const string FreeShapeComment = "free shape";

        private readonly Document _doc;
        private readonly List<Level> _levels;
        private readonly OpeningMatcher _matcher;
        private readonly double _pbpZ, _spZ, _spSharedZ;
        private readonly HashSet<string> _missingParams = new HashSet<string>();

        // Opening ID -> the element that owns it. A copied opening carries its original's ID;
        // the element with the lower ElementId keeps it, the copy gets a new one when touched.
        private readonly Dictionary<int, ElementId> _idOwners = new Dictionary<int, ElementId>();
        private int _nextId = 1;

        public int Created { get; private set; }
        public int Updated { get; private set; }
        public int Deleted { get; private set; }
        public int NoFloorFound { get; private set; }
        public int KeptLocked { get; private set; }
        public int KeptPinned { get; private set; }
        public int LockedButMoved { get; private set; }
        public List<ElementId> Touched { get; } = new List<ElementId>();
        public IEnumerable<string> MissingParams => _missingParams;

        private readonly bool _respectLocks;
        private readonly OpeningWriter _writer;
        private readonly OpeningMode _mode;

        /// <summary>Openings asked for as a family that became DirectShapes, and why.</summary>
        public int NotFamily { get; private set; }
        public List<string> Notes { get; } = new List<string>();

        public OpeningPlacer(Document doc, List<ExistingOpening> existing, OpeningMatcher matcher, bool respectLocks,
                             OpeningWriter writer, OpeningMode mode)
        {
            _doc = doc;
            _writer = writer;
            _mode = mode;
            _respectLocks = respectLocks;
            _matcher = matcher;
            _levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation).ToList();

            foreach (ExistingOpening e in existing.OrderBy(e => e.Element.Id.GetValue()))
            {
                Parameter p = e.Element.get_Parameter(OpeningParams.OpeningId.Guid);
                if (p == null || !p.HasValue) continue;
                int id = p.AsInteger();
                if (!_idOwners.ContainsKey(id)) _idOwners[id] = e.Element.Id;
                _nextId = Math.Max(_nextId, id + 1);
            }

            BasePoint pbp = BasePoint.GetProjectBasePoint(doc);
            BasePoint sp = BasePoint.GetSurveyPoint(doc);
            _pbpZ = pbp.Position.Z;
            _spZ = sp.Position.Z;
            _spSharedZ = sp.SharedPosition.Z;
        }

        /// <summary>Inside a transaction.</summary>
        public void Place(OpeningPlan plan, FloorHit floor)
        {
            _matcher.Reuse.TryGetValue(plan, out ExistingOpening existing);
            if (existing != null && existing.Pinned)
            {
                // Pinned in Revit: no run moves or resizes it, whatever its MEP elements did.
                KeptPinned++;
                Touched.Add(existing.Element.Id);
                return;
            }
            if (existing != null && existing.Locked && _respectLocks)
            {
                // Locked (by hand, or edited / changed by the user): a whole-view run leaves it as it is —
                // unless its MEP elements moved since it was placed; then it follows them like any other.
                // A run on selected elements always gets past this and updates it.
                if (!existing.MepMoved(_doc, plan.MepIds))
                {
                    KeptLocked++;
                    Touched.Add(existing.Element.Id);
                    return;
                }
                LockedButMoved++;
            }

            // An existing opening stays what it is (Convert changes that); a new one is what the window says.
            OpeningMode want = existing?.Mode ?? _mode;
            Element el = _writer.Write(plan, existing?.Element, want, out OpeningPlan used, out string note);
            if (want == OpeningMode.Family && OpeningElements.ModeOf(el) != want)
            {
                NotFamily++;
                if (note != null) Notes.Add($"Opening at MEP {plan.MepIds.FirstOrDefault()}: DirectShape — {note}");
            }
            if (existing != null)
            {
                // Same ElementId where possible, so tags survive; a replacement keeps the Opening ID.
                Updated++;
                if (el.Id != existing.Element.Id)
                    foreach (int id in _idOwners.Where(kv => kv.Value == existing.Element.Id).Select(kv => kv.Key).ToList())
                        _idOwners[id] = el.Id;
            }
            else Created++;

            OpeningElements.SetKey(el, ExistingOpening.KeyOf(used, MepFingerprint.Of(_doc, plan.MepIds)));
            if (existing != null && existing.Manual) Set(el, OpeningParams.Locked, 1);   // the old flag lived in the key
            Touched.Add(el.Id);

            WriteOpeningId(el);
            WriteSize(el, used);
            WriteElevations(el, used, floor);
            WriteInfo(el, used);
        }

        /// <summary>Inside a transaction, after every Place: removes openings the matcher found superseded or stale.</summary>
        public void DeleteObsolete()
        {
            foreach (ExistingOpening e in _matcher.Obsolete)
            {
                if (!e.Element.IsValidObject) continue;
                _doc.Delete(e.Element.Id);
                Deleted++;
            }
        }

        private void WriteOpeningId(Element ds)
        {
            Parameter p = ds.get_Parameter(OpeningParams.OpeningId.Guid);
            if (p != null && p.HasValue && _idOwners.TryGetValue(p.AsInteger(), out ElementId owner) && owner == ds.Id)
                return;   // already has its own ID

            int id = _nextId++;
            _idOwners[id] = ds.Id;
            Set(ds, OpeningParams.OpeningId, id);
        }

        // ── Geometry ──────────────────────────────────────────────────────────

        /// <summary>
        /// Profile across the host, centred on plan.Center, extruded through the host.
        /// Wall: profile in (X, Z), extruded along Y. Floor: profile in (X, Y), extruded along Z.
        /// </summary>
        internal static Solid BuildSolid(OpeningPlan plan)
        {
            Frame f = plan.Frame;
            XYZ dir = plan.Kind == HostKind.Wall ? f.Y : f.Z;
            XYZ u = f.X;
            XYZ v = dir.CrossProduct(u);   // u × v = dir → the loop below runs CCW about dir
            XYZ c = plan.Center - dir * (plan.Depth / 2);

            CurveLoop loop = new CurveLoop();
            if (plan.IsFree)
            {
                // Shaft profile, dropped onto the bottom of the slab and wound CCW about the extrusion.
                loop = CurveLoop.CreateViaTransform(plan.FreeProfile,
                    Transform.CreateTranslation(new XYZ(0, 0, c.Z - plan.FreeProfile.First().GetEndPoint(0).Z)));
                if (!loop.IsCounterclockwise(dir)) loop.Flip();
            }
            else if (plan.Round)
            {
                double r = plan.Diameter / 2;
                loop.Append(Arc.Create(c, r, 0, Math.PI, u, v));
                loop.Append(Arc.Create(c, r, Math.PI, 2 * Math.PI, u, v));
            }
            else
            {
                double a = plan.Width / 2, b = plan.Height / 2;
                XYZ p0 = c - a * u - b * v, p1 = c + a * u - b * v, p2 = c + a * u + b * v, p3 = c - a * u + b * v;
                loop.Append(Line.CreateBound(p0, p1));
                loop.Append(Line.CreateBound(p1, p2));
                loop.Append(Line.CreateBound(p2, p3));
                loop.Append(Line.CreateBound(p3, p0));
            }

            return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, dir, plan.Depth);
        }

        // ── Parameters ────────────────────────────────────────────────────────

        private void WriteSize(Element ds, OpeningPlan plan) => WriteSize(ds, plan, _missingParams);

        /// <summary>Size, shape and host type parameters (also used by the editor and Convert).</summary>
        internal static void WriteSize(Element ds, OpeningPlan plan, HashSet<string> missing)
        {
            void Set(Element e, OpeningParam op, object value) => SetParam(e, op, value, missing);

            Set(ds, OpeningParams.Depth, plan.Depth);
            Set(ds, OpeningParams.Shape, plan.IsFree ? "Free Shape" : plan.Round ? "Round" : "Rectangular");

            // Free shape is flagged in Comments as well; a user's own comment is never overwritten.
            Parameter comments = ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comments != null && !comments.IsReadOnly)
            {
                string current = comments.AsString() ?? "";
                if (plan.IsFree && current.Length == 0) comments.Set(FreeShapeComment);
                else if (!plan.IsFree && current == FreeShapeComment) comments.Set("");
            }
            Set(ds, OpeningParams.HostType, plan.Kind == HostKind.Wall ? "Wall" : "Floor");

            // Clear the fields that do not apply, so an opening that changed shape on a re-run
            // does not keep stale values.
            if (plan.Round)
            {
                Set(ds, OpeningParams.Diameter, plan.Diameter);
                Clear(ds, OpeningParams.Width); Clear(ds, OpeningParams.Height); Clear(ds, OpeningParams.Length);
            }
            else
            {
                Clear(ds, OpeningParams.Diameter);
                Set(ds, OpeningParams.Width, plan.Width);
                if (plan.Kind == HostKind.Wall) { Set(ds, OpeningParams.Height, plan.Height); Clear(ds, OpeningParams.Length); }
                else { Set(ds, OpeningParams.Length, plan.Height); Clear(ds, OpeningParams.Height); }
            }
        }

        private void WriteElevations(Element ds, OpeningPlan plan, FloorHit floor)
        {
            double bottom = plan.BottomZ;
            double center = plan.Center.Z;

            Set(ds, OpeningParams.BottomElevPbp, bottom - _pbpZ);
            Set(ds, OpeningParams.CenterElevPbp, center - _pbpZ);
            Set(ds, OpeningParams.BottomElevSp, bottom - _spZ + _spSharedZ);
            Set(ds, OpeningParams.CenterElevSp, center - _spZ + _spSharedZ);

            Level level = _levels.LastOrDefault(l => l.ProjectElevation <= center + 1e-6) ?? _levels.FirstOrDefault();
            Set(ds, OpeningParams.LevelName, level?.Name ?? "");

            if (plan.Kind == HostKind.Wall && floor != null)
            {
                Set(ds, OpeningParams.BottomFromFloor, bottom - floor.Z);
                Set(ds, OpeningParams.CenterFromFloor, center - floor.Z);
                Set(ds, OpeningParams.FloorInfo, floor.Describe);
            }
            else
            {
                Clear(ds, OpeningParams.BottomFromFloor);
                Clear(ds, OpeningParams.CenterFromFloor);
                Clear(ds, OpeningParams.FloorInfo);
                if (plan.Kind == HostKind.Wall) NoFloorFound++;
            }
        }

        private void WriteInfo(Element ds, OpeningPlan plan)
        {
            Set(ds, OpeningParams.HostInfo,
                string.Join("; ", plan.Members.Select(m => m.Host.Describe()).Distinct()));
            Set(ds, OpeningParams.MepInfo,
                string.Join(", ", plan.Members.Select(m => m.Mep.Id.GetValue()).Distinct()));
        }

        private void Set(Element e, OpeningParam op, object value) => SetParam(e, op, value, _missingParams);

        internal static void SetParam(Element e, OpeningParam op, object value, HashSet<string> missing)
        {
            Parameter p = e.get_Parameter(op.Guid);
            if (p == null || p.IsReadOnly) { missing?.Add(op.Name); return; }
            switch (value)
            {
                case string s: p.Set(s); break;
                case double d: p.Set(d); break;
                case int i: p.Set(i); break;
            }
        }

        internal static void Clear(Element e, OpeningParam op)
        {
            Parameter p = e.get_Parameter(op.Guid);
            if (p != null && !p.IsReadOnly && p.HasValue) p.ClearValue();
        }
    }
}
