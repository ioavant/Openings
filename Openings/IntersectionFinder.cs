using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>A model the walls/floors come from: a link instance or the current file.</summary>
    public class HostSource
    {
        public string Title { get; set; }          // document title, used to remember the choice
        public string Label { get; set; }          // what the window shows
        public Document Doc { get; set; }
        public Transform Transform { get; set; }   // link -> host coordinates (identity for the current file)
        public RevitLinkInstance Instance { get; set; }

        public static List<HostSource> Collect(Document doc)
        {
            List<HostSource> list = new List<HostSource>();
            foreach (RevitLinkInstance li in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document linkDoc = li.GetLinkDocument();
                if (linkDoc == null) continue;   // unloaded
                list.Add(new HostSource
                {
                    Title = linkDoc.Title,
                    Label = li.Name,
                    Doc = linkDoc,
                    Transform = li.GetTotalTransform(),
                    Instance = li
                });
            }
            list.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            list.Add(new HostSource
            {
                Title = "<Current model>",
                Label = "<Current model>",
                Doc = doc,
                Transform = Transform.Identity
            });
            return list;
        }
    }

    /// <summary>A wall/floor/... from one of the host sources, in host coordinates.</summary>
    public class HostItem
    {
        public HostSource Source;
        public Element Element;
        public HostKind Kind;
        public XYZ Min, Max;
        private List<Solid> _solids;
        private Curve _axis;
        private bool _axisResolved;

        public string Key => (Source.Instance?.Id.GetValue() ?? 0) + ":" + Element.Id.GetValue();

        public string Describe() =>
            $"{(Source.Instance == null ? "Current model" : Source.Title)} : " +
            $"{Element.Category?.Name} {Element.Id.GetValue()}";

        public List<Solid> Solids => _solids ??= GeometryUtils.GetSolids(Element, Source.Transform);

        /// <summary>Wall / beam location line in host coordinates, or null.</summary>
        public Curve Axis
        {
            get
            {
                if (!_axisResolved)
                {
                    _axisResolved = true;
                    if (Element.Location is LocationCurve lc && lc.Curve != null)
                        _axis = lc.Curve.CreateTransformed(Source.Transform);
                }
                return _axis;
            }
        }
    }

    /// <summary>One MEP element crossing one host element.</summary>
    public class Penetration
    {
        public Element Mep;
        public HostItem Host;
        public List<XYZ> Points = new List<XYZ>();   // vertices of the intersection solid
        public XYZ Axis;                              // MEP direction (unit)
        public XYZ SectionX;                          // MEP cross-section width direction (unit)
        public bool IsRound;
        public double Insulation;
        public Connector End;                         // fitting/accessory: its connector on the crossing side
        public bool Extended;                         // a selected pair that does not touch: placed on the host's extended faces
        public Frame Frame;

        // ShaftMaster shaft crossing a slab: one opening per shaft, exactly its profile, no margin.
        public string ShaftKey;                       // null: not a shaft
        public ProfileShape ShaftShape;
        public CurveLoop ShaftProfile;                // plan profile (horizontal), any elevation
        public bool IsShaft => ShaftKey != null;
    }

    public class IntersectionFinder
    {
        // |cos| between the MEP axis and the host normal below which the element is
        // considered to run along the host rather than through it (~75°).
        private const double MinCrossingCos = 0.26;

        public List<string> Warnings { get; } = new List<string>();

        /// <summary>HostItem.Key of every host searched: an opening in any other host is out of this run's scope.</summary>
        public HashSet<string> CheckedHosts { get; } = new HashSet<string>();

        /// <summary>Selected pairs that do not touch and got their crossing on the host's extended faces.</summary>
        public int Extended { get; private set; }

        /// <param name="pickedHosts">Walls / floors the user selected (here or in a link); with <paramref name="pickedMeps"/>
        /// they form pairs that get an opening even where they do not touch — the host's faces are extended to the element.
        /// Searched even when not visible or not of a ticked category: the user picked them.</param>
        public List<Penetration> Find(
            IList<Element> mepElements,
            IList<HostSource> sources,
            ICollection<BuiltInCategory> hostCategories,
            View view,
            IList<HostItem> pickedHosts = null,
            ICollection<long> pickedMeps = null)
        {
            List<HostItem> hosts = CollectHosts(sources, hostCategories, view);
            HashSet<string> forced = new HashSet<string>();
            if (pickedHosts != null && pickedMeps != null && pickedMeps.Count > 0)
                foreach (HostItem h in pickedHosts)
                {
                    forced.Add(h.Key);
                    if (hosts.All(x => x.Key != h.Key)) hosts.Add(h);
                }
            foreach (HostItem h in hosts) CheckedHosts.Add(h.Key);
            List<Penetration> result = new List<Penetration>();
            if (hosts.Count == 0) return result;

            foreach (Element mep in mepElements)
            {
                BoundingBoxXYZ bb = mep.get_BoundingBox(null);
                if (bb == null) continue;
                bool picked = pickedMeps != null && pickedMeps.Contains(mep.Id.GetValue());

                List<Solid> mepSolids = null;
                foreach (HostItem host in hosts)
                {
                    bool force = picked && forced.Contains(host.Key);
                    if (!force && !GeometryUtils.BoxesOverlap(bb.Min, bb.Max, host.Min, host.Max)) continue;

                    mepSolids ??= GeometryUtils.GetSolids(mep, null);
                    if (mepSolids.Count == 0) break;

                    Penetration p = Intersect(mep, mepSolids, host, force);
                    if (p != null) result.Add(p);
                }
            }
            return result;
        }

        /// <summary>Host elements of the chosen categories that are visible in <paramref name="view"/>.</summary>
        private List<HostItem> CollectHosts(IList<HostSource> sources, ICollection<BuiltInCategory> categories, View view)
        {
            List<HostItem> hosts = new List<HostItem>();
            if (categories.Count == 0) return hosts;

            foreach (HostSource src in sources)
            {
                if (src.Instance != null && src.Instance.IsHidden(view)) continue;

                // Linked elements visible in the host view (exact on Revit 2024+, approximated before).
                FilteredElementCollector collector = src.Instance == null
                    ? new FilteredElementCollector(src.Doc, view.Id)
                    : LinkViewCollector.Collect(view, src.Instance, src.Doc);

                ElementMulticategoryFilter filter = new ElementMulticategoryFilter(categories.ToList());
                foreach (Element e in collector.WherePasses(filter).WhereElementIsNotElementType())
                {
                    if (e is Wall w && w.WallType.Kind == WallKind.Curtain) continue;
                    HostItem h = MakeHost(src, e);
                    if (h != null) hosts.Add(h);
                }
            }
            return hosts;
        }

        /// <summary>A host element of <paramref name="src"/> with its box in host coordinates; null without geometry.</summary>
        public static HostItem MakeHost(HostSource src, Element e)
        {
            BoundingBoxXYZ bb = e.get_BoundingBox(null);
            if (bb == null) return null;
            GeometryUtils.TransformBox(bb.Min, bb.Max, src.Transform, out XYZ min, out XYZ max);
            return new HostItem { Source = src, Element = e, Kind = KindOf(e), Min = min, Max = max };
        }

        private static HostKind KindOf(Element e)
        {
            long cat = e.Category?.Id.GetValue() ?? 0;
            return cat == (long)BuiltInCategory.OST_Walls || cat == (long)BuiltInCategory.OST_StructuralFraming
                ? HostKind.Wall
                : HostKind.Floor;
        }

        /// <param name="force">A pair the user selected: with no real intersection, the host's faces are extended.</param>
        private Penetration Intersect(Element mep, List<Solid> mepSolids, HostItem host, bool force)
        {
            List<XYZ> points = new List<XYZ>();
            foreach (Solid ms in mepSolids)
            {
                foreach (Solid hs in host.Solids)
                {
                    Solid inter;
                    try
                    {
                        inter = BooleanOperationsUtils.ExecuteBooleanOperation(ms, hs, BooleanOperationsType.Intersect);
                    }
                    catch (Exception ex)
                    {
                        Warnings.Add($"Boolean failed: MEP {mep.Id.GetValue()} × {host.Describe()} ({ex.Message})");
                        continue;
                    }
                    if (inter != null && inter.Volume > 1e-6)
                        GeometryUtils.CollectVertices(inter, points);
                }
            }
            bool extended = false;
            if (points.Count == 0)
            {
                if (!force) return null;
                points = ExtendedCrossing(mep, host);
                if (points == null) return null;
                extended = true;
                Extended++;
            }

            Penetration p = new Penetration { Mep = mep, Host = host, Points = points, Extended = extended };
            ReadMepProfile(mep, p);
            if (host.Kind == HostKind.Floor) ReadShaft(mep, mepSolids, p);
            bool axisKnown = p.Axis != null;
            if (!axisKnown) p.Axis = host.Kind == HostKind.Floor ? XYZ.BasisZ : XYZ.BasisX;
            p.Frame = BuildFrame(p);

            XYZ normal = host.Kind == HostKind.Wall ? p.Frame.Y : p.Frame.Z;
            if (!axisKnown) p.Axis = normal;   // nothing to go by: assume it goes straight through
            if (Math.Abs(p.Axis.DotProduct(normal)) < MinCrossingCos)
            {
                Warnings.Add($"MEP {mep.Id.GetValue()} runs along {host.Describe()} — skipped.");
                return null;
            }

            p.Points = SweepSection(mep, mepSolids, p, normal);
            return p;
        }

        // ── Selected pairs that do not touch ──────────────────────────────────

        /// <summary>
        /// A selected MEP element and a selected host that do not touch — a modelling gap: a wall not taken up
        /// to the slab, a duct just past the end of a wall. The host's two main faces are taken as unbounded
        /// planes, and the crossing is where the element's axis runs between them: two points on the axis, one
        /// on each plane (they give the opening its depth and position; its size comes from the element's
        /// section as usual). Null, with a warning, when the element runs along the host or never reaches it.
        /// </summary>
        private List<XYZ> ExtendedCrossing(Element mep, HostItem host)
        {
            string what = $"Selected MEP {mep.Id.GetValue()} × {host.Describe()}";
            Line axis = StraightAxis(mep);
            if (axis == null) { Warnings.Add($"{what}: no straight axis to extend to the host — skipped."); return null; }
            if (!HostPlanes(host, out XYZ n, out double dMin, out double dMax))
            {
                Warnings.Add($"{what}: the host has no flat faces to extend (curved?) — skipped.");
                return null;
            }

            XYZ p0 = axis.GetEndPoint(0), dir = axis.Direction;
            double along = n.DotProduct(dir);
            if (Math.Abs(along) < MinCrossingCos) { Warnings.Add($"{what}: runs along the host — skipped."); return null; }

            double t1 = (dMin - n.DotProduct(p0)) / along, t2 = (dMax - n.DotProduct(p0)) / along;
            if (Math.Max(t1, t2) < 0 || Math.Min(t1, t2) > axis.Length)
            {
                Warnings.Add($"{what}: does not reach the host's extended faces — skipped.");
                return null;
            }
            return new List<XYZ> { p0 + dir * t1, p0 + dir * t2 };
        }

        /// <summary>Duct / pipe location line; a straight fitting or accessory between its two opposite connectors.</summary>
        private static Line StraightAxis(Element mep)
        {
            if (mep.Location is LocationCurve lc && lc.Curve is Line line) return line;

            ConnectorManager cm = (mep as FamilyInstance)?.MEPModel?.ConnectorManager;
            if (cm == null) return null;
            List<Connector> ends = cm.Connectors.Cast<Connector>().Where(c => c.ConnectorType == ConnectorType.End).ToList();
            if (ends.Count != 2 || ends[0].Origin.DistanceTo(ends[1].Origin) < 1e-6) return null;
            if (ends[0].CoordinateSystem.BasisZ.DotProduct(ends[1].CoordinateSystem.BasisZ) > -0.999) return null;   // an elbow
            return Line.CreateBound(ends[0].Origin, ends[1].Origin);
        }

        /// <summary>
        /// The host's main faces as planes: normal <paramref name="n"/> and their offsets along it. Walls and beams:
        /// the largest upright face (square to a straight location line); floors, roofs, ceilings: the largest lying one.
        /// </summary>
        private static bool HostPlanes(HostItem host, out XYZ n, out double dMin, out double dMax)
        {
            n = null;
            dMin = double.MaxValue;
            dMax = double.MinValue;

            XYZ along = null;
            if (host.Kind == HostKind.Wall)
            {
                if (host.Axis != null && !(host.Axis is Line)) return false;   // curved wall: no planes to extend
                along = (host.Axis as Line)?.Direction;
            }

            List<PlanarFace> faces = host.Solids.SelectMany(sol => sol.Faces.OfType<PlanarFace>()).ToList();
            PlanarFace main = faces
                .Where(f => host.Kind == HostKind.Wall
                    ? Math.Abs(f.FaceNormal.Z) < 0.5 && (along == null || Math.Abs(f.FaceNormal.DotProduct(along)) < 0.1)
                    : Math.Abs(f.FaceNormal.Z) >= 0.5)
                .OrderByDescending(f => f.Area)
                .FirstOrDefault();
            if (main == null) return false;

            n = main.FaceNormal;
            foreach (PlanarFace f in faces)
            {
                if (Math.Abs(f.FaceNormal.DotProduct(n)) < 0.999) continue;
                double d = f.Origin.DotProduct(n);
                dMin = Math.Min(dMin, d);
                dMax = Math.Max(dMax, d);
            }
            return dMax - dMin > 1e-6;
        }

        // ── Section swept through the host ────────────────────────────────────

        private readonly Dictionary<long, SectionBox> _sections = new Dictionary<long, SectionBox>();

        /// <summary>Extents of the full MEP solid across its own axis.</summary>
        private class SectionBox
        {
            public XYZ Origin, Sx, Sy;
            public double MinX = double.MaxValue, MaxX = double.MinValue, MinY = double.MaxValue, MaxY = double.MinValue;
        }

        /// <summary>
        /// The opening is sized from the whole MEP cross-section, not from the intersection solid:
        /// the host may already be partly cut (an architect's opening, a joined element), and its
        /// intersection then covers only part of the duct. The intersection only says where the host
        /// is; its faces are found through the host solid along the normal, so an element that stops
        /// inside the wall (a fitting takes over there) still gets an opening through the full
        /// thickness. The four corners of the section are run along the MEP axis to both faces,
        /// and those eight points size the opening.
        /// </summary>
        private List<XYZ> SweepSection(Element mep, List<Solid> mepSolids, Penetration p, XYZ normal)
        {
            double dMin = double.MaxValue, dMax = double.MinValue;
            foreach (XYZ q in p.Points)
            {
                double d = q.DotProduct(normal);
                if (d < dMin) dMin = d;
                if (d > dMax) dMax = d;
            }
            ExtendToHostFaces(p.Host, Centroid(p.Points), normal, ref dMin, ref dMax);

            SectionBox s = (p.End != null ? ConnectorSection(p.End, p.Axis) : null) ?? Section(mep, mepSolids, p);
            double an = p.Axis.DotProduct(normal);
            List<XYZ> result = new List<XYZ>(8);
            for (int i = 0; i < 4; i++)
            {
                XYZ corner = s.Origin
                           + s.Sx * ((i & 1) == 0 ? s.MinX : s.MaxX)
                           + s.Sy * ((i & 2) == 0 ? s.MinY : s.MaxY);
                double c = corner.DotProduct(normal);
                result.Add(corner + p.Axis * ((dMin - c) / an));
                result.Add(corner + p.Axis * ((dMax - c) / an));
            }
            return result;
        }

        /// <summary>
        /// Widens [dMin, dMax] to the host's full thickness at the crossing: the host solid is cut
        /// with a line along the normal, and every span touching the part the element is in is taken.
        /// </summary>
        private static void ExtendToHostFaces(HostItem host, XYZ at, XYZ normal, ref double dMin, ref double dMax)
        {
            const double reach = 10;   // feet, far more than any wall or slab is thick
            const double tol = 1e-3;
            Line line = Line.CreateBound(at - normal * reach, at + normal * reach);
            SolidCurveIntersectionOptions opt = new SolidCurveIntersectionOptions
            {
                ResultType = SolidCurveIntersectionMode.CurveSegmentsInside
            };

            List<(double a, double b)> spans = new List<(double a, double b)>();
            foreach (Solid hs in host.Solids)
            {
                SolidCurveIntersection sci;
                try { sci = hs.IntersectWithCurve(line, opt); }
                catch { continue; }
                for (int i = 0; i < sci.SegmentCount; i++)
                {
                    Curve seg = sci.GetCurveSegment(i);
                    double a = seg.GetEndPoint(0).DotProduct(normal), b = seg.GetEndPoint(1).DotProduct(normal);
                    spans.Add((Math.Min(a, b), Math.Max(a, b)));
                }
            }

            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var span in spans)
                {
                    if (span.a > dMax + tol || span.b < dMin - tol) continue;
                    if (span.a < dMin - 1e-9) { dMin = span.a; grew = true; }
                    if (span.b > dMax + 1e-9) { dMax = span.b; grew = true; }
                }
            }
        }

        /// <summary>Section of a fitting/accessory at its connector; null if the connector has no usable size.</summary>
        private static SectionBox ConnectorSection(Connector c, XYZ axis)
        {
            try
            {
                Transform cs = c.CoordinateSystem;
                double hx, hy;
                if (c.Shape == ConnectorProfileType.Round) hx = hy = c.Radius;
                else { hx = c.Width / 2; hy = c.Height / 2; }
                if (hx <= 0 || hy <= 0) return null;

                return new SectionBox
                {
                    Origin = cs.Origin,
                    Sx = cs.BasisX,
                    Sy = axis.CrossProduct(cs.BasisX).Normalize(),
                    MinX = -hx, MaxX = hx, MinY = -hy, MaxY = hy
                };
            }
            catch { return null; }
        }

        private SectionBox Section(Element mep, List<Solid> mepSolids, Penetration p)
        {
            long key = mep.Id.GetValue();
            if (_sections.TryGetValue(key, out SectionBox cached)) return cached;

            XYZ a = p.Axis;
            XYZ sx = p.SectionX != null ? p.SectionX - a * p.SectionX.DotProduct(a) : null;
            if (sx == null || sx.GetLength() < 1e-6)
                sx = Math.Abs(a.Z) < 0.9 ? XYZ.BasisZ.CrossProduct(a) : XYZ.BasisX.CrossProduct(a);
            sx = sx.Normalize();

            SectionBox s = new SectionBox
            {
                Origin = (mep.Location as LocationCurve)?.Curve?.GetEndPoint(0) ?? XYZ.Zero,
                Sx = sx,
                Sy = a.CrossProduct(sx).Normalize()
            };

            List<XYZ> verts = new List<XYZ>();
            foreach (Solid ms in mepSolids) GeometryUtils.CollectVertices(ms, verts);
            foreach (XYZ q in verts)
            {
                XYZ r = q - s.Origin;
                double x = r.DotProduct(s.Sx), y = r.DotProduct(s.Sy);
                if (x < s.MinX) s.MinX = x; if (x > s.MaxX) s.MaxX = x;
                if (y < s.MinY) s.MinY = y; if (y > s.MaxY) s.MaxY = y;
            }

            _sections[key] = s;
            return s;
        }

        /// <summary>Axis (null if unknown), section direction and shape of the MEP element at the crossing.</summary>
        private static void ReadMepProfile(Element mep, Penetration p)
        {
            p.Axis = null;
            p.SectionX = null;
            p.IsRound = false;
            p.End = null;

            if (mep is MEPCurve curve)
            {
                if (curve.Location is LocationCurve lc && lc.Curve != null)
                {
                    XYZ d = lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0);
                    if (d.GetLength() > 1e-9) p.Axis = d.Normalize();
                }
                foreach (Connector c in curve.ConnectorManager.Connectors)
                {
                    if (c.ConnectorType != ConnectorType.End) continue;
                    p.IsRound = c.Shape == ConnectorProfileType.Round;
                    try { p.SectionX = c.CoordinateSystem.BasisX; } catch { }
                    break;
                }
            }
            else if ((mep as FamilyInstance)?.MEPModel?.ConnectorManager is ConnectorManager cm)
            {
                // Fittings and accessories: the connector nearest the crossing gives direction and size.
                XYZ at = Centroid(p.Points);
                double best = double.MaxValue;
                foreach (Connector c in cm.Connectors)
                {
                    if (c.ConnectorType != ConnectorType.End) continue;
                    double dist = c.Origin.DistanceTo(at);
                    if (dist < best) { best = dist; p.End = c; }
                }
                if (p.End != null)
                {
                    p.IsRound = p.End.Shape == ConnectorProfileType.Round;
                    p.Axis = p.End.CoordinateSystem.BasisZ;
                    p.SectionX = p.End.CoordinateSystem.BasisX;
                }
            }

            Parameter ins = mep.get_Parameter(BuiltInParameter.RBS_REFERENCE_INSULATION_THICKNESS);
            p.Insulation = ins != null && ins.HasValue ? ins.AsDouble() : 0;
        }

        /// <summary>
        /// ShaftMaster shaft through a slab: vertical axis, its profile and shape. A rectangle's side
        /// becomes the section direction, so the frame (and Width / Length) follow the shaft's sides.
        /// </summary>
        private static void ReadShaft(Element mep, List<Solid> mepSolids, Penetration p)
        {
            string key = ShaftProfile.ShaftKeyOf(mep);
            if (key == null) return;

            CurveLoop loop = ShaftProfile.Read(mepSolids);
            if (loop == null) return;   // no bottom face: handled as an ordinary element

            p.ShaftKey = key;
            p.ShaftProfile = loop;
            p.ShaftShape = ShaftProfile.Classify(loop, out XYZ side);
            p.Axis = XYZ.BasisZ;
            p.SectionX = side ?? XYZ.BasisX;
            p.IsRound = p.ShaftShape == ProfileShape.Circle;
            p.Insulation = 0;
        }

        private static Frame BuildFrame(Penetration p)
        {
            if (p.Host.Kind == HostKind.Wall)
            {
                Curve axis = p.Host.Axis;
                if (axis != null)
                {
                    XYZ c = Centroid(p.Points);
                    IntersectionResult ir = axis.Project(c);
                    XYZ tangent = ir != null
                        ? axis.ComputeDerivatives(ir.Parameter, false).BasisX
                        : axis.GetEndPoint(1) - axis.GetEndPoint(0);
                    return Frame.Horizontal(tangent);
                }
                // No location line: the opening faces the MEP element.
                return Frame.Horizontal(XYZ.BasisZ.CrossProduct(p.Axis));
            }

            // Floors: align the opening with the duct's cross-section when it runs vertically,
            // with its plan direction when it is sloped.
            XYZ horiz = new XYZ(p.Axis.X, p.Axis.Y, 0);
            if (horiz.GetLength() > 0.1) return Frame.Horizontal(horiz);
            return Frame.Horizontal(p.SectionX ?? XYZ.BasisX);
        }

        private static XYZ Centroid(List<XYZ> pts)
        {
            XYZ sum = XYZ.Zero;
            foreach (XYZ q in pts) sum += q;
            return sum / pts.Count;
        }
    }
}
