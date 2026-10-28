using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>One line of the main window's list: an opening's parameters, already formatted.</summary>
    public class OpeningRow
    {
        public long ElementId { get; set; }
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
            foreach (ExistingOpening e in ExistingOpening.Collect(doc))
            {
                DirectShape ds = e.Shape;
                string shape = Text(ds, OpeningParams.Shape);
                string hostType = Text(ds, OpeningParams.HostType);
                string size = shape == "Round"
                    ? "Ø " + Len(Number(ds, OpeningParams.Diameter))
                    : Len(Number(ds, OpeningParams.Width)) + " × " +
                      Len(Number(ds, hostType == "Floor" ? OpeningParams.Length : OpeningParams.Height));

                rows.Add(new OpeningRow
                {
                    ElementId = ds.Id.GetValue(),
                    OpeningId = Integer(ds, OpeningParams.OpeningId),
                    HostType = hostType,
                    Shape = shape,
                    Size = size,
                    Level = Text(ds, OpeningParams.LevelName),
                    BottomFromFloor = Len(Number(ds, OpeningParams.BottomFromFloor)),
                    Host = Text(ds, OpeningParams.HostInfo),
                    Floor = Text(ds, OpeningParams.FloorInfo),
                    Mep = Text(ds, OpeningParams.MepInfo)
                });
            }
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
