using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AntennaSimulatorApp.Models;
using AntennaSimulatorApp.ViewModels;
using Microsoft.Win32;

namespace AntennaSimulatorApp.Views
{
    // ── Value converter for DataGrid "Board" column ──────────────────────────

    public sealed class ViaWindow_BoardConverter : IValueConverter
    {
        public static readonly ViaWindow_BoardConverter Instance = new();
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value is bool b && b ? "Carrier" : "Module";
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => throw new NotSupportedException();
    }

    // ── Window ───────────────────────────────────────────────────────────────

    public partial class DrawViaWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly ObservableCollection<Via> _vias;
        private bool _suppressUpdate;
        private List<Via> _clipboardVias = new List<Via>();

        // ── View state (world-mm coordinates of visible area) ────────────────
        private double _viewCenterX, _viewCenterY;  // centre of view in mm
        private double _viewRange = 0;               // half-width of visible range in mm (0 = auto-fit)
        private const double ZoomFactor = 1.3;
        private const double PanStepFraction = 0.15;  // fraction of visible range per pan step

        public DrawViaWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;

            // Clone the existing vias so we can cancel
            _vias = new ObservableCollection<Via>();
            foreach (var v in vm.Vias)
            {
                _vias.Add(new Via
                {
                    Name        = v.Name,
                    IsCarrier   = v.IsCarrier,
                    FromLayer   = v.FromLayer,
                    ToLayer     = v.ToLayer,
                    DiameterMil = v.DiameterMil,
                    X           = v.X,
                    Y           = v.Y,
                });
            }

            ViaGrid.ItemsSource = _vias;
            _vias.CollectionChanged += (_, __) => RefreshPreview();

            // Populate board combo
            BoardCombo.Items.Add("Carrier");
            if (vm.HasModule)
                BoardCombo.Items.Add("Module");

            EditorPanel.IsEnabled = false;

            if (_vias.Count > 0)
                ViaGrid.SelectedIndex = 0;

