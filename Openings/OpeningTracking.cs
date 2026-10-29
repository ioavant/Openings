using System;
using System.Collections.Generic;
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
        }

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

            foreach (ElementId id in data.GetModifiedElementIds())
            {
                Element el = doc.GetElement(id);
                if (el == null || !OpeningElements.IsOpening(el)) continue;
                Parameter p = el.get_Parameter(OpeningParams.Locked.Guid);
                if (p != null && !p.IsReadOnly && p.AsInteger() != 1) p.Set(1);
            }

            ICollection<ElementId> deleted = data.GetDeletedElementIds();
            if (deleted.Count == 0 || !_known.TryGetValue(doc.Title, out var known)) return;
            List<DeletedOpening> gone = deleted.Select(id => known.TryGetValue(id.GetValue(), out var d) ? d : null)
                .Where(d => d != null).ToList();
            if (gone.Count == 0) return;

            List<DeletedOpening> list = DeletedOpenings.Read(doc);
            list.AddRange(gone);
            DeletedOpenings.Write(doc, list);
        }

        public UpdaterId GetUpdaterId() => _myId;
        public ChangePriority GetChangePriority() => ChangePriority.Annotations;
        public string GetUpdaterName() => "Vixeldorf Openings tracker";
        public string GetAdditionalInformation() => "Locks openings changed by hand and remembers deleted ones.";
    }

    /// <summary>Sets <see cref="OpeningUpdater.Suppress"/> for the add-in's own changes.</summary>
    public sealed class OwnChanges : IDisposable
    {
        private readonly bool _was;
        public OwnChanges() { _was = OpeningUpdater.Suppress; OpeningUpdater.Suppress = true; }
        public void Dispose() => OpeningUpdater.Suppress = _was;
    }
}
