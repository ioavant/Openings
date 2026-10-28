using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>
    /// Builds the four opening families from "Metric Generic Model.rft" through the API:
    /// one extrusion centred on the origin, every size driven by a labelled dimension to
    /// the extrusion faces plus an EQ dimension through the centre reference plane, so the
    /// box always grows symmetrically about the insertion point.
    /// </summary>
    public static class OpeningFamilyBuilder
    {
        private const double MmToFeet = 1.0 / 304.8;

        // Initial geometry; the labels are set to the same values so nothing flexes on creation.
        private const double DefWidth = 300 * MmToFeet;
        private const double DefHeight = 200 * MmToFeet;
        private const double DefDepth = 200 * MmToFeet;
        private const double DefDiameter = 200 * MmToFeet;

        // Dimension lines sit outside the geometry: EQ closer, the label further out.
        private const double DimOffset = 1.5;

        public static string Build(Application app, string familyName, HostKind kind, bool round, string folder)
        {
            Document fdoc = app.NewFamilyDocument(FindTemplate(app));
            try
            {
                using (Transaction t = new Transaction(fdoc, "Build opening family"))
                {
                    t.Start();
                    BuildContent(fdoc, kind, round);
                    t.Commit();
                }

                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, familyName + ".rfa");
                fdoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                return path;
            }
            finally
            {
                fdoc.Close(false);
            }
        }

        private static void BuildContent(Document fdoc, HostKind kind, bool round)
        {
            FamilyManager fm = fdoc.FamilyManager;
            if (fm.CurrentType == null) fm.NewType("Standard");

            Parameter wpb = fdoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED);
            if (wpb != null && !wpb.IsReadOnly) wpb.Set(0);

            // ── Parameters ────────────────────────────────────────────────────
            FamilyParameter pDepth = AddLength(fm, OpeningParams.Depth, GroupTypeId.Geometry, DefDepth);
            FamilyParameter pWidth = null, pSecond = null, pDiameter = null;
            if (round)
                pDiameter = AddLength(fm, OpeningParams.Diameter, GroupTypeId.Geometry, DefDiameter);
            else
            {
                pWidth = AddLength(fm, OpeningParams.Width, GroupTypeId.Geometry, DefWidth);
                pSecond = AddLength(fm, kind == HostKind.Wall ? OpeningParams.Height : OpeningParams.Length,
                    GroupTypeId.Geometry, DefHeight);
            }

            if (kind == HostKind.Wall)
            {
                AddLength(fm, OpeningParams.BottomFromFloor, GroupTypeId.Constraints, 0);
                AddLength(fm, OpeningParams.CenterFromFloor, GroupTypeId.Constraints, 0);
            }
            AddLength(fm, OpeningParams.BottomElevPbp, GroupTypeId.Constraints, 0);
            AddLength(fm, OpeningParams.CenterElevPbp, GroupTypeId.Constraints, 0);
            AddLength(fm, OpeningParams.BottomElevSp, GroupTypeId.Constraints, 0);
            AddLength(fm, OpeningParams.CenterElevSp, GroupTypeId.Constraints, 0);
            fm.AddParameter(OpeningParams.HostInfo, GroupTypeId.IdentityData, SpecTypeId.String.Text, true);
            fm.AddParameter(OpeningParams.MepInfo, GroupTypeId.IdentityData, SpecTypeId.String.Text, true);

            // ── Views and centre planes ───────────────────────────────────────
            View plan = new FilteredElementCollector(fdoc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate)
                .OrderBy(v => v.Name == "Ref. Level" ? 0 : 1)
                .First();
            View front = new FilteredElementCollector(fdoc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Elevation)
                .OrderBy(v => v.ViewDirection.Y)            // Front looks along +Y: ViewDirection = -Y
                .First();

            ReferencePlane cX = CenterPlane(fdoc, XYZ.BasisX, plan);
            ReferencePlane cY = CenterPlane(fdoc, XYZ.BasisY, plan);
            ReferencePlane cZ = CenterPlane(fdoc, XYZ.BasisZ, front);

            // ── Geometry ──────────────────────────────────────────────────────
            // Wall: profile in the XZ plane, extruded along Y (through the wall).
            // Floor: profile in the XY plane, extruded along Z (through the slab).
            XYZ extrDir = kind == HostKind.Wall ? XYZ.BasisY : XYZ.BasisZ;
            XYZ u = XYZ.BasisX;
            XYZ v = kind == HostKind.Wall ? XYZ.BasisZ : XYZ.BasisY;

            CurveArray loop = new CurveArray();
            if (round)
            {
                double r = DefDiameter / 2;
                loop.Append(Arc.Create(XYZ.Zero, r, 0, Math.PI, u, v));
                loop.Append(Arc.Create(XYZ.Zero, r, Math.PI, 2 * Math.PI, u, v));
            }
            else
            {
                double a = DefWidth / 2, b = DefHeight / 2;
                XYZ p0 = -a * u - b * v, p1 = a * u - b * v, p2 = a * u + b * v, p3 = -a * u + b * v;
                loop.Append(Line.CreateBound(p0, p1));
                loop.Append(Line.CreateBound(p1, p2));
                loop.Append(Line.CreateBound(p2, p3));
                loop.Append(Line.CreateBound(p3, p0));
            }
            CurveArrArray profile = new CurveArrArray();
            profile.Append(loop);

            SketchPlane sp = SketchPlane.Create(fdoc, Plane.CreateByNormalAndOrigin(extrDir, XYZ.Zero));
            Extrusion ex = fdoc.FamilyCreate.NewExtrusion(true, profile, sp, DefDepth);
            ex.StartOffset = -DefDepth / 2;
            ex.EndOffset = DefDepth / 2;
            fdoc.Regenerate();

            // ── Dimensions ────────────────────────────────────────────────────
            if (kind == HostKind.Wall)
            {
                DriveAxis(fdoc, ex, plan, XYZ.BasisY, cY, pDepth);
                if (round)
                    DriveDiameter(fdoc, ex, front, pDiameter);
                else
                {
                    DriveAxis(fdoc, ex, plan, XYZ.BasisX, cX, pWidth);
                    DriveAxis(fdoc, ex, front, XYZ.BasisZ, cZ, pSecond);
                }
            }
            else
            {
                DriveAxis(fdoc, ex, front, XYZ.BasisZ, cZ, pDepth);
                if (round)
                    DriveDiameter(fdoc, ex, plan, pDiameter);
                else
                {
                    DriveAxis(fdoc, ex, plan, XYZ.BasisX, cX, pWidth);
                    DriveAxis(fdoc, ex, plan, XYZ.BasisY, cY, pSecond);
                }
            }
            fdoc.Regenerate();
        }

        private static FamilyParameter AddLength(FamilyManager fm, string name, ForgeTypeId group, double value)
        {
            FamilyParameter p = fm.AddParameter(name, group, SpecTypeId.Length, true);
            fm.Set(p, value);
            return p;
        }

        /// <summary>
        /// EQ dimension face–centre plane–face, then a labelled overall dimension
        /// face–face along <paramref name="axis"/>.
        /// </summary>
        private static void DriveAxis(Document fdoc, Extrusion ex, View view, XYZ axis,
                                      ReferencePlane center, FamilyParameter label)
        {
            Reference neg = FaceRef(ex, -axis);
            Reference pos = FaceRef(ex, axis);
            if (neg == null || pos == null)
                throw new InvalidOperationException($"Extrusion face along {axis} not found.");

            // Dimension lines lie in the view plane, offset sideways from the geometry.
            XYZ side = Math.Abs(axis.Z) > 0.5 ? -XYZ.BasisX
                     : view.ViewType == ViewType.Elevation ? -XYZ.BasisZ
                     : Math.Abs(axis.X) > 0.5 ? -XYZ.BasisY : -XYZ.BasisX;

            ReferenceArray eq = new ReferenceArray();
            eq.Append(neg);
            eq.Append(center.GetReference());
            eq.Append(pos);
            Dimension eqDim = fdoc.FamilyCreate.NewDimension(view, DimLine(axis, side, DimOffset), eq);
            eqDim.AreSegmentsEqual = true;

            ReferenceArray overall = new ReferenceArray();
            overall.Append(neg);
            overall.Append(pos);
            Dimension dim = fdoc.FamilyCreate.NewDimension(view, DimLine(axis, side, DimOffset + 0.5), overall);
            dim.FamilyLabel = label;
        }

        private static void DriveDiameter(Document fdoc, Extrusion ex, View view, FamilyParameter label)
        {
            Options opt = new Options { ComputeReferences = true };
            foreach (GeometryObject go in ex.get_Geometry(opt))
            {
                if (!(go is Solid s)) continue;
                foreach (Edge e in s.Edges)
                {
                    if (!(e.AsCurve() is Arc arc)) continue;
                    XYZ origin = arc.Center + (arc.XDirection + arc.YDirection).Normalize() * (arc.Radius + 0.5);
                    Dimension dim = fdoc.FamilyCreate.NewDiameterDimension(view, e.Reference, origin);
                    dim.FamilyLabel = label;
                    return;
                }
            }
            throw new InvalidOperationException("Circular edge for the diameter dimension not found.");
        }

        private static Line DimLine(XYZ axis, XYZ side, double offset)
        {
            XYZ o = side * offset;
            return Line.CreateBound(o - axis, o + axis);
        }

        private static Reference FaceRef(Extrusion ex, XYZ normal)
        {
            Options opt = new Options { ComputeReferences = true };
            foreach (GeometryObject go in ex.get_Geometry(opt))
            {
                if (!(go is Solid s)) continue;
                foreach (Face f in s.Faces)
                    if (f is PlanarFace pf && pf.FaceNormal.DotProduct(normal) > 0.999)
                        return pf.Reference;
            }
            return null;
        }

        /// <summary>Template's centre reference plane with this normal through the origin, or a new one.</summary>
        private static ReferencePlane CenterPlane(Document fdoc, XYZ normal, View view)
        {
            ReferencePlane existing = new FilteredElementCollector(fdoc).OfClass(typeof(ReferencePlane))
                .Cast<ReferencePlane>()
                .FirstOrDefault(rp => Math.Abs(rp.Normal.DotProduct(normal)) > 0.999 &&
                                      Math.Abs(rp.BubbleEnd.DotProduct(normal)) < 1e-6);
            if (existing != null) return existing;

            // Plane through the origin spanned by the line bubble->free and cutVec.
            XYZ lineDir = Math.Abs(normal.X) > 0.5 ? XYZ.BasisY : XYZ.BasisX;
            XYZ cutVec = normal.CrossProduct(lineDir);
            ReferencePlane rp2 = fdoc.FamilyCreate.NewReferencePlane(-2 * lineDir, 2 * lineDir, cutVec, view);
            rp2.Name = "Center (Elevation)";
            return rp2;
        }

        private static string FindTemplate(Application app)
        {
            string[] names = { "Metric Generic Model.rft", "Generic Model.rft" };
            foreach (string n in names)
            {
                string p = Path.Combine(app.FamilyTemplatePath ?? "", n);
                if (File.Exists(p)) return p;
            }

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "RVT " + app.VersionNumber, "Family Templates");
            if (Directory.Exists(root))
            {
                foreach (string n in names)
                {
                    string hit = Directory.EnumerateFiles(root, n, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
            }
            throw new FileNotFoundException(
                "Family template 'Metric Generic Model.rft' not found. Install the Revit family templates " +
                "or put your own opening families (OPN_*.rfa) in the Families folder.");
        }
    }
}