            ResetView();
            RefreshPreview();
        }

        /// <summary>Compute auto-fit view bounds and reset to them.</summary>
        private void ResetView()
        {
            ComputeContentBounds(out double minX, out double maxX, out double minY, out double maxY);
            double margin = 3.0;
            _viewCenterX = (minX + maxX) / 2;
            _viewCenterY = (minY + maxY) / 2;
            _viewRange = Math.Max((maxX - minX) / 2 + margin, (maxY - minY) / 2 + margin);
            if (_viewRange < 1) _viewRange = 10;
        }

        // ── Add / Delete ──────────────────────────────────────────────────────

        private void AddVia_Click(object sender, RoutedEventArgs e)
        {
            int idx = _vias.Count + 1;
            var via = new Via { Name = $"Via{idx}" };

            // Default layers from carrier
            var layers = _vm.CarrierBoard.Stackup.Layers.Where(l => l.IsConductive).ToList();
            if (layers.Count >= 1) via.FromLayer = layers.First().Name;
            if (layers.Count >= 2) via.ToLayer = layers.Last().Name;

            _vias.Add(via);
            ViaGrid.SelectedItem = via;
            ViaGrid.ScrollIntoView(via);
        }

        private void DeleteVia_Click(object sender, RoutedEventArgs e)
        {
            if (ViaGrid.SelectedItem is Via v)
                _vias.Remove(v);
        }

        private void CopyVia_Click(object sender, RoutedEventArgs e)
        {
            var selected = ViaGrid.SelectedItems.Cast<Via>().ToList();
            if (selected.Count == 0) return;
            _clipboardVias = selected.Select(CloneVia).ToList();
        }

        private void PasteVia_Click(object sender, RoutedEventArgs e)
        {
            if (_clipboardVias.Count == 0) return;
            Via? last = null;
            foreach (var v in _clipboardVias)
            {
                last = CloneVia(v);
                last.Name = last.Name + "_copy";
                _vias.Add(last);
            }
            if (last != null)
            {
                ViaGrid.SelectedItem = last;
                ViaGrid.ScrollIntoView(last);
            }
        }

        private static Via CloneVia(Via src) => new Via
        {
            Name        = src.Name,
            IsCarrier   = src.IsCarrier,
            FromLayer   = src.FromLayer,
            ToLayer     = src.ToLayer,
            DiameterMil = src.DiameterMil,
            X           = src.X,
            Y           = src.Y,
        };

        // ── Selection changed → populate editor ──────────────────────────────

        private void ViaGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViaGrid.SelectedItem is Via v)
            {
                _suppressUpdate = true;
                EditorPanel.IsEnabled = true;

                ViaNameBox.Text = v.Name;
                BoardCombo.SelectedItem = v.IsCarrier ? "Carrier" : "Module";
                PopulateLayerCombos();

                SelectLayerByName(FromLayerCombo, v.FromLayer);
                SelectLayerByName(ToLayerCombo, v.ToLayer);

                DiameterBox.Text = v.DiameterMil.ToString("F1");
                PosXBox.Text = v.X.ToString("F2");
                PosYBox.Text = v.Y.ToString("F2");

                _suppressUpdate = false;
                UpdateBoundsHint();
            }
            else
            {
                EditorPanel.IsEnabled = false;
            }
        }

        // ── Board / Layer combos ──────────────────────────────────────────────

        private bool IsCarrierSelected => BoardCombo.SelectedItem as string == "Carrier";

        private bool IsPreviewCarrier
        {
            get
            {
                if (PreviewBoardCombo == null) return true;
                var item = PreviewBoardCombo.SelectedItem as ComboBoxItem;
                return item == null || (item.Content as string) == "Carrier";
            }
        }

        private void BoardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PopulateLayerCombos();
            ApplyEditorToSelected();
            UpdateBoundsHint();
            ResetView();
        }

        private void PopulateLayerCombos()
        {
            var stackup = IsCarrierSelected ? _vm.CarrierBoard.Stackup : _vm.Module.Stackup;
            var conductiveLayers = stackup.Layers.Where(l => l.IsConductive).ToList();

            FromLayerCombo.Items.Clear();
            ToLayerCombo.Items.Clear();
            foreach (var layer in conductiveLayers)
            {
                FromLayerCombo.Items.Add(layer);
                ToLayerCombo.Items.Add(layer);
            }

            if (FromLayerCombo.Items.Count > 0) FromLayerCombo.SelectedIndex = 0;
            if (ToLayerCombo.Items.Count > 1)   ToLayerCombo.SelectedIndex = ToLayerCombo.Items.Count - 1;
            else if (ToLayerCombo.Items.Count > 0) ToLayerCombo.SelectedIndex = 0;
        }

        private void LayerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyEditorToSelected();
        }

        private void SelectLayerByName(ComboBox combo, string name)
        {
            foreach (var item in combo.Items)
            {
                if (item is Layer l && l.Name == name)
                {
                    combo.SelectedItem = l;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private void UpdateBoundsHint()
        {
            double w, h;
            if (IsCarrierSelected) { w = _vm.CarrierBoard.Width; h = _vm.CarrierBoard.Height; }
            else                   { w = _vm.Module.Width;       h = _vm.Module.Height;       }
            BoundsHint.Text = $"Board bounds — X: [{-h / 2:F1}, {h / 2:F1}]   Y: [{-w:F1}, 0]  (mm)";
        }

        // ── Text box changes → push values to selected Via ───────────────────

        private void ViaProperty_Changed(object sender, TextChangedEventArgs e)
        {
            ApplyEditorToSelected();
        }

        private void ApplyEditorToSelected()
        {
            if (_suppressUpdate) return;
            if (ViaGrid.SelectedItem is not Via v) return;

            v.Name = ViaNameBox.Text;
            v.IsCarrier = IsCarrierSelected;

            if (FromLayerCombo.SelectedItem is Layer fl)
                v.FromLayer = fl.Name;
            if (ToLayerCombo.SelectedItem is Layer tl)
                v.ToLayer = tl.Name;

            if (double.TryParse(DiameterBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dia))
                v.DiameterMil = dia;
            if (double.TryParse(PosXBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double px))
                v.X = px;
            if (double.TryParse(PosYBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double py))
                v.Y = py;

            // Refresh the DataGrid row
            ViaGrid.Items.Refresh();
            RefreshPreview();
        }

        // ── OK / Cancel ──────────────────────────────────────────────────────

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // Commit to ViewModel
            _vm.Vias.Clear();
            foreach (var v in _vias)
                _vm.Vias.Add(v);

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ── Save / Open via list (.vialist) ──────────────────────────────────

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private void SaveViaList_Click(object sender, RoutedEventArgs e)
        {
            if (_vias.Count == 0)
            {
                MessageBox.Show("No vias to save.", "Save Via List",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "Via List (*.vialist)|*.vialist|All Files (*.*)|*.*",
                DefaultExt = ".vialist",
                Title = "Save Via List",
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var dtos = _vias.Select(v => new ViaDto
                {
                    Name = v.Name,
                    IsCarrier = v.IsCarrier,
                    FromLayer = v.FromLayer,
                    ToLayer = v.ToLayer,
                    DiameterMil = v.DiameterMil,
                    X = v.X,
                    Y = v.Y,
                }).ToList();

                string json = JsonSerializer.Serialize(dtos, _jsonOpts);
                File.WriteAllText(dlg.FileName, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save: {ex.Message}", "Save Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenViaList_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Via List (*.vialist)|*.vialist|All Files (*.*)|*.*",
                Title = "Open Via List",
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                string json = File.ReadAllText(dlg.FileName);
                var dtos = JsonSerializer.Deserialize<List<ViaDto>>(json, _jsonOpts);
                if (dtos == null || dtos.Count == 0)
                {
                    MessageBox.Show("File contains no via data.", "Open Via List",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _vias.Clear();
                foreach (var d in dtos)
                {
                    _vias.Add(new Via
                    {
                        Name = d.Name,
                        IsCarrier = d.IsCarrier,
                        FromLayer = d.FromLayer,
                        ToLayer = d.ToLayer,
                        DiameterMil = d.DiameterMil,
                        X = d.X,
                        Y = d.Y,
                    });
                }

                if (_vias.Count > 0)
                    ViaGrid.SelectedIndex = 0;

                ResetView();
                RefreshPreview();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open: {ex.Message}", "Open Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── 2D Preview drawing ───────────────────────────────────────────────

        /// <summary>Gather world-mm bounding box of all content.</summary>
        // No rotation — coordinates map directly
        private static double RotX(double x, double y) => x;
        private static double RotY(double x, double y) => y;

        private void ComputeContentBounds(out double minX, out double maxX, out double minY, out double maxY)
        {
            var board = IsPreviewCarrier ? _vm.CarrierBoard : _vm.Module;
            double boardW = board.Width;
            double boardH = board.Height;
            // Board faces Y+ direction: X∈[-Height/2, Height/2], Y∈[-Width, 0]
            double[] bxs = { -boardH / 2, boardH / 2, -boardH / 2, boardH / 2 };
            double[] bys = { -boardW, -boardW, 0, 0 };
            minX = bxs.Min(); maxX = bxs.Max();
            minY = bys.Min(); maxY = bys.Max();

            bool previewCarrier = IsPreviewCarrier;

            foreach (var v in _vias.Where(v => v.IsCarrier == previewCarrier))
            {
                double r = v.DiameterMm / 2;
                double rx = RotX(v.X, v.Y), ry = RotY(v.X, v.Y);
                minX = Math.Min(minX, rx - r); maxX = Math.Max(maxX, rx + r);
                minY = Math.Min(minY, ry - r); maxY = Math.Max(maxY, ry + r);
            }

            foreach (var ms in _vm.ManualShapes)
            {
                if (!ms.ShowIn3D) continue;
                if (ms.IsCarrier != previewCarrier) continue;
                var gd = ms.ToGerberData();
                foreach (var s in gd.Shapes)
                {
                    if (s.IsClear) continue;
                    foreach (var pt in s.Points)
                    {
                        double rx = RotX(pt.X, pt.Y), ry = RotY(pt.X, pt.Y);
                        minX = Math.Min(minX, rx); maxX = Math.Max(maxX, rx);
                        minY = Math.Min(minY, ry); maxY = Math.Max(maxY, ry);
                    }
                }
            }
        }

        private void RefreshPreview()
        {
            PreviewCanvas.Children.Clear();

            double cw = PreviewCanvas.ActualWidth;
            double ch = PreviewCanvas.ActualHeight;
            if (cw < 10 || ch < 10)
            {
                Dispatcher.InvokeAsync(RefreshPreview,
                    System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            // ── Margin (pixels) ───────
            const double mg = 10;

            double drawW = cw - 2 * mg;
            double drawH = ch - 2 * mg;
            if (drawW < 20 || drawH < 20) return;

            // ── Compute scale from view state ───────
            double viewHalfX = _viewRange;
            double viewHalfY = _viewRange * (drawH / drawW);
            // Maintain aspect ratio: use uniform scale
            double scaleX = drawW / (2 * viewHalfX);
            double scaleY = drawH / (2 * viewHalfY);
            double scale = Math.Min(scaleX, scaleY);

            // World bounds visible
            double wMinX = _viewCenterX - drawW / (2 * scale);
            double wMaxX = _viewCenterX + drawW / (2 * scale);
            double wMinY = _viewCenterY - drawH / (2 * scale);
            double wMaxY = _viewCenterY + drawH / (2 * scale);

            // Transforms: world (mm) → canvas pixel
            double Tx(double wx) => mg + (wx - wMinX) * scale;
            double Ty(double wy) => mg + drawH - (wy - wMinY) * scale; // flip Y

            // ── Collect shape polygons (rotated) ───────
            var previewBoard = IsPreviewCarrier ? _vm.CarrierBoard : _vm.Module;
            double boardW = previewBoard.Width;
            double boardH = previewBoard.Height;

            bool previewCarrier = IsPreviewCarrier;
            var copperPolygons = new List<List<(double X, double Y)>>();
            var antennaPolygons = new List<List<(double X, double Y)>>();
            foreach (var ms in _vm.ManualShapes)
            {
                if (!ms.ShowIn3D) continue;
                if (ms.IsCarrier != previewCarrier) continue;
                var gd = ms.ToGerberData();
                bool isAntenna = ms.Name != null && ms.Name.StartsWith("Antenna (");
                foreach (var s in gd.Shapes)
                {
                    if (s.IsClear || s.Points.Count < 3) continue;
                    var rotated = s.Points.Select(p => (X: RotX(p.X, p.Y), Y: RotY(p.X, p.Y))).ToList();
                    (isAntenna ? antennaPolygons : copperPolygons).Add(rotated);
                }
            }

            // ── Draw board outline ───────
            // Board faces Y+ direction: X∈[-Height/2, Height/2], Y∈[-Width, 0]
            double[] bcx = { -boardH / 2, boardH / 2, -boardH / 2, boardH / 2 };
            double[] bcy = { -boardW, -boardW, 0, 0 };
            double bMinX = bcx.Min(), bMaxX = bcx.Max();
            double bMinY = bcy.Min(), bMaxY = bcy.Max();
            var boardRect = new Rectangle
            {
                Width  = (bMaxX - bMinX) * scale,
                Height = (bMaxY - bMinY) * scale,
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(20, 200, 200, 200)),
            };
            Canvas.SetLeft(boardRect, Tx(bMinX));
            Canvas.SetTop(boardRect,  Ty(bMaxY));
            PreviewCanvas.Children.Add(boardRect);

            // ── Draw copper shapes ───────
            foreach (var poly in copperPolygons)
                DrawPolygon(poly, Tx, Ty, new SolidColorBrush(Color.FromArgb(60, 0, 160, 0)), Brushes.DarkGreen, 0.8);

            // ── Draw antenna traces ───────
            foreach (var poly in antennaPolygons)
                DrawPolygon(poly, Tx, Ty, new SolidColorBrush(Color.FromArgb(70, 30, 80, 220)), Brushes.Blue, 0.8);

            // ── Draw origin cross + label ───────
            double oxPx = Tx(0), oyPx = Ty(0);
            if (oxPx > mg - 2 && oxPx < cw - mg + 2 &&
                oyPx > mg - 2 && oyPx < ch - mg + 2)
            {
                double crossLen = Math.Min(15, Math.Min(drawW, drawH) * 0.06);
                PreviewCanvas.Children.Add(new Line { X1 = oxPx - crossLen, Y1 = oyPx, X2 = oxPx + crossLen, Y2 = oyPx, Stroke = Brushes.Red, StrokeThickness = 1 });
                PreviewCanvas.Children.Add(new Line { X1 = oxPx, Y1 = oyPx - crossLen, X2 = oxPx, Y2 = oyPx + crossLen, Stroke = Brushes.Red, StrokeThickness = 1 });
                var originLbl = new TextBlock { Text = "(0, 0)", FontSize = 9, Foreground = Brushes.Red, FontWeight = FontWeights.Bold };
                Canvas.SetLeft(originLbl, oxPx + 4);
                Canvas.SetTop(originLbl, oyPx + 2);
                PreviewCanvas.Children.Add(originLbl);
            }

            // ── Draw each via ───────
            var selected = ViaGrid.SelectedItem as Via;
            foreach (var v in _vias.Where(v => v.IsCarrier == previewCarrier))
            {
                double rPx = (v.DiameterMm / 2) * scale;
                if (rPx < 2) rPx = 2;

                double cx = Tx(RotX(v.X, v.Y));
                double cy = Ty(RotY(v.X, v.Y));
                bool isSel = v == selected;

                var circle = new Ellipse
                {
                    Width  = rPx * 2, Height = rPx * 2,
                    Stroke = isSel ? Brushes.Red : Brushes.DarkGoldenrod,
                    StrokeThickness = isSel ? 2 : 1.2,
                    Fill = new SolidColorBrush(Color.FromArgb(80, 218, 165, 32)),
                };
                Canvas.SetLeft(circle, cx - rPx);
                Canvas.SetTop(circle,  cy - rPx);
                PreviewCanvas.Children.Add(circle);

                double cr = rPx * 0.5;
                PreviewCanvas.Children.Add(new Line { X1 = cx - cr, Y1 = cy, X2 = cx + cr, Y2 = cy, Stroke = Brushes.DarkRed, StrokeThickness = 0.8 });
                PreviewCanvas.Children.Add(new Line { X1 = cx, Y1 = cy - cr, X2 = cx, Y2 = cy + cr, Stroke = Brushes.DarkRed, StrokeThickness = 0.8 });

                var label = new TextBlock { Text = v.Name, FontSize = 10, Foreground = isSel ? Brushes.Red : Brushes.Black };
                Canvas.SetLeft(label, cx + rPx + 3);
                Canvas.SetTop(label,  cy - 7);
                PreviewCanvas.Children.Add(label);
            }

            // ── Legend ───────
            double legendY = mg + 2;
            double legendX = cw - 140;
            AddLegendItem(legendX, ref legendY, new SolidColorBrush(Color.FromArgb(60, 0, 160, 0)), "Copper Shape");
            AddLegendItem(legendX, ref legendY, new SolidColorBrush(Color.FromArgb(70, 30, 80, 220)), "Antenna");
            AddLegendItem(legendX, ref legendY, new SolidColorBrush(Color.FromArgb(80, 218, 165, 32)), "Via");

            // ── Axis indicator (bottom-left corner) ───────
            DrawAxisIndicator(cw, ch);
        }

        // ── Zoom / Pan controls ──────────────────────────────────────────────

        private void ZoomIn_Click(object sender, RoutedEventArgs e)  { _viewRange /= ZoomFactor; RefreshPreview(); }
        private void ZoomOut_Click(object sender, RoutedEventArgs e) { _viewRange *= ZoomFactor; RefreshPreview(); }

        private void PanLeft_Click(object sender, RoutedEventArgs e)  { _viewCenterX -= _viewRange * PanStepFraction; RefreshPreview(); }
        private void PanRight_Click(object sender, RoutedEventArgs e) { _viewCenterX += _viewRange * PanStepFraction; RefreshPreview(); }
        private void PanUp_Click(object sender, RoutedEventArgs e)    { _viewCenterY += _viewRange * PanStepFraction; RefreshPreview(); }
        private void PanDown_Click(object sender, RoutedEventArgs e)  { _viewCenterY -= _viewRange * PanStepFraction; RefreshPreview(); }

        private void FitView_Click(object sender, RoutedEventArgs e) { ResetView(); RefreshPreview(); }

        private void PreviewBoardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_vm == null) return;  // not yet initialized
            ResetView();
            RefreshPreview();
        }

        private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshPreview();

        private void PreviewCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (shift)
            {
                // Shift + wheel → horizontal pan
                double step = _viewRange * PanStepFraction;
                _viewCenterX += (e.Delta > 0) ? step : -step;
            }
            else if (ctrl)
            {
                // Ctrl + wheel → vertical pan
                double step = _viewRange * PanStepFraction;
                _viewCenterY += (e.Delta > 0) ? step : -step;
            }
            else
            {
                // Plain wheel → zoom centered on mouse
                var pos = e.GetPosition(PreviewCanvas);
                const double mg = 10;
                double cw = PreviewCanvas.ActualWidth;
                double ch = PreviewCanvas.ActualHeight;
                double drawW = Math.Max(cw - 2 * mg, 20);
                double drawH = Math.Max(ch - 2 * mg, 20);
                double scale = drawW / (2 * _viewRange);

                // World coordinate under mouse
                double wx = _viewCenterX + (pos.X - mg - drawW / 2) / scale;
                double wy = _viewCenterY + (mg + drawH / 2 - pos.Y) / scale;

                // Apply zoom
                if (e.Delta > 0) _viewRange /= ZoomFactor;
                else             _viewRange *= ZoomFactor;

                // Adjust center so world point stays at same pixel
                double newScale = drawW / (2 * _viewRange);
                _viewCenterX = wx - (pos.X - mg - drawW / 2) / newScale;
                _viewCenterY = wy - (mg + drawH / 2 - pos.Y) / newScale;
            }

            RefreshPreview();
            e.Handled = true;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+C / Ctrl+V work regardless of focus
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.C) { CopyVia_Click(this, e); e.Handled = true; return; }
                if (e.Key == Key.V) { PasteVia_Click(this, e); e.Handled = true; return; }
            }

            // Don't intercept keys when a TextBox or ComboBox has focus
            if (e.OriginalSource is TextBox || e.OriginalSource is ComboBox) return;

            switch (e.Key)
            {
                case Key.Left:  _viewCenterX -= _viewRange * PanStepFraction; RefreshPreview(); e.Handled = true; break;
                case Key.Right: _viewCenterX += _viewRange * PanStepFraction; RefreshPreview(); e.Handled = true; break;
                case Key.Up:    _viewCenterY += _viewRange * PanStepFraction; RefreshPreview(); e.Handled = true; break;
                case Key.Down:  _viewCenterY -= _viewRange * PanStepFraction; RefreshPreview(); e.Handled = true; break;
            }
        }

        /// <summary>Draw a filled polygon on the preview canvas.</summary>
        private void DrawPolygon(List<(double X, double Y)> pts,
            Func<double, double> Tx, Func<double, double> Ty,
            Brush fill, Brush stroke, double strokeThickness)
        {
            if (pts.Count < 3) return;
            var polygon = new Polygon
            {
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = strokeThickness,
            };
            foreach (var pt in pts)
                polygon.Points.Add(new Point(Tx(pt.X), Ty(pt.Y)));
            PreviewCanvas.Children.Add(polygon);
        }

        /// <summary>Add a small colored square + label to the legend.</summary>
        private void AddLegendItem(double x, ref double y, Brush color, string text)
        {
            var rect = new Rectangle { Width = 10, Height = 10, Fill = color, Stroke = Brushes.Gray, StrokeThickness = 0.5 };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            PreviewCanvas.Children.Add(rect);

            var tb = new TextBlock { Text = text, FontSize = 9, Foreground = Brushes.DimGray };
            Canvas.SetLeft(tb, x + 14);
            Canvas.SetTop(tb, y - 1);
            PreviewCanvas.Children.Add(tb);

            y += 16;
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
            PreviewCanvas.Children.Add(new Line { X1 = oX, Y1 = oY, X2 = oX + len, Y2 = oY, Stroke = axisBrush, StrokeThickness = thick });
            PreviewCanvas.Children.Add(new Line { X1 = oX + len, Y1 = oY, X2 = oX + len - arrowSize, Y2 = oY - arrowSize * 0.6, Stroke = axisBrush, StrokeThickness = thick });
            PreviewCanvas.Children.Add(new Line { X1 = oX + len, Y1 = oY, X2 = oX + len - arrowSize, Y2 = oY + arrowSize * 0.6, Stroke = axisBrush, StrokeThickness = thick });
            var xLbl = new TextBlock { Text = "X", FontSize = 11, Foreground = axisBrush, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(xLbl, oX + len + 2); Canvas.SetTop(xLbl, oY - 7);
            PreviewCanvas.Children.Add(xLbl);

            // Y axis: origin → up
            PreviewCanvas.Children.Add(new Line { X1 = oX, Y1 = oY, X2 = oX, Y2 = oY - len, Stroke = axisBrush, StrokeThickness = thick });
            PreviewCanvas.Children.Add(new Line { X1 = oX, Y1 = oY - len, X2 = oX - arrowSize * 0.6, Y2 = oY - len + arrowSize, Stroke = axisBrush, StrokeThickness = thick });
            PreviewCanvas.Children.Add(new Line { X1 = oX, Y1 = oY - len, X2 = oX + arrowSize * 0.6, Y2 = oY - len + arrowSize, Stroke = axisBrush, StrokeThickness = thick });
            var yLbl = new TextBlock { Text = "Y", FontSize = 11, Foreground = axisBrush, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(yLbl, oX - 3); Canvas.SetTop(yLbl, oY - len - 15);
            PreviewCanvas.Children.Add(yLbl);
        }

        private void DrawEmptyHint(double cw, double ch)
        {
            var hint = new TextBlock
            {
                Text = "Click '＋ Add Via' to create a via",
                FontSize = 13,
                Foreground = Brushes.Gray,
                FontStyle = FontStyles.Italic,
            };
            Canvas.SetLeft(hint, cw / 2 - 110);
            Canvas.SetTop(hint,  ch / 2 - 10);
            PreviewCanvas.Children.Add(hint);
        }
    }
}
