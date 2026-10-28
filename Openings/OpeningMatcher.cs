using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>
    /// An opening already in the model, with the host and MEP elements it was placed for.
    /// Those are stored in the DirectShape's ApplicationDataId as "host=&lt;link&gt;:&lt;id&gt;;mep=&lt;id&gt;,&lt;id&gt;"
    /// (HostItem.Key and MEP ElementIds), so a re-run recognises the opening wherever it moved.
    /// </summary>
    public class ExistingOpening
    {
        public DirectShape Shape;
        public XYZ Center;
        public string HostKey;                         // null: placed before keys were stored
        public HashSet<long> Meps = new HashSet<long>();
        public XYZ XDir;                               // opening's X (along the wall); null on older openings
        public bool Manual;                            // size edited by hand: a re-run leaves it alone

        public static List<ExistingOpening> Collect(Document doc)
        {
            List<ExistingOpening> list = new List<ExistingOpening>();
            foreach (DirectShape ds in new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>())
            {
                if (ds.ApplicationId != OpeningParams.AppId) continue;
                BoundingBoxXYZ bb = ds.get_BoundingBox(null);
                if (bb == null) continue;

                ExistingOpening e = new ExistingOpening { Shape = ds, Center = (bb.Min + bb.Max) / 2 };
                foreach (string part in (ds.ApplicationDataId ?? "").Split(';'))
                {
                    if (part.StartsWith("host=")) e.HostKey = part.Substring(5);
                    else if (part.StartsWith("mep="))
                    {
                        foreach (string id in part.Substring(4).Split(','))
                            if (long.TryParse(id, out long v)) e.Meps.Add(v);
                    }
                    else if (part.StartsWith("x="))
                    {
                        string[] xy = part.Substring(2).Split(',');
                        if (xy.Length == 2 &&
                            double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double dx) &&
                            double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double dy))
                            e.XDir = new XYZ(dx, dy, 0);
                    }
                    else if (part == "manual=1") e.Manual = true;
                }
                if (e.HostKey != null && e.Meps.Count == 0) e.HostKey = null;
                list.Add(e);
            }
            return list;
        }

        public static string KeyOf(OpeningPlan plan) =>
            KeyOf(plan.HostKey, plan.MepIds, plan.Frame.X, false);

        /// <summary>"host=…;mep=…;x=dx,dy[;manual=1]" — x is the opening's horizontal X, for editing later.</summary>
        public static string KeyOf(string hostKey, IEnumerable<long> meps, XYZ xDir, bool manual) =>
            "host=" + hostKey + ";mep=" + string.Join(",", meps.OrderBy(id => id)) +
            (xDir == null ? "" : ";x=" + xDir.X.ToString("R", CultureInfo.InvariantCulture) + "," +
                                         xDir.Y.ToString("R", CultureInfo.InvariantCulture)) +
            (manual ? ";manual=1" : "");
    }

    /// <summary>
    /// Decides which existing opening each new plan updates and which existing openings go.
    ///
    /// A plan takes over the existing opening that shares the most of its MEP elements in the
    /// same host (nearest centre on a tie), so it keeps its ElementId and Opening ID. When ducts
    /// merge, the other openings they had are superseded and deleted; when a merged group splits,
    /// the larger part keeps the opening and the rest get new ones. An opening whose MEP elements
    /// were all checked in a host that was searched, but no longer cross it, is deleted too.
    /// </summary>
    public class OpeningMatcher
    {
        // Openings placed before keys were stored are matched by centre, as they used to be.
        private static readonly double LegacyTolerance = 50 / 304.8;

        public Dictionary<OpeningPlan, ExistingOpening> Reuse { get; } = new Dictionary<OpeningPlan, ExistingOpening>();
        public List<ExistingOpening> Obsolete { get; } = new List<ExistingOpening>();

        public OpeningMatcher(List<ExistingOpening> existing, List<OpeningPlan> plans,
                              ISet<long> checkedMeps, ISet<string> checkedHosts)
        {
            var candidates = new List<(OpeningPlan plan, ExistingOpening e, int overlap, double dist)>();
            foreach (OpeningPlan plan in plans)
                foreach (ExistingOpening e in existing)
                {
                    if (e.HostKey != plan.HostKey) continue;
                    int overlap = plan.MepIds.Count(e.Meps.Contains);
                    if (overlap > 0) candidates.Add((plan, e, overlap, e.Center.DistanceTo(plan.Center)));
                }

            HashSet<ExistingOpening> taken = new HashSet<ExistingOpening>();
            foreach (var c in candidates.OrderByDescending(c => c.overlap).ThenBy(c => c.dist))
            {
                if (Reuse.ContainsKey(c.plan) || taken.Contains(c.e)) continue;
                Reuse[c.plan] = c.e;
                taken.Add(c.e);
            }

            foreach (OpeningPlan plan in plans)
            {
                if (Reuse.ContainsKey(plan)) continue;
                ExistingOpening legacy = existing
                    .Where(e => e.HostKey == null && !taken.Contains(e))
                    .OrderBy(e => e.Center.DistanceTo(plan.Center))
                    .FirstOrDefault();
                if (legacy != null && legacy.Center.DistanceTo(plan.Center) < LegacyTolerance)
                {
                    Reuse[plan] = legacy;
                    taken.Add(legacy);
                }
            }

            HashSet<ExistingOpening> superseded = new HashSet<ExistingOpening>(candidates.Select(c => c.e));
            foreach (ExistingOpening e in existing)
            {
                if (taken.Contains(e) || e.HostKey == null) continue;
                bool noLongerCrosses = checkedHosts.Contains(e.HostKey) && e.Meps.All(checkedMeps.Contains);
                if (superseded.Contains(e) || noLongerCrosses)
                    Obsolete.Add(e);
            }
        }
    }
}
