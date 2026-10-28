using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>
    /// Writes openings as Generic Model DirectShapes (as ShaftMaster does with shafts):
    /// the solid is built straight in model coordinates, already oriented to the host,
    /// so there is no family, no level offset and no rotation to get right.
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
        public int KeptManual { get; private set; }
        public List<ElementId> Touched { get; } = new List<ElementId>();
        public IEnumerable<string> MissingParams => _missingParams;

        public OpeningPlacer(Document doc, List<ExistingOpening> existing, OpeningMatcher matcher)
        {
            _doc = doc;
            _matcher = matcher;
            _levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation).ToList();

            foreach (ExistingOpening e in existing.OrderBy(e => e.Shape.Id.GetValue()))
            {
                Parameter p = e.Shape.get_Parameter(OpeningParams.OpeningId.Guid);
                if (p == null || !p.HasValue) continue;
                int id = p.AsInteger();
                if (!_idOwners.ContainsKey(id)) _idOwners[id] = e.Shape.Id;
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
            DirectShape ds;
            if (_matcher.Reuse.TryGetValue(plan, out ExistingOpening existing) && existing.Manual)
            {
                // Size set by hand (Edit Width): kept as the user left it. Delete it to recalculate.
                KeptManual++;
                Touched.Add(existing.Shape.Id);
                return;
            }

            Solid solid = BuildSolid(plan);
            if (existing != null)
            {
                // Same ElementId, so tags survive.
                ds = existing.Shape;
                Updated++;
            }
            else
            {
                ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
                ds.ApplicationId = OpeningParams.AppId;
                Created++;
            }

            ds.ApplicationDataId = ExistingOpening.KeyOf(plan);
            ds.SetShape(new List<GeometryObject> { solid });
            ds.Name = $"Opening – {(plan.Kind == HostKind.Wall ? "Wall" : "Floor")} – {(plan.Round ? "Round" : "Rectangular")}";
            Touched.Add(ds.Id);

            WriteOpeningId(ds);
            WriteSize(ds, plan);
            WriteElevations(ds, plan, floor);
            WriteInfo(ds, plan);
        }

        /// <summary>Inside a transaction, after every Place: removes openings the matcher found superseded or stale.</summary>
        public void DeleteObsolete()
        {
            foreach (ExistingOpening e in _matcher.Obsolete)
            {
                if (!e.Shape.IsValidObject) continue;
                _doc.Delete(e.Shape.Id);
                Deleted++;
            }
        }

        private void WriteOpeningId(DirectShape ds)
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

        private void WriteSize(DirectShape ds, OpeningPlan plan)
        {
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

        private void WriteElevations(DirectShape ds, OpeningPlan plan, FloorHit floor)
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

        private void WriteInfo(DirectShape ds, OpeningPlan plan)
        {
            Set(ds, OpeningParams.HostInfo,
                string.Join("; ", plan.Members.Select(m => m.Host.Describe()).Distinct()));
            Set(ds, OpeningParams.MepInfo,
                string.Join(", ", plan.Members.Select(m => m.Mep.Id.GetValue()).Distinct()));
        }

        private void Set(Element e, OpeningParam op, object value)
        {
            Parameter p = e.get_Parameter(op.Guid);
            if (p == null || p.IsReadOnly) { _missingParams.Add(op.Name); return; }
            switch (value)
            {
                case string s: p.Set(s); break;
                case double d: p.Set(d); break;
                case int i: p.Set(i); break;
            }
        }

        private static void Clear(Element e, OpeningParam op)
        {
            Parameter p = e.get_Parameter(op.Guid);
            if (p != null && !p.IsReadOnly && p.HasValue) p.ClearValue();
        }
    }
}
