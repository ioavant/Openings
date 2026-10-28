using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Openings
{
    public enum ProfileShape
    {
        Rectangle,
        Circle,
        Free
    }

    /// <summary>
    /// ShaftMaster shafts as MEP elements: recognising one, and reading and classifying its plan
    /// profile. A shaft is a Generic Model DirectShape per level segment, extruded straight up,
    /// so the outer loop of its bottom face is the profile.
    /// </summary>
    public static class ShaftProfile
    {
        private const double Tol = 1e-4;   // feet

        // ShaftMaster's own identifiers (ShaftMaster.cs: ShaftSchema / ShaftParams). Never change them.
        private static readonly Guid ShaftSchemaGuid = new Guid("f2a3b4c5-cafe-6005-b400-000000000001");
        private static readonly Guid ShaftIdParamGuid = new Guid("a1b2c3d4-0001-0000-0000-000000000001");

        /// <summary>
        /// The shaft's id (all its per-level segments share it), or null if the element is not a
        /// ShaftMaster shaft. Same test as ShaftMaster's own selection filter: a DirectShape carrying
        /// its Extensible Storage entity; the "ShaftId" shared parameter is the fallback.
        /// </summary>
        public static string ShaftKeyOf(Element e)
        {
            if (!(e is DirectShape)) return null;

            string id = null;
            try
            {
                Schema schema = Schema.Lookup(ShaftSchemaGuid);
                Entity entity = schema != null ? e.GetEntity(schema) : null;
                if (entity != null && entity.IsValid()) id = entity.Get<string>("ShaftId");
            }
            catch { /* schema changed — fall back to the parameter */ }

            if (string.IsNullOrWhiteSpace(id))
            {
                Parameter p = e.get_Parameter(ShaftIdParamGuid);
                id = p != null && p.HasValue ? p.AsString() : null;
            }
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        /// <summary>Outer loop of the lowest downward-facing planar face, or null.</summary>
        public static CurveLoop Read(List<Solid> solids)
        {
            PlanarFace bottom = null;
            foreach (Solid s in solids)
                foreach (Face f in s.Faces)
                    if (f is PlanarFace pf && pf.FaceNormal.Z < -0.999 &&
                        (bottom == null || pf.Origin.Z < bottom.Origin.Z - Tol || pf.Area > bottom.Area + Tol))
                        bottom = pf;
            if (bottom == null) return null;

            return bottom.GetEdgesAsCurveLoops()
                .OrderByDescending(l => LoopArea(l))
                .FirstOrDefault();
        }

        /// <summary>Rectangle (sideDir = one side), circle, or free.</summary>
        public static ProfileShape Classify(CurveLoop loop, out XYZ sideDir)
        {
            sideDir = null;
            List<Curve> curves = loop.ToList();

            if (curves.Count > 0 && curves.All(c => c is Arc))
            {
                Arc a0 = (Arc)curves[0];
                if (curves.Cast<Arc>().All(a => a.Center.DistanceTo(a0.Center) < Tol && Math.Abs(a.Radius - a0.Radius) < Tol))
                    return ProfileShape.Circle;
                return ProfileShape.Free;
            }

            if (!curves.All(c => c is Line)) return ProfileShape.Free;

            // Collinear neighbours (a side drawn in several pieces) count as one side.
            List<XYZ> dirs = new List<XYZ>();
            foreach (Line l in curves.Cast<Line>())
            {
                XYZ d = l.Direction;
                if (dirs.Count > 0 && dirs[dirs.Count - 1].CrossProduct(d).GetLength() < 1e-6) continue;
                dirs.Add(d);
            }
            if (dirs.Count > 4 && dirs[0].CrossProduct(dirs[dirs.Count - 1]).GetLength() < 1e-6)
                dirs.RemoveAt(dirs.Count - 1);
            if (dirs.Count != 4) return ProfileShape.Free;

            for (int i = 0; i < 4; i++)
                if (Math.Abs(dirs[i].DotProduct(dirs[(i + 1) % 4])) > 1e-6) return ProfileShape.Free;

            sideDir = dirs[0];
            return ProfileShape.Rectangle;
        }

        private static double LoopArea(CurveLoop loop)
        {
            List<XYZ> pts = new List<XYZ>();
            foreach (Curve c in loop)
            {
                IList<XYZ> t = c.Tessellate();
                for (int i = 0; i < t.Count - 1; i++) pts.Add(t[i]);
            }
            double area = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                XYZ a = pts[i], b = pts[(i + 1) % pts.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(area) / 2;
        }
    }
}
