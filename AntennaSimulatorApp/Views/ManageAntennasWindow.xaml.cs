using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AntennaSimulatorApp.Models;
using AntennaSimulatorApp.ViewModels;

namespace AntennaSimulatorApp.Views
{
    // ── Converter: bool IsCarrier → "Carrier" / "Module" ─────────────────────
    public class AntennaBoardConverter : IValueConverter
    {
        public static readonly AntennaBoardConverter Instance = new();
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value is bool b && b ? "Carrier" : "Module";
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => throw new NotSupportedException();
    }

    public partial class ManageAntennasWindow : Window
    {
        private readonly MainViewModel _vm;
        private List<AntennaParams>? _clipboard;
        private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _collectionHandler;

        public ManageAntennasWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;

            // Commit any pending AddNew / EditItem on the default view
            // to avoid 'DeferRefresh' crash when re-binding the same collection.
            if (CollectionViewSource.GetDefaultView(vm.DrawnAntennas) is IEditableCollectionView ecv)
            {
                if (ecv.IsAddingNew)    ecv.CommitNew();
                if (ecv.IsEditingItem)  ecv.CommitEdit();
            }

            AntennasGrid.ItemsSource = vm.DrawnAntennas;
            UpdateStatus();
            _collectionHandler = (_, __) => UpdateStatus();
            vm.DrawnAntennas.CollectionChanged += _collectionHandler;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _vm.DrawnAntennas.CollectionChanged -= _collectionHandler;
        }

        private void UpdateStatus()
        {
            int n = _vm.DrawnAntennas.Count;
            StatusBar.Text = $"{n} antenna{(n == 1 ? "" : "s")}";
        }

        // ── Toolbar handlers ──────────────────────────────────────────────────

        private void NewAntenna_Click(object sender, RoutedEventArgs e)
        {
            // Generate default name
            int idx = _vm.DrawnAntennas.Count + 1;
            string name = $"Antenna {idx}";
            while (_vm.DrawnAntennas.Any(a => a.Name == name))
                name = $"Antenna {++idx}";

            var win = new DrawAntennaWindow(_vm, defaultName: name) { Owner = this };
            win.ShowDialog();

            if (win.Result != null)
            {
                _vm.DrawnAntennas.Add(win.Result);
            }
        }

        private void EditAntenna_Click(object sender, RoutedEventArgs e) => EditSelected();

        private void EditSelected()
        {
            if (AntennasGrid.SelectedItem is not AntennaParams ap) return;
            int idx = _vm.DrawnAntennas.IndexOf(ap);
            if (idx < 0) return;

            var win = new DrawAntennaWindow(_vm, editParams: ap) { Owner = this };
            win.ShowDialog();

            if (win.Result != null)
            {
                _vm.DrawnAntennas[idx] = win.Result;
            }
        }

        private void DeleteAntenna_Click(object sender, RoutedEventArgs e)
        {
            var selected = AntennasGrid.SelectedItems.Cast<AntennaParams>().ToList();
            if (selected.Count == 0) return;

            string names = selected.Count == 1
                ? $"\"{selected[0].Name}\""
                : $"{selected.Count} antennas";

            var r = MessageBox.Show($"Delete {names}?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            foreach (var ap in selected)
            {
                // Remove the matching ManualShape
                string shapeName = $"Antenna ({ap.Name})";
                var shape = _vm.ManualShapes.FirstOrDefault(s => s.Name == shapeName);
                if (shape != null)
                    _vm.ManualShapes.Remove(shape);

                _vm.DrawnAntennas.Remove(ap);
            }
        }

        private void CopyAntenna_Click(object sender, RoutedEventArgs e) => CopySelected();

        private void PasteAntenna_Click(object sender, RoutedEventArgs e) => PasteAntennas();

        private void CopySelected()
        {
            var selected = AntennasGrid.SelectedItems.Cast<AntennaParams>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Select one or more antennas to copy.", "Copy",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _clipboard = selected.Select(CloneAntennaParams).ToList();
            StatusBar.Text = $"Copied {_clipboard.Count} antenna(s)";
        }

        private void PasteAntennas()
        {
            if (_clipboard == null || _clipboard.Count == 0)
            {
                MessageBox.Show("Nothing to paste. Copy antennas first.", "Paste",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var ap in _clipboard)
            {
                var copy = CloneAntennaParams(ap);
                // Ensure unique name
                string baseName = copy.Name;
                int suffix = 2;
                while (_vm.DrawnAntennas.Any(a => a.Name == copy.Name))
                    copy.Name = $"{baseName} ({suffix++})";

                _vm.DrawnAntennas.Add(copy);
            }
            StatusBar.Text = $"Pasted {_clipboard.Count} antenna(s)";
        }

        internal static AntennaParams CloneAntennaParams(AntennaParams src) => new()
        {
            Type           = src.Type,
            IsCarrier      = src.IsCarrier,
            LayerName      = src.LayerName,
            Name           = src.Name,
            OffsetX        = src.OffsetX,
            OffsetY        = src.OffsetY,
            HasGroundStub  = src.HasGroundStub,
            MirrorEnabled  = src.MirrorEnabled,
            MirrorAxis     = src.MirrorAxis,
            FreqGHz        = src.FreqGHz,
            LengthL        = src.LengthL,
            HeightH        = src.HeightH,
            FeedGap        = src.FeedGap,
            ShortPinWidth  = src.ShortPinWidth,
            FeedPinWidth   = src.FeedPinWidth,
            MatchStubWidth = src.MatchStubWidth,
            RadiatorWidth  = src.RadiatorWidth,
            MifaHeightH    = src.MifaHeightH,
            MeanderHeight  = src.MeanderHeight,
            MeanderPitch   = src.MeanderPitch,
            MifaShortWidth = src.MifaShortWidth,
            MifaFeedWidth  = src.MifaFeedWidth,
            MifaHorizWidth = src.MifaHorizWidth,
            MifaVertWidth  = src.MifaVertWidth,
            AvailWidth     = src.AvailWidth,
            AvailHeight    = src.AvailHeight,
            PcbOffsetX     = src.PcbOffsetX,
            PcbOffsetY     = src.PcbOffsetY,
            Clearance      = src.Clearance,
            CustomVertices = src.CustomVertices.Select(v => (v.X, v.Y)).ToList(),
        };

        // ── Double-click → edit ────────────────────────────────────────────────

        private void AntennasGrid_DoubleClick(object sender, MouseButtonEventArgs e) => EditSelected();

        private void AntennasGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        // ── Keyboard shortcuts ─────────────────────────────────────────────────

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.C) { CopySelected(); e.Handled = true; }
                if (e.Key == Key.V) { PasteAntennas(); e.Handled = true; }
            }
        }
    }
}
