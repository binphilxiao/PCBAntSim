using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AntennaSimulatorApp.Models;
using AntennaSimulatorApp.ViewModels;
using Microsoft.Win32;

namespace AntennaSimulatorApp.Views
{
    public partial class DrawCopperWindow : Window
    {
        private readonly MainViewModel _vm;

        /// <summary>Shape returned when the user clicks OK (null on Cancel).</summary>
        public ManualShape? Result { get; private set; }

        // Locally-held vertex list bound to the DataGrid
        private readonly ObservableCollection<ShapeVertex> _vertices = new ObservableCollection<ShapeVertex>();

        // ── Construction ──────────────────────────────────────────────────────

        /// <summary>
        /// Open the window.
        /// Pass <paramref name="editShape"/> to pre-populate an existing shape for editing.
        /// </summary>
        public DrawCopperWindow(MainViewModel vm, ManualShape? editShape = null)
        {
            InitializeComponent();
            _vm = vm;

            BoardCombo.Items.Add("Carrier");
            if (vm.HasModule)
                BoardCombo.Items.Add("Module");
            BoardCombo.SelectedIndex = 0;

            VertexGrid.ItemsSource = _vertices;
            _vertices.CollectionChanged += (_, __) => RefreshPreview();

            if (editShape != null)
            {
                NameBox.Text = editShape.Name;

                BoardCombo.SelectedItem = editShape.IsCarrier ? "Carrier" : "Module";
                PopulateLayerCombo();
                if (LayerCombo.Items.Cast<Layer>().FirstOrDefault(l => l.Name == editShape.LayerName) is Layer lyr)
                    LayerCombo.SelectedItem = lyr;

                foreach (var v in editShape.Vertices)
                    _vertices.Add(new ShapeVertex(v.X, v.Y));
            }
            else
            {
                PopulateLayerCombo();
            }
        }

        // ── Board / Layer combo logic ─────────────────────────────────────────

        private void BoardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PopulateLayerCombo();
            UpdateBoundsHint();
        }

        private bool IsCarrierSelected => BoardCombo.SelectedItem as string == "Carrier";

        private void PopulateLayerCombo()
        {
            LayerCombo.Items.Clear();
            var stackup = IsCarrierSelected ? _vm.CarrierBoard.Stackup : _vm.Module.Stackup;
            foreach (var layer in stackup.Layers.Where(l => l.IsConductive))
                LayerCombo.Items.Add(layer);
            if (LayerCombo.Items.Count > 0)
                LayerCombo.SelectedIndex = 0;
            UpdateBoundsHint();
        }

        private void UpdateBoundsHint()
        {
            double w, h;
            if (IsCarrierSelected) { w = _vm.CarrierBoard.Width; h = _vm.CarrierBoard.Height; }
            else                   { w = _vm.Module.Width;       h = _vm.Module.Height;       }
            BoundsHint.Text = $"Board bounds – X: [{-w:F1}, 0]   Y: [{-h / 2:F1}, {h / 2:F1}]  (mm)";
        }

        // ── Vertex editor buttons ─────────────────────────────────────────────

        private void AddVertex_Click(object sender, RoutedEventArgs e)
        {
            VertexGrid.CommitEdit(DataGridEditingUnit.Row, true);
            int sel = VertexGrid.SelectedIndex;
            if (sel >= 0 && sel < _vertices.Count - 1)
            {
                // Insert after selected row
                _vertices.Insert(sel + 1, new ShapeVertex(0, 0));
                VertexGrid.SelectedIndex = sel + 1;
            }
            else
            {
                // Nothing selected or last row selected → append
                _vertices.Add(new ShapeVertex(0, 0));
                VertexGrid.SelectedIndex = _vertices.Count - 1;
            }
            VertexGrid.ScrollIntoView(VertexGrid.SelectedItem);
            VertexGrid.Focus();
            RefreshPreview();
        }

