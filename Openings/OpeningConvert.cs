using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Openings
{
    /// <summary>
    /// The window's Convert button: turns openings into the representation chosen in the window
    /// (Generic Model DirectShape ↔ face-based family). The selected openings, or — with none
    /// selected — every opening visible in the active view. Only the representation changes: size,
    /// host, MEP elements, Opening ID, Lock and the user's Comments / Mark go over to the new element.
    /// Pinned openings are left alone; free shapes a family cannot take stay DirectShapes.
    /// </summary>
    public static class OpeningConvert
    {
        /// <summary>API context. Returns the converted openings.</summary>
        public static List<ElementId> Execute(UIDocument uidoc, OpeningSettings settings)
        {
            List<ElementId> touched = new List<ElementId>();
            Document doc = uidoc.Document;
            View view = doc.ActiveView;
            OpeningMode target = settings.Mode;
            string targetName = target == OpeningMode.Family ? "families" : "Generic Models (DirectShape)";

            OpeningWriter writer = new OpeningWriter(doc, settings);
            if (target == OpeningMode.Family && writer.FamilyProblem != null)
            {
                TaskDialog.Show("Openings", writer.FamilyProblem);
                return touched;
            }

            HashSet<long> ids = new HashSet<long>(uidoc.Selection.GetElementIds()
                .Where(id => doc.GetElement(id) is Element e && OpeningElements.IsOpening(e))
                .Select(id => id.GetValue()));
            string scope = "Selected";
            if (ids.Count == 0)
            {
                ids = new HashSet<long>(OpeningElements.Collect(doc, view.Id).Select(e => e.Id.GetValue()));
                scope = $"Visible in \"{view.Name}\"";
            }
            List<ExistingOpening> openings = ExistingOpening.Collect(doc).Where(e => ids.Contains(e.Element.Id.GetValue())).ToList();
            if (openings.Count == 0)
            {
                TaskDialog.Show("Openings", "No openings selected, and none visible in the active view.");
                return touched;
            }

            SharedParamHelper.EnsureParameters(doc);

            int already = 0, pinned = 0, converted = 0, failed = 0;
            List<string> notes = new List<string>();
            HashSet<string> missing = new HashSet<string>();
            using (new OwnChanges())
            using (Transaction t = new Transaction(doc, "Convert openings"))
            {
                t.Start();
                foreach (ExistingOpening e in openings)
                {
                    string name = OpeningEditor.IdOf(e.Element) is int oid ? "#" + oid : e.Element.Id.GetValue().ToString();
                    if (e.Mode == target) { already++; continue; }
                    if (e.Pinned) { pinned++; continue; }

                    OpeningPlan plan = WallOpening.From(e)?.ToPlan() ?? FloorOpening.From(e)?.ToPlan();
                    if (plan == null) { failed++; notes.Add($"Opening {name}: its shape or size cannot be read."); continue; }
                    if (target == OpeningMode.Family && e.HostKey == null)
                    {
                        failed++;
                        notes.Add($"Opening {name}: its host is not recorded (an older opening) — run Place Openings on it first.");
                        continue;
                    }

                    try
                    {
                        Element el = writer.Write(plan, e.Element, target, out OpeningPlan used, out string note);
                        if (OpeningElements.ModeOf(el) != target)
                        {
                            failed++;
                            notes.Add($"Opening {name}: stays a DirectShape — {note}.");
                            continue;
                        }
                        OpeningPlacer.WriteSize(el, used, missing);
                        OpeningElements.SetKey(el, ExistingOpening.KeyOf(e.HostKey ?? "", e.Meps, used.Frame.X, e.Geo));
                        touched.Add(el.Id);
                        converted++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        notes.Add($"Opening {name}: {ex.Message}");
                    }
                }
                t.Commit();
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"{scope}: {openings.Count} openings");
            sb.AppendLine($"Converted to {targetName}: {converted}");
            if (already > 0) sb.AppendLine($"Already {targetName}: {already}");
            if (pinned > 0) sb.AppendLine($"Pinned, left as they are: {pinned}");
            if (failed > 0) sb.AppendLine($"Not converted: {failed} (see details)");
            if (writer.MissingFamilyParams.Any())
                sb.AppendLine("Family parameters not found (check the mapping in Options): " + string.Join(", ", writer.MissingFamilyParams));
            if (missing.Any())
                sb.AppendLine("Shared parameters not available: " + string.Join(", ", missing));

            TaskDialog td = new TaskDialog("Openings")
            {
                MainInstruction = $"{converted} openings converted",
                MainContent = sb.ToString()
            };
            if (notes.Count > 0)
                td.ExpandedContent = string.Join("\n", notes.Take(50)) + (notes.Count > 50 ? $"\n… and {notes.Count - 50} more" : "");
            td.Show();
            return touched;
        }
    }
}
