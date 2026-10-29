using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;

namespace Openings
{
    /// <summary>
    /// Everything about how openings are made, opened from the main window's Options button.
    /// Writes into the settings object only on OK.
    /// </summary>
    public partial class OptionsWindow : Window
    {
        // (label, BuiltInCategory name) — names, not enum values, so they round-trip through settings.json.
        private static readonly List<KeyValuePair<string, string>> HostCategoryChoices = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("Walls", "OST_Walls"),
            new KeyValuePair<string, string>("Floors", "OST_Floors"),
            new KeyValuePair<string, string>("Roofs", "OST_Roofs"),
            new KeyValuePair<string, string>("Ceilings", "OST_Ceilings"),
            new KeyValuePair<string, string>("Structural Framing (beams)", "OST_StructuralFraming"),
        };

        // Fittings and accessories are separate choices: where a duct hands over to a fitting
        // inside the wall the duct alone already gets a full-depth opening, so they are opt-in.
        private static readonly List<KeyValuePair<string, string>> MepCategoryChoices = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("Ducts", "OST_DuctCurves"),
            new KeyValuePair<string, string>("Duct Fittings", "OST_DuctFitting"),
            new KeyValuePair<string, string>("Duct Accessories", "OST_DuctAccessory"),
            new KeyValuePair<string, string>("Pipes", "OST_PipeCurves"),
            new KeyValuePair<string, string>("Pipe Fittings", "OST_PipeFitting"),
            new KeyValuePair<string, string>("Pipe Accessories", "OST_PipeAccessory"),
            new KeyValuePair<string, string>("Cable Trays", "OST_CableTray"),
            new KeyValuePair<string, string>("Cable Tray Fittings", "OST_CableTrayFitting"),
            new KeyValuePair<string, string>("Conduits", "OST_Conduit"),
            new KeyValuePair<string, string>("Conduit Fittings", "OST_ConduitFitting"),
            // ShaftMaster shafts get an opening of their own profile, without margin; other
            // generic models are sized like any element. Openings themselves are never MEP.
            new KeyValuePair<string, string>("Generic Models (ShaftMaster shafts)", "OST_GenericModel"),
        };

        private readonly OpeningSettings _settings;
        private readonly ForgeTypeId _lengthUnit;
        private readonly string _unitLabel;

        private readonly List<FamilyChoice> _families;

        /// <param name="families">Face-based Generic Model family types loaded in the project, with their parameters.</param>
        public OptionsWindow(OpeningSettings settings, ForgeTypeId lengthUnit, List<FamilyChoice> families)
        {
            InitializeComponent();
            _settings = settings;
            _lengthUnit = lengthUnit;
            _unitLabel = LabelUtils.GetLabelForUnit(_lengthUnit);
            _families = families ?? new List<FamilyChoice>();

            MarginLabel.Text = $"Margin on each side ({_unitLabel}):";
            MergeLabel.Text = $"Max gap between openings ({_unitLabel}):";
            ProbeLabel.Text = $"Probe past each wall face along the duct, then down to the floor ({_unitLabel}):";
            ToleranceLabel.Text = $"A free shape (e.g. a ShaftMaster shaft) within this of its bounding rectangle / circle " +
                                  $"is placed as that family, otherwise as a Generic Model ({_unitLabel}):";
            FamiliesText.Text = _families.Count == 0
                ? "No face-based Generic Model families are loaded in this project: openings can only be Generic Models (DirectShape)."
                : "Data goes to the \"Opening …\" shared parameters either way; the family's insertion point is the middle of the opening " +
                  "on the host face, X along the width, cutting Depth into the host.";

            FillFamily(RectFamilyCombo, settings.RectFamily, MapWidthCombo, MapHeightCombo, MapDepthCombo);
            FillFamily(RoundFamilyCombo, settings.RoundFamily, MapDiameterCombo, MapRoundDepthCombo);
            MapWidthCombo.Text = settings.MapWidth;
            MapHeightCombo.Text = settings.MapHeight;
            MapDepthCombo.Text = settings.MapDepth;
            MapDiameterCombo.Text = settings.MapDiameter;
            MapRoundDepthCombo.Text = settings.MapRoundDepth;
            ToleranceTextBox.Text = FormatForDisplay(settings.FamilyTolerance);

            FillCategories(HostCategoriesDrop, HostCategoryChoices, settings.HostCategories);
            FillCategories(MepCategoriesDrop, MepCategoryChoices, settings.MepCategories);

            MarginTextBox.Text = FormatForDisplay(settings.MarginPerSide);
            InsulationCheck.IsChecked = settings.IncludeInsulation;
            RoundCheck.IsChecked = settings.RoundForRoundElements;
            MergeCheck.IsChecked = settings.MergeEnabled;
            MergeTextBox.Text = FormatForDisplay(settings.MergeDistance);
            ProbeTextBox.Text = FormatForDisplay(settings.FloorProbeOffset);
        }

        /// <summary>The family types to choose from; the mapping lists follow the chosen family's parameters.</summary>
        private void FillFamily(ComboBox family, string selected, params ComboBox[] maps)
        {
            family.ItemsSource = _families.Select(f => f.Label).ToList();
            family.SelectionChanged += (s, e) =>
            {
                List<string> names = _families.FirstOrDefault(f => f.Label == family.SelectedItem as string)?.Parameters
                                     ?? new List<string>();
                foreach (ComboBox map in maps)
                {
                    string keep = map.Text;
                    map.ItemsSource = names;
                    map.Text = keep;
                }
            };
            family.SelectedItem = _families.Any(f => f.Label == selected) ? selected : null;
        }

        private static void FillCategories(CheckDropDown drop, List<KeyValuePair<string, string>> choices, List<string> selected)
        {
            foreach (var c in choices)
                drop.AddItem(c.Key, c.Value, selected.Contains(c.Value));
        }

        private static List<string> CheckedCategories(CheckDropDown drop) =>
            drop.CheckedTags.Cast<string>().ToList();

        private string FormatForDisplay(double internalUnits)
        {
            double display = UnitUtils.ConvertFromInternalUnits(internalUnits, _lengthUnit);
            return display.ToString("G6", CultureInfo.InvariantCulture);
        }

        private bool TryParseDisplay(string text, out double internalUnits)
        {
            string normalized = (text ?? "").Replace(',', '.');
            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double displayValue) && displayValue >= 0)
            {
                internalUnits = UnitUtils.ConvertToInternalUnits(displayValue, _lengthUnit);
                return true;
            }
            internalUnits = 0;
            return false;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            List<string> hostCats = CheckedCategories(HostCategoriesDrop);
            List<string> mepCats = CheckedCategories(MepCategoriesDrop);

            string error = null;
            if (hostCats.Count == 0) error = "Tick at least one host category.";
            else if (mepCats.Count == 0) error = "Tick at least one MEP category.";
            else if (!TryParseDisplay(MarginTextBox.Text, out _)) error = $"Margin: enter a non-negative number ({_unitLabel}).";
            else if (!TryParseDisplay(MergeTextBox.Text, out _)) error = $"Max gap: enter a non-negative number ({_unitLabel}).";
            else if (!TryParseDisplay(ProbeTextBox.Text, out _)) error = $"Probe: enter a non-negative number ({_unitLabel}).";
            else if (!TryParseDisplay(ToleranceTextBox.Text, out _)) error = $"Family tolerance: enter a non-negative number ({_unitLabel}).";

            if (error != null)
            {
                ErrorText.Text = error;
                ErrorText.Visibility = System.Windows.Visibility.Visible;
                return;
            }

            TryParseDisplay(MarginTextBox.Text, out double margin);
            TryParseDisplay(MergeTextBox.Text, out double merge);
            TryParseDisplay(ProbeTextBox.Text, out double probe);

            _settings.HostCategories = hostCats;
            _settings.MepCategories = mepCats;
            _settings.MarginPerSide = margin;
            _settings.IncludeInsulation = InsulationCheck.IsChecked == true;
            _settings.RoundForRoundElements = RoundCheck.IsChecked == true;
            _settings.MergeEnabled = MergeCheck.IsChecked == true;
            _settings.MergeDistance = merge;
            _settings.FloorProbeOffset = probe;

            TryParseDisplay(ToleranceTextBox.Text, out double tolerance);
            // A family that is not loaded right now is kept, not forgotten.
            if (RectFamilyCombo.SelectedItem is string rect) _settings.RectFamily = rect;
            if (RoundFamilyCombo.SelectedItem is string round) _settings.RoundFamily = round;
            _settings.MapWidth = MapWidthCombo.Text.Trim();
            _settings.MapHeight = MapHeightCombo.Text.Trim();
            _settings.MapDepth = MapDepthCombo.Text.Trim();
            _settings.MapDiameter = MapDiameterCombo.Text.Trim();
            _settings.MapRoundDepth = MapRoundDepthCombo.Text.Trim();
            _settings.FamilyTolerance = tolerance;

            DialogResult = true;
        }

        private void NumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            string current = textBox?.Text ?? "";
            string incoming = e.Text;

            bool isDigit = char.IsDigit(incoming[0]);
            bool isSeparator = (incoming == "." || incoming == ",")
                               && !current.Contains(".") && !current.Contains(",");

            e.Handled = !(isDigit || isSeparator);
        }
    }
}