        private void DeleteVertex_Click(object sender, RoutedEventArgs e)
        {
            if (VertexGrid.SelectedItem is ShapeVertex sv)
                _vertices.Remove(sv);
            RefreshPreview();
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            VertexGrid.CommitEdit(DataGridEditingUnit.Row, true);
            int i = VertexGrid.SelectedIndex;
            if (i <= 0 || i >= _vertices.Count) return;
            _vertices.Move(i, i - 1);
            VertexGrid.SelectedIndex = i - 1;
            VertexGrid.ScrollIntoView(VertexGrid.SelectedItem);
            VertexGrid.Focus();
            RefreshPreview();
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            VertexGrid.CommitEdit(DataGridEditingUnit.Row, true);
            int i = VertexGrid.SelectedIndex;
            if (i < 0 || i >= _vertices.Count - 1) return;
            _vertices.Move(i, i + 1);
            VertexGrid.SelectedIndex = i + 1;
            VertexGrid.ScrollIntoView(VertexGrid.SelectedItem);
            VertexGrid.Focus();
            RefreshPreview();
        }

        private void ClosePolygon_Click(object sender, RoutedEventArgs e)
        {
            if (_vertices.Count < 3)
            {
                SetStatus("Need at least 3 vertices to close.", ok: false);
                return;
            }
            var first = _vertices[0];
            var last  = _vertices[_vertices.Count - 1];
            if (Math.Abs(first.X - last.X) < 1e-4 && Math.Abs(first.Y - last.Y) < 1e-4)
            {
                SetStatus("Polygon is already closed.", ok: true);
                return;
            }
            _vertices.Add(new ShapeVertex(first.X, first.Y));
            SetStatus("Closing vertex added.", ok: true);
            RefreshPreview();
        }

        // ── DataGrid cell edit ────────────────────────────────────────────────

