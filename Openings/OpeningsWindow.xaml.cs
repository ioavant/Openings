using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.DB;
using WColor = System.Windows.Media.Color;

namespace Openings
{
    /// <summary>
    /// Main window, modeless like ShaftMaster's palette: the openings in the project, the models
    /// walls / floors are taken from, and the actions. Everything that touches the model goes
    /// through <see cref="OpeningsSession.Raise"/>; the window is filled back by <see cref="Fill"/>.
    /// </summary>
    public partial class OpeningsWindow : Window
    {
        private readonly OpeningSettings _settings;
        private readonly Dictionary<long, string> _sourceTitles = new Dictionary<long, string>();
        private ForgeTypeId _lengthUnit;
        private string _sourcesSignature;

        public OpeningsWindow(OpeningSettings settings)
        {
            InitializeComponent();
            _settings = settings;

            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            Title = $"Openings v{v.Major}.{v.Minor}.{v.Build}";

            SourcesDrop.EmptyText = "(none)";
            SourcesDrop.Changed += SaveSources;

            ButtonRow.Children.Add(MakeActionButton("Place Openings",
                "Selected MEP elements, or\nall visible in the active view", AccentGradient(), Place));
            ButtonRow.Children.Add(MakeActionButton("Select",
                "Select the chosen rows\nin the model", null, SelectRows));
            ButtonRow.Children.Add(MakeActionButton("Edit",
                "Sketch: plan (width, floor\nprofile), section (+ height)", null, () => OneOrPick(OpeningsAction.Edit)));
            ButtonRow.Children.Add(MakeActionButton("Section",
                "Wall opening: temporary\nsection through it", null, () => OneOrPick(OpeningsAction.Section)));
            ButtonRow.Children.Add(MakeActionButton("Delete",
                "Remove the chosen\nopenings from the model", null, DeleteRows));
            ButtonRow.Children.Add(MakeActionButton("Options…",
                "Categories, size, merging,\nfloor search", null, ShowOptions));

            EditApplyButton.Click += (s, e) => OpeningsSession.Raise(OpeningsAction.EditApply);
            EditCancelButton.Click += (s, e) => OpeningsSession.Raise(OpeningsAction.EditCancel);

            OpeningsGrid.MouseDoubleClick += (s, e) => { if (OpeningsGrid.SelectedItem != null) SelectRows(); };

            // The model may have changed while the window was in the background.
            Activated += (s, e) => OpeningsSession.Raise(OpeningsAction.Refresh);
        }

        // ── Filled from the API context ───────────────────────────────────────

        public void Fill(string docTitle, List<HostSource> sources, List<OpeningRow> rows,
                         ForgeTypeId lengthUnit, IEnumerable<long> highlight)
        {
            _lengthUnit = lengthUnit;
            IsEnabled = true;

            // Links: rebuilt only when they changed, so a refresh on activation never resets ticks
            // being made. Remembered by title; on the first run every link is ticked, the current model is not.
            string signature = docTitle + "|" + string.Join("|", sources.Select(s => (s.Instance?.Id.GetValue() ?? 0) + ":" + s.Label));
            if (signature != _sourcesSignature)
            {
                _sourcesSignature = signature;
                bool firstRun = _settings.HostSources.Count == 0;
                SourcesDrop.Clear();
                _sourceTitles.Clear();
                foreach (HostSource src in sources)
                {
                    long key = src.Instance?.Id.GetValue() ?? 0;
                    _sourceTitles[key] = src.Title;
                    SourcesDrop.AddItem(src.Label, key,
                        firstRun ? src.Instance != null : _settings.HostSources.Contains(src.Title));
                }
            }

            HashSet<long> keep = new HashSet<long>(highlight ?? SelectedIds());
            OpeningsGrid.ItemsSource = rows;
            OpeningsGrid.SelectedItems.Clear();
            foreach (OpeningRow r in rows.Where(r => keep.Contains(r.ElementId)))
                OpeningsGrid.SelectedItems.Add(r);
            if (OpeningsGrid.SelectedItem != null) OpeningsGrid.ScrollIntoView(OpeningsGrid.SelectedItem);

            ListLabel.Text = $"Openings in {docTitle}";
            StatusText.Text = rows.Count == 1 ? "1 opening" : $"{rows.Count} openings";
        }

        public void ShowNoProject()
        {
            SourcesDrop.Clear();
            _sourceTitles.Clear();
            _sourcesSignature = null;
            OpeningsGrid.ItemsSource = null;
            ListLabel.Text = "Openings";
            StatusText.Text = "Open a project.";
            IsEnabled = false;
        }

