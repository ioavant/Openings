using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>One opening to place: the result of one penetration or of several merged ones.</summary>
    public class OpeningPlan
    {
        public HostKind Kind;
        public bool Round;
        public XYZ Center;
        public Frame Frame;
        public double Width;      // along Frame.X
        public double Height;     // wall: along Z; floor: "Length" along Frame.Y
        public double Depth;      // through the host
        public double Diameter;   // round only
        public List<Penetration> Members = new List<Penetration>();
        public XYZ ProbeAxis;     // direction the floor probes step along (wall openings)

        /// <summary>Shaft that is neither a rectangle nor a circle: its own plan profile, extruded through the slab.</summary>
        public CurveLoop FreeProfile;
        public bool IsFree => FreeProfile != null;

        /// <summary>All members share one host (plans are built per host). Set for a plan read back from the model.</summary>
        public string HostKey
        {
            get => _hostKey ?? (Members.Count > 0 ? Members[0].Host.Key : null);
            set => _hostKey = value;
        }
        private string _hostKey;

        public OpeningPlan Clone() => (OpeningPlan)MemberwiseClone();
        public List<long> MepIds => Members.Select(m => m.Mep.Id.GetValue()).Distinct().ToList();

        public double BottomZ => Kind == HostKind.Wall
            ? Center.Z - (Round ? Diameter : Height) / 2
            : Center.Z - Depth / 2;
    }

    public class OpeningPlanner
    {
        // Within ~5° of the host normal a round element gets a round opening;
        // beyond that its cut through the host is an ellipse and a rectangle fits better.
        private const double RoundMaxCos = 0.996;

        // Parts of one round run (a duct and the fitting it hands over to inside the wall) still
        // make a round opening when their sections agree within this, i.e. width ≈ height.
        private const double RoundSectionTolerance = 5 / 304.8;

        private readonly OpeningSettings _s;

        public int MergedGroups { get; private set; }

        public OpeningPlanner(OpeningSettings settings) { _s = settings; }

        public List<OpeningPlan> Plan(List<Penetration> penetrations)
        {
            List<OpeningPlan> plans = new List<OpeningPlan>();
            foreach (var hostGroup in penetrations.GroupBy(p => p.Host.Key))
            {
                List<Penetration> items = hostGroup.Where(p => !p.IsShaft).ToList();
                foreach (List<Penetration> cluster in Cluster(items))
                {
                    if (cluster.Count > 1) MergedGroups++;
                    plans.Add(BuildPlan(cluster));
                }

                // A shaft is never merged with ducts; its per-level segments in one slab make one opening.
                foreach (var shaft in hostGroup.Where(p => p.IsShaft).GroupBy(p => p.ShaftKey))
                    plans.Add(BuildShaftPlan(shaft.ToList()));
            }
            return plans;
        }

        // ── Merging ───────────────────────────────────────────────────────────

        private List<List<Penetration>> Cluster(List<Penetration> items)
        {
            int n = items.Count;
            int[] parent = Enumerable.Range(0, n).ToArray();
            int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }

            if (n > 1)
            {
                for (int i = 0; i < n; i++)
                {
                    Frame f = items[i].Frame;
                    LocalBox a = FaceBox(items[i], f);
                    for (int j = i + 1; j < n; j++)
                    {
                        if (Root(i) == Root(j)) continue;
                        LocalBox b = FaceBox(items[j], f);
                        if (CloseEnough(a, b, items[i].Host.Kind))
                            parent[Root(j)] = Root(i);
                    }
                }
            }

            return Enumerable.Range(0, n)
                .GroupBy(Root)
                .Select(g => g.Select(i => items[i]).ToList())
                .ToList();
        }

        /// <summary>
        /// Elements that overlap on the host face (a duct and its fitting inside the wall) always share
        /// one opening. Otherwise, with merging on: the clear gap between the two openings (both already
        /// grown by the margin) is within the merge distance.
        /// </summary>
        private bool CloseEnough(LocalBox a, LocalBox b, HostKind kind)
        {
            const double touch = 1e-3;
            double gapX = Math.Max(a.MinX, b.MinX) - Math.Min(a.MaxX, b.MaxX);
            double gapF = kind == HostKind.Wall
                ? Math.Max(a.MinZ, b.MinZ) - Math.Min(a.MaxZ, b.MaxZ)
                : Math.Max(a.MinY, b.MinY) - Math.Min(a.MaxY, b.MaxY);
            if (gapX <= touch && gapF <= touch) return true;
            if (!_s.MergeEnabled) return false;

            double m = 2 * _s.MarginPerSide;   // each opening grows by the margin on its side
            return gapX - m <= _s.MergeDistance && gapF - m <= _s.MergeDistance;
        }

        /// <summary>The penetration's extents in frame f, grown by its insulation across the face.</summary>
        private LocalBox FaceBox(Penetration p, Frame f)
        {
            LocalBox b = LocalBox.Of(p.Points, f);
            double ins = _s.IncludeInsulation ? p.Insulation : 0;
            if (ins <= 0) return b;

            b.MinX -= ins; b.MaxX += ins;
            if (p.Host.Kind == HostKind.Wall) { b.MinZ -= ins; b.MaxZ += ins; }
            else { b.MinY -= ins; b.MaxY += ins; }
            return b;
        }

        // ── Sizing ────────────────────────────────────────────────────────────

        /// <summary>
        /// Shaft: the opening is the shaft's own section — no margin, no insulation. A rectangle or a
        /// circle becomes the usual rectangular / round opening (the frame follows the rectangle's
        /// sides, see IntersectionFinder); anything else is extruded from the shaft profile as a free shape.
        /// </summary>
        private static OpeningPlan BuildShaftPlan(List<Penetration> parts)
        {
            Penetration first = parts[0];
            Frame f = first.Frame;

            LocalBox box = new LocalBox();
            foreach (Penetration p in parts) box.Add(LocalBox.Of(p.Points, f));

            OpeningPlan plan = new OpeningPlan
            {
                Kind = first.Host.Kind,
                Center = box.CenterIn(f),
                Frame = f,
                Width = box.MaxX - box.MinX,
                Height = first.Host.Kind == HostKind.Wall ? box.MaxZ - box.MinZ : box.MaxY - box.MinY,
                Depth = first.Host.Kind == HostKind.Wall ? box.MaxY - box.MinY : box.MaxZ - box.MinZ,
                Members = parts,
                ProbeAxis = first.Axis
            };

            if (first.ShaftShape == ProfileShape.Circle)
            {
                plan.Round = true;
                plan.Diameter = Math.Max(plan.Width, plan.Height);
            }
            else if (first.ShaftShape == ProfileShape.Free && first.ShaftProfile != null)
                plan.FreeProfile = first.ShaftProfile;
            return plan;
        }

        private OpeningPlan BuildPlan(List<Penetration> cluster)
        {
            Penetration first = cluster[0];
            Frame f = first.Frame;
            HostKind kind = first.Host.Kind;

            LocalBox box = new LocalBox();
            foreach (Penetration p in cluster) box.Add(FaceBox(p, f));

            double m = _s.MarginPerSide;
            double width = box.MaxX - box.MinX + 2 * m;
            double height, depth;
            if (kind == HostKind.Wall)
            {
                height = box.MaxZ - box.MinZ + 2 * m;
                depth = box.MaxY - box.MinY;
            }
            else
            {
                height = box.MaxY - box.MinY + 2 * m;
                depth = box.MaxZ - box.MinZ;
            }

            OpeningPlan plan = new OpeningPlan
            {
                Kind = kind,
                Center = box.CenterIn(f),
                Frame = f,
                Width = width,
                Height = height,
                Depth = depth,
                Members = cluster,
                ProbeAxis = first.Axis
            };

            // Round: every part is round and runs (nearly) straight through, and together they still
            // make a round section — one element, or a duct and its fitting, not two ducts side by side.
            XYZ normal = kind == HostKind.Wall ? f.Y : f.Z;
            if (_s.RoundForRoundElements &&
                cluster.All(p => p.IsRound && Math.Abs(p.Axis.DotProduct(normal)) >= RoundMaxCos) &&
                (cluster.Count == 1 || Math.Abs(width - height) <= RoundSectionTolerance))
            {
                plan.Round = true;
                plan.Diameter = Math.Max(width, height);
            }
            return plan;
        }
    }
}