        private void VertexGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            // Defer so the binding commits before preview reads the values
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(RefreshPreview));
        }

        // ── Validate ──────────────────────────────────────────────────────────

        private void ValidateButton_Click(object sender, RoutedEventArgs e)
        {
            CommitCurrentEdit();
            var shape = BuildShapeFromUI();
            if (shape.Validate(out string err))
                SetStatus(err, ok: true);
            else
                SetStatus(err, ok: false);
        }

        // ── OK / Cancel ───────────────────────────────────────────────────────

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            CommitCurrentEdit();
            var shape = BuildShapeFromUI();

            if (!shape.Validate(out string err))
            {
                SetStatus(err, ok: false);
                return;   // stay open so user can fix
            }

            Result = shape;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Result = null;
            DialogResult = false;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private ManualShape BuildShapeFromUI()
        {
            var shape = new ManualShape
            {
                Name      = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "Shape",
                IsCarrier = IsCarrierSelected,
                LayerName = (LayerCombo.SelectedItem as Layer)?.Name ?? "",
                ShowIn3D  = true
            };
            foreach (var v in _vertices)
                shape.Vertices.Add(new ShapeVertex(v.X, v.Y));
            return shape;
        }

        private void CommitCurrentEdit()
        {
            if (VertexGrid.CurrentCell.Item != null)
            {
                VertexGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
                VertexGrid.CommitEdit(DataGridEditingUnit.Row,  exitEditingMode: true);
            }
        }

        private void SetStatus(string msg, bool ok)
        {
            StatusText.Text       = msg;
            StatusText.Foreground = ok ? Brushes.DarkGreen : Brushes.DarkRed;
        }

        // ── 2D preview ────────────────────────────────────────────────────────

        private void PreviewCanvas2D_SizeChanged(object sender, SizeChangedEventArgs e)
            => DrawPreview2D();

        private void RefreshPreview()
        {
            DrawPreview2D();
            UpdateVertexSummary();
        }

        private void DrawPreview2D()
        {
            PreviewCanvas2D.Children.Clear();
            if (_vertices.Count < 2) return;

            double cw = PreviewCanvas2D.ActualWidth;
            double ch = PreviewCanvas2D.ActualHeight;
            if (cw < 10 || ch < 10) return;

            double minX = _vertices.Min(v => v.X), maxX = _vertices.Max(v => v.X);
            double minY = _vertices.Min(v => v.Y), maxY = _vertices.Max(v => v.Y);
            // Include origin in bounds
            minX = Math.Min(minX, 0); maxX = Math.Max(maxX, 0);
            minY = Math.Min(minY, 0); maxY = Math.Max(maxY, 0);
            double rngX = maxX - minX, rngY = maxY - minY;
            if (rngX < 1e-6) rngX = 10;
            if (rngY < 1e-6) rngY = 10;

            const double Mg = 18;
            double scaleX = (cw - 2 * Mg) / rngX;
            double scaleY = (ch - 2 * Mg) / rngY;
            double scale  = Math.Min(scaleX, scaleY);
            double offX   = Mg + (cw - 2 * Mg - rngX * scale) / 2;
            double offY   = Mg + (ch - 2 * Mg - rngY * scale) / 2;

            // Flip Y so +Y is up (on rotated coordinates)
            double Tx(double rx) => (rx - minX) * scale + offX;
            double Ty(double ry) => ch - ((ry - minY) * scale + offY);

            // Filled polygon
            var poly = new Polygon
            {
                Stroke          = new SolidColorBrush(Color.FromRgb(0x20, 0x60, 0xCC)),
                StrokeThickness = 1.5,
                Fill            = new SolidColorBrush(Color.FromArgb(55, 0x20, 0x60, 0xCC))
            };
            foreach (var v in _vertices)
                poly.Points.Add(new System.Windows.Point(Tx(v.X), Ty(v.Y)));
            PreviewCanvas2D.Children.Add(poly);

            // Vertex dots + index labels
            for (int i = 0; i < _vertices.Count; i++)
            {
                double px = Tx(_vertices[i].X);
                double py = Ty(_vertices[i].Y);

                bool isFirst = i == 0;
                bool isLast  = i == _vertices.Count - 1;
                bool isClosed = isLast && _vertices.Count > 1
                    && Math.Abs(_vertices[0].X - _vertices[i].X) < 1e-4
                    && Math.Abs(_vertices[0].Y - _vertices[i].Y) < 1e-4;

                var dot = new Ellipse
                {
                    Width  = isFirst ? 9 : 6,
                    Height = isFirst ? 9 : 6,
                    Fill   = isClosed ? Brushes.Green :
                             isFirst  ? Brushes.DarkBlue : Brushes.CornflowerBlue
                };
                Canvas.SetLeft(dot, px - dot.Width / 2);
                Canvas.SetTop(dot,  py - dot.Height / 2);
                PreviewCanvas2D.Children.Add(dot);

                if (!isClosed)
                {
                    var lbl = new TextBlock
                    {
                        Text     = i.ToString(),
                        FontSize = 9,
                        Foreground = Brushes.DimGray
                    };
                    Canvas.SetLeft(lbl, px + 4);
                    Canvas.SetTop(lbl,  py - 7);
                    PreviewCanvas2D.Children.Add(lbl);
                }
            }

            // Axis cross at origin (0,0) – always visible since origin is included in bounds
            {
                double ox = Tx(0), oy = Ty(0);
                var hLine = new Line { X1=0, X2=cw, Y1=oy, Y2=oy,
                    Stroke=Brushes.LightGray, StrokeThickness=0.8,
                    StrokeDashArray=new DoubleCollection(new[]{4.0,3.0}) };
                var vLine = new Line { X1=ox, X2=ox, Y1=0, Y2=ch,
                    Stroke=Brushes.LightGray, StrokeThickness=0.8,
                    StrokeDashArray=new DoubleCollection(new[]{4.0,3.0}) };
                PreviewCanvas2D.Children.Insert(0, hLine);
                PreviewCanvas2D.Children.Insert(0, vLine);
                var orig = new TextBlock { Text="(0,0)", FontSize=9, Foreground=Brushes.Gray };
                Canvas.SetLeft(orig, ox+2); Canvas.SetTop(orig, oy+1);
                PreviewCanvas2D.Children.Add(orig);
            }

            // Axis indicator (bottom-left corner)
            DrawAxisIndicator(cw, ch);
        }

        /// <summary>Draws a small X/Y axis indicator in the bottom-left corner of the preview canvas.</summary>
        private void DrawAxisIndicator(double cw, double ch)
        {
            if (cw < 60 || ch < 60) return;

            const double len = 32;
            const double margin = 14;
            double oX = margin;
            double oY = ch - margin;

            var axisBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            const double thick = 1.2;
            const double arrowSize = 5;

            // X axis: origin → right
            PreviewCanvas2D.Children.Add(new Line { X1 = oX, Y1 = oY, X2 = oX + len, Y2 = oY, Stroke = axisBrush, StrokeThickness = thick });
            PreviewCanvas2D.Children.Add(new Line { X1 = oX + len, Y1 = oY, X2 = oX + len - arrowSize, Y2 = oY - arrowSize * 0.6, Stroke = axisBrush, StrokeThickness = thick });
            PreviewCanvas2D.Children.Add(new Line { X1 = oX + len, Y1 = oY, X2 = oX + len - arrowSize, Y2 = oY + arrowSize * 0.6, Stroke = axisBrush, StrokeThickness = thick });
            var xLbl = new TextBlock { Text = "X", FontSize = 11, Foreground = axisBrush, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(xLbl, oX + len + 2); Canvas.SetTop(xLbl, oY - 7);
            PreviewCanvas2D.Children.Add(xLbl);

            // Y axis: origin → up
            PreviewCanvas2D.Children.Add(new Line { X1 = oX, Y1 = oY, X2 = oX, Y2 = oY - len, Stroke = axisBrush, StrokeThickness = thick });
            PreviewCanvas2D.Children.Add(new Line { X1 = oX, Y1 = oY - len, X2 = oX - arrowSize * 0.6, Y2 = oY - len + arrowSize, Stroke = axisBrush, StrokeThickness = thick });
            PreviewCanvas2D.Children.Add(new Line { X1 = oX, Y1 = oY - len, X2 = oX + arrowSize * 0.6, Y2 = oY - len + arrowSize, Stroke = axisBrush, StrokeThickness = thick });
            var yLbl = new TextBlock { Text = "Y", FontSize = 11, Foreground = axisBrush, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(yLbl, oX - 3); Canvas.SetTop(yLbl, oY - len - 15);
            PreviewCanvas2D.Children.Add(yLbl);
        }

        private void UpdateVertexSummary()
        {
            VertexSummaryList.Items.Clear();
            for (int i = 0; i < _vertices.Count; i++)
                VertexSummaryList.Items.Add($"[{i}]  ({_vertices[i].X:F3},  {_vertices[i].Y:F3})");
        }

        // ── Save / Open ───────────────────────────────────────────────────────

        private class CopperShapeFile
        {
            public string Name { get; set; } = "";
            public string Board { get; set; } = "Carrier";
            public string LayerName { get; set; } = "";
            public List<double[]>? Vertices { get; set; }
        }

        private void SaveShape_Click(object sender, RoutedEventArgs e)
        {
            CommitCurrentEdit();

            var data = new CopperShapeFile
            {
                Name      = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "Shape",
                Board     = IsCarrierSelected ? "Carrier" : "Module",
                LayerName = (LayerCombo.SelectedItem as Layer)?.Name ?? "",
                Vertices  = _vertices.Select(v => new double[] { v.X, v.Y }).ToList()
            };

            var dlg = new SaveFileDialog
            {
                Title      = "Save Copper Shape",
                Filter     = "Copper Shape (*.coppershape)|*.coppershape|All files (*.*)|*.*",
                DefaultExt = ".coppershape",
                FileName   = data.Name
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dlg.FileName, json);
                SetStatus($"Saved → {System.IO.Path.GetFileName(dlg.FileName)}", ok: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenShape_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Open Copper Shape",
                Filter = "Copper Shape (*.coppershape)|*.coppershape|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var json = File.ReadAllText(dlg.FileName);
                var data = JsonSerializer.Deserialize<CopperShapeFile>(json);
                if (data == null) { SetStatus("File is empty or invalid.", ok: false); return; }

                // Apply name
                NameBox.Text = data.Name;

                // Apply board selection
                if (data.Board == "Module" && BoardCombo.Items.Contains("Module"))
                    BoardCombo.SelectedItem = "Module";
                else
                    BoardCombo.SelectedItem = "Carrier";

                PopulateLayerCombo();

                // Try to select the saved layer
                if (!string.IsNullOrEmpty(data.LayerName))
                {
                    var match = LayerCombo.Items.Cast<Layer>().FirstOrDefault(l => l.Name == data.LayerName);
                    if (match != null) LayerCombo.SelectedItem = match;
                }

                // Load vertices
                _vertices.Clear();
                if (data.Vertices != null)
                {
                    foreach (var v in data.Vertices)
                    {
                        if (v.Length >= 2)
                            _vertices.Add(new ShapeVertex(v[0], v[1]));
                    }
                }

                RefreshPreview();
                SetStatus($"Loaded ← {System.IO.Path.GetFileName(dlg.FileName)}", ok: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Open failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            RefreshPreview();
        }
    }
}
