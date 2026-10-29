using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Openings
{
    /// <summary>
    /// One "Place openings" run: finds where ducts/pipes/... cross walls and floors of the chosen
    /// models and places, updates or deletes Generic Model DirectShape openings. Runs in an API
    /// context (the window's external event) and reports with a TaskDialog.
    /// </summary>
    public static class OpeningRun
    {
        /// <param name="sourceKeys">Ticked models: link instance ElementId values, 0 for the current model.</param>
        /// <returns>The openings created or updated.</returns>
        public static List<ElementId> Execute(UIDocument uidoc, OpeningSettings settings, ICollection<long> sourceKeys)
        {
            List<ElementId> none = new List<ElementId>();
            Document doc = uidoc.Document;

            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show("Openings", "Run this in a project, not in the family editor.");
                return none;
            }

            View view = doc.ActiveView;
            if (!(view is ViewPlan || view is ViewSection || view is View3D) || view.IsTemplate)
            {
                TaskDialog.Show("Openings", "Open a plan, section or 3D view: walls and floors are taken from what it shows.");
                return none;
            }

            List<HostSource> sources = HostSource.Collect(doc)
                .Where(s => sourceKeys.Contains(s.Instance?.Id.GetValue() ?? 0))
                .ToList();
            if (sources.Count == 0)
            {
                TaskDialog.Show("Openings", "Tick at least one model with walls / floors.");
                return none;
            }

            List<string> warnings = new List<string>();
            Stopwatch sw = Stopwatch.StartNew();

            // ── MEP elements: the selected ones, or everything visible in the view ─
            List<ExistingOpening> existing = ExistingOpening.Collect(doc);
            IList<Element> meps = CollectMep(doc, view, uidoc.Selection.GetElementIds(), settings, existing, out string scope);
            if (meps.Count == 0)
            {
                TaskDialog.Show("Openings", "No elements of the ticked MEP categories are visible in this view.");
                return none;
            }

            // ── Intersections → openings ──────────────────────────────────────
            List<BuiltInCategory> hostCats = ToCategories(settings.HostCategories);
            IntersectionFinder finder = new IntersectionFinder();
            List<Penetration> penetrations = finder.Find(meps, sources, hostCats, view);
            warnings.AddRange(finder.Warnings);

            OpeningPlanner planner = new OpeningPlanner(settings);
            List<OpeningPlan> plans = planner.Plan(penetrations);
            // A run over the whole view respects locks and the user's deletions; a run on selected
            // elements is the user asking for exactly those, so it updates locked openings and brings
            // deleted ones back.
            bool selectedRun = scope == "Selected";
            OpeningMatcher matcher = new OpeningMatcher(existing, plans,
                new HashSet<long>(meps.Select(m => m.Id.GetValue())), finder.CheckedHosts, !selectedRun, doc);

            List<DeletedOpening> deleted = DeletedOpenings.Read(doc);
            List<DeletedOpening> revived = new List<DeletedOpening>();
            int suppressed = 0;
            foreach (OpeningPlan plan in plans.ToList())
            {
                if (matcher.Reuse.ContainsKey(plan)) continue;
                List<DeletedOpening> hit = deleted.Where(d => d.Covers(plan)).ToList();
                if (hit.Count == 0) continue;
                // The user's deletion holds while its MEP elements stay where they were; once they move,
                // the opening follows them like any other (as a locked one does).
                if (selectedRun || hit.All(d => d.Geo != null && MepFingerprint.Of(doc, d.Meps) != d.Geo))
                    revived.AddRange(hit);
                else { plans.Remove(plan); suppressed++; }
            }

            if (plans.Count == 0 && matcher.Obsolete.Count == 0)
            {
                TaskDialog.Show("Openings",
                        $"{scope}: {meps.Count} MEP elements — " +
                    (suppressed > 0 ? $"{suppressed} openings were deleted by hand and stay deleted (unlock them in the list, or run on selected elements)."
                                    : "no intersections with the walls / floors visible in this view.") +
                    Tail(warnings));
                return none;
            }

            // Shared parameters (own transaction, like ShaftMaster).
            SharedParamHelper.EnsureParameters(doc);

            // ── Place ─────────────────────────────────────────────────────────
            OpeningPlacer placer = new OpeningPlacer(doc, existing, matcher, !selectedRun);
            int skipped = 0;
            using (new OwnChanges())
            using (Transaction t = new Transaction(doc, "Place openings"))
            {
                t.Start();
                FloorProbe probe = new FloorProbe(doc, settings.FloorProbeOffset, sources);

                foreach (OpeningPlan plan in plans)
                {
                    try
                    {
                        FloorHit floor = plan.Kind == HostKind.Wall ? probe.FloorUnder(plan) : null;
                        placer.Place(plan, floor);
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        warnings.Add($"Opening at MEP {plan.Members[0].Mep.Id.GetValue()}: {ex.Message}");
                    }
                }
                placer.DeleteObsolete();
                if (revived.Count > 0)
                    DeletedOpenings.Write(doc, deleted.Except(revived).ToList());
                t.Commit();
            }

            // ── Report ────────────────────────────────────────────────────────
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"{scope}: {meps.Count} MEP elements checked");
            sb.AppendLine($"Crossings found: {penetrations.Count}");
            sb.AppendLine($"Openings: {plans.Count} ({plans.Count(p => p.Round)} round, {planner.MergedGroups} merged)");
            sb.AppendLine($"Created: {placer.Created}, updated: {placer.Updated}, deleted: {placer.Deleted}, skipped: {skipped}");
            if (placer.LockedButMoved > 0)
                sb.AppendLine($"Locked, but their MEP elements moved — updated: {placer.LockedButMoved}");
            if (placer.KeptPinned > 0)
                sb.AppendLine($"Pinned, left as they are: {placer.KeptPinned}");
            if (placer.KeptLocked > 0)
                sb.AppendLine($"Locked, left as they are: {placer.KeptLocked} (run on selected elements, or unlock, to update)");
            if (suppressed > 0)
                sb.AppendLine($"Deleted by hand, not recreated: {suppressed}");
            if (revived.Count > 0)
                sb.AppendLine($"Recreated after a manual delete: {revived.Count}");
            if (placer.NoFloorFound > 0)
                sb.AppendLine($"Wall openings with no floor found below: {placer.NoFloorFound}");
            if (placer.MissingParams.Any())
                sb.AppendLine("Shared parameters not available: " + string.Join(", ", placer.MissingParams));
            sb.AppendLine($"Time: {sw.Elapsed.TotalSeconds:0.0} s");

            TaskDialog td = new TaskDialog("Openings")
            {
                MainInstruction = $"{placer.Created + placer.Updated} openings placed",
                MainContent = sb.ToString()
            };
            if (warnings.Count > 0)
                td.ExpandedContent = string.Join("\n", warnings.Take(50)) +
                                     (warnings.Count > 50 ? $"\n… and {warnings.Count - 50} more" : "");
            td.Show();

            return placer.Touched;
        }

        /// <summary>
        /// Selected elements of the ticked MEP categories; when none are selected (nothing, or only
        /// openings, walls, ...), everything of those categories visible in the view.
        /// </summary>
        private static IList<Element> CollectMep(Document doc, View view, ICollection<ElementId> selection,
                                                 OpeningSettings s, List<ExistingOpening> existing, out string scope)
        {
            scope = "";
            List<BuiltInCategory> cats = ToCategories(s.MepCategories);
            if (cats.Count == 0) return new List<Element>();
            ElementMulticategoryFilter filter = new ElementMulticategoryFilter(cats);

            List<Element> meps = selection.Count > 0
                ? new FilteredElementCollector(doc, selection).WherePasses(filter).WhereElementIsNotElementType().ToElements().ToList()
                : new List<Element>();
            meps.RemoveAll(IsOpening);   // selected openings do not count as a selection of MEP elements
            if (meps.Count > 0)
                scope = "Selected";
            else
            {
                scope = $"Visible in \"{view.Name}\"";
                meps = new FilteredElementCollector(doc, view.Id).WherePasses(filter).WhereElementIsNotElementType()
                    .ToElements().ToList();
            }

            // With Generic Models ticked, the openings themselves would cross every wall they are in.
            meps.RemoveAll(IsOpening);
            if (meps.Count == 0) return meps;

            // An existing opening shared by several elements is rebuilt from all of them: checking only
            // the selected duct would shrink a merged opening to that duct and drop the others' share.
            HashSet<long> ids = new HashSet<long>(meps.Select(m => m.Id.GetValue()));
            HashSet<long> catIds = new HashSet<long>(cats.Select(c => (long)c));
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (ExistingOpening e in existing)
                {
                    if (!e.Meps.Overlaps(ids)) continue;
                    foreach (long id in e.Meps)
                    {
                        if (ids.Contains(id)) continue;
                        Element el = doc.GetElement(ElementIdExtensions.FromValue(id));
                        if (el?.Category == null || !catIds.Contains(el.Category.Id.GetValue())) continue;
                        meps.Add(el);
                        ids.Add(id);
                        grew = true;
                    }
                }
            }
            return meps;
        }

        private static bool IsOpening(Element e) =>
            e is DirectShape ds && ds.ApplicationId == OpeningParams.AppId;

        private static List<BuiltInCategory> ToCategories(IEnumerable<string> names)
        {
            List<BuiltInCategory> list = new List<BuiltInCategory>();
            foreach (string n in names)
                if (Enum.TryParse(n, out BuiltInCategory bic)) list.Add(bic);
            return list;
        }

        private static string Tail(List<string> warnings) =>
            warnings.Count == 0 ? "" : "\n\n" + string.Join("\n", warnings.Take(20));
    }
}
