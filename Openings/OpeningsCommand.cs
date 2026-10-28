using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Openings
{
    /// <summary>
    /// Opens the Openings window (modeless; a second run brings it back and refreshes it).
    /// Entry point for Add-in Manager during the prototype.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class OpeningsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            if (uiapp.ActiveUIDocument?.Document?.IsFamilyDocument != false)
            {
                TaskDialog.Show("Openings", "Open a project first — openings are not placed in the family editor.");
                return Result.Cancelled;
            }

            OpeningsSession.Show(uiapp);
            return Result.Succeeded;
        }
    }
}