        /// <summary>While a width sketch is open only Apply / Cancel make sense.</summary>
        public void EnterEditMode(string text)
        {
            EditText.Text = text;
            EditBanner.Visibility = System.Windows.Visibility.Visible;
            ButtonRow.IsEnabled = OpeningsGrid.IsEnabled = SourcesDrop.IsEnabled = false;
        }

        public void ExitEditMode()
        {
            EditBanner.Visibility = System.Windows.Visibility.Collapsed;
            ButtonRow.IsEnabled = OpeningsGrid.IsEnabled = SourcesDrop.IsEnabled = true;
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private void LockBox_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as CheckBox)?.DataContext is OpeningRow row)) return;
            bool locked = (sender as CheckBox).IsChecked == true;
            if (row.IsDeleted && locked) { (sender as CheckBox).IsChecked = true; return; }   // nothing to lock: it is gone
            OpeningsSession.Raise(locked ? OpeningsAction.Lock : OpeningsAction.Unlock, new List<long> { row.ElementId });
        }

        /// <summary>One chosen row, or none to pick the opening in the model.</summary>
        private void OneOrPick(OpeningsAction action)
        {
            List<long> ids = SelectedIds();
            ids.RemoveAll(id => id < 0);   // deleted openings
            if (ids.Count > 1)
            {
                MessageBox.Show(this, "Choose one opening in the list, or none to pick it in the model.", "Openings",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpeningsSession.Raise(action, ids);
        }

        private void SaveSources()
        {
            _settings.HostSources = SourcesDrop.CheckedTags.Cast<long>()
                .Where(_sourceTitles.ContainsKey).Select(k => _sourceTitles[k]).Distinct().ToList();
            _settings.Save();
        }

        private void Place()
        {
            List<long> keys = SourcesDrop.CheckedTags.Cast<long>().ToList();
            if (keys.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one model with walls / floors.", "Openings",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpeningsSession.Raise(OpeningsAction.Place, keys);
        }

        private void SelectRows()
        {
            List<long> ids = SelectedIds();
            if (ids.Count == 0)
            {
                MessageBox.Show(this, "Choose openings in the list first.", "Openings",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpeningsSession.Raise(OpeningsAction.Select, ids);
        }

        private void DeleteRows()
        {
            List<long> ids = SelectedIds();
            if (ids.Count == 0)
            {
                MessageBox.Show(this, "Choose openings in the list first.", "Openings",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string what = ids.Count == 1 ? "this opening" : $"these {ids.Count} openings";
            if (MessageBox.Show(this, $"Delete {what} from the model?", "Openings",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            OpeningsSession.Raise(OpeningsAction.Delete, ids);
        }

        private void ShowOptions()
        {
            if (_lengthUnit == null) return;
            OptionsWindow dlg = new OptionsWindow(_settings, _lengthUnit) { Owner = this };
            if (dlg.ShowDialog() == true) _settings.Save();
        }

        private List<long> SelectedIds() =>
            OpeningsGrid.SelectedItems.OfType<OpeningRow>().Select(r => r.ElementId).ToList();

        // ── Buttons (same look as ShaftMaster's palette) ──────────────────────

        /// <summary>Accent gradient (#1F39CA → #FF11AD), bottom-right → top-left, for the main action.</summary>
        private static Brush AccentGradient()
        {
            LinearGradientBrush grad = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(1, 1),
                EndPoint = new System.Windows.Point(0, 0)
            };
            grad.GradientStops.Add(new GradientStop(WColor.FromRgb(0x1F, 0x39, 0xCA), 0.0));
            grad.GradientStops.Add(new GradientStop(WColor.FromRgb(0xFF, 0x11, 0xAD), 1.0));
            return grad;
        }

        /// <param name="background">Fill brush; null for a plain button with the default chrome.</param>
        private static Button MakeActionButton(string title, string subtitle, Brush background, Action onClick)
        {
            bool colored = background != null;

            StackPanel panel = new StackPanel { Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = colored ? Brushes.White : new SolidColorBrush(WColor.FromRgb(40, 40, 40)),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            });
            panel.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 10,
                Foreground = colored ? new SolidColorBrush(WColor.FromRgb(220, 235, 255))
                                     : new SolidColorBrush(WColor.FromRgb(110, 110, 110)),
                TextAlignment = TextAlignment.Center
            });

            Button btn = new Button { Content = panel, Height = 68, Cursor = Cursors.Hand, Margin = new Thickness(2) };
            if (colored)
            {
                btn.Background = background;
                btn.BorderThickness = new Thickness(0);
            }
            btn.Click += (s, e) => onClick();
            return btn;
        }
    }
}
