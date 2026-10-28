using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace Openings
{
    public enum HostKind
    {
        /// <summary>Walls and beams: the MEP element passes through horizontally.</summary>
        Wall,
        /// <summary>Floors, roofs, ceilings: the MEP element passes through vertically.</summary>
        Floor
    }

    /// <summary>One shared parameter written on every opening.</summary>
    public class OpeningParam
    {
        public string Name { get; }
        public Guid Guid { get; }
        public ForgeTypeId Spec { get; }
        public ForgeTypeId Group { get; }

        public OpeningParam(string name, string guid, ForgeTypeId spec, ForgeTypeId group)
        {
            Name = name; Guid = new Guid(guid); Spec = spec; Group = group;
        }
    }

    /// <summary>
    /// Openings are DirectShapes of category Generic Models (same approach as ShaftMaster),
    /// stamped with <see cref="AppId"/> so a re-run finds and updates them. Data goes into
    /// shared parameters bound to Generic Models. The GUIDs must never change — schedules
    /// and tags in existing projects are tied to them.
    /// </summary>
    public static class OpeningParams
    {
        public const string AppId = "Vixeldorf.Openings";

        /// <summary>Unique per project: kept while the opening is updated, never handed to two openings.</summary>
        public static readonly OpeningParam OpeningId = new OpeningParam("Opening ID", "6E445977-CB82-4BBA-A2E2-BDF3735105CA", SpecTypeId.Int.Integer, GroupTypeId.IdentityData);

        /// <summary>Locked: a run over the whole view leaves the opening alone; a run on selected elements still updates it.</summary>
        public static readonly OpeningParam Locked = new OpeningParam("Opening Lock", "C1D39D34-BDB6-4AFE-98A4-CCD9DAC43764", SpecTypeId.Boolean.YesNo, GroupTypeId.IdentityData);

        public static readonly OpeningParam Width    = new OpeningParam("Opening Width",    "017C58C4-62F1-413D-81E1-89A29C037119", SpecTypeId.Length, GroupTypeId.Geometry);
        public static readonly OpeningParam Height   = new OpeningParam("Opening Height",   "64DB7E30-E019-4B3A-9358-4CB1D5AA2072", SpecTypeId.Length, GroupTypeId.Geometry);
        public static readonly OpeningParam Length   = new OpeningParam("Opening Length",   "5314D699-017E-466D-B063-C6118A0DE9FF", SpecTypeId.Length, GroupTypeId.Geometry);
        public static readonly OpeningParam Depth    = new OpeningParam("Opening Depth",    "3BCC3DB5-6726-4FFA-B40D-E6395664B25C", SpecTypeId.Length, GroupTypeId.Geometry);
        public static readonly OpeningParam Diameter = new OpeningParam("Opening Diameter", "D6842DDF-15CE-4F66-A6B9-763AC70C011F", SpecTypeId.Length, GroupTypeId.Geometry);

        public static readonly OpeningParam BottomFromFloor = new OpeningParam("Opening Bottom From Floor",   "1B264F17-563B-4A40-8F1C-7B3458A69B60", SpecTypeId.Length, GroupTypeId.Constraints);
        public static readonly OpeningParam CenterFromFloor = new OpeningParam("Opening Center From Floor",   "95907DBF-B898-4285-A31C-E2ACB6FD20DA", SpecTypeId.Length, GroupTypeId.Constraints);
        public static readonly OpeningParam BottomElevPbp   = new OpeningParam("Opening Bottom Elevation PBP", "6F5550E1-A93E-4E10-9F45-4B44D9E1D182", SpecTypeId.Length, GroupTypeId.Constraints);
        public static readonly OpeningParam CenterElevPbp   = new OpeningParam("Opening Center Elevation PBP", "EB5944A7-D812-4799-BCDE-5311AC0DCC2F", SpecTypeId.Length, GroupTypeId.Constraints);
        public static readonly OpeningParam BottomElevSp    = new OpeningParam("Opening Bottom Elevation SP",  "0CEE280C-8835-4F4C-8BA7-E6C00BF6A5C9", SpecTypeId.Length, GroupTypeId.Constraints);
        public static readonly OpeningParam CenterElevSp    = new OpeningParam("Opening Center Elevation SP",  "C25CB872-A03D-4C35-B7DD-F1E8AD696CEA", SpecTypeId.Length, GroupTypeId.Constraints);

        public static readonly OpeningParam HostInfo  = new OpeningParam("Opening Host",         "A361974F-F16B-4F3F-AF4A-E30C2F8ED5AA", SpecTypeId.String.Text, GroupTypeId.IdentityData);
        public static readonly OpeningParam MepInfo   = new OpeningParam("Opening MEP Elements", "196CE352-DD71-440E-8623-55FD2FE2A95C", SpecTypeId.String.Text, GroupTypeId.IdentityData);
        public static readonly OpeningParam Shape     = new OpeningParam("Opening Shape",        "172D20DA-C59B-4BC7-A0B4-5BFBC37D8660", SpecTypeId.String.Text, GroupTypeId.IdentityData);
        public static readonly OpeningParam HostType  = new OpeningParam("Opening Host Type",    "4794D1F6-8EA9-498B-837C-4C3A45DB8140", SpecTypeId.String.Text, GroupTypeId.IdentityData);
        public static readonly OpeningParam FloorInfo = new OpeningParam("Opening Floor",        "311349E8-B498-4EC0-BC61-00F85C7377A6", SpecTypeId.String.Text, GroupTypeId.IdentityData);
        public static readonly OpeningParam LevelName = new OpeningParam("Opening Level",        "5BFBDE60-0A35-4122-B259-43538DD636E1", SpecTypeId.String.Text, GroupTypeId.IdentityData);

        public static IEnumerable<OpeningParam> All
        {
            get
            {
                yield return OpeningId; yield return Locked;
                yield return Width; yield return Height; yield return Length; yield return Depth; yield return Diameter;
                yield return BottomFromFloor; yield return CenterFromFloor;
                yield return BottomElevPbp; yield return CenterElevPbp; yield return BottomElevSp; yield return CenterElevSp;
                yield return HostInfo; yield return MepInfo; yield return Shape; yield return HostType; yield return FloorInfo; yield return LevelName;
            }
        }
    }
}
