using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AntennaSimulatorApp.Models;
using AntennaSimulatorApp.ViewModels;

namespace AntennaSimulatorApp.Views
{
    public partial class SimSettingsWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly SimSettings   _settings;

        /// <summary>Flat list of all conductive layer names across both boards.</summary>
        private List<string> _allLayerNames = new();

        // Flag to suppress re-entrant PortField_Changed while loading a port into the detail panel
        private bool _loadingPort = false;

        public SimSettingsWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm       = vm;
            _settings = vm.SimSettings;

            BuildLayerNameList();
            PopulateGroundPlanes();
            PopulatePortLayerCombos();
            LoadSettingsToUI();

            // Draw initial preview once layout is ready
            Loaded += (_, __) => RedrawPortPreview();
        }

        // ── Layer helpers ─────────────────────────────────────────────────────

        private void BuildLayerNameList()
        {
            _allLayerNames.Clear();
            foreach (var l in _vm.CarrierBoard.Stackup.Layers.Where(l => l.IsConductive))
                _allLayerNames.Add($"Carrier – {l.Name}");
            if (_vm.HasModule)
                foreach (var l in _vm.Module.Stackup.Layers.Where(l => l.IsConductive))
                    _allLayerNames.Add($"Module – {l.Name}");
        }

        private void PopulatePortLayerCombos()
        {
            FromLayerCombo.Items.Clear();
            ToLayerCombo.Items.Clear();
            foreach (var name in _allLayerNames)
            {
                FromLayerCombo.Items.Add(name);
                ToLayerCombo.Items.Add(name);
            }
            // Auto-select: first layer for From, second (or first) for To
            if (FromLayerCombo.Items.Count > 0)
                FromLayerCombo.SelectedIndex = 0;
            if (ToLayerCombo.Items.Count > 1)
                ToLayerCombo.SelectedIndex = 1;
            else if (ToLayerCombo.Items.Count > 0)
                ToLayerCombo.SelectedIndex = 0;
        }

        private void PopulateGroundPlanes()
        {
            // Sync GroundPlaneEntry list with current stackup layers
            // Preserve existing IsGround selections if the entry already exists
            var existing = _settings.GroundPlanes.ToDictionary(g => g.DisplayName);
            _settings.GroundPlanes.Clear();

            void AddEntries(string boardName, IEnumerable<Layer> layers)
            {
                foreach (var l in layers.Where(ll => ll.IsConductive))
                {
                    var entry = new GroundPlaneEntry
                    {
                        BoardName = boardName,
                        LayerName = l.Name
                    };
                    // Default-tick layers whose type is Ground; preserve user choice if re-opening
                    if (existing.TryGetValue(entry.DisplayName, out var prev))
                        entry.IsGround = prev.IsGround;
                    else
                        entry.IsGround = (l.Type == LayerType.Ground);

                    _settings.GroundPlanes.Add(entry);
                }
            }

            AddEntries("Carrier", _vm.CarrierBoard.Stackup.Layers);
            if (_vm.HasModule)
                AddEntries("Module", _vm.Module.Stackup.Layers);

            GroundPlaneList.ItemsSource = _settings.GroundPlanes;
        }

        // ── Load / save UI ────────────────────────────────────────────────────

        private void LoadSettingsToUI()
        {
            // ── Ports ─────────────────────────────────────────────────────────
            PortListBox.ItemsSource = _settings.Ports;
            PortDetailPanel.IsEnabled = false;

            // ── Boundary ──────────────────────────────────────────────────────
            var b = _settings.Boundary;
            PmlRadio.IsChecked          = b.Type == BoundaryType.PML;
            OpenAddSpaceRadio.IsChecked = b.Type == BoundaryType.OpenAddSpace;
            PmlLayersBox.Text           = b.PmlLayers.ToString();
            SpXpBox.Text = b.SpacingXPlus.ToString("G5");
            SpXmBox.Text = b.SpacingXMinus.ToString("G5");
            SpYpBox.Text = b.SpacingYPlus.ToString("G5");
            SpYmBox.Text = b.SpacingYMinus.ToString("G5");
            SpZpBox.Text = b.SpacingZPlus.ToString("G5");
            SpZmBox.Text = b.SpacingZMinus.ToString("G5");
            SetSymCombo(SymXCombo, b.SymmetryX);
            SetSymCombo(SymYCombo, b.SymmetryY);
            SetSymCombo(SymZCombo, b.SymmetryZ);
            ManualSimAreaCheck.IsChecked = b.ManualSimArea;
            SimXMinBox.Text = b.SimXMin.ToString("G5");
            SimXMaxBox.Text = b.SimXMax.ToString("G5");
            SimYMinBox.Text = b.SimYMin.ToString("G5");
            SimYMaxBox.Text = b.SimYMax.ToString("G5");
            SimZMinBox.Text = b.SimZMin.ToString("G5");
            SimZMaxBox.Text = b.SimZMax.ToString("G5");
            UpdateBoundaryUI();

            // ── Mesh ──────────────────────────────────────────────────────────
            var m = _settings.Mesh;
            AdaptiveCheck.IsChecked = m.AdaptiveMeshing;
            MeshFreqBox.Text        = m.MeshFreqGHz.ToString("G5");
            CellsPerWLBox.Text      = m.CellsPerWavelength.ToString();
            MinStepBox.Text         = m.MinStepMm.ToString("G5");
            MaxPassesBox.Text       = m.MaxAdaptivePasses.ToString();
            ConvDeltaBox.Text       = m.ConvergenceDelta.ToString("G4");

            // ── Sweep ─────────────────────────────────────────────────────────
            var s = _settings.Sweep;
            FastSweepRadio.IsChecked          = s.Type == SweepType.Fast;
            InterpolatingSweepRadio.IsChecked = s.Type == SweepType.Interpolating;
            DiscreteSweepRadio.IsChecked      = s.Type == SweepType.Discrete;
            SweepStartBox.Text  = s.StartGHz.ToString("G5");
            SweepStopBox.Text   = s.StopGHz.ToString("G5");
            SweepStepBox.Text   = s.StepGHz.ToString("G5");
            SweepPointsBox.Text = s.NumPoints.ToString();

            // ── Solver ────────────────────────────────────────────────────────
            var sv = _settings.Solver;
            MaxTimestepsBox.Text = sv.MaxTimesteps.ToString();
            EndCriteriaBox.Text  = sv.EndCriteria.ToString("E1");
        }

        private void SetSymCombo(ComboBox cb, SymmetryType sym)
        {
            cb.SelectedIndex = sym switch
            {
                SymmetryType.PEC  => 1,
                SymmetryType.PMC  => 2,
                _                 => 0
            };
        }

        private SymmetryType GetSymCombo(ComboBox cb)
        {
            return cb.SelectedIndex switch
            {
                1 => SymmetryType.PEC,
                2 => SymmetryType.PMC,
                _ => SymmetryType.None
            };
        }

        // ── Apply UI to model ─────────────────────────────────────────────────

        private void ApplyToModel()
        {
            // Commit any selected port first
            CommitSelectedPort();

            // Boundary
            var b = _settings.Boundary;
            b.Type = (OpenAddSpaceRadio.IsChecked == true) ? BoundaryType.OpenAddSpace : BoundaryType.PML;
            b.PmlLayers = Math.Clamp(ParseInt(PmlLayersBox.Text, 8), 1, 32);
            b.SpacingXPlus  = ParseDouble(SpXpBox.Text, 10);
            b.SpacingXMinus = ParseDouble(SpXmBox.Text, 10);
            b.SpacingYPlus  = ParseDouble(SpYpBox.Text, 10);
            b.SpacingYMinus = ParseDouble(SpYmBox.Text, 10);
            b.SpacingZPlus  = ParseDouble(SpZpBox.Text, 10);
            b.SpacingZMinus = ParseDouble(SpZmBox.Text, 10);
            b.SymmetryX = GetSymCombo(SymXCombo);
            b.SymmetryY = GetSymCombo(SymYCombo);
            b.SymmetryZ = GetSymCombo(SymZCombo);
            b.ManualSimArea = ManualSimAreaCheck.IsChecked == true;
            b.SimXMin = ParseDouble(SimXMinBox.Text, -80);
            b.SimXMax = ParseDouble(SimXMaxBox.Text,  80);
            b.SimYMin = ParseDouble(SimYMinBox.Text, -130);
            b.SimYMax = ParseDouble(SimYMaxBox.Text,  30);
            b.SimZMin = ParseDouble(SimZMinBox.Text, -30);
            b.SimZMax = ParseDouble(SimZMaxBox.Text,  30);

            // Mesh
            var m = _settings.Mesh;
            m.AdaptiveMeshing    = AdaptiveCheck.IsChecked == true;
            m.MeshFreqGHz        = ParseDouble(MeshFreqBox.Text,  2.4);
            m.CellsPerWavelength = ParseInt(CellsPerWLBox.Text, 20);
            m.MinStepMm          = ParseDouble(MinStepBox.Text, 0.05);
            m.MaxAdaptivePasses  = ParseInt(MaxPassesBox.Text, 10);
            m.ConvergenceDelta   = ParseDouble(ConvDeltaBox.Text, 0.01);

            // Sweep
            var s = _settings.Sweep;
            s.Type      = FastSweepRadio.IsChecked == true ? SweepType.Fast
                        : DiscreteSweepRadio.IsChecked == true ? SweepType.Discrete
                        : SweepType.Interpolating;
            s.StartGHz  = ParseDouble(SweepStartBox.Text,  1.0);
            s.StopGHz   = ParseDouble(SweepStopBox.Text,   6.0);
            s.StepGHz   = ParseDouble(SweepStepBox.Text,   0.01);
            s.NumPoints = ParseInt(SweepPointsBox.Text, 501);

            // Solver
            var sv = _settings.Solver;
            sv.MaxTimesteps = Math.Clamp(ParseInt(MaxTimestepsBox.Text, 200000), 1000, 10000000);
            sv.EndCriteria  = ParseDouble(EndCriteriaBox.Text, 1e-5);
        }

        private static double ParseDouble(string s, double fallback)
            => double.TryParse(s, out var v) ? v : fallback;

        private static int ParseInt(string s, int fallback)
            => int.TryParse(s, out var v) ? v : fallback;

        // ── Port list management ──────────────────────────────────────────────

        private void AddPort_Click(object sender, RoutedEventArgs e)
        {
            var fp = new FeedPoint
            {
                Label     = $"Port {_settings.Ports.Count + 1}",
                Impedance = 50.0
            };
            _settings.Ports.Add(fp);
            PortListBox.SelectedItem = fp;
            RedrawPortPreview();
        }

        private void RemovePort_Click(object sender, RoutedEventArgs e)
        {
            if (PortListBox.SelectedItem is FeedPoint fp)
            {
                _settings.Ports.Remove(fp);
                RedrawPortPreview();
            }
        }

        private void PortListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PortListBox.SelectedItem is FeedPoint fp)
                LoadPortToDetail(fp);
            else
                PortDetailPanel.IsEnabled = false;
            RedrawPortPreview();
        }

        private void LoadPortToDetail(FeedPoint fp)
        {
            _loadingPort = true;
            try
            {
                PortDetailPanel.IsEnabled = true;
                PortLabelBox.Text = fp.Label;

                PortTypeCombo.SelectedIndex = fp.PortType == PortType.WaveguidePort ? 1 : 0;

                // Select matching layers
                SelectComboItem(FromLayerCombo, fp.FromLayer);
                SelectComboItem(ToLayerCombo, fp.ToLayer);

                PortXBox.Text = fp.X.ToString("G5");
                PortYBox.Text = fp.Y.ToString("G5");
                PortWidthXBox.Text = fp.WidthX.ToString("G5");
                PortWidthYBox.Text = fp.WidthY.ToString("G5");
                PortZBox.Text = fp.Impedance.ToString("G5");

                IntegLineCombo.SelectedIndex = fp.IntegLine switch
                {
                    IntegLine.ZMinus => 1,
                    IntegLine.XPlus  => 2,
                    IntegLine.XMinus => 3,
                    IntegLine.YPlus  => 4,
                    IntegLine.YMinus => 5,
                    _                => 0   // ZPlus
                };
            }
            finally { _loadingPort = false; }
        }

        private void SelectComboItem(ComboBox cb, string value)
        {
            for (int i = 0; i < cb.Items.Count; i++)
            {
                if (cb.Items[i] is string s && s == value)
                { cb.SelectedIndex = i; return; }
            }
            if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        }

        private void PortField_Changed(object sender, EventArgs e)
        {
            if (_loadingPort) return;
            CommitSelectedPort();
            RedrawPortPreview();
        }

        private void CommitSelectedPort()
        {
            if (!(PortListBox.SelectedItem is FeedPoint fp)) return;

            fp.Label     = PortLabelBox.Text;
            fp.PortType  = PortTypeCombo.SelectedIndex == 1 ? PortType.WaveguidePort : PortType.LumpedPort;
            fp.FromLayer = FromLayerCombo.SelectedItem as string ?? "";
            fp.ToLayer   = ToLayerCombo.SelectedItem as string ?? "";
            fp.X         = ParseDouble(PortXBox.Text, 0);
            fp.Y         = ParseDouble(PortYBox.Text, 0);
            fp.WidthX    = ParseDouble(PortWidthXBox.Text, 0.5);
            fp.WidthY    = ParseDouble(PortWidthYBox.Text, 0.5);
            fp.Impedance = ParseDouble(PortZBox.Text, 50);
            fp.IntegLine = IntegLineCombo.SelectedIndex switch
            {
                1 => IntegLine.ZMinus,
                2 => IntegLine.XPlus,
                3 => IntegLine.XMinus,
                4 => IntegLine.YPlus,
                5 => IntegLine.YMinus,
                _ => IntegLine.ZPlus
            };

            // Refresh list display (label may have changed)
            PortListBox.Items.Refresh();
        }

        // ── OK / Apply / Cancel ───────────────────────────────────────────────

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyToModel();
            DialogResult = true;
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
            => ApplyToModel();

        private void CancelButton_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;

        // ── Boundary UI toggle ─────────────────────────────────────────────────────

        private void BoundaryType_Changed(object sender, RoutedEventArgs e)
            => UpdateBoundaryUI();

        private void ManualSimArea_Changed(object sender, RoutedEventArgs e)
            => UpdateBoundaryUI();

        private void UpdateBoundaryUI()
        {
            // Guard: this can fire during InitializeComponent before controls exist
            if (AirBoxPaddingGroup == null || ManualSimAreaCheck == null || SimAreaGrid == null)
                return;

            bool isOpenAdd = OpenAddSpaceRadio.IsChecked == true;
            bool manualArea = ManualSimAreaCheck.IsChecked == true;
            AirBoxPaddingGroup.IsEnabled = isOpenAdd && !manualArea;
            SimAreaGrid.IsEnabled = manualArea;
        }

        // ── Port preview: zoom / pan state ─────────────────────────────────

        private double _pvCenterX, _pvCenterY;   // view centre in mm
        private double _pvRange = 0;              // half-width of visible range (0 = auto-fit)
        private const double PvZoomFactor = 1.3;
        private const double PvPanStep    = 0.15; // fraction of range per pan

        private bool IsPortPreviewCarrier
        {
            get
            {
                if (PortPreviewBoardCombo == null) return true;
                var item = PortPreviewBoardCombo.SelectedItem as ComboBoxItem;
                return item == null || (item.Content as string) == "Carrier";
            }
        }

        private void PortPreviewBoardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_vm == null) return;
            PortPreviewResetView();
            RedrawPortPreview();
        }

        private void PortPreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
            => RedrawPortPreview();

        // ── Zoom / Pan buttons ──

        private void PortZoomIn_Click(object sender, RoutedEventArgs e)  { _pvRange /= PvZoomFactor; RedrawPortPreview(); }
        private void PortZoomOut_Click(object sender, RoutedEventArgs e) { _pvRange *= PvZoomFactor; RedrawPortPreview(); }
        private void PortPanLeft_Click(object sender, RoutedEventArgs e)  { _pvCenterX -= _pvRange * PvPanStep; RedrawPortPreview(); }
        private void PortPanRight_Click(object sender, RoutedEventArgs e) { _pvCenterX += _pvRange * PvPanStep; RedrawPortPreview(); }
        private void PortPanUp_Click(object sender, RoutedEventArgs e)    { _pvCenterY += _pvRange * PvPanStep; RedrawPortPreview(); }
        private void PortPanDown_Click(object sender, RoutedEventArgs e)  { _pvCenterY -= _pvRange * PvPanStep; RedrawPortPreview(); }
        private void PortFitView_Click(object sender, RoutedEventArgs e)
        {
            PortPreviewResetView();
            RedrawPortPreview();
        }

        private void PortPreviewCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (shift)
            {
                double step = _pvRange * PvPanStep;
                _pvCenterY += (e.Delta > 0) ? step : -step;
            }
            else if (ctrl)
            {
                double step = _pvRange * PvPanStep;
                _pvCenterX += (e.Delta > 0) ? step : -step;
            }
            else
            {
                if (e.Delta > 0) _pvRange /= PvZoomFactor;
                else             _pvRange *= PvZoomFactor;
            }
            RedrawPortPreview();
            e.Handled = true;
        }

        // ── Compute world bounds for auto-fit ──

        private void PortPreviewComputeBounds(out double minX, out double maxX,
                                               out double minY, out double maxY)
        {
            bool carrier = IsPortPreviewCarrier;
            var board = carrier ? _vm.CarrierBoard : (BoardConfig)_vm.Module;
            double boardW = board.Width;
            double boardH = board.Height;
            // Board: X ∈ [-Height/2, Height/2], Y ∈ [-Width, 0]
            minX = -boardH / 2; maxX = boardH / 2;
            minY = -boardW;     maxY = 0;

            // Expand for shapes on this board
            foreach (var ms in _vm.ManualShapes)
            {
                if (ms.IsCarrier != carrier) continue;
                var gd = ms.ToGerberData();
                foreach (var s in gd.Shapes)
                {
                    if (s.IsClear) continue;
                    foreach (var pt in s.Points)
                    {
                        minX = Math.Min(minX, pt.X); maxX = Math.Max(maxX, pt.X);
                        minY = Math.Min(minY, pt.Y); maxY = Math.Max(maxY, pt.Y);
                    }
                }
            }

            // Expand for ports
            foreach (var fp in _settings.Ports)
            {
                minX = Math.Min(minX, fp.X); maxX = Math.Max(maxX, fp.X);
                minY = Math.Min(minY, fp.Y); maxY = Math.Max(maxY, fp.Y);
            }
        }

        private void PortPreviewResetView()
        {
            PortPreviewComputeBounds(out double minX, out double maxX, out double minY, out double maxY);
            const double margin = 3.0;
            _pvCenterX = (minX + maxX) / 2;
            _pvCenterY = (minY + maxY) / 2;

            double halfRangeX = (maxX - minX) / 2 + margin;
            double halfRangeY = (maxY - minY) / 2 + margin;

            // Account for canvas aspect ratio: _pvRange is half-width in X,
            // visible Y half-range = _pvRange * drawH / drawW.
            // To fit Y content we need _pvRange >= halfRangeY * drawW / drawH.
            const double mg = 10;
            double cw = PortPreviewCanvas?.ActualWidth  ?? 300;
            double ch = PortPreviewCanvas?.ActualHeight ?? 220;
            double drawW = Math.Max(cw - 2 * mg, 20);
            double drawH = Math.Max(ch - 2 * mg, 20);

            _pvRange = Math.Max(halfRangeX, halfRangeY * drawW / drawH);
            if (_pvRange < 1) _pvRange = 10;
        }

        // ── Main drawing routine ──

        private void RedrawPortPreview()
        {
            if (PortPreviewCanvas == null) return;
            PortPreviewCanvas.Children.Clear();

            double cw = PortPreviewCanvas.ActualWidth;
            double ch = PortPreviewCanvas.ActualHeight;
            if (cw < 10 || ch < 10)
            {
                Dispatcher.InvokeAsync(RedrawPortPreview,
                    System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            if (_pvRange <= 0) PortPreviewResetView();

            const double mg = 10;
            double drawW = cw - 2 * mg;
            double drawH = ch - 2 * mg;
            if (drawW < 20 || drawH < 20) return;

            // Compute scale from view state (same as DrawViaWindow)
            double viewHalfX = _pvRange;
            double viewHalfY = _pvRange * (drawH / drawW);
            double scaleX = drawW / (2 * viewHalfX);
            double scaleY = drawH / (2 * viewHalfY);
            double scale = Math.Min(scaleX, scaleY);

            double wMinX = _pvCenterX - drawW / (2 * scale);
            double wMinY = _pvCenterY - drawH / (2 * scale);

            // World → canvas transforms
            double Tx(double wx) => mg + (wx - wMinX) * scale;
            double Ty(double wy) => mg + drawH - (wy - wMinY) * scale; // flip Y

            // Cache transform for click hit-testing
            _pvWMinX = wMinX; _pvWMinY = wMinY; _pvScale = scale;

            bool carrier = IsPortPreviewCarrier;
            var board = carrier ? _vm.CarrierBoard : (BoardConfig)_vm.Module;
            double boardW = board.Width;
            double boardH = board.Height;

            // ── Board outline ──
            double bMinX = -boardH / 2, bMaxX = boardH / 2;
            double bMinY = -boardW,     bMaxY = 0;
            var boardRect = new Rectangle
            {
                Width  = (bMaxX - bMinX) * scale,
                Height = (bMaxY - bMinY) * scale,
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill   = new SolidColorBrush(Color.FromArgb(20, 200, 200, 200)),
                ToolTip = $"{(carrier ? "Carrier" : "Module")} ({boardH}×{boardW} mm)"
            };
            Canvas.SetLeft(boardRect, Tx(bMinX));
            Canvas.SetTop(boardRect,  Ty(bMaxY));
            PortPreviewCanvas.Children.Add(boardRect);

            // ── Copper & antenna shapes on selected board ──
            foreach (var ms in _vm.ManualShapes)
            {
                if (ms.IsCarrier != carrier) continue;
                if (!ms.ShowIn3D) continue;
                bool isAntenna = ms.Name != null && ms.Name.StartsWith("Antenna (");
                var gd = ms.ToGerberData();
                foreach (var s in gd.Shapes)
                {
                    if (s.IsClear || s.Points.Count < 3) continue;
                    var pts = s.Points.ToList();
                    DrawPvPolygon(pts, Tx, Ty, isAntenna, ms.Name ?? "");
                }
            }

            // ── Origin cross ──
            double oxPx = Tx(0), oyPx = Ty(0);
            if (oxPx > mg - 2 && oxPx < cw - mg + 2 &&
                oyPx > mg - 2 && oyPx < ch - mg + 2)
            {
                double crossLen = Math.Min(15, Math.Min(drawW, drawH) * 0.06);
                PortPreviewCanvas.Children.Add(new Line { X1 = oxPx - crossLen, Y1 = oyPx, X2 = oxPx + crossLen, Y2 = oyPx, Stroke = Brushes.LightGray, StrokeThickness = 0.8 });
                PortPreviewCanvas.Children.Add(new Line { X1 = oxPx, Y1 = oyPx - crossLen, X2 = oxPx, Y2 = oyPx + crossLen, Stroke = Brushes.LightGray, StrokeThickness = 0.8 });
            }

            // ── Port markers ──
            var selectedPort = PortListBox.SelectedItem as FeedPoint;
            foreach (var fp in _settings.Ports)
            {
                double px = Tx(fp.X);
                double py = Ty(fp.Y);
                bool isSel = fp == selectedPort;
                double arm = isSel ? 10 : 7;

                // Crosshair
                PortPreviewCanvas.Children.Add(new Line { X1 = px - arm, Y1 = py, X2 = px + arm, Y2 = py,
                    Stroke = Brushes.Red, StrokeThickness = isSel ? 2.5 : 1.5 });
                PortPreviewCanvas.Children.Add(new Line { X1 = px, Y1 = py - arm, X2 = px, Y2 = py + arm,
                    Stroke = Brushes.Red, StrokeThickness = isSel ? 2.5 : 1.5 });

                // Circle
                double r = isSel ? 5 : 4;
                var circle = new Ellipse
                {
                    Width = r * 2, Height = r * 2,
                    Stroke = Brushes.Red,
                    StrokeThickness = isSel ? 2 : 1.2,
                    Fill = new SolidColorBrush(Color.FromArgb(80, 255, 0, 0)),
                    ToolTip = $"{fp.Label}  ({fp.X:F2}, {fp.Y:F2})"
                };
                Canvas.SetLeft(circle, px - r);
                Canvas.SetTop(circle,  py - r);
                PortPreviewCanvas.Children.Add(circle);

                // Label
                var lbl = new TextBlock
                {
                    Text = fp.Label,
                    FontSize = 10,
                    Foreground = Brushes.Red,
                    FontWeight = isSel ? FontWeights.Bold : FontWeights.SemiBold
                };
                Canvas.SetLeft(lbl, px + r + 4);
                Canvas.SetTop(lbl,  py - 12);
                PortPreviewCanvas.Children.Add(lbl);
            }

            // ── Legend (top-right) ──
            double legendY = mg + 2;
            double legendX = cw - 130;
            AddPvLegendItem(legendX, ref legendY, new SolidColorBrush(Color.FromArgb(60, 0, 160, 0)), "Copper");
            AddPvLegendItem(legendX, ref legendY, new SolidColorBrush(Color.FromArgb(70, 30, 80, 220)), "Antenna");
            AddPvLegendItem(legendX, ref legendY, new SolidColorBrush(Color.FromArgb(120, 255, 0, 0)), "Port");

            // ── Axis indicator (bottom-left) ──
            DrawPvAxisIndicator(cw, ch);
        }

        private void DrawPvPolygon(List<(double X, double Y)> pts,
            Func<double, double> Tx, Func<double, double> Ty,
            bool isAntenna, string tooltip)
        {
            if (pts.Count < 3) return;
            var polygon = new Polygon
            {
                Fill = isAntenna
                    ? new SolidColorBrush(Color.FromArgb(70, 30, 80, 220))
                    : new SolidColorBrush(Color.FromArgb(60, 0, 160, 0)),
                Stroke = isAntenna ? Brushes.Blue : Brushes.DarkGreen,
                StrokeThickness = 0.8,
                ToolTip = tooltip
            };
            foreach (var pt in pts)
                polygon.Points.Add(new Point(Tx(pt.X), Ty(pt.Y)));
            PortPreviewCanvas.Children.Add(polygon);
        }

        private void AddPvLegendItem(double x, ref double y, Brush color, string text)
        {
            var rect = new Rectangle { Width = 10, Height = 10, Fill = color, Stroke = Brushes.Gray, StrokeThickness = 0.5 };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            PortPreviewCanvas.Children.Add(rect);

            var tb = new TextBlock { Text = text, FontSize = 9, Foreground = Brushes.DimGray };
            Canvas.SetLeft(tb, x + 14);
            Canvas.SetTop(tb, y - 1);
            PortPreviewCanvas.Children.Add(tb);

            y += 16;
        }

        private void DrawPvAxisIndicator(double cw, double ch)
        {
            if (cw < 60 || ch < 60) return;
            const double len = 28;
            const double margin = 12;
            double oX = margin;
            double oY = ch - margin;

            var ab = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            const double thick = 1.2;
            const double arr = 4.5;

            // X axis
            PortPreviewCanvas.Children.Add(new Line { X1 = oX, Y1 = oY, X2 = oX + len, Y2 = oY, Stroke = ab, StrokeThickness = thick });
            PortPreviewCanvas.Children.Add(new Line { X1 = oX + len, Y1 = oY, X2 = oX + len - arr, Y2 = oY - arr * 0.6, Stroke = ab, StrokeThickness = thick });
            PortPreviewCanvas.Children.Add(new Line { X1 = oX + len, Y1 = oY, X2 = oX + len - arr, Y2 = oY + arr * 0.6, Stroke = ab, StrokeThickness = thick });
            var xl = new TextBlock { Text = "X", FontSize = 10, Foreground = ab, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(xl, oX + len + 2); Canvas.SetTop(xl, oY - 6);
            PortPreviewCanvas.Children.Add(xl);

            // Y axis
            PortPreviewCanvas.Children.Add(new Line { X1 = oX, Y1 = oY, X2 = oX, Y2 = oY - len, Stroke = ab, StrokeThickness = thick });
            PortPreviewCanvas.Children.Add(new Line { X1 = oX, Y1 = oY - len, X2 = oX - arr * 0.6, Y2 = oY - len + arr, Stroke = ab, StrokeThickness = thick });
            PortPreviewCanvas.Children.Add(new Line { X1 = oX, Y1 = oY - len, X2 = oX + arr * 0.6, Y2 = oY - len + arr, Stroke = ab, StrokeThickness = thick });
            var yl = new TextBlock { Text = "Y", FontSize = 10, Foreground = ab, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(yl, oX - 3); Canvas.SetTop(yl, oY - len - 14);
            PortPreviewCanvas.Children.Add(yl);
        }

        // ── Click on edge: show segment corner coordinates ────────────────

        // Cached transform state for hit-testing clicks → world coords
        private double _pvWMinX, _pvWMinY, _pvScale;

        /// <summary>
        /// Collects all polygon edge segments (pairs of world-space vertices)
        /// from the shapes currently displayed on the selected board.
        /// </summary>
        private List<((double X, double Y) A, (double X, double Y) B, string ShapeName)> CollectEdgeSegments()
        {
            var edges = new List<((double, double), (double, double), string)>();
            bool carrier = IsPortPreviewCarrier;
            foreach (var ms in _vm.ManualShapes)
            {
                if (ms.IsCarrier != carrier || !ms.ShowIn3D) continue;
                var gd = ms.ToGerberData();
                foreach (var s in gd.Shapes)
                {
                    if (s.IsClear || s.Points.Count < 3) continue;
                    var pts = s.Points;
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var a = pts[i];
                        var b = pts[(i + 1) % pts.Count];
                        edges.Add((a, b, ms.Name ?? ""));
                    }
                }
            }
            return edges;
        }

        private void PortPreviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_pvScale <= 0) return;

            // Canvas pixel → world mm
            var pos = e.GetPosition(PortPreviewCanvas);
            const double mg = 10;
            double wx = (pos.X - mg) / _pvScale + _pvWMinX;
            double wy = -((pos.Y - mg - (PortPreviewCanvas.ActualHeight - 2 * mg)) / _pvScale) + _pvWMinY;

            var edges = CollectEdgeSegments();
            if (edges.Count == 0) { EdgeInfoText.Text = ""; return; }

            // Find closest edge
            double bestDist = double.MaxValue;
            (double X, double Y) bestA = default, bestB = default;
            string bestName = "";
            foreach (var (a, b, name) in edges)
            {
                double d = PointToSegmentDist(wx, wy, a.X, a.Y, b.X, b.Y);
                if (d < bestDist) { bestDist = d; bestA = a; bestB = b; bestName = name; }
            }

            // Threshold: only report if click is reasonably close (within 2mm world or 10px)
            double thresholdMm = Math.Max(2.0, 10.0 / _pvScale);
            if (bestDist > thresholdMm) { EdgeInfoText.Text = ""; return; }

            // Compute the bounding rectangle of this edge segment
            double minX = Math.Min(bestA.X, bestB.X);
            double maxX = Math.Max(bestA.X, bestB.X);
            double minY = Math.Min(bestA.Y, bestB.Y);
            double maxY = Math.Max(bestA.Y, bestB.Y);

            // Highlight the clicked edge on canvas
            double Tx(double x) => mg + (x - _pvWMinX) * _pvScale;
            double Ty(double y) => mg + (PortPreviewCanvas.ActualHeight - 2 * mg) - (y - _pvWMinY) * _pvScale;
            var highlight = new Line
            {
                X1 = Tx(bestA.X), Y1 = Ty(bestA.Y),
                X2 = Tx(bestB.X), Y2 = Ty(bestB.Y),
                Stroke = Brushes.OrangeRed, StrokeThickness = 3,
                Tag = "EdgeHighlight"
            };
            // Remove previous highlights
            for (int i = PortPreviewCanvas.Children.Count - 1; i >= 0; i--)
                if (PortPreviewCanvas.Children[i] is Line ln && ln.Tag as string == "EdgeHighlight")
                    PortPreviewCanvas.Children.RemoveAt(i);
            PortPreviewCanvas.Children.Add(highlight);

            EdgeInfoText.Text =
                $"[{bestName}]  Edge: ({bestA.X:F3}, {bestA.Y:F3}) → ({bestB.X:F3}, {bestB.Y:F3})    " +
                $"Bounds: X=[{minX:F3}, {maxX:F3}]  Y=[{minY:F3}, {maxY:F3}]";
        }

        /// <summary>Distance from point (px,py) to line segment (ax,ay)-(bx,by).</summary>
        private static double PointToSegmentDist(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-12) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
            double t = Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq));
            double cx = ax + t * dx, cy = ay + t * dy;
            return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }
    }
}
