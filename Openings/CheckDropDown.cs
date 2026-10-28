using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Openings
{
    /// <summary>
    /// Drop-down list of check boxes: a combo-box-looking toggle that opens a popup with
    /// one check box per item plus All / None. The toggle shows what is ticked.
    /// </summary>
    public class CheckDropDown : UserControl
    {
        private readonly ToggleButton _toggle;
        private readonly TextBlock _text;
        private readonly Popup _popup;
        private readonly StackPanel _items;

        public string EmptyText { get; set; } = "(none)";

        /// <summary>Raised when the user ticks or unticks an item (not by AddItem / Clear).</summary>
        public event System.Action Changed;

        public CheckDropDown()
        {
            _text = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0)
            };
            TextBlock arrow = new TextBlock
            {
                Text = "▾",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid face = new Grid();
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(arrow, 1);
            face.Children.Add(_text);
            face.Children.Add(arrow);

            _toggle = new ToggleButton
            {
                Content = face,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 4, 6, 4),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 13
            };

            _items = new StackPanel { Margin = new Thickness(8, 4, 8, 6) };

            Button all = LinkButton("All", () => SetAll(true));
            Button none = LinkButton("None", () => SetAll(false));
            StackPanel header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 0) };
            header.Children.Add(all);
            header.Children.Add(none);

            StackPanel content = new StackPanel();
            content.Children.Add(header);
            content.Children.Add(new ScrollViewer
            {
                Content = _items,
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });

            _popup = new Popup
            {
                PlacementTarget = _toggle,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = false,
                Child = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                    BorderThickness = new Thickness(1),
                    Child = content
                }
            };

            // Popup and toggle follow each other; while the popup is open the toggle ignores
            // clicks, otherwise the click that closes the popup (StaysOpen=false) would reopen it.
            _toggle.Checked += (s, e) =>
            {
                ((Border)_popup.Child).MinWidth = _toggle.ActualWidth;
                _popup.IsOpen = true;
            };
            _toggle.Unchecked += (s, e) => _popup.IsOpen = false;
            _popup.Opened += (s, e) => _toggle.IsHitTestVisible = false;
            _popup.Closed += (s, e) => { _toggle.IsChecked = false; _toggle.IsHitTestVisible = true; };

            Grid root = new Grid();
            root.Children.Add(_toggle);
            root.Children.Add(_popup);
            Content = root;

            UpdateText();
        }

        private static Button LinkButton(string text, System.Action onClick)
        {
            Button b = new Button
            {
                Content = text,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 12, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x5F, 0xB4)),
                FontSize = 12
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        public void AddItem(string label, object tag, bool isChecked)
        {
            CheckBox cb = new CheckBox { Content = label, Tag = tag, IsChecked = isChecked, Margin = new Thickness(0, 3, 0, 3) };
            cb.Checked += (s, e) => { UpdateText(); Changed?.Invoke(); };
            cb.Unchecked += (s, e) => { UpdateText(); Changed?.Invoke(); };
            _items.Children.Add(cb);
            UpdateText();
        }

        public void Clear()
        {
            _items.Children.Clear();
            UpdateText();
        }

        public List<object> CheckedTags =>
            Boxes.Where(cb => cb.IsChecked == true).Select(cb => cb.Tag).ToList();

        private IEnumerable<CheckBox> Boxes => _items.Children.OfType<CheckBox>();

        private void SetAll(bool value)
        {
            foreach (CheckBox cb in Boxes) cb.IsChecked = value;
        }

        private void UpdateText()
        {
            List<string> names = Boxes.Where(cb => cb.IsChecked == true).Select(cb => cb.Content.ToString()).ToList();
            int total = Boxes.Count();

            if (names.Count == 0) _text.Text = EmptyText;
            else if (names.Count == total && total > 1) _text.Text = $"All ({total})";
            else if (names.Count <= 2) _text.Text = string.Join(", ", names);
            else _text.Text = $"{names.Count} of {total} selected";

            _toggle.ToolTip = names.Count == 0 ? null : string.Join("\n", names);
        }
    }
}
