using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
        public bool Manual;                            // older "edited by hand" flag in the key; counts as locked
        public bool Locked;                            // Opening Lock: a whole-view run leaves it alone
        public bool Pinned;                            // pinned in Revit: no run touches it
        public string Geo;                             // MepFingerprint of its MEP elements when last placed; null on older openings

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
                    else if (part.StartsWith("geo=")) e.Geo = part.Substring(4);
                }
                if (e.HostKey != null && e.Meps.Count == 0) e.HostKey = null;
                Parameter lp = ds.get_Parameter(OpeningParams.Locked.Guid);
                e.Locked = e.Manual || (lp != null && lp.HasValue && lp.AsInteger() == 1);
                e.Pinned = ds.Pinned;
                list.Add(e);
            }
            return list;
        }

        public static string KeyOf(OpeningPlan plan, string geo) =>
            KeyOf(plan.HostKey, plan.MepIds, plan.Frame.X, geo);

        /// <summary>
        /// "host=…;mep=…;x=dx,dy;geo=…" — x is the opening's horizontal X (for editing later), geo where
        /// its MEP elements were when it was placed (<see cref="MepFingerprint"/>).
        /// </summary>
        public static string KeyOf(string hostKey, IEnumerable<long> meps, XYZ xDir, string geo) =>
            "host=" + hostKey + ";mep=" + string.Join(",", meps.OrderBy(id => id)) +
            (xDir == null ? "" : ";x=" + xDir.X.ToString("R", CultureInfo.InvariantCulture) + "," +
                                         xDir.Y.ToString("R", CultureInfo.InvariantCulture)) +
            (string.IsNullOrEmpty(geo) ? "" : ";geo=" + geo);

        /// <summary>
        /// Its MEP elements moved (or were resized, or the set changed) since the opening was placed.
        /// False when that is unknown (an opening from before the fingerprint was stored).
        /// </summary>
        public bool MepMoved(Document doc, IEnumerable<long> mepsNow) =>
            Geo != null && MepFingerprint.Of(doc, mepsNow) != Geo;
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

        /// <param name="respectLocks">Whole-view run: locked openings are neither updated (see OpeningPlacer) nor deleted.</param>
        public OpeningMatcher(List<ExistingOpening> existing, List<OpeningPlan> plans,
                              ISet<long> checkedMeps, ISet<string> checkedHosts, bool respectLocks, Document doc)
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
                // Pinned: never deleted by a run. Locked: kept by a whole-view run — unless its MEP elements
                // moved, then it follows them like any other opening (here: it no longer crosses, so it goes).
                if (e.Pinned) continue;
                if (respectLocks && e.Locked && !e.MepMoved(doc, e.Meps)) continue;
                bool noLongerCrosses = checkedHosts.Contains(e.HostKey) && e.Meps.All(checkedMeps.Contains);
                if (superseded.Contains(e) || noLongerCrosses)
                    Obsolete.Add(e);
            }
        }
    }

    /// <summary>
    /// Where a set of MEP elements is: each one's bounding box rounded to 1 mm, hashed (FNV-1a, 64 bit —
    /// string.GetHashCode is randomised per process on .NET 8). Moving, resizing or rerouting an element
    /// changes it; a deleted element counts as "missing".
    /// </summary>
    public static class MepFingerprint
    {
        public static string Of(Document doc, IEnumerable<long> meps)
        {
            StringBuilder sb = new StringBuilder();
            foreach (long id in meps.Distinct().OrderBy(i => i))
            {
                sb.Append(id).Append(':');
                BoundingBoxXYZ bb = doc.GetElement(ElementIdExtensions.FromValue(id))?.get_BoundingBox(null);
                if (bb == null) sb.Append("missing");
                else
                    foreach (double v in new[] { bb.Min.X, bb.Min.Y, bb.Min.Z, bb.Max.X, bb.Max.Y, bb.Max.Z })
                        sb.Append(Math.Round(v * 304.8).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(';');
            }

            ulong hash = 14695981039346656037UL;
            foreach (char c in sb.ToString())
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }
    }
}
