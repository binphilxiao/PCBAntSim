using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntennaSimulatorApp.Models;
using AntennaSimulatorApp.ViewModels;
using Clipper2Lib;

namespace AntennaSimulatorApp.Views
{
    public partial class ManageShapesWindow : Window
    {
        private readonly MainViewModel _vm;
        private List<ManualShape>? _clipboardShapes;
        private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _collectionHandler;

        public ManageShapesWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;

            // Show only non-antenna shapes (antennas are managed via Draw → Draw Antenna)
            var view = new System.Windows.Data.ListCollectionView(vm.ManualShapes);
            view.Filter = o => o is ManualShape s && (s.Name == null || !s.Name.StartsWith("Antenna ("));
            ShapesGrid.ItemsSource = view;

            UpdateStatus();
            _collectionHandler = (_, __) => UpdateStatus();
            vm.ManualShapes.CollectionChanged += _collectionHandler;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _vm.ManualShapes.CollectionChanged -= _collectionHandler;
        }

        private void UpdateStatus()
        {
            int n = _vm.ManualShapes.Count(s => s.Name == null || !s.Name.StartsWith("Antenna ("));
            StatusBar.Text = $"{n} shape{(n == 1 ? "" : "s")}";
        }

        // ── Toolbar handlers ──────────────────────────────────────────────────

        private void NewShape_Click(object sender, RoutedEventArgs e)
        {
            var win = new DrawCopperWindow(_vm) { Owner = this };
            if (win.ShowDialog() == true && win.Result != null)
                _vm.ManualShapes.Add(win.Result);
        }

        private void EditShape_Click(object sender, RoutedEventArgs e) => EditSelected();

