using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Openings
{
    /// <summary>An opening the user deleted: remembered so a run over the whole view does not bring it back.</summary>
    public class DeletedOpening
    {
        public string HostKey;
        public HashSet<long> Meps = new HashSet<long>();
        public int? OpeningId;
        public string HostType, Shape, Size, Level, Host, Mep;
        public string Geo;   // MepFingerprint at deletion; null if unknown

        /// <summary>The row id the palette uses for it: negative, so it never collides with an ElementId.</summary>
        public long RowId => -1 - (OpeningId ?? (Math.Abs((long)(HostKey + "|" + string.Join(",", Meps)).GetHashCode()) % 1000000000));

        public bool Covers(OpeningPlan plan) =>
            HostKey == plan.HostKey && plan.MepIds.Any(Meps.Contains);

        private const char Sep = '\u001F';

        public string Serialize() => string.Join(Sep.ToString(),
            HostKey ?? "", string.Join(",", Meps), OpeningId?.ToString() ?? "",
            HostType ?? "", Shape ?? "", Size ?? "", Level ?? "", Host ?? "", Mep ?? "", Geo ?? "");

        public static DeletedOpening Parse(string s)
        {
            string[] f = s.Split(Sep);
            if (f.Length < 9) return null;
            DeletedOpening d = new DeletedOpening
            {
                HostKey = f[0], HostType = f[3], Shape = f[4], Size = f[5], Level = f[6], Host = f[7], Mep = f[8]
            };
            foreach (string id in f[1].Split(','))
                if (long.TryParse(id, out long v)) d.Meps.Add(v);
            if (int.TryParse(f[2], out int oid)) d.OpeningId = oid;
            if (f.Length > 9 && f[9].Length > 0) d.Geo = f[9];
            return d.HostKey.Length > 0 && d.Meps.Count > 0 ? d : null;
        }
    }

    /// <summary>The list of user-deleted openings, kept in the project on a DataStorage element.</summary>
    public static class DeletedOpenings
    {
        private static readonly Guid SchemaGuid = new Guid("20E3D659-26F7-4B3A-BB7F-731507CF68A0");
        private const string Field = "Deleted";

        private static Schema GetSchema()
        {
            Schema s = Schema.Lookup(SchemaGuid);
            if (s != null) return s;
            SchemaBuilder b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName("VixeldorfOpeningsDeleted");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddArrayField(Field, typeof(string));
            return b.Finish();
        }

        private static DataStorage Find(Document doc, Schema schema) =>
            new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .FirstOrDefault(d => d.GetEntity(schema).IsValid());

        public static List<DeletedOpening> Read(Document doc)
        {
            List<DeletedOpening> list = new List<DeletedOpening>();
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return list;
                DataStorage ds = Find(doc, schema);
                if (ds == null) return list;
                foreach (string s in ds.GetEntity(schema).Get<IList<string>>(Field))
                {
                    DeletedOpening d = DeletedOpening.Parse(s);
                    if (d != null) list.Add(d);
                }
            }
            catch { /* unreadable storage — behave as if nothing was deleted */ }
            return list;
        }

        /// <summary>Inside a transaction (or an updater).</summary>
        public static void Write(Document doc, List<DeletedOpening> list)
        {
            Schema schema = GetSchema();
            DataStorage ds = Find(doc, schema);
            if (ds == null)
            {
                if (list.Count == 0) return;
                ds = DataStorage.Create(doc);
            }
            Entity entity = new Entity(schema);
            entity.Set<IList<string>>(Field, list.Select(d => d.Serialize()).ToList());
            ds.SetEntity(entity);
        }
    }

    /// <summary>
    /// Notices what the user does to openings outside this add-in: an opening changed by hand
    /// (moved, parameters edited, …) gets Opening Lock; a deleted one is remembered in
    /// <see cref="DeletedOpenings"/>. The add-in's own transactions run with <see cref="Suppress"/> set.
    /// </summary>
    public class OpeningUpdater : IUpdater
    {
        private static UpdaterId _id;

        /// <summary>True while this add-in itself changes openings.</summary>
        public static bool Suppress { get; set; }

        // Per document (by title): what each opening was, so a deletion can still be described.
        private static readonly Dictionary<string, Dictionary<long, DeletedOpening>> _known =
            new Dictionary<string, Dictionary<long, DeletedOpening>>();

        /// <summary>From an API context with an add-in id (the command). Registers once per session.</summary>
        public static void Register(AddInId addInId)
        {
            if (_id != null) return;
            OpeningUpdater u = new OpeningUpdater(addInId);
            _id = u.GetUpdaterId();
            if (UpdaterRegistry.IsUpdaterRegistered(_id)) return;

            // Optional: a project opened on a machine without the add-in raises no warning.
            UpdaterRegistry.RegisterUpdater(u, true);
            // DirectShape openings and family ones; Execute picks out this add-in's.
            ElementFilter filter = new LogicalOrFilter(new ElementClassFilter(typeof(DirectShape)),
                                                       new ElementClassFilter(typeof(FamilyInstance)));
            UpdaterRegistry.AddTrigger(_id, filter, Element.GetChangeTypeAny());
            UpdaterRegistry.AddTrigger(_id, filter, Element.GetChangeTypeElementDeletion());
            // MEP elements: a deleted one takes its openings along.
            UpdaterRegistry.AddTrigger(_id, new ElementMulticategoryFilter(MepCategories), Element.GetChangeTypeElementDeletion());
        }

        // Every category an opening can be placed for (the Options list, flex ducts / pipes as well).
        private static readonly List<BuiltInCategory> MepCategories = new List<BuiltInCategory>
        {
            BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory,
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting,
            BuiltInCategory.OST_GenericModel,
        };

        /// <summary>Remembers the openings as they are now (refreshed with the palette list).</summary>
        public static void Remember(Document doc, Dictionary<long, DeletedOpening> openings) =>
            _known[doc.Title] = openings;

        private readonly UpdaterId _myId;

        private OpeningUpdater(AddInId addInId)
        {
            _myId = new UpdaterId(addInId, new Guid("83CDC006-4C2D-48C7-B6EA-9D3EBEC4DD9A"));
        }

        public void Execute(UpdaterData data)
        {
            if (Suppress) return;
            Document doc = data.GetDocument();

            OpeningSync sync = null;
            foreach (ElementId id in data.GetModifiedElementIds())
            {
                Element el = doc.GetElement(id);
                if (el == null || !OpeningElements.IsOpening(el)) continue;
                Parameter p = el.get_Parameter(OpeningParams.Locked.Guid);
                if (p != null && !p.IsReadOnly && p.AsInteger() != 1) p.Set(1);
                // Resized with its grips, moved, rotated: the Opening … parameters follow at once.
                if (sync == null) sync = new OpeningSync(doc, OpeningsSession.Settings ?? OpeningSettings.Load());
                sync.Refresh(el);
            }

            ICollection<ElementId> deleted = data.GetDeletedElementIds();
            if (deleted.Count == 0) return;
            _known.TryGetValue(doc.Title, out var known);
            DeleteOrphans(doc, deleted, known);
            if (known == null) return;
            List<DeletedOpening> gone = deleted.Select(id => known.TryGetValue(id.GetValue(), out var d) ? d : null)
                .Where(d => d != null).ToList();
            if (gone.Count == 0) return;

            List<DeletedOpening> list = DeletedOpenings.Read(doc);
            list.AddRange(gone);
            DeletedOpenings.Write(doc, list);
        }

        /// <summary>
        /// Openings whose MEP elements are now all gone go with the last of them — locked ones too;
        /// pinned ones stay. An opening that keeps some of its MEP elements is reshaped by the next run.
        /// </summary>
        private static void DeleteOrphans(Document doc, ICollection<ElementId> deleted, Dictionary<long, DeletedOpening> known)
        {
            HashSet<long> gone = new HashSet<long>(deleted.Select(id => id.GetValue()));
            List<ElementId> orphans = OpeningElements.Collect(doc)
                .Where(el => !el.Pinned && !gone.Contains(el.Id.GetValue()) && Editable(doc, el.Id))
                .Where(el =>
                {
                    HashSet<long> meps = OpeningSync.MepsOf(el);
                    return meps.Overlaps(gone) && meps.All(m => gone.Contains(m) || doc.GetElement(ElementIdExtensions.FromValue(m)) == null);
                })
                .Select(el => el.Id).ToList();
            if (orphans.Count == 0) return;
            // Not the user deleting an opening: no tombstone, a new duct there gets a new opening.
            foreach (ElementId id in orphans) known?.Remove(id.GetValue());
            doc.Delete(orphans);
        }

        /// <summary>Not borrowed by someone else (workshared).</summary>
        internal static bool Editable(Document doc, ElementId id) =>
            !doc.IsWorkshared || WorksharingUtils.GetCheckoutStatus(doc, id) != CheckoutStatus.OwnedByOtherUser;

        public UpdaterId GetUpdaterId() => _myId;
        public ChangePriority GetChangePriority() => ChangePriority.Annotations;
        public string GetUpdaterName() => "Vixeldorf Openings tracker";
        public string GetAdditionalInformation() =>
            "Locks openings changed by hand and updates their data, remembers deleted ones, deletes the openings of deleted MEP elements.";
    }

    /// <summary>
    /// Brings an opening's data in line with the element as it stands: a family resized with its grips
    /// (the mapped family parameters → Opening Width / Height / Length / Diameter / Depth), any opening
    /// moved or rotated by hand (elevations, level, the X in its key). Inside a transaction or an updater.
    /// </summary>
    public class OpeningSync
    {
        private readonly Document _doc;
        private readonly OpeningSettings _s;
        private readonly List<Level> _levels;
        private readonly double _pbpZ, _spZ, _spSharedZ;

        public OpeningSync(Document doc, OpeningSettings settings)
        {
            _doc = doc;
            _s = settings;
            _levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation).ToList();
            BasePoint sp = BasePoint.GetSurveyPoint(doc);
            _pbpZ = BasePoint.GetProjectBasePoint(doc).Position.Z;
            _spZ = sp.Position.Z;
            _spSharedZ = sp.SharedPosition.Z;
        }

        /// <summary>
        /// Openings changed while the add-in was not watching (before the window was opened in this session,
        /// on a machine without the add-in) are brought up to date: one undo step, and only if something changed.
        /// </summary>
        public static int RefreshAll(Document doc, OpeningSettings settings)
        {
            if (doc == null || doc.IsFamilyDocument || doc.IsReadOnly || doc.IsModifiable) return 0;
            List<Element> all = OpeningElements.Collect(doc).Where(e => OpeningUpdater.Editable(doc, e.Id)).ToList();
            if (all.Count == 0) return 0;
            using (new OwnChanges())   // not a change by hand: no Opening Lock
            using (Transaction t = new Transaction(doc, "Update opening data"))
            {
                t.Start();
                OpeningSync sync = new OpeningSync(doc, settings);
                int n = all.Count(sync.Refresh);
                if (n > 0) t.Commit(); else t.RollBack();
                return n;
            }
        }

        /// <returns>True if anything was written.</returns>
        public bool Refresh(Element el)
        {
            bool changed = false;
            string shape = OpeningEditor.Text(el, OpeningParams.Shape);
            bool floor = OpeningEditor.Text(el, OpeningParams.HostType) == "Floor";

            if (el is FamilyInstance fi && (shape == "Rectangular" || shape == "Round"))
            {
                if (shape == "Round")
                {
                    changed |= FromFamily(fi, _s.MapDiameter, OpeningParams.Diameter);
                    changed |= FromFamily(fi, _s.MapRoundDepth, OpeningParams.Depth);
                }
                else
                {
                    changed |= FromFamily(fi, _s.MapWidth, OpeningParams.Width);
                    changed |= FromFamily(fi, _s.MapHeight, floor ? OpeningParams.Length : OpeningParams.Height);
                    changed |= FromFamily(fi, _s.MapDepth, OpeningParams.Depth);
                }
                changed |= KeepX(fi);
            }

            XYZ c = OpeningElements.CenterOf(el);
            if (c == null) return changed;
            double half = floor ? OpeningEditor.Number(el, OpeningParams.Depth) / 2
                        : shape == "Round" ? OpeningEditor.Number(el, OpeningParams.Diameter) / 2
                        : OpeningEditor.Number(el, OpeningParams.Height) / 2;
            double bottom = c.Z - half;

            // The floor below a wall opening stays where it was found: its level, from the values written then.
            Parameter oldCenter = el.get_Parameter(OpeningParams.CenterElevPbp.Guid);
            Parameter oldFromFloor = el.get_Parameter(OpeningParams.CenterFromFloor.Guid);
            if (oldCenter != null && oldCenter.HasValue && oldFromFloor != null && oldFromFloor.HasValue)
            {
                double floorZ = oldCenter.AsDouble() + _pbpZ - oldFromFloor.AsDouble();
                changed |= Put(el, OpeningParams.BottomFromFloor, bottom - floorZ);
                changed |= Put(el, OpeningParams.CenterFromFloor, c.Z - floorZ);
            }
            changed |= Put(el, OpeningParams.BottomElevPbp, bottom - _pbpZ);
            changed |= Put(el, OpeningParams.CenterElevPbp, c.Z - _pbpZ);
            changed |= Put(el, OpeningParams.BottomElevSp, bottom - _spZ + _spSharedZ);
            changed |= Put(el, OpeningParams.CenterElevSp, c.Z - _spZ + _spSharedZ);

            Level level = _levels.LastOrDefault(l => l.ProjectElevation <= c.Z + 1e-6) ?? _levels.FirstOrDefault();
            if (level != null && OpeningEditor.Text(el, OpeningParams.LevelName) != level.Name)
                changed |= Put(el, OpeningParams.LevelName, level.Name);
            return changed;
        }

        /// <summary>The MEP element ids in the opening's key.</summary>
        public static HashSet<long> MepsOf(Element el)
        {
            HashSet<long> meps = new HashSet<long>();
            string part = (OpeningElements.GetKey(el) ?? "").Split(';').FirstOrDefault(p => p.StartsWith("mep="));
            if (part != null)
                foreach (string id in part.Substring(4).Split(','))
                    if (long.TryParse(id, out long v)) meps.Add(v);
            return meps;
        }

        private static bool FromFamily(FamilyInstance fi, string name, OpeningParam op)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            Parameter p = fi.LookupParameter(name);
            return p != null && p.HasValue && p.StorageType == StorageType.Double && Put(fi, op, p.AsDouble());
        }

        /// <summary>A family rotated by hand: the X stored in the key (used by Edit and Section) follows it.</summary>
        private static bool KeepX(FamilyInstance fi)
        {
            XYZ h = new XYZ(fi.HandOrientation.X, fi.HandOrientation.Y, 0);
            if (h.GetLength() < 1e-9) return false;
            h = h.Normalize();
            string key = OpeningElements.GetKey(fi) ?? "";
            List<string> parts = key.Split(';').Where(p => p.Length > 0).ToList();
            int i = parts.FindIndex(p => p.StartsWith("x="));
            if (i >= 0)
            {
                string[] xy = parts[i].Substring(2).Split(',');
                if (xy.Length == 2 &&
                    double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double dx) &&
                    double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double dy) &&
                    new XYZ(dx, dy, 0).DotProduct(h) > 1 - 1e-9)
                    return false;
            }
            else if (!key.Contains("host=")) return false;   // no key to keep it in

            string x = "x=" + h.X.ToString("R", CultureInfo.InvariantCulture) + "," + h.Y.ToString("R", CultureInfo.InvariantCulture);
            if (i >= 0) parts[i] = x;
            else parts.Insert(Math.Min(2, parts.Count), x);
            OpeningElements.SetKey(fi, string.Join(";", parts));
            return true;
        }

        private static bool Put(Element e, OpeningParam op, double value)
        {
            Parameter p = e.get_Parameter(op.Guid);
            if (p == null || p.IsReadOnly || (p.HasValue && Math.Abs(p.AsDouble() - value) < 1e-6)) return false;
            p.Set(value);
            return true;
        }

        private static bool Put(Element e, OpeningParam op, string value)
        {
            Parameter p = e.get_Parameter(op.Guid);
            if (p == null || p.IsReadOnly) return false;
            p.Set(value);
            return true;
        }
    }

    /// <summary>Sets <see cref="OpeningUpdater.Suppress"/> for the add-in's own changes.</summary>
    public sealed class OwnChanges : IDisposable
    {
        private readonly bool _was;
        public OwnChanges() { _was = OpeningUpdater.Suppress; OpeningUpdater.Suppress = true; }
        public void Dispose() => OpeningUpdater.Suppress = _was;
    }
}
