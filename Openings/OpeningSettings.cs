using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Openings
{
    /// <summary>
    /// Everything the window collects. Lengths are stored in Revit internal units (feet);
    /// they are converted to/from project length units only at the window boundary.
    /// Persisted per user so the next run starts where the last one ended.
    /// </summary>
    public class OpeningSettings
    {
        private const double MmToFeet = 1.0 / 304.8;

        // Link instances are remembered by their document title, not ElementId:
        // ids change when a link is removed and re-added.
        public List<string> HostSources { get; set; } = new List<string>();

        public List<string> HostCategories { get; set; } = new List<string> { "OST_Walls", "OST_Floors" };
        public List<string> MepCategories { get; set; } = new List<string> { "OST_DuctCurves" };

        /// <summary>How much bigger than the element the opening is, on each side.</summary>
        public double MarginPerSide { get; set; } = 50 * MmToFeet;

        /// <summary>Add the duct/pipe insulation thickness to the element size.</summary>
        public bool IncludeInsulation { get; set; } = true;

        /// <summary>Merge openings in the same host whose clear gap is not larger than MergeDistance.</summary>
        public bool MergeEnabled { get; set; } = true;
        public double MergeDistance { get; set; } = 100 * MmToFeet;

        /// <summary>Single round elements crossing (nearly) perpendicular get a round opening.</summary>
        public bool RoundForRoundElements { get; set; } = true;

        /// <summary>How far past each wall face (along the duct) the floor is probed from.</summary>
        public double FloorProbeOffset { get; set; } = 300 * MmToFeet;

        // ── Representation ────────────────────────────────────────────────────

        /// <summary>What new openings are placed as, and what Convert turns openings into.</summary>
        public OpeningMode Mode { get; set; } = OpeningMode.DirectShape;

        /// <summary>Face-based family types ("Family : Type") for rectangular and round openings.</summary>
        public string RectFamily { get; set; } = "";
        public string RoundFamily { get; set; } = "";

        // Family instance parameters the sizes go to (empty: not set).
        public string MapWidth { get; set; } = "Width";
        public string MapHeight { get; set; } = "Height";
        public string MapDepth { get; set; } = "Depth";
        public string MapDiameter { get; set; } = "Diameter";
        public string MapRoundDepth { get; set; } = "Depth";

        /// <summary>A free shape this close to its bounding rectangle / circle is placed as that family.</summary>
        public double FamilyTolerance { get; set; } = 20 * MmToFeet;

        // ── Persistence ───────────────────────────────────────────────────────

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Vixeldorf", "Openings", "settings.json");

        // DataContractJsonSerializer, not System.Text.Json: it ships with both net48 and
        // net8, so Revit 2022-2024 and 2025+ read and write the same file without an extra
        // DLL beside the add-in. The class has no [DataContract], so the serializer calls
        // the constructor and members missing from an older file keep their defaults.
        private static DataContractJsonSerializer Serializer => new DataContractJsonSerializer(typeof(OpeningSettings));

        public static OpeningSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    using (FileStream stream = File.OpenRead(SettingsPath))
                        return Serializer.ReadObject(stream) as OpeningSettings ?? new OpeningSettings();
            }
            catch { /* corrupt or old file — fall back to defaults */ }
            return new OpeningSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                using (FileStream stream = File.Create(SettingsPath))
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                    Serializer.WriteObject(writer, this);
            }
            catch { /* settings are a convenience; never fail the command over them */ }
        }
    }
}