        private void DeleteShape_Click(object sender, RoutedEventArgs e)
        {
            var selected = ShapesGrid.SelectedItems.Cast<ManualShape>().ToList();
            if (selected.Count == 0) return;

            string names = selected.Count == 1
                ? $"\"{selected[0].Name}\""
                : $"{selected.Count} shapes";

            var r = MessageBox.Show($"Delete {names}?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            foreach (var s in selected)
                _vm.ManualShapes.Remove(s);
        }

        private void CopyShape_Click(object sender, RoutedEventArgs e) => CopySelected();

        private void PasteShape_Click(object sender, RoutedEventArgs e) => PasteShapes();

        private void CopySelected()
        {
            var selected = ShapesGrid.SelectedItems.Cast<ManualShape>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Select one or more shapes to copy.", "Copy",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _clipboardShapes = selected.Select(CloneShape).ToList();
            StatusBar.Text = $"Copied {_clipboardShapes.Count} shape(s)";
        }

        private void PasteShapes()
        {
            if (_clipboardShapes == null || _clipboardShapes.Count == 0)
            {
                MessageBox.Show("Nothing to paste. Copy shapes first.", "Paste",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var s in _clipboardShapes)
            {
                var copy = CloneShape(s);
                copy.Name += " (copy)";
                _vm.ManualShapes.Add(copy);
            }
            StatusBar.Text = $"Pasted {_clipboardShapes.Count} shape(s)";
        }

        private static ManualShape CloneShape(ManualShape src)
        {
            var clone = new ManualShape
            {
                Name      = src.Name,
                IsCarrier = src.IsCarrier,
                LayerName = src.LayerName,
                ShowIn3D  = src.ShowIn3D,
            };
            foreach (var v in src.Vertices)
                clone.Vertices.Add(new ShapeVertex(v.X, v.Y));
            foreach (var poly in src.MergedPolygons)
                clone.MergedPolygons.Add(poly.Select(v => new ShapeVertex(v.X, v.Y)).ToList());
            return clone;
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.C &&
                (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                CopySelected();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.V &&
                     (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                PasteShapes();
                e.Handled = true;
            }
        }

        private void MergeShapes_Click(object sender, RoutedEventArgs e)
        {
            var selected = ShapesGrid.SelectedItems.Cast<ManualShape>().ToList();
            if (selected.Count < 2)
            {
                MessageBox.Show("Select 2 or more shapes to merge.", "Merge",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // All must be on the same board + layer
            var first = selected[0];
            bool sameTarget = selected.All(s =>
                s.IsCarrier == first.IsCarrier &&
                s.LayerName == first.LayerName);

            if (!sameTarget)
            {
                MessageBox.Show(
                    "All selected shapes must be on the same board and layer.",
                    "Merge – Mismatch",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Boolean union via Clipper2
            // Collect all polygons from all selected shapes
            var allPaths = new PathsD();
            foreach (var s in selected)
            {
                // Primary polygon
                if (s.Vertices.Count >= 3)
                    allPaths.Add(VerticesToPathD(s.Vertices));
                // Merged sub-polygons
                foreach (var sub in s.MergedPolygons)
                {
                    if (sub.Count >= 3)
                        allPaths.Add(VerticesToPathD(sub));
                }
            }

            // Ensure all polygons have the same winding (positive = CCW in Clipper2)
            // so that overlapping regions don't cancel each other under NonZero fill
            for (int i = 0; i < allPaths.Count; i++)
            {
                if (!Clipper.IsPositive(allPaths[i]))
                    allPaths[i].Reverse();
            }

            // Perform boolean union
            var unionResult = Clipper.Union(allPaths, FillRule.NonZero);

            if (unionResult == null || unionResult.Count == 0)
            {
                MessageBox.Show("Boolean union produced no result.", "Merge",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Apply result to the first shape
            var target = first;

            // Set primary polygon from first result path
            target.Vertices.Clear();
            target.MergedPolygons.Clear();

            PathDToVertices(unionResult[0], target.Vertices);

            // Additional result paths become MergedPolygons
            for (int i = 1; i < unionResult.Count; i++)
            {
                var subVerts = new List<ShapeVertex>();
                PathDToVertices(unionResult[i], subVerts);
                target.MergedPolygons.Add(subVerts);
            }

            // Remove all other selected shapes
            for (int i = 1; i < selected.Count; i++)
                _vm.ManualShapes.Remove(selected[i]);

            // Force DisplayName refresh
            target.Name = target.Name; // triggers OnPropertyChanged

            MessageBox.Show(
                $"Boolean union of {selected.Count} shapes → \"{target.Name}\".\n" +
                $"Result: {unionResult.Count} polygon(s), {target.Vertices.Count + target.MergedPolygons.Sum(p => p.Count)} vertices.",
                "Merge Complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ── Clipper2 helpers ──────────────────────────────────────────────────

        /// <summary>Convert our ShapeVertex collection to Clipper2 PathD (skip closing duplicate).</summary>
        private static PathD VerticesToPathD(IEnumerable<ShapeVertex> vertices)
        {
            var list = vertices.ToList();
            var path = new PathD();

            int count = list.Count;
            // If last == first (closed polygon), skip the duplicate closing vertex
            if (count > 1 &&
                Math.Abs(list[0].X - list[count - 1].X) < 1e-4 &&
                Math.Abs(list[0].Y - list[count - 1].Y) < 1e-4)
            {
                count--;
            }

            for (int i = 0; i < count; i++)
                path.Add(new PointD(list[i].X, list[i].Y));

            return path;
        }

        /// <summary>Convert Clipper2 PathD back to ShapeVertex list (auto-close).</summary>
        private static void PathDToVertices(PathD path, ICollection<ShapeVertex> output)
        {
            foreach (var pt in path)
                output.Add(new ShapeVertex(Math.Round(pt.x, 6), Math.Round(pt.y, 6)));
            // Close: duplicate first vertex at end
            if (path.Count > 0)
                output.Add(new ShapeVertex(Math.Round(path[0].x, 6), Math.Round(path[0].y, 6)));
        }

        private void ShapesGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ShapesGrid.SelectedItem is ManualShape) EditSelected();
        }

        private void ShapesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // no-op – could enable/disable buttons via binding later
        }

        private void EditSelected()
        {
            if (!(ShapesGrid.SelectedItem is ManualShape shape)) return;

            var win = new DrawCopperWindow(_vm, editShape: shape) { Owner = this };
            if (win.ShowDialog() == true && win.Result != null)
            {
                // Preserve merged polygons when editing primary shape
                win.Result.MergedPolygons.AddRange(shape.MergedPolygons);

                int idx = _vm.ManualShapes.IndexOf(shape);
                if (idx >= 0)
                    _vm.ManualShapes[idx] = win.Result;
            }
        }
    }
}
