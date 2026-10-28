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
        EditWidth, // start the plan sketch for a wall opening's width (the given row, or pick in the model)
        EditApply, // read the sketch back into the opening
        EditCancel,// drop the sketch
        Section,   // temporary section through a wall opening (the given row, or pick in the model)
        Cleanup    // palette closed: drop a running sketch and the temporary sections
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
                try { Run(app, item.action, item.ids); }
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

                case OpeningsAction.EditWidth:
                {
                    if (_editor != null) break;
                    WallOpening w = Resolve(uidoc, ids, "Pick the wall opening whose width to edit.  ESC = cancel.");
                    if (w == null) break;
                    _editor = OpeningEditor.Start(uidoc, w);
                    if (_editor != null)
                        OpeningsSession.Window?.EnterEditMode(
                            $"Editing the width of opening {(w.OpeningId.HasValue ? "#" + w.OpeningId : w.Shape.Id.GetValue().ToString())}: " +
                            "move the two red side lines in the plan (Move, drag, Align…), then Apply.");
                    break;
                }

                case OpeningsAction.EditApply:
                case OpeningsAction.EditCancel:
                {
                    OpeningEditor ed = _editor;
                    _editor = null;
                    OpeningsSession.Window?.ExitEditMode();
                    if (ed == null) break;

                    List<ElementId> changed = null;
                    if (action == OpeningsAction.EditCancel) ed.Cancel(doc);
                    else if (ed.Apply(doc, out string msg)) changed = new List<ElementId> { ElementIdExtensions.FromValue(ed.OpeningElementId) };
                    else if (msg != null) TaskDialog.Show("Openings", msg);
                    OpeningsSession.RefreshWindow(app, changed);
                    break;
                }

                case OpeningsAction.Section:
                {
                    WallOpening w = Resolve(uidoc, ids, "Pick the wall opening to show in a section.  ESC = cancel.");
                    if (w != null) OpeningSection.Show(uidoc, w);
                    break;
                }

                case OpeningsAction.Cleanup:
                    if (_editor != null) { _editor.Cancel(doc); _editor = null; }
                    OpeningSection.Cleanup(doc, uidoc.ActiveView?.Id);
                    break;

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
        /// The wall opening of the chosen row, or — with no row chosen — one picked in the model
        /// (palette hidden meanwhile, as ShaftMaster does). Null when cancelled or not a wall opening.
        /// </summary>
        private static WallOpening Resolve(UIDocument uidoc, List<long> ids, string prompt)
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
                    id = uidoc.Selection.PickObject(ObjectType.Element, new WallOpeningFilter(), prompt).ElementId;
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
                finally { OpeningsSession.Window?.Show(); }
            }

            ExistingOpening e = ExistingOpening.Collect(doc).FirstOrDefault(o => o.Shape.Id == id);
            WallOpening w = e == null ? null : WallOpening.From(e);
            if (w == null)
                TaskDialog.Show("Openings", "Choose an opening in a wall (rectangular or round).");
            return w;
        }

        /// <summary>The ids that are still openings of this add-in in this document.</summary>
        private static List<ElementId> Openings(Document doc, List<long> ids) =>
            ids.Select(ElementIdExtensions.FromValue)
               .Where(id => doc.GetElement(id) is DirectShape ds && ds.ApplicationId == OpeningParams.AppId)
               .ToList();
    }
}
