using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Openings
{
    /// <summary>A wall opening as it stands in the model: its frame and size, read back from the DirectShape.</summary>
    public class WallOpening
    {
        public ExistingOpening Existing;
        public DirectShape Shape => Existing.Shape;
        public Frame Frame;
        public XYZ Center;
        public bool Round;
        public double Width, Height, Depth, Diameter;
        public int? OpeningId;

        /// <summary>Null unless the element is one of this add-in's openings in a wall (not a free shape).</summary>
        public static WallOpening From(ExistingOpening e)
        {
            DirectShape ds = e.Shape;
            if (Text(ds, OpeningParams.HostType) != "Wall") return null;
            string shape = Text(ds, OpeningParams.Shape);
            if (shape != "Rectangular" && shape != "Round") return null;

            WallOpening w = new WallOpening
            {
                Existing = e,
                Center = e.Center,
                Round = shape == "Round",
                Width = Number(ds, OpeningParams.Width),
                Height = Number(ds, OpeningParams.Height),
                Depth = Number(ds, OpeningParams.Depth),
                Diameter = Number(ds, OpeningParams.Diameter)
            };
            Parameter idp = ds.get_Parameter(OpeningParams.OpeningId.Guid);
            if (idp != null && idp.HasValue) w.OpeningId = idp.AsInteger();
            if (w.Round) { w.Width = w.Height = w.Diameter; }
            if (w.Width <= 0 || w.Depth <= 0) return null;

            XYZ x = e.XDir ?? XFromGeometry(ds, w.Depth);
            if (x == null) return null;
            w.Frame = Frame.Horizontal(x);
            return w;
        }

        public OpeningPlan ToPlan() => new OpeningPlan
        {
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

        private static string Text(Element e, OpeningParam op)
        {
            Parameter p = e.get_Parameter(op.Guid);
            return p != null && p.HasValue ? p.AsString() : null;
        }

        private static double Number(Element e, OpeningParam op)
        {
            Parameter p = e.get_Parameter(op.Guid);
            return p != null && p.HasValue ? p.AsDouble() : 0;
        }
    }

    /// <summary>Lets Selection.PickObject take only this add-in's wall openings.</summary>
    public class WallOpeningFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (!(elem is DirectShape ds) || ds.ApplicationId != OpeningParams.AppId) return false;
            Parameter p = ds.get_Parameter(OpeningParams.HostType.Guid);
            return p != null && p.HasValue && p.AsString() == "Wall";
        }

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    /// <summary>
    /// Width (diameter) of a wall opening edited by sketch, the ShaftMaster way: plain model lines the
    /// user works on with Revit's own tools, read back when they are done. Here the opening's two side
    /// edges are drawn in the plan; the user moves them (Move, drag, Align…) and presses Apply in the
    /// palette. The distance between them along the wall is the new width, their middle the new centre.
    /// </summary>
    public class OpeningEditor
    {
        private const int LineWeight = 8;

        public long OpeningElementId { get; private set; }
        public int? OpeningId { get; private set; }
        public List<ElementId> LineIds { get; } = new List<ElementId>();

        /// <summary>API context, outside a transaction. Null (after telling the user why) if it cannot start.</summary>
        public static OpeningEditor Start(UIDocument uidoc, WallOpening w)
        {
            Document doc = uidoc.Document;
            if (!(doc.ActiveView is ViewPlan plan) || plan.IsTemplate)
            {
                TaskDialog.Show("Openings", "Open a floor plan to edit the width: the opening's side lines are drawn there.");
                return null;
            }

            double z = plan.GenLevel?.ProjectElevation ?? w.Center.Z;
            XYZ c = new XYZ(w.Center.X, w.Center.Y, z);
            XYZ x = w.Frame.X, y = w.Frame.Y;
            double half = w.Width / 2, d = w.Depth / 2;

            OpeningEditor ed = new OpeningEditor { OpeningElementId = w.Shape.Id.GetValue(), OpeningId = w.OpeningId };
            using (Transaction t = new Transaction(doc, "Openings: edit width"))
            {
                t.Start();
                SketchPlane sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, c));

                OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(new Color(230, 0, 60));
                ogs.SetProjectionLineWeight(LineWeight);

                for (int side = -1; side <= 1; side += 2)
                {
                    XYZ mid = c + x * (side * half);
                    ModelCurve mc = doc.Create.NewModelCurve(Line.CreateBound(mid - y * d, mid + y * d), sp);
                    plan.SetElementOverrides(mc.Id, ogs);
                    ed.LineIds.Add(mc.Id);
                }
                t.Commit();
            }
            uidoc.Selection.SetElementIds(ed.LineIds);
            return ed;
        }

        /// <summary>API context. Rebuilds the opening from the lines, then removes them. False: nothing changed.</summary>
        public bool Apply(Document doc, out string message)
        {
            message = null;
            DirectShape ds = doc.GetElement(ElementIdExtensions.FromValue(OpeningElementId)) as DirectShape;
            ExistingOpening e = ds == null ? null : ExistingOpening.Collect(doc).FirstOrDefault(o => o.Shape.Id == ds.Id);
            WallOpening w = e == null ? null : WallOpening.From(e);
            if (w == null) { message = "The opening is no longer in the model."; Cancel(doc); return false; }

            List<Line> lines = LineIds.Select(id => doc.GetElement(id)).OfType<ModelCurve>()
                .Select(mc => mc.GeometryCurve).OfType<Line>().ToList();
            if (lines.Count != 2)
            {
                message = "Keep exactly the two side lines (they were deleted or replaced). Nothing changed.";
                Cancel(doc);
                return false;
            }

            // Each line counts at its middle, projected on the wall direction through the opening centre.
            XYZ x = w.Frame.X;
            double a = (lines[0].Evaluate(0.5, true) - w.Center).DotProduct(x);
            double b = (lines[1].Evaluate(0.5, true) - w.Center).DotProduct(x);
            double width = Math.Abs(a - b);
            if (width < 10 / 304.8) { message = "The lines are (almost) on top of each other. Nothing changed."; Cancel(doc); return false; }

            double oldBottom = w.Center.Z - (w.Round ? w.Diameter : w.Height) / 2;
            w.Center += x * ((a + b) / 2);
            if (w.Round) w.Diameter = w.Width = w.Height = width;
            else w.Width = width;
            double bottomDelta = (w.Center.Z - (w.Round ? w.Diameter : w.Height) / 2) - oldBottom;

            using (Transaction t = new Transaction(doc, "Openings: apply width"))
            {
                t.Start();
                DeleteLines(doc);

                ds.SetShape(new List<GeometryObject> { OpeningPlacer.BuildSolid(w.ToPlan()) });
                ds.ApplicationDataId = ExistingOpening.KeyOf(e.HostKey ?? "", e.Meps, x, true);

                if (w.Round)
                {
                    Set(ds, OpeningParams.Diameter, w.Diameter);
                    Shift(ds, OpeningParams.BottomFromFloor, bottomDelta);
                    Shift(ds, OpeningParams.BottomElevPbp, bottomDelta);
                    Shift(ds, OpeningParams.BottomElevSp, bottomDelta);
                }
                else Set(ds, OpeningParams.Width, w.Width);
                t.Commit();
            }
            return true;
        }

        /// <summary>API context: removes the lines, leaves the opening as it was.</summary>
        public void Cancel(Document doc)
        {
            if (LineIds.Count == 0) return;
            try
            {
                using (Transaction t = new Transaction(doc, "Openings: cancel width edit"))
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

        private static void Set(Element e, OpeningParam op, double value)
        {
            Parameter p = e.get_Parameter(op.Guid);
            if (p != null && !p.IsReadOnly) p.Set(value);
        }

        private static void Shift(Element e, OpeningParam op, double delta)
        {
            Parameter p = e.get_Parameter(op.Guid);
            if (p != null && !p.IsReadOnly && p.HasValue) p.Set(p.AsDouble() + delta);
        }
    }
}
