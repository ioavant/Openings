using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Openings
{
    /// <summary>A wall opening as it stands in the model: its frame and size, read back from the element.</summary>
    public class WallOpening
    {
        public ExistingOpening Existing;
        public Element Element => Existing.Element;
        public Frame Frame;
        public XYZ Center;
        public bool Round;
        public double Width, Height, Depth, Diameter;
        public int? OpeningId;

        /// <summary>Null unless the element is one of this add-in's openings in a wall (not a free shape).</summary>
        public static WallOpening From(ExistingOpening e)
        {
            Element ds = e.Element;
            if (OpeningEditor.Text(ds, OpeningParams.HostType) != "Wall") return null;
            string shape = OpeningEditor.Text(ds, OpeningParams.Shape);
            if (shape != "Rectangular" && shape != "Round") return null;

            WallOpening w = new WallOpening
            {
                Existing = e,
                Center = e.Center,
                Round = shape == "Round",
                Width = OpeningEditor.Number(ds, OpeningParams.Width),
                Height = OpeningEditor.Number(ds, OpeningParams.Height),
                Depth = OpeningEditor.Number(ds, OpeningParams.Depth),
                Diameter = OpeningEditor.Number(ds, OpeningParams.Diameter),
                OpeningId = OpeningEditor.IdOf(ds)
            };
            if (w.Round) { w.Width = w.Height = w.Diameter; }
            if (w.Width <= 0 || w.Height <= 0 || w.Depth <= 0) return null;

            XYZ x = e.XDir ?? (ds is DirectShape asShape ? XFromGeometry(asShape, w.Depth) : null);
            if (x == null) return null;
            w.Frame = Frame.Horizontal(x);
            return w;
        }

        public OpeningPlan ToPlan() => new OpeningPlan
        {
            HostKey = Existing.HostKey,
            Kind = HostKind.Wall,
            Round = Round,
            Center = Center,
            Frame = Frame,
            Width = Width,
            Height = Height,
            Depth = Depth,
            Diameter = Diameter
        };

        /// <summary>
        /// Openings placed before the frame was stored: of the horizontal face normals, the one along
        /// which the solid is as thick as Opening Depth is the wall normal (Y); X = Y × Z.
        /// </summary>
        private static XYZ XFromGeometry(DirectShape ds, double depth)
        {
            List<Solid> solids = GeometryUtils.GetSolids(ds, null);
            List<XYZ> verts = new List<XYZ>();
            List<XYZ> normals = new List<XYZ>();
            foreach (Solid s in solids)
            {
                GeometryUtils.CollectVertices(s, verts);
                foreach (Face f in s.Faces)
                    if (f is PlanarFace pf && Math.Abs(pf.FaceNormal.Z) < 1e-6 &&
                        !normals.Any(n => Math.Abs(n.DotProduct(pf.FaceNormal)) > 0.9999))
                        normals.Add(pf.FaceNormal);
            }
            if (normals.Count == 0 || verts.Count == 0) return null;

            XYZ y = normals.OrderBy(n =>
            {
                double lo = verts.Min(v => v.DotProduct(n)), hi = verts.Max(v => v.DotProduct(n));
                return Math.Abs(hi - lo - depth);
            }).First();
            return y.CrossProduct(XYZ.BasisZ);
        }
    }

    /// <summary>A floor opening as it stands in the model: its plan profile (the bottom face) and depth.</summary>
    public class FloorOpening
    {
        public ExistingOpening Existing;
        public Element Element => Existing.Element;
        public CurveLoop Profile;     // at the bottom of the opening
        public double BottomZ, Depth;
        public Frame Frame;
        public int? OpeningId;

        public static FloorOpening From(ExistingOpening e)
        {
            Element ds = e.Element;
            if (OpeningEditor.Text(ds, OpeningParams.HostType) != "Floor") return null;
            double depth = OpeningEditor.Number(ds, OpeningParams.Depth);
            Frame frame = Frame.Horizontal(e.XDir ?? XYZ.BasisX);
            CurveLoop loop = ds is DirectShape shape
                ? ShaftProfile.Read(GeometryUtils.GetSolids(shape, null))
                : FamilyProfile(ds, frame, e.Center.Z - depth / 2);
            if (loop == null || depth <= 0) return null;

            return new FloorOpening
            {
                Existing = e,
                Profile = loop,
                BottomZ = loop.First().GetEndPoint(0).Z,
                Depth = depth,
                Frame = frame,
                OpeningId = OpeningEditor.IdOf(ds)
            };
        }

        /// <summary>A family's rectangle / circle, from its Opening … parameters, at the bottom of the slab.</summary>
        private static CurveLoop FamilyProfile(Element fi, Frame f, double bottomZ)
        {
            XYZ c0 = OpeningElements.CenterOf(fi);
            XYZ c = new XYZ(c0.X, c0.Y, bottomZ);
            CurveLoop loop = new CurveLoop();
            if (OpeningEditor.Text(fi, OpeningParams.Shape) == "Round")
            {
                double r = OpeningEditor.Number(fi, OpeningParams.Diameter) / 2;
                if (r <= 0) return null;
                loop.Append(Arc.Create(c, r, 0, Math.PI, f.X, f.Y));
                loop.Append(Arc.Create(c, r, Math.PI, 2 * Math.PI, f.X, f.Y));
                return loop;
            }
            double a = OpeningEditor.Number(fi, OpeningParams.Width) / 2, b = OpeningEditor.Number(fi, OpeningParams.Length) / 2;
            if (a <= 0 || b <= 0) return null;
            XYZ p0 = c - a * f.X - b * f.Y, p1 = c + a * f.X - b * f.Y, p2 = c + a * f.X + b * f.Y, p3 = c - a * f.X + b * f.Y;
            loop.Append(Line.CreateBound(p0, p1));
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p0));
            return loop;
        }

        public OpeningPlan ToPlan() => PlanFrom(Profile);

        /// <summary>
        /// The opening a closed plan profile at BottomZ makes: rectangle (in its own sides' frame),
        /// circle, or free shape (bounding size in the opening's frame).
        /// </summary>
        public OpeningPlan PlanFrom(CurveLoop loop)
        {
            ProfileShape shape = ShaftProfile.Classify(loop, out XYZ side);
            Frame frame = shape == ProfileShape.Rectangle ? Frame.Horizontal(side) : Frame;
            LocalBox box = LocalBox.Of(loop.SelectMany(c => c.Tessellate()), frame);
            OpeningPlan plan = new OpeningPlan
            {
                HostKey = Existing.HostKey,
                Kind = HostKind.Floor,
                Frame = frame,
                Center = box.CenterIn(frame),
                Width = box.MaxX - box.MinX,
                Height = box.MaxY - box.MinY,
                Depth = Depth
            };
            plan.Center = new XYZ(plan.Center.X, plan.Center.Y, BottomZ + Depth / 2);
            if (shape == ProfileShape.Circle)
            {
                plan.Round = true;
                plan.Diameter = Math.Max(plan.Width, plan.Height);
            }
            else if (shape == ProfileShape.Free)
                plan.FreeProfile = loop;
            return plan;
        }
    }

    /// <summary>Lets Selection.PickObject take only this add-in's openings (optionally only those in walls).</summary>
    public class OpeningFilter : ISelectionFilter
    {
        private readonly bool _wallsOnly;
        public OpeningFilter(bool wallsOnly) { _wallsOnly = wallsOnly; }

        public bool AllowElement(Element elem)
        {
            if (!OpeningElements.IsOpening(elem)) return false;
            return !_wallsOnly || OpeningEditor.Text(elem, OpeningParams.HostType) == "Wall";
        }

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    /// <summary>
    /// An opening edited by sketch, the ShaftMaster way: plain model lines the user works on with
    /// Revit's own tools (Move, drag, Align, draw, delete…), read back when they press Apply in the
    /// palette. What is drawn depends on the opening and the active view:
    /// <list type="bullet">
    /// <item>wall opening, plan — its two side edges: width (diameter) and position along the wall;</item>
    /// <item>wall opening, section facing the wall — sides, top and bottom: width and height;</item>
    /// <item>floor opening, plan — its whole profile: any closed shape (rectangle, circle or free).</item>
    /// </list>
    /// The edited opening is locked, so a run over the whole view leaves it as the user made it.
    /// </summary>
    public class OpeningEditor
    {
        private enum Mode { WallPlan, WallSection, FloorProfile }

        private const int LineWeight = 8;
        private static readonly double Join = 0.5 / 304.8;      // endpoints this close are one point
        private static readonly double Bridge = 50 / 304.8;     // gaps up to this are closed with a line

        private Mode _mode;
        private double _planeZ;
        private HashSet<long> _linesBefore;                    // floor profile: model lines that existed already

        public long OpeningElementId { get; private set; }
        public int? OpeningId { get; private set; }
        public string Hint { get; private set; }
        public List<ElementId> LineIds { get; } = new List<ElementId>();

        // ── Start ─────────────────────────────────────────────────────────────

        /// <summary>API context, outside a transaction. Null (after telling the user why) if it cannot start.</summary>
        public static OpeningEditor Start(UIDocument uidoc, ExistingOpening e)
        {
            Document doc = uidoc.Document;
            View view = doc.ActiveView;
            WallOpening w = WallOpening.From(e);
            FloorOpening f = w == null ? FloorOpening.From(e) : null;
            if (w == null && f == null)
            {
                TaskDialog.Show("Openings", "This opening cannot be edited (unknown shape or size).");
                return null;
            }

            OpeningEditor ed = new OpeningEditor { OpeningElementId = e.Element.Id.GetValue(), OpeningId = w?.OpeningId ?? f?.OpeningId };
            string name = ed.OpeningId.HasValue ? "#" + ed.OpeningId : e.Element.Id.GetValue().ToString();

            if (w != null && view is ViewPlan && !view.IsTemplate)
            {
                ed._mode = Mode.WallPlan;
                ed.Hint = $"Opening {name}: move the two red side lines in the plan (Move, drag, Align…) to set the " +
                          (w.Round ? "diameter" : "width") + ", then Apply.";
            }
            else if (w != null && view is ViewSection vs && !vs.IsTemplate &&
                     Math.Abs(vs.ViewDirection.DotProduct(w.Frame.Y)) > 0.9)
            {
                ed._mode = Mode.WallSection;
                ed.Hint = $"Opening {name}: move the red side lines (width) and top / bottom lines (height)" +
                          (w.Round ? " — a round opening takes the size you changed as its diameter" : "") + ", then Apply.";
            }
            else if (f != null && view is ViewPlan && !view.IsTemplate)
            {
                ed._mode = Mode.FloorProfile;
                ed.Hint = $"Opening {name}: edit the red profile in the plan — move, drag, delete or draw model lines " +
                          "until it is one closed shape — then Apply.";
            }
            else
            {
                TaskDialog.Show("Openings", w != null
                    ? "Open a floor plan (width) or a section facing the wall — the Section button makes one — (width and height)."
                    : "Open a floor plan to edit a floor opening's profile.");
                return null;
            }

            using (Transaction t = new Transaction(doc, "Openings: edit"))
            {
                t.Start();
                if (ed._mode == Mode.WallPlan) ed.DrawWallPlan(doc, (ViewPlan)view, w);
                else if (ed._mode == Mode.WallSection) ed.DrawWallSection(doc, (ViewSection)view, w);
                else ed.DrawFloorProfile(doc, (ViewPlan)view, f);
                t.Commit();
            }
            uidoc.Selection.SetElementIds(ed.LineIds);
            return ed;
        }

        private void DrawWallPlan(Document doc, ViewPlan plan, WallOpening w)
        {
            _planeZ = plan.GenLevel?.ProjectElevation ?? w.Center.Z;
            XYZ c = new XYZ(w.Center.X, w.Center.Y, _planeZ);
            SketchPlane sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, c));
            XYZ x = w.Frame.X, y = w.Frame.Y;
            for (int side = -1; side <= 1; side += 2)
            {
                XYZ mid = c + x * (side * w.Width / 2);
                AddLine(doc, plan, sp, Line.CreateBound(mid - y * (w.Depth / 2), mid + y * (w.Depth / 2)));
            }
        }

        private void DrawWallSection(Document doc, ViewSection view, WallOpening w)
        {
            // On the section's cut plane, so the lines are never hidden behind the wall.
            XYZ n = view.ViewDirection;
            XYZ c = w.Center - n * (w.Center - view.Origin).DotProduct(n);
            SketchPlane sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(n, c));
            XYZ x = w.Frame.X, z = XYZ.BasisZ;
            double hw = w.Width / 2, hh = w.Height / 2;
            for (int side = -1; side <= 1; side += 2)
            {
                AddLine(doc, view, sp, Line.CreateBound(c + x * (side * hw) - z * hh, c + x * (side * hw) + z * hh));
                AddLine(doc, view, sp, Line.CreateBound(c + z * (side * hh) - x * hw, c + z * (side * hh) + x * hw));
            }
        }

        private void DrawFloorProfile(Document doc, ViewPlan plan, FloorOpening f)
        {
            _planeZ = plan.GenLevel?.ProjectElevation ?? f.BottomZ;
            Transform move = Transform.CreateTranslation(new XYZ(0, 0, _planeZ - f.BottomZ));
            SketchPlane sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, _planeZ)));
            _linesBefore = new HashSet<long>(ModelCurveIds(doc));
            foreach (Curve c in f.Profile)
                AddLine(doc, plan, sp, c.CreateTransformed(move));
        }

        private void AddLine(Document doc, View view, SketchPlane sp, Curve curve)
        {
            OverrideGraphicSettings ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(new Color(230, 0, 60));
            ogs.SetProjectionLineWeight(LineWeight);

            ModelCurve mc = doc.Create.NewModelCurve(curve, sp);
            view.SetElementOverrides(mc.Id, ogs);
            LineIds.Add(mc.Id);
        }

        // ── Apply ─────────────────────────────────────────────────────────────

        /// <summary>
        /// API context. Rebuilds the opening from the lines, then removes them. False with a message:
        /// for a sketch that cannot be read the lines stay, so the user can fix it and Apply again
        /// (<paramref name="keepEditing"/>); otherwise the edit is over. A family opening edited into a free
        /// shape becomes a DirectShape (<see cref="OpeningElementId"/> then names the new element).
        /// </summary>
        public bool Apply(Document doc, OpeningSettings settings, out string message, out bool keepEditing)
        {
            message = null;
            keepEditing = false;
            ExistingOpening e = ExistingOpening.Collect(doc).FirstOrDefault(o => o.Element.Id.GetValue() == OpeningElementId);
            if (e == null) { message = "The opening is no longer in the model."; Cancel(doc); return false; }

            try
            {
                OpeningWriter writer = new OpeningWriter(doc, settings);
                Element el = _mode == Mode.FloorProfile ? ApplyFloor(doc, e, writer, out string note) : ApplyWall(doc, e, writer, out note);
                OpeningElementId = el.Id.GetValue();
                if (e.Mode == OpeningMode.Family && OpeningElements.ModeOf(el) != OpeningMode.Family)
                    message = $"The opening is now a Generic Model (DirectShape): {note}.";
                return true;
            }
            catch (SketchException ex)
            {
                message = ex.Message;
                keepEditing = true;
                return false;
            }
        }

        private class SketchException : Exception
        {
            public SketchException(string message) : base(message) { }
        }

        private List<Curve> Lines(Document doc) =>
            LineIds.Select(id => doc.GetElement(id)).OfType<ModelCurve>().Select(mc => mc.GeometryCurve).ToList();

        private Element ApplyWall(Document doc, ExistingOpening e, OpeningWriter writer, out string note)
        {
            WallOpening w = WallOpening.From(e) ?? throw new SketchException("The opening can no longer be read.");
            List<Line> lines = Lines(doc).OfType<Line>().ToList();
            XYZ x = w.Frame.X;

            // A line counts at its middle; side lines run across the width direction, the others along it.
            List<double> sides = lines.Where(l => Math.Abs(l.Direction.DotProduct(x)) < 0.5)
                .Select(l => (l.Evaluate(0.5, true) - w.Center).DotProduct(x)).ToList();
            List<double> levels = lines.Where(l => Math.Abs(l.Direction.DotProduct(x)) >= 0.5)
                .Select(l => l.Evaluate(0.5, true).Z - w.Center.Z).ToList();

            if (sides.Count != 2 || (_mode == Mode.WallSection && levels.Count != 2))
                throw new SketchException(_mode == Mode.WallSection
                    ? "Keep the two side lines and the top and bottom lines (one of them was deleted, or a line was added)."
                    : "Keep exactly the two side lines (one was deleted, or a line was added).");

            double width = Math.Abs(sides[0] - sides[1]);
            double dx = (sides[0] + sides[1]) / 2;
            double height = w.Height, dz = 0;
            if (_mode == Mode.WallSection)
            {
                height = Math.Abs(levels[0] - levels[1]);
                dz = (levels[0] + levels[1]) / 2;
            }
            if (width < 10 / 304.8 || height < 10 / 304.8)
                throw new SketchException("Two lines are (almost) on top of each other.");

            double oldCenterZ = w.Center.Z;
            double oldBottom = oldCenterZ - w.Height / 2;
            w.Center += x * dx + XYZ.BasisZ * dz;
            if (w.Round)
            {
                // One size for both: the one the user changed (the larger change if both were).
                double d = Math.Abs(width - w.Diameter) >= Math.Abs(height - w.Diameter) ? width : height;
                w.Diameter = w.Width = w.Height = d;
            }
            else { w.Width = width; w.Height = height; }

            double centerDelta = w.Center.Z - oldCenterZ;
            double bottomDelta = (w.Center.Z - w.Height / 2) - oldBottom;

            using (Transaction t = new Transaction(doc, "Openings: apply edit"))
            {
                t.Start();
                DeleteLines(doc);
                Element ds = writer.Write(w.ToPlan(), e.Element, e.Mode, out OpeningPlan plan, out note);
                OpeningPlacer.WriteSize(ds, plan, null);
                OpeningElements.SetKey(ds, ExistingOpening.KeyOf(e.HostKey ?? "", e.Meps, x, e.Geo));
                OpeningPlacer.SetParam(ds, OpeningParams.Locked, 1, null);

                Shift(ds, OpeningParams.CenterFromFloor, centerDelta);
                Shift(ds, OpeningParams.CenterElevPbp, centerDelta);
                Shift(ds, OpeningParams.CenterElevSp, centerDelta);
                Shift(ds, OpeningParams.BottomFromFloor, bottomDelta);
                Shift(ds, OpeningParams.BottomElevPbp, bottomDelta);
                Shift(ds, OpeningParams.BottomElevSp, bottomDelta);
                t.Commit();
                return ds;
            }
        }

        private Element ApplyFloor(Document doc, ExistingOpening e, OpeningWriter writer, out string note)
        {
            FloorOpening f = FloorOpening.From(e) ?? throw new SketchException("The opening can no longer be read.");

            // Our lines that are left, plus any the user drew on the same plane meanwhile.
            List<Curve> curves = Lines(doc);
            List<ElementId> drawn = new List<ElementId>();
            foreach (long id in ModelCurveIds(doc))
            {
                if (_linesBefore.Contains(id) || LineIds.Any(l => l.GetValue() == id)) continue;
                ModelCurve mc = doc.GetElement(ElementIdExtensions.FromValue(id)) as ModelCurve;
                Curve c = mc?.GeometryCurve;
                if (c == null || Math.Abs(c.GetEndPoint(0).Z - _planeZ) > 1e-3) continue;
                curves.Add(c);
                drawn.Add(mc.Id);
            }

            CurveLoop loop = Assemble(curves) ??
                throw new SketchException("The red lines do not make one closed shape. Fix them (or Cancel) and Apply again.");
            loop = CurveLoop.CreateViaTransform(loop, Transform.CreateTranslation(new XYZ(0, 0, f.BottomZ - _planeZ)));

            using (Transaction t = new Transaction(doc, "Openings: apply edit"))
            {
                t.Start();
                DeleteLines(doc);
                if (drawn.Count > 0) doc.Delete(drawn);
                Element ds = writer.Write(f.PlanFrom(loop), e.Element, e.Mode, out OpeningPlan plan, out note);
                OpeningPlacer.WriteSize(ds, plan, null);
                OpeningElements.SetKey(ds, ExistingOpening.KeyOf(e.HostKey ?? "", e.Meps, plan.Frame.X, e.Geo));
                OpeningPlacer.SetParam(ds, OpeningParams.Locked, 1, null);
                t.Commit();
                return ds;
            }
        }

        /// <summary>
        /// Chains the curves end to end into one closed loop (as ShaftMaster's AssembleCurveLoop does):
        /// curves are reversed as needed, small gaps closed with a line. Null if they do not close.
        /// A full circle the user drew is split in two halves.
        /// </summary>
        private static CurveLoop Assemble(List<Curve> input)
        {
            List<Curve> curves = new List<Curve>();
            foreach (Curve c in input)
            {
                if (!c.IsBound && c is Arc circle)
                {
                    curves.Add(Arc.Create(circle.Center, circle.Radius, 0, Math.PI, circle.XDirection, circle.YDirection));
                    curves.Add(Arc.Create(circle.Center, circle.Radius, Math.PI, 2 * Math.PI, circle.XDirection, circle.YDirection));
                }
                else if (c.IsBound && c.Length > Join) curves.Add(c);
            }
            if (curves.Count == 0) return null;

            List<Curve> chain = new List<Curve> { curves[0] };
            curves.RemoveAt(0);
            while (curves.Count > 0)
            {
                XYZ end = chain[chain.Count - 1].GetEndPoint(1);
                int best = -1; bool reverse = false; double bestDist = double.MaxValue;
                for (int i = 0; i < curves.Count; i++)
                {
                    double d0 = curves[i].GetEndPoint(0).DistanceTo(end), d1 = curves[i].GetEndPoint(1).DistanceTo(end);
                    if (d0 < bestDist) { bestDist = d0; best = i; reverse = false; }
                    if (d1 < bestDist) { bestDist = d1; best = i; reverse = true; }
                }
                if (bestDist > Bridge) return null;   // a separate piece: not one shape

                Curve next = reverse ? curves[best].CreateReversed() : curves[best];
                curves.RemoveAt(best);
                if (bestDist > Join) chain.Add(Line.CreateBound(end, next.GetEndPoint(0)));
                chain.Add(next);
            }

            XYZ last = chain[chain.Count - 1].GetEndPoint(1), first = chain[0].GetEndPoint(0);
            double gap = last.DistanceTo(first);
            if (gap > Bridge) return null;
            if (gap > Join) chain.Add(Line.CreateBound(last, first));

            try
            {
                CurveLoop loop = new CurveLoop();
                foreach (Curve c in chain) loop.Append(c);
                return loop.IsOpen() ? null : loop;
            }
            catch { return null; }   // ends meet only within the gap tolerance, or the loop crosses itself
        }

        // ── Cancel / helpers ──────────────────────────────────────────────────

        /// <summary>API context: removes the lines, leaves the opening as it was.</summary>
        public void Cancel(Document doc)
        {
            if (LineIds.Count == 0) return;
            try
            {
                using (Transaction t = new Transaction(doc, "Openings: cancel edit"))
                {
                    t.Start();
                    DeleteLines(doc);
                    t.Commit();
                }
            }
            catch { /* another document is active — the lines stay, they are plain model lines */ }
        }

        private void DeleteLines(Document doc)
        {
            List<ElementId> alive = LineIds.Where(id => doc.GetElement(id) != null).ToList();
            if (alive.Count > 0) doc.Delete(alive);
            LineIds.Clear();
        }

        // OfClass(typeof(ModelCurve)) throws on Revit 2023 (ShaftMaster's note): go through CurveElement.
        private static IEnumerable<long> ModelCurveIds(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).Where(c => c is ModelCurve)
                .Select(c => c.Id.GetValue());

        private static void Shift(Element e, OpeningParam op, double delta)
        {
            if (Math.Abs(delta) < 1e-9) return;
            Parameter p = e.get_Parameter(op.Guid);
            if (p != null && !p.IsReadOnly && p.HasValue) p.Set(p.AsDouble() + delta);
        }

        internal static string Text(Element e, OpeningParam op)
        {
            Parameter p = e.get_Parameter(op.Guid);
            return p != null && p.HasValue ? p.AsString() : null;
        }

        internal static double Number(Element e, OpeningParam op)
        {
            Parameter p = e.get_Parameter(op.Guid);
            return p != null && p.HasValue ? p.AsDouble() : 0;
        }

        internal static int? IdOf(Element e)
        {
            Parameter p = e.get_Parameter(OpeningParams.OpeningId.Guid);
            return p != null && p.HasValue ? p.AsInteger() : (int?)null;
        }
    }
}
