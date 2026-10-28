using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Openings
{
    public enum OpeningsAction
    {
        Refresh,   // re-read the openings and links of the active document
        Place,     // run the placement with the ticked models
        Select,    // select (and show) the given openings in Revit
        Delete,    // delete the given openings
        Edit,      // start the sketch for an opening (the given row, or pick in the model)
        EditApply, // read the sketch back into the opening
        EditCancel,// drop the sketch
        Section,   // temporary section through a wall opening (the given row, or pick in the model)
        Cleanup,   // palette closed: drop a running sketch and the temporary sections
        Lock,      // set Opening Lock on the given openings
        Unlock     // clear it; for a deleted opening (negative id) forget the deletion, so a run recreates it
    }

    /// <summary>
    /// The modeless main window and the external event that does its Revit work, as in ShaftMaster:
    /// the window never touches the model itself, it queues an action and raises the event.
    /// </summary>
    public static class OpeningsSession
    {
        private static OpeningsHandler _handler;
        private static ExternalEvent _event;

        internal static OpeningsWindow Window { get; private set; }
        internal static OpeningSettings Settings { get; private set; }

        /// <summary>From the command (API context): opens the window, or brings it back and refreshes it.</summary>
        public static void Show(UIApplication uiapp)
        {
            // ExternalEvent.Create needs an API context, which IExternalCommand.Execute is.
            if (_handler == null) _handler = new OpeningsHandler();
            if (_event == null) _event = ExternalEvent.Create(_handler);
            OpeningUpdater.Register(uiapp.ActiveAddInId);

            if (Window == null || !Window.IsLoaded)
            {
                Settings = OpeningSettings.Load();
                Window = new OpeningsWindow(Settings);
                new WindowInteropHelper(Window).Owner = uiapp.MainWindowHandle;
                Window.Closed += (s, e) => { Window = null; Raise(OpeningsAction.Cleanup); };
                Window.Show();
            }
            else
                Window.Activate();

            RefreshWindow(uiapp, null);
        }

        /// <summary>From the window: queue an action for the next API context.</summary>
        internal static void Raise(OpeningsAction action, IEnumerable<long> ids = null)
        {
            if (_event == null) return;
            _handler.Enqueue(action, ids);
            _event.Raise();
        }

        /// <summary>API context: fill the window from the active document.</summary>
        internal static void RefreshWindow(UIApplication uiapp, ICollection<ElementId> highlight)
        {
            if (Window == null) return;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                Window.ShowNoProject();
                return;
            }
            Window.Fill(doc.Title,
                HostSource.Collect(doc),
                OpeningRow.Build(doc),
                doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId(),
                highlight?.Select(id => id.GetValue()));
        }
    }

    public class OpeningsHandler : IExternalEventHandler
    {
        // The width sketch in progress, if any (one at a time).
        private static OpeningEditor _editor;

        private readonly object _lock = new object();
        private readonly List<(OpeningsAction action, List<long> ids)> _queue = new List<(OpeningsAction, List<long>)>();

        // Raise() while the event is still pending runs it once: actions queue up instead of
        // overwriting each other (a refresh on window activation must not swallow a Place click).
        public void Enqueue(OpeningsAction action, IEnumerable<long> ids)
        {
            lock (_lock)
            {
                if (action == OpeningsAction.Refresh && _queue.Any(q => q.action == OpeningsAction.Refresh)) return;
                _queue.Add((action, ids?.ToList() ?? new List<long>()));
            }
        }

        public void Execute(UIApplication app)
        {
            List<(OpeningsAction action, List<long> ids)> batch;
            lock (_lock)
            {
                batch = _queue.ToList();
                _queue.Clear();
            }

            foreach (var item in batch)
            {
                // Everything here is the add-in's own doing, except Delete: an opening the user deletes
                // from the palette is remembered like one deleted in Revit, so a run does not recreate it.
                try
                {
                    using (item.action == OpeningsAction.Delete ? null : new OwnChanges())
                        Run(app, item.action, item.ids);
                }
                catch (Exception ex) { TaskDialog.Show("Openings", $"{item.action} failed:\n{ex.Message}"); }
            }
        }

        public string GetName() => "Vixeldorf Openings";

        private static void Run(UIApplication app, OpeningsAction action, List<long> ids)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null)
            {
                OpeningsSession.RefreshWindow(app, null);
                return;
            }

            switch (action)
            {
                case OpeningsAction.Refresh:
                    OpeningsSession.RefreshWindow(app, null);
                    break;

                case OpeningsAction.Place:
                    List<ElementId> touched = OpeningRun.Execute(uidoc, OpeningsSession.Settings, ids);
                    OpeningsSession.RefreshWindow(app, touched);
                    break;

                case OpeningsAction.Select:
                {
                    List<ElementId> sel = Openings(doc, ids);
                    uidoc.Selection.SetElementIds(sel);
                    if (sel.Count > 0)
                        try { uidoc.ShowElements(sel); } catch { /* no suitable view — the selection is still set */ }
                    break;
                }

                case OpeningsAction.Edit:
                {
                    if (_editor != null) break;
                    ExistingOpening e = Resolve(uidoc, ids, false, "Pick the opening to edit.  ESC = cancel.");
                    if (e == null) break;
                    _editor = OpeningEditor.Start(uidoc, e);
                    if (_editor != null) OpeningsSession.Window?.EnterEditMode(_editor.Hint);
                    break;
                }

                case OpeningsAction.EditApply:
                {
                    if (_editor == null) { OpeningsSession.Window?.ExitEditMode(); break; }
                    bool ok = _editor.Apply(doc, out string msg, out bool keepEditing);
                    if (msg != null) TaskDialog.Show("Openings", msg);
                    if (keepEditing) break;   // the sketch is still there to fix

                    long changed = _editor.OpeningElementId;
                    _editor = null;
                    OpeningsSession.Window?.ExitEditMode();
                    OpeningsSession.RefreshWindow(app, ok ? new List<ElementId> { ElementIdExtensions.FromValue(changed) } : null);
                    break;
                }

                case OpeningsAction.EditCancel:
                {
                    _editor?.Cancel(doc);
                    _editor = null;
                    OpeningsSession.Window?.ExitEditMode();
                    OpeningsSession.RefreshWindow(app, null);
                    break;
                }

                case OpeningsAction.Section:
                {
                    ExistingOpening e = Resolve(uidoc, ids, true, "Pick the wall opening to show in a section.  ESC = cancel.");
                    WallOpening w = e == null ? null : WallOpening.From(e);
                    if (e != null && w == null) TaskDialog.Show("Openings", "Choose an opening in a wall (rectangular or round).");
                    if (w != null) OpeningSection.Show(uidoc, w);
                    break;
                }

                case OpeningsAction.Cleanup:
                    if (_editor != null) { _editor.Cancel(doc); _editor = null; }
                    OpeningSection.Cleanup(doc, uidoc.ActiveView?.Id);
                    break;

                case OpeningsAction.Lock:
                case OpeningsAction.Unlock:
                {
                    bool locking = action == OpeningsAction.Lock;
                    SharedParamHelper.EnsureParameters(doc);   // projects from before Opening Lock existed
                    using (Transaction t = new Transaction(doc, locking ? "Lock openings" : "Unlock openings"))
                    {
                        t.Start();
                        foreach (ElementId id in Openings(doc, ids))
                        {
                            DirectShape ds = (DirectShape)doc.GetElement(id);
                            Parameter p = ds.get_Parameter(OpeningParams.Locked.Guid);
                            if (p != null && !p.IsReadOnly) p.Set(locking ? 1 : 0);
                            if (!locking && (ds.ApplicationDataId ?? "").Contains(";manual=1"))
                                ds.ApplicationDataId = ds.ApplicationDataId.Replace(";manual=1", "");
                        }
                        if (!locking && ids.Any(i => i < 0))
                        {
                            List<DeletedOpening> deleted = DeletedOpenings.Read(doc);
                            if (deleted.RemoveAll(d => ids.Contains(d.RowId)) > 0) DeletedOpenings.Write(doc, deleted);
                        }
                        t.Commit();
                    }
                    OpeningsSession.RefreshWindow(app, null);
                    break;
                }

                case OpeningsAction.Delete:
                {
                    List<ElementId> del = Openings(doc, ids);
                    if (del.Count > 0)
                        using (Transaction t = new Transaction(doc, "Delete openings"))
                        {
                            t.Start();
                            doc.Delete(del);
                            t.Commit();
                        }
                    OpeningsSession.RefreshWindow(app, null);
                    break;
                }
            }
        }

        /// <summary>
        /// The opening of the chosen row, or — with no row chosen — one picked in the model
        /// (palette hidden meanwhile, as ShaftMaster does). Null when cancelled.
        /// </summary>
        private static ExistingOpening Resolve(UIDocument uidoc, List<long> ids, bool wallsOnly, string prompt)
        {
            Document doc = uidoc.Document;
            ElementId id = null;
            if (ids.Count > 0)
                id = ElementIdExtensions.FromValue(ids[0]);
            else
            {
                OpeningsSession.Window?.Hide();
                try
                {
                    id = uidoc.Selection.PickObject(ObjectType.Element, new OpeningFilter(wallsOnly), prompt).ElementId;
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
                finally { OpeningsSession.Window?.Show(); }
            }

            return ExistingOpening.Collect(doc).FirstOrDefault(o => o.Shape.Id == id);
        }

        /// <summary>The ids that are still openings of this add-in in this document.</summary>
        private static List<ElementId> Openings(Document doc, List<long> ids) =>
            ids.Select(ElementIdExtensions.FromValue)
               .Where(id => doc.GetElement(id) is DirectShape ds && ds.ApplicationId == OpeningParams.AppId)
               .ToList();
    }
}
