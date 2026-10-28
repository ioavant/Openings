using Autodesk.Revit.DB;
using System;
using System.IO;
using System.Linq;

namespace Openings
{
    /// <summary>
    /// Ensures the Openings shared parameters are bound to Generic Models in the project.
    /// Same mechanism as ShaftMaster's SharedParamHelper: a private shared-parameter file,
    /// fixed GUIDs, the user's own shared-parameter file restored afterwards.
    /// </summary>
    public static class SharedParamHelper
    {
        private const string GroupName = "Openings";

        private static string OurParamFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Vixeldorf", "Openings", "Openings_SharedParams.txt");

        /// <summary>Runs its own transaction — call outside any open transaction.</summary>
        public static void EnsureParameters(Document doc)
        {
            if (OpeningParams.All.All(p => IsAlreadyBound(doc, p.Guid))) return;

            var app = doc.Application;
            string origFile = app.SharedParametersFilename;

            try
            {
                EnsureFileExists();
                app.SharedParametersFilename = OurParamFilePath;

                var defFile = app.OpenSharedParameterFile();
                if (defFile == null)
                    throw new InvalidOperationException("Cannot open " + OurParamFilePath);

                var group = defFile.Groups.get_Item(GroupName)
                         ?? defFile.Groups.Create(GroupName);

                var cats = app.Create.NewCategorySet();
                cats.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_GenericModel));
                var binding = app.Create.NewInstanceBinding(cats);

                using (var tx = new Transaction(doc, "Bind Openings shared parameters"))
                {
                    tx.Start();
                    foreach (OpeningParam p in OpeningParams.All)
                        TryBind(doc, group, defFile, binding, p);
                    tx.Commit();
                }
            }
            finally
            {
                app.SharedParametersFilename = origFile ?? string.Empty;
            }
        }

        /// <summary>
        /// Binds one shared parameter. Looks for the GUID anywhere in the file before
        /// creating it, so a renamed parameter from an older version does not throw.
        /// </summary>
        private static void TryBind(Document doc, DefinitionGroup group, DefinitionFile defFile,
                                    InstanceBinding binding, OpeningParam p)
        {
            if (IsAlreadyBound(doc, p.Guid)) return;

            ExternalDefinition def = null;
            foreach (DefinitionGroup g in defFile.Groups)
            {
                foreach (Definition d in g.Definitions)
                    if (d is ExternalDefinition ed && ed.GUID == p.Guid) { def = ed; break; }
                if (def != null) break;
            }

            if (def == null)
                def = group.Definitions.Create(new ExternalDefinitionCreationOptions(p.Name, p.Spec) { GUID = p.Guid })
                      as ExternalDefinition;

            if (def != null)
                doc.ParameterBindings.Insert(def, binding, p.Group);
        }

        private static bool IsAlreadyBound(Document doc, Guid guid)
        {
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
                if (it.Key is ExternalDefinition ed && ed.GUID == guid)
                    return true;
            return false;
        }

        private static void EnsureFileExists()
        {
            string dir = Path.GetDirectoryName(OurParamFilePath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(OurParamFilePath))
                File.WriteAllText(OurParamFilePath,
                    "# This is a Revit shared parameter file.\r\n" +
                    "# Do not edit manually.\r\n" +
                    "*META\tVERSION\tMINVERSION\r\n" +
                    "META\t2\t1\r\n" +
                    "*GROUP\tID\tNAME\r\n" +
                    "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n");
        }
    }
}
