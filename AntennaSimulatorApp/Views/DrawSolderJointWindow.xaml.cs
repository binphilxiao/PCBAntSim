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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AntennaSimulatorApp.Models;
using AntennaSimulatorApp.ViewModels;
using Microsoft.Win32;

namespace AntennaSimulatorApp.Views
{
    public partial class DrawSolderJointWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly ObservableCollection<SolderJoint> _items;
        private bool _suppressUpdate;
        private List<SolderJoint> _clipboardItems = new List<SolderJoint>();

        // ── View state (world-mm coordinates of visible area) ────────────────
        private double _viewCenterX, _viewCenterY;
        private double _viewRange = 0;
        private const double ZoomFactor = 1.3;
        private const double PanStepFraction = 0.15;

        public DrawSolderJointWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;

            // Clone the existing solder joints so we can cancel
            _items = new ObservableCollection<SolderJoint>();
            foreach (var sj in vm.SolderJoints)
            {
                _items.Add(CloneItem(sj));
            }

            ItemGrid.ItemsSource = _items;
            _items.CollectionChanged += (_, __) => RefreshPreview();

            EditorPanel.IsEnabled = false;

            if (_items.Count > 0)
                ItemGrid.SelectedIndex = 0;

            UpdateBoundsHint();
            ResetView();
            RefreshPreview();
        }

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

        private void AddItem_Click(object sender, RoutedEventArgs e)
        {
            int idx = _items.Count + 1;
            var sj = new SolderJoint { Name = $"SJ{idx}" };
            _items.Add(sj);
            ItemGrid.SelectedItem = sj;
            ItemGrid.ScrollIntoView(sj);
        }

        private void DeleteItem_Click(object sender, RoutedEventArgs e)
        {
            if (ItemGrid.SelectedItem is SolderJoint sj)
                _items.Remove(sj);
        }

        private void CopyItem_Click(object sender, RoutedEventArgs e)
        {
            var selected = ItemGrid.SelectedItems.Cast<SolderJoint>().ToList();
            if (selected.Count == 0) return;
            _clipboardItems = selected.Select(CloneItem).ToList();
        }

        private void PasteItem_Click(object sender, RoutedEventArgs e)
        {
            if (_clipboardItems.Count == 0) return;
            SolderJoint? last = null;
            foreach (var sj in _clipboardItems)
            {
                last = CloneItem(sj);
                last.Name = last.Name + "_copy";
                _items.Add(last);
            }
            if (last != null)
            {
                ItemGrid.SelectedItem = last;
                ItemGrid.ScrollIntoView(last);
            }
        }

        private static SolderJoint CloneItem(SolderJoint src) => new SolderJoint
        {
            Name        = src.Name,
            DiameterMil = src.DiameterMil,
            X           = src.X,
            Y           = src.Y,
        };

        // ── Selection changed → populate editor ──────────────────────────────

        private void ItemGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ItemGrid.SelectedItem is SolderJoint sj)
            {
                _suppressUpdate = true;
                EditorPanel.IsEnabled = true;

                NameBox.Text     = sj.Name;
                DiameterBox.Text = sj.DiameterMil.ToString("F1");
                PosXBox.Text     = sj.X.ToString("F2");
                PosYBox.Text     = sj.Y.ToString("F2");

                _suppressUpdate = false;
                UpdateBoundsHint();
            }
            else
            {
                EditorPanel.IsEnabled = false;
            }
        }

        private void UpdateBoundsHint()
        {
            // Solder joints sit in the overlap area between module and carrier.
            // Show module bounds as reference since they define the connection area.
            double w = _vm.Module.Width;
            double h = _vm.Module.Height;
            BoundsHint.Text = $"Module bounds — X: [{-h / 2:F1}, {h / 2:F1}]   Y: [{-w:F1}, 0]  (mm)\n" +
                              $"Position is relative to module origin.";
        }

        // ── Text box changes → push values to selected item ──────────────────

        private void Property_Changed(object sender, TextChangedEventArgs e)
        {
            ApplyEditorToSelected();
        }

        private void ApplyEditorToSelected()
        {
            if (_suppressUpdate) return;
            if (ItemGrid.SelectedItem is not SolderJoint sj) return;

            sj.Name = NameBox.Text;

            if (double.TryParse(DiameterBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dia))
                sj.DiameterMil = dia;
            if (double.TryParse(PosXBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double px))
                sj.X = px;
            if (double.TryParse(PosYBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double py))
                sj.Y = py;

            ItemGrid.Items.Refresh();
            RefreshPreview();
        }

        // ── OK / Cancel ──────────────────────────────────────────────────────

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            _vm.SolderJoints.Clear();
            foreach (var sj in _items)
                _vm.SolderJoints.Add(sj);

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ── Save / Open (.sjlist) ────────────────────────────────────────────

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private void SaveList_Click(object sender, RoutedEventArgs e)
        {
            if (_items.Count == 0)
            {
                MessageBox.Show("No solder joints to save.", "Save",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "Solder Joint List (*.sjlist)|*.sjlist|All Files (*.*)|*.*",
                DefaultExt = ".sjlist",
                Title = "Save Solder Joint List",
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var dtos = _items.Select(sj => new SolderJointDto
                {
                    Name        = sj.Name,
                    DiameterMil = sj.DiameterMil,
                    X           = sj.X,
                    Y           = sj.Y,
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

        private void OpenList_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Solder Joint List (*.sjlist)|*.sjlist|All Files (*.*)|*.*",
                Title = "Open Solder Joint List",
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                string json = File.ReadAllText(dlg.FileName);
                var dtos = JsonSerializer.Deserialize<List<SolderJointDto>>(json, _jsonOpts);
                if (dtos == null || dtos.Count == 0)
                {
                    MessageBox.Show("File contains no solder joint data.", "Open",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _items.Clear();
                foreach (var d in dtos)
                {
                    _items.Add(new SolderJoint
                    {
                        Name        = d.Name,
                        DiameterMil = d.DiameterMil,
                        X           = d.X,
                        Y           = d.Y,
                    });
                }

                if (_items.Count > 0)
                    ItemGrid.SelectedIndex = 0;

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

        private void ComputeContentBounds(out double minX, out double maxX, out double minY, out double maxY)
        {
            // Show module board outline as reference
            double boardW = _vm.Module.Width;
            double boardH = _vm.Module.Height;
            double[] bxs = { -boardH / 2, boardH / 2 };
            double[] bys = { -boardW, 0 };
            minX = bxs.Min(); maxX = bxs.Max();
            minY = bys.Min(); maxY = bys.Max();

            foreach (var sj in _items)
            {
                double r = sj.DiameterMm / 2;
                minX = Math.Min(minX, sj.X - r); maxX = Math.Max(maxX, sj.X + r);
                minY = Math.Min(minY, sj.Y - r); maxY = Math.Max(maxY, sj.Y + r);
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

            const double mg = 10;
            double drawW = cw - 2 * mg;
            double drawH = ch - 2 * mg;
            if (drawW < 20 || drawH < 20) return;

            double viewHalfX = _viewRange;
            double viewHalfY = _viewRange * (drawH / drawW);
            double scaleX = drawW / (2 * viewHalfX);
            double scaleY = drawH / (2 * viewHalfY);
            double scale = Math.Min(scaleX, scaleY);

            double wMinX = _viewCenterX - drawW / (2 * scale);
            double wMaxX = _viewCenterX + drawW / (2 * scale);
            double wMinY = _viewCenterY - drawH / (2 * scale);
            double wMaxY = _viewCenterY + drawH / (2 * scale);

            double Tx(double wx) => mg + (wx - wMinX) * scale;
            double Ty(double wy) => mg + drawH - (wy - wMinY) * scale;

            // ── Draw module board outline ───────
            double boardW = _vm.Module.Width;
            double boardH = _vm.Module.Height;
            double bMinX = -boardH / 2, bMaxX = boardH / 2;
            double bMinY = -boardW, bMaxY = 0;
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

            // Module label
            var moduleLbl = new TextBlock
            {
                Text = "Module",
                FontSize = 10,
                Foreground = Brushes.Gray,
                FontWeight = FontWeights.Bold,
            };
            Canvas.SetLeft(moduleLbl, Tx(bMinX) + 4);
            Canvas.SetTop(moduleLbl,  Ty(bMaxY) + 2);
            PreviewCanvas.Children.Add(moduleLbl);

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

            // ── Draw each solder joint ───────
            var selected = ItemGrid.SelectedItem as SolderJoint;
            foreach (var sj in _items)
            {
                double rPx = (sj.DiameterMm / 2) * scale;
                if (rPx < 2) rPx = 2;

                double cx = Tx(sj.X);
                double cy = Ty(sj.Y);
                bool isSel = sj == selected;

                var circle = new Ellipse
                {
                    Width  = rPx * 2, Height = rPx * 2,
                    Stroke = isSel ? Brushes.Red : Brushes.DarkOrange,
                    StrokeThickness = isSel ? 2 : 1.2,
                    Fill = new SolidColorBrush(Color.FromArgb(80, 255, 165, 0)),
                };
                Canvas.SetLeft(circle, cx - rPx);
                Canvas.SetTop(circle,  cy - rPx);
                PreviewCanvas.Children.Add(circle);

                // Cross-hair inside
                double cr = rPx * 0.5;
                PreviewCanvas.Children.Add(new Line { X1 = cx - cr, Y1 = cy, X2 = cx + cr, Y2 = cy, Stroke = Brushes.DarkRed, StrokeThickness = 0.8 });
                PreviewCanvas.Children.Add(new Line { X1 = cx, Y1 = cy - cr, X2 = cx, Y2 = cy + cr, Stroke = Brushes.DarkRed, StrokeThickness = 0.8 });

                var label = new TextBlock { Text = sj.Name, FontSize = 10, Foreground = isSel ? Brushes.Red : Brushes.Black };
                Canvas.SetLeft(label, cx + rPx + 3);
                Canvas.SetTop(label,  cy - 7);
                PreviewCanvas.Children.Add(label);
            }

            // ── Legend ───────
            double legendY = mg + 2;
            double legendX = cw - 140;
            AddLegendItem(legendX, ref legendY, new SolidColorBrush(Color.FromArgb(80, 255, 165, 0)), "Solder Joint");

            // ── Axis indicator ───────
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

        private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshPreview();

        private void PreviewCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (shift)
            {
                double step = _viewRange * PanStepFraction;
                _viewCenterY += (e.Delta > 0) ? step : -step;
            }
            else if (ctrl)
            {
                double step = _viewRange * PanStepFraction;
                _viewCenterX += (e.Delta > 0) ? step : -step;
            }
            else
            {
                if (e.Delta > 0) _viewRange /= ZoomFactor;
                else             _viewRange *= ZoomFactor;
            }

            RefreshPreview();
            e.Handled = true;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.C) { CopyItem_Click(this, e); e.Handled = true; return; }
                if (e.Key == Key.V) { PasteItem_Click(this, e); e.Handled = true; return; }
            }

            if (e.OriginalSource is TextBox || e.OriginalSource is ComboBox) return;

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            switch (e.Key)
            {
                case Key.Left:  _viewCenterX -= _viewRange * PanStepFraction; RefreshPreview(); e.Handled = true; break;
                case Key.Right: _viewCenterX += _viewRange * PanStepFraction; RefreshPreview(); e.Handled = true; break;
                case Key.Up:
                    if (shift) { _viewRange /= ZoomFactor; }
                    else       { _viewCenterY += _viewRange * PanStepFraction; }
                    RefreshPreview(); e.Handled = true; break;
                case Key.Down:
                    if (shift) { _viewRange *= ZoomFactor; }
                    else       { _viewCenterY -= _viewRange * PanStepFraction; }
                    RefreshPreview(); e.Handled = true; break;
            }
        }

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

            PreviewCanvas.Children.Add(new Line { X1 = oX, Y1 = oY, X2 = oX + len, Y2 = oY, Stroke = axisBrush, StrokeThickness = thick });
            PreviewCanvas.Children.Add(new Line { X1 = oX + len, Y1 = oY, X2 = oX + len - arrowSize, Y2 = oY - arrowSize * 0.6, Stroke = axisBrush, StrokeThickness = thick });
            PreviewCanvas.Children.Add(new Line { X1 = oX + len, Y1 = oY, X2 = oX + len - arrowSize, Y2 = oY + arrowSize * 0.6, Stroke = axisBrush, StrokeThickness = thick });
            var xLbl = new TextBlock { Text = "X", FontSize = 11, Foreground = axisBrush, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(xLbl, oX + len + 2); Canvas.SetTop(xLbl, oY - 7);
            PreviewCanvas.Children.Add(xLbl);

            PreviewCanvas.Children.Add(new Line { X1 = oX, Y1 = oY, X2 = oX, Y2 = oY - len, Stroke = axisBrush, StrokeThickness = thick });
            PreviewCanvas.Children.Add(new Line { X1 = oX, Y1 = oY - len, X2 = oX - arrowSize * 0.6, Y2 = oY - len + arrowSize, Stroke = axisBrush, StrokeThickness = thick });
            PreviewCanvas.Children.Add(new Line { X1 = oX, Y1 = oY - len, X2 = oX + arrowSize * 0.6, Y2 = oY - len + arrowSize, Stroke = axisBrush, StrokeThickness = thick });
            var yLbl = new TextBlock { Text = "Y", FontSize = 11, Foreground = axisBrush, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(yLbl, oX - 3); Canvas.SetTop(yLbl, oY - len - 15);
            PreviewCanvas.Children.Add(yLbl);
        }
    }
}
