using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>Orthonormal frame an opening is measured in (host model coordinates).</summary>
    public class Frame
    {
        public XYZ X { get; }
        public XYZ Y { get; }
        public XYZ Z { get; }

        public Frame(XYZ x, XYZ y, XYZ z) { X = x; Y = y; Z = z; }

        /// <summary>Frame with Z up and X horizontal.</summary>
        public static Frame Horizontal(XYZ x)
        {
            XYZ hx = new XYZ(x.X, x.Y, 0);
            hx = hx.GetLength() < 1e-9 ? XYZ.BasisX : hx.Normalize();
            return new Frame(hx, XYZ.BasisZ.CrossProduct(hx), XYZ.BasisZ);
        }
    }

    /// <summary>Axis-aligned box in a Frame's coordinates.</summary>
    public class LocalBox
    {
        public double MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue;
        public double MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue;

        public bool IsEmpty => MinX > MaxX;

        public void Add(double x, double y, double z)
        {
            if (x < MinX) MinX = x; if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y; if (y > MaxY) MaxY = y;
            if (z < MinZ) MinZ = z; if (z > MaxZ) MaxZ = z;
        }

        public void Add(LocalBox other)
        {
            if (other.IsEmpty) return;
            Add(other.MinX, other.MinY, other.MinZ);
            Add(other.MaxX, other.MaxY, other.MaxZ);
        }

        public LocalBox Clone() => (LocalBox)MemberwiseClone();

        public XYZ CenterIn(Frame f) =>
            f.X * ((MinX + MaxX) / 2) + f.Y * ((MinY + MaxY) / 2) + f.Z * ((MinZ + MaxZ) / 2);

        public static LocalBox Of(IEnumerable<XYZ> points, Frame f)
        {
            LocalBox b = new LocalBox();
            foreach (XYZ p in points)
                b.Add(p.DotProduct(f.X), p.DotProduct(f.Y), p.DotProduct(f.Z));
            return b;
        }
    }

    public static class GeometryUtils
    {
        public static List<Solid> GetSolids(Element e, Transform t)
        {
            List<Solid> result = new List<Solid>();
            Options opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false };
            GeometryElement geom = e.get_Geometry(opt);
            if (geom == null) return result;

            Collect(geom, result);

            if (t != null && !t.IsIdentity)
                for (int i = 0; i < result.Count; i++)
                    result[i] = SolidUtils.CreateTransformed(result[i], t);
            return result;
        }

        private static void Collect(GeometryElement geom, List<Solid> into)
        {
            foreach (GeometryObject obj in geom)
            {
                if (obj is Solid s && s.Volume > 1e-9)
                    into.Add(s);
                else if (obj is GeometryInstance gi)
                    Collect(gi.GetInstanceGeometry(), into);
            }
        }

        /// <summary>Points on every edge of the solid (arcs tessellated).</summary>
        public static void CollectVertices(Solid s, List<XYZ> into)
        {
            foreach (Edge edge in s.Edges)
                into.AddRange(edge.Tessellate());
        }

        /// <summary>World-axis bounding box of a box transformed by t.</summary>
        public static void TransformBox(XYZ min, XYZ max, Transform t, out XYZ outMin, out XYZ outMax)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                XYZ p = new XYZ((i & 1) == 0 ? min.X : max.X,
                                (i & 2) == 0 ? min.Y : max.Y,
                                (i & 4) == 0 ? min.Z : max.Z);
                if (t != null) p = t.OfPoint(p);
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            outMin = new XYZ(x0, y0, z0);
            outMax = new XYZ(x1, y1, z1);
        }

        public static bool BoxesOverlap(XYZ aMin, XYZ aMax, XYZ bMin, XYZ bMax) =>
            aMin.X <= bMax.X && aMax.X >= bMin.X &&
            aMin.Y <= bMax.Y && aMax.Y >= bMin.Y &&
            aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;
    }
}
