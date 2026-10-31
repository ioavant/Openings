using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>One line of the main window's list: an opening's parameters, already formatted.</summary>
    public class OpeningRow
    {
        /// <summary>The opening's ElementId value; negative for an opening the user deleted (DeletedOpening.RowId).</summary>
        public long ElementId { get; set; }
        public bool IsLocked { get; set; }
        public bool IsDeleted => ElementId < 0;
        public string Status { get; set; }
        public string Kind { get; set; }     // DirectShape / Family
        public int? OpeningId { get; set; }
        public string HostType { get; set; }
        public string Shape { get; set; }
        public string Size { get; set; }
        public string Level { get; set; }
        public string BottomFromFloor { get; set; }
        public string Host { get; set; }
        public string Floor { get; set; }
        public string Mep { get; set; }

        /// <summary>Every opening in the project, by Opening ID. Needs an API context (reads the model).</summary>
        public static List<OpeningRow> Build(Document doc)
        {
            Units units = doc.GetUnits();
            string Len(double? v) => v.HasValue ? UnitFormatUtils.Format(units, SpecTypeId.Length, v.Value, false) : "";

            List<OpeningRow> rows = new List<OpeningRow>();
            Dictionary<long, DeletedOpening> known = new Dictionary<long, DeletedOpening>();
            foreach (ExistingOpening e in ExistingOpening.Collect(doc))
            {
                Element ds = e.Element;
                string shape = Text(ds, OpeningParams.Shape);
                string hostType = Text(ds, OpeningParams.HostType);
                string size = shape == "Round"
                    ? "Ø " + Len(Number(ds, OpeningParams.Diameter))
                    : Len(Number(ds, OpeningParams.Width)) + " × " +
                      Len(Number(ds, hostType == "Floor" ? OpeningParams.Length : OpeningParams.Height));

                OpeningRow row = new OpeningRow
                {
                    ElementId = ds.Id.GetValue(),
                    IsLocked = e.Locked,
                    Status = e.Pinned ? "Pinned" : null,
                    Kind = e.Mode == OpeningMode.Family ? "Family" : "DirectShape",
                    OpeningId = Integer(ds, OpeningParams.OpeningId),
                    HostType = hostType,
                    Shape = shape,
                    Size = size,
                    Level = Text(ds, OpeningParams.LevelName),
                    BottomFromFloor = Len(Number(ds, OpeningParams.BottomFromFloor)) + (e.FloorByHand ? " *" : ""),
                    Host = Text(ds, OpeningParams.HostInfo),
                    Floor = Text(ds, OpeningParams.FloorInfo),
                    Mep = Text(ds, OpeningParams.MepInfo)
                };
                rows.Add(row);

                // What the updater needs to describe this opening if the user deletes it.
                if (e.HostKey != null)
                    known[row.ElementId] = new DeletedOpening
                    {
                        HostKey = e.HostKey, Meps = new HashSet<long>(e.Meps), OpeningId = row.OpeningId,
                        HostType = row.HostType, Shape = row.Shape, Size = row.Size, Level = row.Level,
                        Host = row.Host, Mep = row.Mep, Geo = e.Geo
                    };
            }
            OpeningUpdater.Remember(doc, known);

            // Openings the user deleted: listed (locked) so they can be unlocked and come back.
            foreach (DeletedOpening d in DeletedOpenings.Read(doc))
                rows.Add(new OpeningRow
                {
                    ElementId = d.RowId,
                    IsLocked = true,
                    Status = "Deleted",
                    OpeningId = d.OpeningId,
                    HostType = d.HostType,
                    Shape = d.Shape,
                    Size = d.Size,
                    Level = d.Level,
                    Host = d.Host,
                    Mep = d.Mep
                });
            return rows.OrderBy(r => r.OpeningId ?? int.MaxValue).ThenBy(r => r.ElementId).ToList();
        }

        private static Parameter Get(Element e, OpeningParam op)
        {
            Parameter p = e.get_Parameter(op.Guid);
            return p != null && p.HasValue ? p : null;
        }

        private static string Text(Element e, OpeningParam op) => Get(e, op)?.AsString() ?? "";
        private static double? Number(Element e, OpeningParam op) => Get(e, op)?.AsDouble();
        private static int? Integer(Element e, OpeningParam op) => Get(e, op)?.AsInteger();
    }
}
