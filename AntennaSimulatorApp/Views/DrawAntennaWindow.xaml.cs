using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AntennaSimulatorApp.Models;
using AntennaSimulatorApp.ViewModels;
using AntennaSimulatorApp.Services;
using Microsoft.Win32;
using System.Globalization;

namespace AntennaSimulatorApp.Views
{
    // ── Antenna type ─────────────────────────────────────────────────────

    public enum AntennaType { InvertedF, MeanderedInvertedF, Custom }

    // ── Parameter model ──────────────────────────────────────────────

    public class AntennaParams
    {
        public AntennaType Type      { get; set; } = AntennaType.InvertedF;
        public bool   IsCarrier      { get; set; } = true;
        public string LayerName      { get; set; } = "";

        // ── Display / placement ────────────────────────────────────────
        public string Name     { get; set; } = "Antenna";
        public double OffsetX  { get; set; } = 0.0;
        public double OffsetY  { get; set; } = 0.0;

        // ── IFA common ──────────────────────────────────────────────────
        public double FreqGHz        { get; set; } = 2.4;
        public double LengthL        { get; set; } = 24.0;
        public double HeightH        { get; set; } = 7.0;
        public double FeedGap        { get; set; } = 3.0;

        // ── IFA per-segment trace widths ─────────────────────────────────
        public double ShortPinWidth  { get; set; } = 1.0;
        public double FeedPinWidth   { get; set; } = 1.0;
        public double MatchStubWidth { get; set; } = 1.0;
        public double RadiatorWidth  { get; set; } = 1.0;

        // ── MIFA-only ────────────────────────────────────────────────────
        public double MifaHeightH    { get; set; } = 3.9;
        public double MeanderHeight  { get; set; } = 2.85;
        public double MeanderPitch   { get; set; } = 5.0;

        // ── MIFA per-segment trace widths ────────────────────────────────
        public double MifaShortWidth { get; set; } = 0.8;
        public double MifaFeedWidth  { get; set; } = 0.8;
        public double MifaHorizWidth { get; set; } = 0.5;
        public double MifaVertWidth  { get; set; } = 0.5;

        // ── PCB space (used for coordinate mapping) ────────────────────────
        public double AvailWidth   { get; set; } = 15.0;
        public double AvailHeight  { get; set; } = 10.0;
        public double PcbOffsetX   { get; set; } = 0.0;
        public double PcbOffsetY   { get; set; } = 0.0;
        public double Clearance    { get; set; } = 0.254;

        // ── Custom antenna vertices ──────────────────────────────────────
        public List<(double X, double Y)> CustomVertices { get; set; } = new();

        /// <summary>Auto-calculated: number of full-pitch meander turns.</summary>
        private int FullMeanderCount => MeanderPitch + MeanderHeight > 0
            ? Math.Max(1, (int)Math.Floor((LengthL - FeedGap) / (MeanderPitch + MeanderHeight)))
            : 1;

        /// <summary>Remaining trace after full turns.</summary>
        private double MeanderRemaining => LengthL - FeedGap - FullMeanderCount * (MeanderPitch + MeanderHeight);

        /// <summary>Total meander count: if remaining ≥ pitch, add one more partial-height turn.</summary>
        public int MeanderCount => MeanderRemaining >= MeanderPitch
            ? FullMeanderCount + 1 : FullMeanderCount;

        /// <summary>Horizontal pitch for every turn (always standard pitch).</summary>
        public double MeanderPitchForTurn(int i) => MeanderPitch;

        /// <summary>Vertical height for turn i (last partial turn may be shorter than h1).</summary>
        public double MeanderHeightForTurn(int i) =>
            (MeanderCount > FullMeanderCount && i == MeanderCount - 1)
                ? Math.Max(MeanderRemaining - MeanderPitch, 0)
                : MeanderHeight;

        /// <summary>Tail length (straight, after last turn). Zero if partial turn absorbed it.</summary>
        public double MeanderTailLen =>
            MeanderRemaining >= MeanderPitch ? 0 : Math.Max(MeanderRemaining, 0);

        // ── Copper trace geometry ────────────────────────────────────────
        /// <summary>
        /// Generates extrudable GerberData for this antenna in LOCAL coordinates
        /// (origin = base of short-circuit post).  Apply OffsetX/OffsetY in BuildMeshes.
        /// </summary>
        public GerberData ToGerberData()
        {
            var gd = new GerberData();

            if (Type == AntennaType.InvertedF)
            {
                double hwSh = ShortPinWidth / 2;
                double hwFe = FeedPinWidth / 2;
                double hwMa = MatchStubWidth / 2;
                double hwRa = RadiatorWidth / 2;
                double hwTopFeed = Math.Max(hwMa, hwRa); // top of feed stub connects to both

                // 1. Shorting stub  (0,0) → (0, H) — extend top by half match width
                AddRect(gd, -hwSh, 0, hwSh, HeightH + hwMa);
                // 2. Matching section (0, H) → (S, H) — extend left/right by half connecting vertical widths
                AddRect(gd, -hwSh, HeightH - hwMa, FeedGap + hwFe, HeightH + hwMa);
                // 3. Feed stub  (S, 0) → (S, H) — extend top by half max(match, radiator)
                AddRect(gd, FeedGap - hwFe, 0, FeedGap + hwFe, HeightH + hwTopFeed);
                // 4. Radiating arm  (S, H) → (L, H) — extend left by half feed width
                AddRect(gd, FeedGap - hwFe, HeightH - hwRa, LengthL, HeightH + hwRa);
            }
            else if (Type == AntennaType.Custom)
            {
                // Custom: single closed polygon from user-defined vertices
                if (CustomVertices.Count >= 3)
                {
                    var s = new GerberShape();
                    foreach (var v in CustomVertices)
                    {
                        s.Points.Add((v.X, v.Y));
                        gd.ExpandBounds(v.X, v.Y);
                    }
                    gd.Shapes.Add(s);
                }
            }
            else  // MIFA
            {
                double H = MifaHeightH;
                double Hm = MeanderHeight;
                double yTop = H;           // top horizontal line
                double yBot = H - Hm;      // bottom of meander fingers
                double hwSh = MifaShortWidth / 2;
                double hwFe = MifaFeedWidth / 2;
                double hwMh = MifaHorizWidth / 2;
                double hwMv = MifaVertWidth / 2;

                // 1. Shorting stub (0,0) → (0, H) — extend top by half horiz width
                AddRect(gd, -hwSh, 0, hwSh, H + hwMh);
                // 2. Matching section (0, H) → (S, H) — extend left/right by half connecting vertical widths
                AddRect(gd, -hwSh, H - hwMh, FeedGap + hwFe, H + hwMh);
                // 3. Feed stub (S, 0) → (S, H) — extend top by half horiz width
                AddRect(gd, FeedGap - hwFe, 0, FeedGap + hwFe, H + hwMh);

                // Meander starts at x = FeedGap, y = yTop
                double x = FeedGap, y = yTop;
                for (int i = 0; i < MeanderCount; i++)
                {
                    double p_i = MeanderPitchForTurn(i);
                    double h_i = MeanderHeightForTurn(i);
                    double yBotI = H - h_i;
                    double nx = x + p_i;
                    double ny = (i % 2 == 0) ? yBotI : yTop;

                    // 4. Horizontal meander trace — extend left/right by half vertical width
                    AddRect(gd, x - hwMv, y - hwMh, nx + hwMv, y + hwMh);
                    // 5. Vertical meander trace — extend top/bottom by half horizontal width
                    double vyMin = Math.Min(y, ny);
                    double vyMax = Math.Max(y, ny);
                    if (vyMax - vyMin > 1e-6)
                        AddRect(gd, nx - hwMv, vyMin - hwMh, nx + hwMv, vyMax + hwMh);

                    x = nx; y = ny;
                }
                // Tail — only drawn if remaining trace wasn't folded into a partial turn
                double tailLen = MeanderTailLen;
                if (tailLen > 0)
                    AddRect(gd, x - hwMv, y - hwMh, x + tailLen, y + hwMh);
            }
            return gd;
        }

        private static void AddTraceSeg(GerberData gd,
            double x0, double y0, double x1, double y1, double w)
        {
            double hw = w / 2;
            if (Math.Abs(x0 - x1) < 1e-9)   // vertical
                AddRect(gd, Math.Min(x0,x1)-hw, Math.Min(y0,y1),
                            Math.Max(x0,x1)+hw, Math.Max(y0,y1));
            else                              // horizontal
                AddRect(gd, Math.Min(x0,x1), Math.Min(y0,y1)-hw,
                            Math.Max(x0,x1), Math.Max(y0,y1)+hw);
        }

        private static void AddRect(GerberData gd,
            double x0, double y0, double x1, double y1)
        {
            var s = new GerberShape();
            s.Points.AddRange(new[] { (x0,y0),(x1,y0),(x1,y1),(x0,y1) });
            gd.ExpandBounds(x0, y0);
            gd.ExpandBounds(x1, y1);
            gd.Shapes.Add(s);
        }
    }

    // ── Window ────────────────────────────────────────────────────────

    public partial class DrawAntennaWindow : Window
    {
        private readonly MainViewModel _vm;
        public  AntennaParams? Result { get; private set; }

        /// <summary>Tracks the ManualShape we created so repeated Update clicks replace it.</summary>
        private ManualShape? _antennaShape;

        /// <summary>Name of the antenna being edited (used to find the ManualShape to replace).</summary>
        private string? _editName;

        /// <summary>Saved edit params so OnContentRendered can re-apply them after grid reset.</summary>
        private AntennaParams? _editParams;

        private static readonly Dictionary<AntennaType, (string Display, string Desc)> TypeMeta =
            new Dictionary<AntennaType, (string, string)>
        {
            [AntennaType.InvertedF] = (
                "Inverted-F Antenna (IFA)",
                "Inverted-F Antenna (IFA): total length L ≈ λ/4; short-circuit post grounds the structure and a feed tap achieves 50 Ω input impedance. "
                + "Compact design, ideal for 2.4 GHz / 5 GHz Wi-Fi and BLE applications."),
            [AntennaType.MeanderedInvertedF] = (
                "Meandered Inverted-F Antenna (MIFA)",
                "Meandered IFA (MIFA): the IFA radiating arm is folded into a meander pattern, preserving the electrical length (λ/4) while greatly reducing the physical footprint. "
                + "Suited for space-constrained compact IoT modules."),
            [AntennaType.Custom] = (
                "Custom Antenna (polygon)",
                "Custom antenna: define the copper shape as a closed polygon by entering vertex coordinates (mm). "
                + "The last vertex must coincide with the first to close the polygon. Suitable for any custom trace layout.")
        };

        // TextBox registry for reading back values
        private readonly Dictionary<string, TextBox> _boxes = new();

        // Custom antenna vertex list (bound to DataGrid)
        private readonly ObservableCollection<ShapeVertex> _customVertices = new();

        public DrawAntennaWindow(MainViewModel vm, AntennaParams? editParams = null, string? defaultName = null)
        {
            InitializeComponent();
            _vm = vm;

            foreach (var kv in TypeMeta)
                AntennaTypeCombo.Items.Add(new ComboBoxItem { Tag = kv.Key, Content = kv.Value.Display });
            AntennaTypeCombo.SelectedIndex = 0;

            BoardCombo.Items.Add("Carrier");
            if (vm.HasModule)
                BoardCombo.Items.Add("Module");
            BoardCombo.SelectedIndex = 0;

            // Bind custom vertex DataGrid
            CustomVertexGrid.ItemsSource = _customVertices;
            _customVertices.CollectionChanged += (_, __) => DrawPreview();

            if (editParams != null)
            {
                // Edit mode: save params for re-application after OnContentRendered reset
                _editParams = editParams;
                _editName = editParams.Name;
                string shapeName = $"Antenna ({editParams.Name})";
                _antennaShape = vm.ManualShapes.FirstOrDefault(s => s.Name == shapeName);
                AntennaNameBox.Text = editParams.Name;
                ProjectSerializer.DiagWrite($"[EditAntenna] editParams: Name={editParams.Name} Type={editParams.Type} Freq={editParams.FreqGHz} L={editParams.LengthL} H={editParams.HeightH} FeedGap={editParams.FeedGap}");
                ApplyAntennaParamsToUI(editParams);
            }
            else
            {
                // New mode
                AntennaNameBox.Text = defaultName ?? "Antenna";
                // Auto-fill PCB space width from module board width (UI "Width" = Height property)
                if (vm.HasModule)
                    AvailWidthBox.Text = vm.Module.Height.ToString("F2");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────

        private AntennaType SelectedType =>
            AntennaTypeCombo.SelectedItem is ComboBoxItem ci && ci.Tag is AntennaType t
                ? t : AntennaType.InvertedF;

        private bool IsCarrierSelected =>
            (BoardCombo.SelectedItem as string ?? "").StartsWith("Carrier");

        private double Get(string key, double fallback = 0)
        {
            if (_boxes.TryGetValue(key, out var tb) && double.TryParse(tb.Text, out double v))
                return v;
            return fallback;
        }

        // lambda/4 in mm given er (substrate effective) and freq in GHz
        private static double LambdaOver4(double freqGHz, double er = 1.0)
            => 300.0 / (freqGHz * 4.0 * Math.Sqrt(er));

        // ── UI rebuild ───────────────────────────────────────────────

        private void PopulateLayerCombo()
        {
            LayerCombo.Items.Clear();
            var stackup = IsCarrierSelected ? _vm.CarrierBoard.Stackup : _vm.Module.Stackup;
            foreach (var l in stackup.Layers.Where(l => l.IsConductive))
                LayerCombo.Items.Add(l);
            if (LayerCombo.Items.Count > 0) LayerCombo.SelectedIndex = 0;
        }

        private void RefreshAll()
        {
            if (!IsInitialized) return;
            TypeDescText.Text = TypeMeta.TryGetValue(SelectedType, out var m) ? m.Desc : "";

            bool isMifa   = SelectedType == AntennaType.MeanderedInvertedF;
            bool isCustom = SelectedType == AntennaType.Custom;
            IfaGroup.Visibility     = (!isMifa && !isCustom) ? Visibility.Visible : Visibility.Collapsed;
            MifaGroup.Visibility    = isMifa   ? Visibility.Visible : Visibility.Collapsed;
            CustomGroup.Visibility  = isCustom ? Visibility.Visible : Visibility.Collapsed;

            BuildGrid(CommonParamGrid, GetCommonFields());
            if (isMifa)
                BuildGrid(MifaParamGrid, GetMifaFields());
            else if (!isCustom)
                BuildGrid(IfaParamGrid, GetIfaFields());
            UpdateWarnings();
            DrawPreview();
        }

        // ── Parameter field definitions ──────────────────────────────
        // Each entry: (display label, registry key, default value, hint text)

        private List<(string Lbl, string Key, double Def, string Hint)> GetCommonFields() =>
            new()
            {
                ("Center frequency f0 (GHz):",  "FreqGHz",  2.4,   "Common: 2.4, 5.0, 5.8 GHz"),
            };

        private List<(string Lbl, string Key, double Def, string Hint)> GetIfaFields() =>
            new()
            {
                ("L — Radiator length (mm):",        "LengthL",        24.0, "≈ λ/4;  23–25 mm recommended for 2.4 GHz + FR4"),
                ("H — Height above GND (mm):",       "HeightH",         7.0, "H > 3 mm gives wider bandwidth and higher efficiency"),
                ("S — Feed gap (mm):",                "FeedGap",         3.0, "Key for impedance matching; tune S to reach 50 Ω"),
                ("W_short — Shorting stub (mm):",    "ShortPinWidth",   1.0, "Width of the grounding pin; 0.5–2.0 mm typical"),
                ("W_feed — Feed stub (mm):",          "FeedPinWidth",    1.0, "Width of the feed pin; 0.5–2.0 mm typical"),
                ("W_match — Matching sect. (mm):",   "MatchStubWidth",  1.0, "Arm between short & feed; affects impedance tuning"),
                ("W_rad — Radiator arm (mm):",        "RadiatorWidth",   1.0, "Main radiating element; 0.5–1.5 mm typical"),
            };

        private List<(string Lbl, string Key, double Def, string Hint)> GetMifaFields() =>
            new()
            {
                ("L — Radiator length (mm):",        "LengthL",        24.0, "≈ λ/4;  electrical length of the meander trace"),
                ("H — Height above GND (mm):",       "MifaHeightH",    3.9, "Total height of short/feed stubs above GND; must be > h1"),
                ("S — Feed gap (mm):",                "FeedGap",         3.0, "Gap between short & feed; key for 50 Ω matching"),
                ("h1 — Meander depth (mm):",          "MeanderHeight",  2.85, "Vertical extent of the meander fingers; h1 < H"),
                ("P — Meander pitch (mm):",          "MeanderPitch",    5.0, "Center-to-center spacing; N = (L−S)/(P+h1) auto"),
                ("W_short — Shorting stub (mm):",   "MifaShortWidth",  0.8, "Width of the grounding pin; 0.5–1.5 mm typical"),
                ("W_feed — Feed stub (mm):",         "MifaFeedWidth",   0.8, "Width of the feed pin; 0.5–1.5 mm typical"),
                ("W_mh — Horiz. trace (mm):",        "MifaHorizWidth",  0.5, "Horizontal meander segments; 0.3–1.0 mm typical"),
                ("W_mv — Vert. trace (mm):",         "MifaVertWidth",   0.5, "Vertical meander connections; 0.3–1.0 mm typical"),
            };

        // ── Build a parameter grid ──────────────────────────────────
        // Columns: [Label 130] [TextBox 80] [Hint *]

        private void BuildGrid(
            Grid grid,
            List<(string Lbl, string Key, double Def, string Hint)> fields)
        {
            grid.Children.Clear();
            grid.RowDefinitions.Clear();

            foreach (var (lbl, key, def, hint) in fields)
            {
                int row = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Label
                var label = new TextBlock
                {
                    Text              = lbl,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(0, 4, 6, 4),
                    FontWeight        = FontWeights.SemiBold
                };
                Grid.SetRow(label, row); Grid.SetColumn(label, 0);
                grid.Children.Add(label);

                // TextBox — reuse existing value if key already in registry
                string current = _boxes.TryGetValue(key, out var existing)
                    ? existing.Text : def.ToString("G5");
                var tb = new TextBox { Text = current };
                tb.TextChanged += (_, __) => { UpdateWarnings(); DrawPreview(); };

                // Auto-recalc L when FreqGHz changes
                if (key == "FreqGHz")
                    tb.TextChanged += (_, __) => AutoCalcL();

                Grid.SetRow(tb, row); Grid.SetColumn(tb, 1);
                grid.Children.Add(tb);
                _boxes[key] = tb;   // register (overwrite previous)

                // Hint text
                var hintTb = new TextBlock
                {
                    Text              = hint,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(8, 4, 0, 4),
                    FontSize          = 11,
                    Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontStyle         = FontStyles.Italic,
                    TextWrapping      = TextWrapping.Wrap
                };
                Grid.SetRow(hintTb, row); Grid.SetColumn(hintTb, 2);
                grid.Children.Add(hintTb);
            }
        }

        // ── Auto-calc L = lambda/4 ──────────────────────────────────

        private bool _suppressAutoCalc = false;

        private void AutoCalcL()
        {
            if (_suppressAutoCalc) return;
            if (!_boxes.ContainsKey("FreqGHz") || !_boxes.ContainsKey("LengthL")) return;

            double freq = Get("FreqGHz", 2.4);
            // Read Er from the stackup's first dielectric layer
            var stackup = IsCarrierSelected ? _vm.CarrierBoard.Stackup : _vm.Module.Stackup;
            double er = stackup.Layers
                .Where(l => !l.IsConductive && l.Type != LayerType.Mask && l.DielectricConstant > 0)
                .Select(l => l.DielectricConstant)
                .FirstOrDefault();
            if (er <= 0) er = 4.3;
            if (freq <= 0) return;

            _suppressAutoCalc = true;
            try { _boxes["LengthL"].Text = LambdaOver4(freq, er).ToString("F2"); }
            finally { _suppressAutoCalc = false; }
        }

        // ── Warning banner ──────────────────────────────────────────

        private void UpdateWarnings()
        {
            if (!IsInitialized) return;
            var sb = new StringBuilder();

            double h     = Get("HeightH",  999);
            double pitch = Get("MeanderPitch", 999);

            if (h < 3.0)
                sb.AppendLine("\u26a0 H < 3 mm: antenna height too small – bandwidth will narrow and radiation efficiency will drop. Recommended H ≥ 3 mm.");

            if (SelectedType == AntennaType.MeanderedInvertedF)
            {
                double wMax = Math.Max(Get("MifaHorizWidth", 0.5), Get("MifaVertWidth", 0.5));
                if (pitch < 2 * wMax)
                    sb.AppendLine($"\u26a0 Pitch ({pitch:F2}) < 2 × max(W_mh,W_mv) ({wMax:F2} mm = {2*wMax:F2} mm): trace spacing too tight, parasitic capacitance will degrade efficiency.");
            }

            if (SelectedType == AntennaType.MeanderedInvertedF)
            {
                double mp = Get("MeanderPitch", 5.0);
                double mh = Get("MeanderHeight", 2.85);
                double mH = Get("MifaHeightH", 3.9);
                double ms = Get("FeedGap", 3.0);
                if (mh >= mH)
                    sb.AppendLine($"\u26a0 Meander depth h1 ({mh:F2}) ≥ Height H ({mH:F2}): h1 must be less than H.");
                int n = (mp + mh) > 0 ? Math.Max(1, (int)Math.Floor((Get("LengthL", 24) - ms) / (mp + mh))) : 1;
                if (n > 8)
                    sb.AppendLine($"\u26a0 N = {n} (auto): meander turns > 8 will significantly increase losses. Increase P or reduce L.");
            }

            WarnText.Text       = sb.ToString().TrimEnd();
            WarnBorder.Visibility = sb.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Preview canvas ───────────────────────────────────────────

        private bool _suppressPreview = false;
        private double _prevOx, _prevOy, _prevScale;
        private readonly List<((double X, double Y) A, (double X, double Y) B)> _edgeSegments = new();
        private double _zoom = 1.0, _panXpx, _panYpx;

        /// <summary>Offset to convert local antenna coords → system (board) coords.
        /// sys = local + _sysOff</summary>
        private double _sysOffX, _sysOffY;

        /// <summary>Compute the local→board coordinate offset from current UI state.</summary>
        private void ComputeSysOffset()
        {
            double offX = double.TryParse(PcbOffsetXBox?.Text, out double ox) ? ox : 0;
            double offY = double.TryParse(PcbOffsetYBox?.Text, out double oy) ? oy : 0;
            double aw   = double.TryParse(AvailWidthBox?.Text, out double a) && a > 0 ? a : 15;
            double ah   = double.TryParse(AvailHeightBox?.Text, out double b) && b > 0 ? b : 10;

            double boardCX, boardTopY;
            if (IsCarrierSelected)
            {
                boardCX   = 0;
                boardTopY = 0;
            }
            else
            {
                boardCX   = _vm.Module.PositionX;
                boardTopY = -_vm.Module.PositionY;
            }
            _sysOffX = -offX - aw / 2.0 + boardCX;
            _sysOffY = -offY - ah + boardTopY;
        }

        private void DrawPreview()
        {
            if (!IsInitialized || _suppressPreview) return;

            try
            {
                ComputeSysOffset();
                PreviewCanvas.Children.Clear();
                EdgeInfoText.Text = "";
                _edgeSegments.Clear();
                if (SelectedType == AntennaType.InvertedF)
                    DrawIFA();
                else if (SelectedType == AntennaType.Custom)
                    DrawCustom();
                else
                    DrawMIFA();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DrawPreview error: {ex}");
            }
        }

        // ── Edge click: show coordinates of the nearest antenna edge ────────
        private void PreviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_prevScale <= 0 || _edgeSegments.Count == 0) { EdgeInfoText.Text = ""; return; }

            var pos = e.GetPosition(PreviewCanvas);
            // Canvas pixel → system (board) mm
            double wx = (pos.X - _prevOx) / _prevScale + _sysOffX;
            double wy = (_prevOy - pos.Y) / _prevScale + _sysOffY;

            // Find closest edge
            double bestDist = double.MaxValue;
            (double X, double Y) bestA = default, bestB = default;
            foreach (var (a, b) in _edgeSegments)
            {
                double d = PointToSegmentDist(wx, wy, a.X, a.Y, b.X, b.Y);
                if (d < bestDist) { bestDist = d; bestA = a; bestB = b; }
            }

            // Threshold: only report if click is reasonably close
            double thresholdMm = Math.Max(1.0, 8.0 / _prevScale);
            if (bestDist > thresholdMm) { EdgeInfoText.Text = ""; return; }

            // Highlight the clicked edge (convert system coords back to canvas pixels)
            double Tx(double sx) => _prevOx + (sx - _sysOffX) * _prevScale;
            double Ty(double sy) => _prevOy - (sy - _sysOffY) * _prevScale;
            for (int i = PreviewCanvas.Children.Count - 1; i >= 0; i--)
                if (PreviewCanvas.Children[i] is Line ln && ln.Tag as string == "EdgeHighlight")
                    PreviewCanvas.Children.RemoveAt(i);
            var hl = new Line
            {
                X1 = Tx(bestA.X), Y1 = Ty(bestA.Y),
                X2 = Tx(bestB.X), Y2 = Ty(bestB.Y),
                Stroke = Brushes.OrangeRed, StrokeThickness = 3,
                Tag = "EdgeHighlight"
            };
            PreviewCanvas.Children.Add(hl);

            double minX = Math.Min(bestA.X, bestB.X);
            double maxX = Math.Max(bestA.X, bestB.X);
            double minY = Math.Min(bestA.Y, bestB.Y);
            double maxY = Math.Max(bestA.Y, bestB.Y);
            EdgeInfoText.Text =
                $"Edge: ({bestA.X:F3}, {bestA.Y:F3}) → ({bestB.X:F3}, {bestB.Y:F3})    " +
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

        // ── Zoom / Pan ───────────────────────────────────────────────────────
        private void PreviewCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                _panXpx += e.Delta > 0 ? 20 : -20;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                _panYpx += e.Delta > 0 ? -20 : 20;
            }
            else
            {
                double factor = e.Delta > 0 ? 1.25 : 0.8;
                var pos = e.GetPosition(PreviewCanvas);
                double oldZoom = _zoom;
                _zoom = Math.Clamp(_zoom * factor, 0.1, 50);
                double r = _zoom / oldZoom;
                _panXpx = pos.X - (pos.X - _panXpx) * r;
                _panYpx = pos.Y - (pos.Y - _panYpx) * r;
            }
            DrawPreview();
            e.Handled = true;
        }

        private void PreviewCanvas_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            double step = 20;
            switch (e.Key)
            {
                case Key.Left:  _panXpx -= step; break;
                case Key.Right: _panXpx += step; break;
                case Key.Up:    _panYpx -= step; break;
                case Key.Down:  _panYpx += step; break;
                default: return;
            }
            DrawPreview();
            e.Handled = true;
        }

        private void FitView_Click(object sender, RoutedEventArgs e)
        {
            _zoom = 1.0; _panXpx = 0; _panYpx = 0;
            DrawPreview();
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)  { _zoom = Math.Clamp(_zoom * 1.25, 0.1, 50); DrawPreview(); }
        private void ZoomOut_Click(object sender, RoutedEventArgs e) { _zoom = Math.Clamp(_zoom * 0.8, 0.1, 50); DrawPreview(); }
        private void PanLeft_Click(object sender, RoutedEventArgs e)  { _panXpx -= 20; DrawPreview(); }
        private void PanRight_Click(object sender, RoutedEventArgs e) { _panXpx += 20; DrawPreview(); }
        private void PanUp_Click(object sender, RoutedEventArgs e)    { _panYpx -= 20; DrawPreview(); }
        private void PanDown_Click(object sender, RoutedEventArgs e)  { _panYpx += 20; DrawPreview(); }

        private Polyline MakePoly(Brush stroke, double thick, params Point[] pts)
        {
            var pl = new Polyline { Stroke = stroke, StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round, SnapsToDevicePixels = true };
            foreach (var p in pts) pl.Points.Add(new Point(Math.Round(p.X), Math.Round(p.Y)));
            return pl;
        }
        private Line MakeLine(double x1, double y1, double x2, double y2, Brush br, double thick = 1.5,
            DoubleCollection? dash = null)
        {
            var ln = new Line { X1 = Math.Round(x1), Y1 = Math.Round(y1), X2 = Math.Round(x2), Y2 = Math.Round(y2), Stroke = br, StrokeThickness = thick, SnapsToDevicePixels = true };
            if (dash != null) ln.StrokeDashArray = dash;
            return ln;
        }
        private void AddLabel(string text, double x, double y, Brush? fg = null, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text       = text,
                FontSize   = 10,
                Foreground = fg ?? Brushes.Black,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
            };
            Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
            PreviewCanvas.Children.Add(tb);
        }
        private void AddDot(double cx, double cy, Brush fill)
        {
            var el = new Ellipse { Width = 7, Height = 7, Fill = fill };
            Canvas.SetLeft(el, cx - 3.5); Canvas.SetTop(el, cy - 3.5);
            PreviewCanvas.Children.Add(el);
        }

        // ── Helpers: draw a filled rectangle on the canvas ─────────
        private void AddFilledRect(double x, double y, double w, double h, Brush fill, Brush? stroke = null)
        {
            if (w < 0) { x += w; w = -w; }
            if (h < 0) { y += h; h = -h; }
            // Record world-space edges in system (board) coordinates
            if (_prevScale > 0)
            {
                double mmX1 = (x - _prevOx) / _prevScale + _sysOffX;
                double mmY1 = (_prevOy - y) / _prevScale + _sysOffY;
                double mmX2 = (x + w - _prevOx) / _prevScale + _sysOffX;
                double mmY2 = (_prevOy - (y + h)) / _prevScale + _sysOffY;
                // Round to 3 decimals to remove floating-point noise
                mmX1 = Math.Round(mmX1, 3); mmY1 = Math.Round(mmY1, 3);
                mmX2 = Math.Round(mmX2, 3); mmY2 = Math.Round(mmY2, 3);
                _edgeSegments.Add(((mmX1, mmY1), (mmX2, mmY1))); // top
                _edgeSegments.Add(((mmX1, mmY2), (mmX2, mmY2))); // bottom
                _edgeSegments.Add(((mmX1, mmY1), (mmX1, mmY2))); // left
                _edgeSegments.Add(((mmX2, mmY1), (mmX2, mmY2))); // right
            }
            // Use Floor for position and Ceiling for size to ensure overlap (no gaps)
            double x2 = x + w;
            double y2 = y + h;
            x = Math.Floor(x);
            y = Math.Floor(y);
            w = Math.Max(Math.Ceiling(x2) - x, 1);
            h = Math.Max(Math.Ceiling(y2) - y, 1);
            var r = new System.Windows.Shapes.Rectangle
            {
                Width           = w,
                Height          = h,
                Fill            = fill,
                Stroke          = stroke,
                StrokeThickness = stroke != null ? 0.5 : 0,
                SnapsToDevicePixels = true,
                UseLayoutRounding   = true,
                Tag = "AntennaRect"
            };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            PreviewCanvas.Children.Add(r);
        }

        // ── Draw a horizontal dimension line with mm value ──────────
        private void AddDimH(double x1, double x2, double y, double mmVal, string label, Brush br)
        {
            var dsh = new DoubleCollection(new[] { 3.0, 2.0 });
            PreviewCanvas.Children.Add(MakeLine(x1, y, x2, y, br, 0.8, dsh));
            PreviewCanvas.Children.Add(MakeLine(x1, y - 3, x1, y + 3, br, 0.8));
            PreviewCanvas.Children.Add(MakeLine(x2, y - 3, x2, y + 3, br, 0.8));
            AddLabel($"{label}={mmVal:F1}", (x1 + x2) / 2 - 12, y - 13, br);
        }
        // ── Draw a vertical dimension line with mm value ────────────
        private void AddDimV(double x, double y1, double y2, double mmVal, string label, Brush br)
        {
            var dsh = new DoubleCollection(new[] { 3.0, 2.0 });
            PreviewCanvas.Children.Add(MakeLine(x, y1, x, y2, br, 0.8, dsh));
            PreviewCanvas.Children.Add(MakeLine(x - 3, y1, x + 3, y1, br, 0.8));
            PreviewCanvas.Children.Add(MakeLine(x - 3, y2, x + 3, y2, br, 0.8));
            AddLabel($"{label}={mmVal:F1}", x + 2, (y1 + y2) / 2 - 6, br);
        }

        /// <summary>
        /// Compute combined bounding box of antenna + PCB space in mm.
        /// Returns (minX, minY, maxX, maxY) to center the drawing.
        /// </summary>
        private (double minX, double minY, double maxX, double maxY) GetCombinedBounds(
            double antennaW, double antennaH)
        {
            double minX = 0, minY = 0, maxX = antennaW, maxY = antennaH;

            if (double.TryParse(AvailWidthBox.Text, out double aw) && aw > 0 &&
                double.TryParse(AvailHeightBox.Text, out double ah) && ah > 0)
            {
                double offX = double.TryParse(PcbOffsetXBox.Text, out double oxv) ? oxv : 0;
                double offY = double.TryParse(PcbOffsetYBox.Text, out double oyv) ? oyv : 0;
                minX = Math.Min(minX, offX);
                minY = Math.Min(minY, offY);
                maxX = Math.Max(maxX, offX + aw);
                maxY = Math.Max(maxY, offY + ah);
            }
            return (minX, minY, maxX, maxY);
        }

        private void DrawIFA()
        {
            double cw = PreviewCanvas.ActualWidth  > 10 ? PreviewCanvas.ActualWidth  : 560;
            double ch = PreviewCanvas.ActualHeight > 10 ? PreviewCanvas.ActualHeight : 260;

            // Read actual mm values
            double L   = Get("LengthL", 24);
            double H   = Get("HeightH", 7);
            double S   = Get("FeedGap", 3);
            double wSh = Get("ShortPinWidth", 1);
            double wFe = Get("FeedPinWidth", 1);
            double wMa = Get("MatchStubWidth", 1);
            double wRa = Get("RadiatorWidth", 1);
            double wMax = Math.Max(Math.Max(wSh, wFe), Math.Max(wMa, wRa));

            // Compute combined bounding box (antenna + PCB space)
            var (bx0, by0, bx1, by1) = GetCombinedBounds(L, H + wMax / 2);
            double bw = bx1 - bx0;
            double bh = by1 - by0;

            // Compute uniform scale
            double margin = 50;  // px reserved for labels
            double scaleX = (cw - 2 * margin) / Math.Max(bw, 1);
            double scaleY = (ch - 2 * margin) / Math.Max(bh, 1);
            double sc     = Math.Min(scaleX, scaleY) * _zoom;
            sc = Math.Max(sc, 0.5);  // lower bound

            // Center the combined bounding box in canvas
            double drawW = bw * sc;
            double drawH = bh * sc;
            double ox = (cw - drawW) / 2 - bx0 * sc + _panXpx;
            double oy = (ch + drawH) / 2 + by0 * sc + _panYpx;  // GND line y (y increases downward)

            // Scaled helper
            double px(double mm) => ox + mm * sc;
            double py(double mm) => oy - mm * sc;
            double pw(double mm) => Math.Max(mm * sc, 1);

            _prevOx = ox; _prevOy = oy; _prevScale = sc;

            var blue = new SolidColorBrush(Color.FromRgb(0x20, 0x60, 0xCC));
            var ltbl = new SolidColorBrush(Color.FromRgb(0x60, 0xA0, 0xEE));
            var gray = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
            var red  = new SolidColorBrush(Color.FromRgb(0xCC, 0x20, 0x20));
            var grn  = new SolidColorBrush(Color.FromRgb(0x20, 0xAA, 0x40));

            // GND plane (full width, thin gray bar)
            AddFilledRect(ox - 4, oy, L * sc + 8, 3, gray);
            AddLabel("GND", ox - 4, oy + 4, gray);

            // --- Draw horizontal segments first ---
            // 2. Matching section: extend left/right to cover half of connecting vertical widths
            AddFilledRect(px(0) - pw(wSh) / 2, py(H) - pw(wMa) / 2,
                          S * sc + pw(wSh) / 2 + pw(wFe) / 2, pw(wMa), ltbl);
            if (S * sc > 20)
                AddLabel("Match", px(S / 2) - 12, py(H) - pw(wMa) / 2 - 13, ltbl);

            // 4. Radiating arm: extend left to cover half of feed pin width
            AddFilledRect(px(S) - pw(wFe) / 2, py(H) - pw(wRa) / 2,
                          (L - S) * sc + pw(wFe) / 2, pw(wRa), blue);
            AddLabel("Radiator", px((S + L) / 2) - 18, py(H) - pw(wRa) / 2 - 13, blue, bold: true);

            // --- Draw vertical segments on top (extend to cover junction corners) ---
            // 1. Shorting stub: extend top by half match width
            AddFilledRect(px(0) - pw(wSh) / 2, py(H) - pw(wMa) / 2,
                          pw(wSh), H * sc + pw(wMa) / 2, blue);
            AddLabel("Short", px(0) - 4, py(H) - 13, blue);

            // 3. Feed stub: extend top by half of max(match, radiator) width
            double hwTopFeed = Math.Max(pw(wMa), pw(wRa)) / 2;
            AddFilledRect(px(S) - pw(wFe) / 2, py(H) - hwTopFeed,
                          pw(wFe), H * sc + hwTopFeed, red);
            AddLabel("Feed", px(S) + pw(wFe) / 2 + 2, py(H / 2) - 5, red);

            // Dimension annotations
            double dimY1 = oy + 12;
            AddDimH(px(0), px(L), dimY1, L, "L", grn);
            AddDimH(px(0), px(S), dimY1 + 16, S, "S", grn);
            double dimX1 = px(L) + 8;
            AddDimV(dimX1, py(H), oy, H, "H", grn);

            // Width labels next to each segment
            AddLabel($"W={wSh:F1}", px(0) - 30, py(H / 2), grn);
            AddLabel($"W={wFe:F1}", px(S) + pw(wFe) / 2 + 2, py(H) + 2, grn);
            if (S * sc > 20)
                AddLabel($"W={wMa:F1}", px(S / 2) - 8, py(H) + pw(wMa) / 2 + 2, grn);
            AddLabel($"W={wRa:F1}", px((S + L) / 2) - 8, py(H) + pw(wRa) / 2 + 2, grn);

            // Available space overlay
            DrawAvailSpaceOverlay(ox, oy, sc, L, H);

            // Origin cross at (0,0)
            DrawOriginCross(px, py, cw, ch);

            // Axis indicator
            DrawAxisIndicator();

            // Dimension summary
            UpdateDimensionSummary_IFA();
        }

        private void UpdateDimensionSummary_IFA()
        {
            double L  = Get("LengthL", 24);
            double H  = Get("HeightH", 7);
            double S  = Get("FeedGap", 3);
            double ws = Get("ShortPinWidth", 1);
            double wf = Get("FeedPinWidth", 1);
            double wm = Get("MatchStubWidth", 1);
            double wr = Get("RadiatorWidth", 1);

            DimensionSummary.Text =
                $"Actual size:  L = {L:F2} mm  |  H = {H:F2} mm  |  S = {S:F2} mm\n"
              + $"Trace widths: W_short = {ws:F2}  |  W_feed = {wf:F2}  |  W_match = {wm:F2}  |  W_rad = {wr:F2} mm\n"
              + $"Footprint:    {L:F1} × {H:F1} mm";
        }

        private void DrawMIFA()
        {
            double cw = PreviewCanvas.ActualWidth  > 10 ? PreviewCanvas.ActualWidth  : 560;
            double ch = PreviewCanvas.ActualHeight > 10 ? PreviewCanvas.ActualHeight : 260;

            // Read actual mm values
            double L      = Get("LengthL", 24);
            double pitch  = Get("MeanderPitch", 5.0);
            double H      = Get("MifaHeightH", 3.9);   // total height above GND
            double Hm     = Get("MeanderHeight", 2.85); // meander finger depth
            double feedS  = Get("FeedGap", 3);
            double wSh    = Get("MifaShortWidth", 0.8);
            double wFe    = Get("MifaFeedWidth", 0.8);
            double wMh    = Get("MifaHorizWidth", 0.5);
            double wMv    = Get("MifaVertWidth", 0.5);
            if (Hm >= H) Hm = H * 0.8; // safety clamp
            int    n_full = (pitch + Hm) > 0 ? Math.Max(1, (int)Math.Floor((L - feedS) / (pitch + Hm))) : 1;
            double remaining = L - feedS - n_full * (pitch + Hm);
            bool   hasPartial = remaining >= pitch;  // fold if remaining >= one pitch
            int    n = hasPartial ? n_full + 1 : n_full;
            double tailLen     = hasPartial ? 0 : Math.Max(remaining, 0);
            double partialHm   = hasPartial ? Math.Max(remaining - pitch, 0) : 0; // reduced vertical for last turn

            // Meander y-coordinates in mm:  top = H, bottom = H − Hm
            double yTop = H;

            // Total physical width = feed gap + sum of pitches + tail
            double totalW   = feedS + n * pitch + tailLen;
            double totalH   = H;
            double wMaxAll  = Math.Max(Math.Max(wSh, wFe), Math.Max(wMh, wMv));

            // Compute combined bounding box (antenna + PCB space)
            var (bx0, by0, bx1, by1) = GetCombinedBounds(totalW, H + wMaxAll / 2);
            double bw = bx1 - bx0;
            double bh = by1 - by0;

            // Compute uniform scale
            double margin = 50;
            double scaleX = (cw - 2 * margin) / Math.Max(bw, 1);
            double scaleY = (ch - 2 * margin) / Math.Max(bh, 1);
            double sc     = Math.Min(scaleX, scaleY) * _zoom;
            sc = Math.Max(sc, 0.5);

            // Center the combined bounding box in canvas
            double drawW = bw * sc;
            double drawH = bh * sc;
            double ox = (cw - drawW) / 2 - bx0 * sc + _panXpx;
            double oy = (ch + drawH) / 2 + by0 * sc + _panYpx;

            double px(double mm) => ox + mm * sc;
            double py(double mm) => oy - mm * sc;
            double pw(double mm) => Math.Max(mm * sc, 1);

            _prevOx = ox; _prevOy = oy; _prevScale = sc;

            var blue = new SolidColorBrush(Color.FromRgb(0x20, 0x60, 0xCC));
            var gray = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
            var red  = new SolidColorBrush(Color.FromRgb(0xCC, 0x20, 0x20));
            var grn  = new SolidColorBrush(Color.FromRgb(0x20, 0xAA, 0x40));
            var purp = new SolidColorBrush(Color.FromRgb(0x88, 0x30, 0xCC));
            var oran = new SolidColorBrush(Color.FromRgb(0xDD, 0x88, 0x00));

            // GND bar
            AddFilledRect(ox - 4, oy, totalW * sc + 8, 3, gray);
            AddLabel("GND", ox - 4, oy + 4, gray);

            // --- Draw horizontal segments first ---
            // 2. Matching section at y = H: extend left/right to cover connecting vertical widths
            AddFilledRect(px(0) - pw(wSh) / 2, py(yTop) - pw(wMh) / 2,
                          feedS * sc + pw(wSh) / 2 + pw(wFe) / 2, pw(wMh), oran);

            // Meander traces (start at x = feedS, y = yTop)
            double mx = feedS, my_mm = yTop;
            for (int i = 0; i < n; i++)
            {
                double h_i = (hasPartial && i == n - 1) ? partialHm : Hm;
                double yBotI = H - h_i;
                double nx = mx + pitch;
                // Vertical connection targets
                double ny_mm = (i % 2 == 0) ? yBotI : yTop;
                double vy0 = Math.Min(my_mm, ny_mm);
                double vy1 = Math.Max(my_mm, ny_mm);

                // 4. Horizontal trace: extend left/right to cover half vertical width
                AddFilledRect(px(mx) - pw(wMv) / 2, py(my_mm) - pw(wMh) / 2,
                              pitch * sc + pw(wMv), pw(wMh), blue);
                // 5. Vertical trace: extend top/bottom to cover half horizontal width
                if (vy1 - vy0 > 1e-6)
                    AddFilledRect(px(nx) - pw(wMv) / 2, py(vy1) - pw(wMh) / 2,
                              pw(wMv), (vy1 - vy0) * sc + pw(wMh), purp);
                mx = nx; my_mm = ny_mm;
            }
            // Tail: only if remaining couldn't be folded
            if (tailLen > 0)
                AddFilledRect(px(mx) - pw(wMv) / 2, py(my_mm) - pw(wMh) / 2,
                              tailLen * sc + pw(wMv) / 2, pw(wMh), blue);

            // --- Draw vertical segments on top ---
            // 1. Shorting stub: from GND (y=0) to top (y=H), extend top by half horiz width
            AddFilledRect(px(0) - pw(wSh) / 2, py(H) - pw(wMh) / 2,
                          pw(wSh), H * sc + pw(wMh) / 2, blue);
            AddLabel("Short", px(0) - 4, py(H) - 13, blue);

            // 3. Feed stub: from GND (y=0) to top (y=H), extend top by half horiz width
            AddFilledRect(px(feedS) - pw(wFe) / 2, py(H) - pw(wMh) / 2,
                          pw(wFe), H * sc + pw(wMh) / 2, red);
            AddLabel("Feed", px(feedS) + pw(wFe) / 2 + 2, py(H / 2) - 5, red);

            // Dimension annotations
            double dimY1 = oy + 18;
            AddDimH(px(0), px(totalW), dimY1, totalW, "Total", grn);
            double dimX1 = px(totalW) + 20;
            // H dimension (total height)
            AddDimV(dimX1, py(H), oy, H, "H", grn);
            // h1 dimension (meander depth)
            AddDimV(dimX1 + 40, py(yTop), py(H - Hm), Hm, "h1", grn);
            // Feed gap
            AddDimH(px(0), px(feedS), dimY1 + 18, feedS, "S", grn);
            // Single pitch between first two vertical legs
            if (n >= 1)
                AddDimH(px(feedS), px(feedS + pitch), dimY1 + 18 + 18, pitch, "P", grn);

            // Trace length estimate
            double traceLen = feedS + n_full * (pitch + Hm) + (hasPartial ? pitch + partialHm : 0) + tailLen;
            AddLabel($"N={n} (auto)  trace≈{traceLen:F1}mm", cw / 2 - 50, 3, blue, bold: true);

            // Available space overlay
            DrawAvailSpaceOverlay(ox, oy, sc, totalW, H);

            // Origin cross at (0,0)
            DrawOriginCross(px, py, cw, ch);

            // Axis indicator
            DrawAxisIndicator();

            // Dimension summary
            UpdateDimensionSummary_MIFA(n);
        }

        private void UpdateDimensionSummary_MIFA(int n)
        {
            double L     = Get("LengthL", 24);
            double H     = Get("MifaHeightH", 3.9);
            double Hm    = Get("MeanderHeight", 2.85);
            double pitch = Get("MeanderPitch", 5.0);
            double feedS = Get("FeedGap", 3);
            double ws    = Get("MifaShortWidth", 0.8);
            double wfeed = Get("MifaFeedWidth", 0.8);
            double wmh   = Get("MifaHorizWidth", 0.5);
            double wmv   = Get("MifaVertWidth", 0.5);
            if (Hm >= H) Hm = H * 0.8;
            int    n_full = (pitch + Hm) > 0 ? Math.Max(1, (int)Math.Floor((L - feedS) / (pitch + Hm))) : 1;
            double remaining = L - feedS - n_full * (pitch + Hm);
            bool   hasPartial = remaining >= pitch;
            int    nTotal = hasPartial ? n_full + 1 : n_full;
            double tailLen    = hasPartial ? 0 : Math.Max(remaining, 0);
            double partialHm  = hasPartial ? Math.Max(remaining - pitch, 0) : 0;
            double totalW     = feedS + nTotal * pitch + tailLen;
            double traceLen   = feedS + n_full * (pitch + Hm) + (hasPartial ? pitch + partialHm : 0) + tailLen;

            DimensionSummary.Text =
                $"Actual size:  L = {L:F2} mm  |  H = {H:F2} mm  |  h1 = {Hm:F2} mm  |  Pitch = {pitch:F2} mm  |  S = {feedS:F2} mm\n"
              + $"Meander count N = {nTotal} (auto from (L−S)/(P+h1)) → physical width = {totalW:F1} mm"
              + (hasPartial ? $"  (last turn h1' = {partialHm:F2} mm)" : "")
              + $"\nTrace length ≈ {traceLen:F1} mm  (= L)"
              + $"\nTrace widths: W_short = {ws:F2}  |  W_feed = {wfeed:F2}  |  W_mh = {wmh:F2}  |  W_mv = {wmv:F2} mm\n"
              + $"Footprint:    {totalW:F1} × {H:F1} mm";
        }

        // ── Draw Custom (polygon) ───────────────────────────────────

        private void DrawCustom()
        {
            if (_customVertices.Count < 2) { DimensionSummary.Text = "Add at least 3 vertices to define the polygon."; return; }

            double cw = PreviewCanvas.ActualWidth  > 10 ? PreviewCanvas.ActualWidth  : 560;
            double ch = PreviewCanvas.ActualHeight > 10 ? PreviewCanvas.ActualHeight : 260;

            double minX = _customVertices.Min(v => v.X);
            double minY = _customVertices.Min(v => v.Y);
            double maxX = _customVertices.Max(v => v.X);
            double maxY = _customVertices.Max(v => v.Y);
            double polyW = maxX - minX;
            double polyH = maxY - minY;
            if (polyW < 1e-6) polyW = 10;
            if (polyH < 1e-6) polyH = 10;

            // Combined bounds (using minX as origin)
            var (bx0, by0, bx1, by1) = GetCombinedBounds(polyW, polyH);
            // Adjust for polygon not starting at 0,0
            bx0 = Math.Min(bx0, minX); by0 = Math.Min(by0, minY);
            bx1 = Math.Max(bx1, maxX); by1 = Math.Max(by1, maxY);
            double bw = bx1 - bx0;
            double bh = by1 - by0;

            double margin = 50;
            double scaleXv = (cw - 2 * margin) / Math.Max(bw, 1);
            double scaleYv = (ch - 2 * margin) / Math.Max(bh, 1);
            double sc      = Math.Min(scaleXv, scaleYv) * _zoom;
            sc = Math.Max(sc, 0.5);

            double drawW = bw * sc;
            double drawH = bh * sc;
            double ox = (cw - drawW) / 2 - bx0 * sc + _panXpx;
            double oy = (ch + drawH) / 2 + by0 * sc + _panYpx;

            double px(double mm) => ox + mm * sc;
            double py(double mm) => oy - mm * sc;    // Y-flip (up = positive)

            _prevOx = ox; _prevOy = oy; _prevScale = sc;

            var blue = new SolidColorBrush(Color.FromRgb(0x20, 0x60, 0xCC));
            var gray = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
            var grn  = new SolidColorBrush(Color.FromRgb(0x20, 0xAA, 0x40));

            // GND line at y=0
            PreviewCanvas.Children.Add(MakeLine(px(bx0 - 1), py(0), px(bx1 + 1), py(0), gray, 1.5));
            AddLabel("GND (y=0)", px(bx0 - 1), py(0) + 3, gray);

            // Filled polygon
            var poly = new Polygon
            {
                Stroke          = blue,
                StrokeThickness = 1.5,
                Fill            = new SolidColorBrush(Color.FromArgb(55, 0x20, 0x60, 0xCC)),
                SnapsToDevicePixels = true
            };
            foreach (var v in _customVertices)
                poly.Points.Add(new System.Windows.Point(px(v.X), py(v.Y)));
            PreviewCanvas.Children.Add(poly);

            // Record polygon edges for edge-click (in system/board coordinates)
            for (int ei = 0; ei < _customVertices.Count; ei++)
            {
                var va = _customVertices[ei];
                var vb = _customVertices[(ei + 1) % _customVertices.Count];
                _edgeSegments.Add(((va.X + _sysOffX, va.Y + _sysOffY), (vb.X + _sysOffX, vb.Y + _sysOffY)));
            }

            // Vertex dots and index labels
            for (int i = 0; i < _customVertices.Count; i++)
            {
                double cx = px(_customVertices[i].X), cy = py(_customVertices[i].Y);
                bool isFirst = i == 0;
                bool isLast  = i == _customVertices.Count - 1;
                bool isClosed = isLast && _customVertices.Count > 2
                    && Math.Abs(_customVertices[0].X - _customVertices[i].X) < 1e-4
                    && Math.Abs(_customVertices[0].Y - _customVertices[i].Y) < 1e-4;

                var dot = new Ellipse
                {
                    Width  = isFirst ? 9 : 6,
                    Height = isFirst ? 9 : 6,
                    Fill   = isClosed ? Brushes.Green :
                             isFirst  ? Brushes.DarkBlue : Brushes.CornflowerBlue
                };
                Canvas.SetLeft(dot, cx - dot.Width / 2);
                Canvas.SetTop(dot,  cy - dot.Height / 2);
                PreviewCanvas.Children.Add(dot);

                if (!isClosed)
                {
                    var lbl = new TextBlock
                    {
                        Text       = i.ToString(),
                        FontSize   = 9,
                        Foreground = Brushes.DimGray
                    };
                    Canvas.SetLeft(lbl, cx + 4); Canvas.SetTop(lbl, cy - 7);
                    PreviewCanvas.Children.Add(lbl);
                }
            }

            // Origin cross at system (0,0)
            {
                double sysLocalX = -_sysOffX;  // system origin in local coords
                double sysLocalY = -_sysOffY;
                var oxPx = px(sysLocalX); var oyPx = py(sysLocalY);
                if (oxPx > -5 && oxPx < cw + 5 && oyPx > -5 && oyPx < ch + 5)
                {
                    PreviewCanvas.Children.Add(MakeLine(0, oyPx, cw, oyPx, Brushes.LightGray, 0.8,
                        new DoubleCollection(new[] { 4.0, 3.0 })));
                    PreviewCanvas.Children.Add(MakeLine(oxPx, 0, oxPx, ch, Brushes.LightGray, 0.8,
                        new DoubleCollection(new[] { 4.0, 3.0 })));
                    AddLabel("(0,0)", oxPx + 2, oyPx + 1, Brushes.Red, bold: true);
                }
            }

            // Dimension: width and height
            double dimYpos = py(minY) + 12;
            AddDimH(px(minX), px(maxX), dimYpos, polyW, "W", grn);
            AddDimV(px(maxX) + 8, py(maxY), py(minY), polyH, "H", grn);

            // Available space overlay
            DrawAvailSpaceOverlay(ox, oy, sc, polyW, polyH);

            // Axis indicator
            DrawAxisIndicator();

            // Dimension summary
            DimensionSummary.Text =
                $"Custom polygon: {_customVertices.Count} vertices\n"
              + $"Bounding box: ({minX:F2}, {minY:F2}) → ({maxX:F2}, {maxY:F2})\n"
              + $"Footprint: {polyW:F1} × {polyH:F1} mm";
        }

        // ── Custom vertex editor handlers ───────────────────────────

        private void CustomAddVertex_Click(object sender, RoutedEventArgs e)
        {
            _customVertices.Add(new ShapeVertex(0, 0));
            CustomVertexGrid.SelectedIndex = _customVertices.Count - 1;
            CustomVertexGrid.ScrollIntoView(CustomVertexGrid.SelectedItem);
            DrawPreview();
        }

        private void CustomDeleteVertex_Click(object sender, RoutedEventArgs e)
        {
            if (CustomVertexGrid.SelectedItem is ShapeVertex sv)
                _customVertices.Remove(sv);
            DrawPreview();
        }

        private void CustomMoveUp_Click(object sender, RoutedEventArgs e)
        {
            int i = CustomVertexGrid.SelectedIndex;
            if (i <= 0 || i >= _customVertices.Count) return;
            _customVertices.Move(i, i - 1);
            CustomVertexGrid.SelectedIndex = i - 1;
            DrawPreview();
        }

        private void CustomMoveDown_Click(object sender, RoutedEventArgs e)
        {
            int i = CustomVertexGrid.SelectedIndex;
            if (i < 0 || i >= _customVertices.Count - 1) return;
            _customVertices.Move(i, i + 1);
            CustomVertexGrid.SelectedIndex = i + 1;
            DrawPreview();
        }

        private void CustomClosePolygon_Click(object sender, RoutedEventArgs e)
        {
            if (_customVertices.Count < 3)
            {
                MessageBox.Show("Need at least 3 vertices to close.", "Close Polygon",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var first = _customVertices[0];
            var last  = _customVertices[_customVertices.Count - 1];
            if (Math.Abs(first.X - last.X) < 1e-4 && Math.Abs(first.Y - last.Y) < 1e-4)
                return; // already closed
            _customVertices.Add(new ShapeVertex(first.X, first.Y));
            DrawPreview();
        }

        private void CustomVertexGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(DrawPreview));
        }

        /// <summary>Draw origin cross at system (board) coordinate (0,0) with label.</summary>
        private void DrawOriginCross(Func<double, double> px, Func<double, double> py, double cw, double ch)
        {
            // System (0,0) in local coords is (-_sysOffX, -_sysOffY)
            double oxPx = px(-_sysOffX), oyPx = py(-_sysOffY);
            if (oxPx < -5 || oxPx > cw + 5 || oyPx < -5 || oyPx > ch + 5) return;
            double crossLen = Math.Min(15, Math.Min(cw, ch) * 0.06);
            PreviewCanvas.Children.Add(MakeLine(oxPx - crossLen, oyPx, oxPx + crossLen, oyPx, Brushes.Red, 1));
            PreviewCanvas.Children.Add(MakeLine(oxPx, oyPx - crossLen, oxPx, oyPx + crossLen, Brushes.Red, 1));
            AddLabel("(0,0)", oxPx + 2, oyPx + 1, Brushes.Red, bold: true);
        }

        // ── Available space overlay ─────────────────────────────────
        /// <summary>
        /// Draws a small X/Y axis indicator in the bottom-left corner of the preview canvas.
        /// </summary>
        private void DrawAxisIndicator()
        {
            double cw = PreviewCanvas.ActualWidth;
            double ch = PreviewCanvas.ActualHeight;
            if (cw < 60 || ch < 60) return;

            const double len = 32;   // arrow length in px
            const double margin = 14; // distance from canvas edge
            double oX = margin;       // origin X on canvas
            double oY = ch - margin;  // origin Y on canvas

            var axisBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            const double thick = 1.2;
            const double arrowSize = 5;

            // X axis: origin → right
            PreviewCanvas.Children.Add(MakeLine(oX, oY, oX + len, oY, axisBrush, thick));
            // X arrowhead
            PreviewCanvas.Children.Add(MakeLine(oX + len, oY, oX + len - arrowSize, oY - arrowSize * 0.6, axisBrush, thick));
            PreviewCanvas.Children.Add(MakeLine(oX + len, oY, oX + len - arrowSize, oY + arrowSize * 0.6, axisBrush, thick));
            AddLabel("X", oX + len + 2, oY - 7, axisBrush, bold: true);

            // Y axis: origin → up
            PreviewCanvas.Children.Add(MakeLine(oX, oY, oX, oY - len, axisBrush, thick));
            // Y arrowhead
            PreviewCanvas.Children.Add(MakeLine(oX, oY - len, oX - arrowSize * 0.6, oY - len + arrowSize, axisBrush, thick));
            PreviewCanvas.Children.Add(MakeLine(oX, oY - len, oX + arrowSize * 0.6, oY - len + arrowSize, axisBrush, thick));
            AddLabel("Y", oX - 3, oY - len - 15, axisBrush, bold: true);
        }

        /// <summary>
        /// Draws a semi-transparent rectangle representing the available PCB space,
        /// offset by user-specified X/Y values, and updates the fit-check status indicator.
        /// Clearance (left/right/top only, no bottom) is shown as dashed inner lines.
        /// </summary>
        private void DrawAvailSpaceOverlay(double ox, double oy, double sc, double antennaW_mm, double antennaH_mm)
        {
            if (!double.TryParse(AvailWidthBox.Text, out double aw) || aw <= 0) return;
            if (!double.TryParse(AvailHeightBox.Text, out double ah) || ah <= 0) return;

            // Read PCB-space offset (mm)
            double offX = double.TryParse(PcbOffsetXBox.Text, out double ox_mm) ? ox_mm : 0;
            double offY = double.TryParse(PcbOffsetYBox.Text, out double oy_mm) ? oy_mm : 0;

            // Read clearance (mm)
            double clr = double.TryParse(ClearanceBox.Text, out double clrVal) && clrVal >= 0 ? clrVal : 0;

            double rw = aw * sc;
            double rh = ah * sc;

            // Check whether antenna footprint fits inside the clearance-reduced PCB area
            // Antenna occupies [0, antennaW] x [0, antennaH] in mm
            // PCB area occupies [offX, offX+aw] x [offY, offY+ah] in mm
            // Usable area (with clearance on left/right/top, not bottom):
            //   X: [offX + clr, offX + aw - clr]
            //   Y: [offY, offY + ah - clr]
            const double eps = 0.01; // tolerance for F2 rounding in offset text boxes
            bool fits = (offX + clr) <= eps && (offX + aw - clr) >= antennaW_mm - eps
                     && offY <= eps && (offY + ah - clr) >= antennaH_mm - eps;

            // Semi-transparent overlay: green if fits, red if not
            var fillColor = fits
                ? Color.FromArgb(30, 0x20, 0xCC, 0x40)    // green tint
                : Color.FromArgb(35, 0xCC, 0x20, 0x20);   // red tint
            var borderColor = fits
                ? Color.FromArgb(160, 0x20, 0xAA, 0x40)
                : Color.FromArgb(160, 0xCC, 0x20, 0x20);

            // Position the PCB rectangle with the offset applied
            double rectLeft = ox + offX * sc;
            double rectTop  = oy - (offY + ah) * sc;

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width           = rw,
                Height          = rh,
                Fill            = new SolidColorBrush(fillColor),
                Stroke          = new SolidColorBrush(borderColor),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection(new[] { 5.0, 3.0 })
            };
            Canvas.SetLeft(rect, rectLeft);
            Canvas.SetTop(rect, rectTop);
            PreviewCanvas.Children.Add(rect);

            // Label on the overlay (above the rectangle to avoid antenna body)
            AddLabel($"{aw:F1}×{ah:F1} mm", rectLeft + 3, rectTop - 14,
                new SolidColorBrush(borderColor), bold: true);

            // ── Draw clearance lines (left / right / top only) ──────────
            if (clr > 0)
            {
                var clrBrush = new SolidColorBrush(Color.FromArgb(180, 0xFF, 0x99, 0x00)); // orange
                var clrDash  = new DoubleCollection(new[] { 2.0, 2.0 });
                double clrPx = clr * sc;

                // Left clearance line (vertical)
                PreviewCanvas.Children.Add(MakeLine(
                    rectLeft + clrPx, rectTop, rectLeft + clrPx, rectTop + rh,
                    clrBrush, 1.0, clrDash));
                // Right clearance line (vertical)
                PreviewCanvas.Children.Add(MakeLine(
                    rectLeft + rw - clrPx, rectTop, rectLeft + rw - clrPx, rectTop + rh,
                    clrBrush, 1.0, clrDash));
                // Top clearance line (horizontal)
                PreviewCanvas.Children.Add(MakeLine(
                    rectLeft, rectTop + clrPx, rectLeft + rw, rectTop + clrPx,
                    clrBrush, 1.0, clrDash));

                // Label clearance value near the top-right corner (outside clearance area)
                AddLabel($"clr={clr:G4}", rectLeft + rw - clrPx + 4, rectTop + clrPx + 1,
                    clrBrush);
            }

            // Update fit status text
            if (fits)
            {
                FitStatusText.Text       = "✔ Antenna fits";
                FitStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x99, 0x30));
            }
            else
            {
                var parts = new List<string>();
                if ((offX + clr) > eps) parts.Add($"left gap {offX + clr:F2}");
                if (antennaW_mm - (offX + aw - clr) > eps) parts.Add($"width +{antennaW_mm - (offX + aw - clr):F2}");
                if (offY > eps)  parts.Add($"bottom gap {offY:F1}");
                if (antennaH_mm - (offY + ah - clr) > eps) parts.Add($"height +{antennaH_mm - (offY + ah - clr):F2}");
                FitStatusText.Text       = parts.Count > 0
                    ? $"✖ Exceeds by {string.Join(", ", parts)} mm"
                    : "✔ Antenna fits";
                FitStatusText.Foreground = parts.Count > 0
                    ? new SolidColorBrush(Color.FromRgb(0xCC, 0x20, 0x20))
                    : new SolidColorBrush(Color.FromRgb(0x20, 0x99, 0x30));
            }
        }

        // ── Event handlers ───────────────────────────────────────────

        private void AvailSpace_TextChanged(object sender, TextChangedEventArgs e)
            => DrawPreview();

        /// <summary>
        /// Auto-center: compute offset so the antenna is centered within the available PCB space.
        /// </summary>
        private void DoAutoCenter()
        {
            if (!double.TryParse(AvailWidthBox.Text, out double aw) || aw <= 0) return;
            if (!double.TryParse(AvailHeightBox.Text, out double ah) || ah <= 0) return;

            double antennaW, antennaTopEdge;
            if (SelectedType == AntennaType.Custom)
            {
                if (_customVertices.Count < 2) return;
                double minX = _customVertices.Min(v => v.X);
                double maxX = _customVertices.Max(v => v.X);
                double maxY = _customVertices.Max(v => v.Y);
                antennaW = maxX - minX;
                antennaTopEdge = maxY;
            }
            else if (SelectedType == AntennaType.InvertedF)
            {
                antennaW = Get("LengthL", 24);
                double H = Get("HeightH", 7);
                double halfTopW = Math.Max(Get("MatchStubWidth", 1), Get("RadiatorWidth", 1)) / 2.0;
                antennaTopEdge = H + halfTopW;
            }
            else
            {
                double feedS = Get("FeedGap", 3);
                double pitch = Get("MeanderPitch", 5.0);
                double Hm    = Get("MeanderHeight", 2.85);
                double H     = Get("MifaHeightH", 3.9);
                double L     = Get("LengthL", 24);
                if (Hm >= H) Hm = H * 0.8;
                int n = (pitch + Hm) > 0 ? Math.Max(1, (int)Math.Floor((L - feedS) / (pitch + Hm))) : 1;
                double rem = L - feedS - n * (pitch + Hm);
                bool partial = rem >= pitch;
                if (partial) n += 1;
                double tail = partial ? 0 : Math.Max(rem, 0);
                antennaW = feedS + n * pitch + tail;
                double halfTopW = Get("MifaHorizWidth", 0.5) / 2.0;
                antennaTopEdge = H + halfTopW;
            }

            double clr = double.TryParse(ClearanceBox.Text, out double clrVal) && clrVal >= 0 ? clrVal : 0;

            double offX = -(aw - antennaW) / 2.0;
            double offY = antennaTopEdge - ah + clr;

            PcbOffsetXBox.Text = offX.ToString("F2");
            PcbOffsetYBox.Text = offY.ToString("F2");
        }

        private void AutoCenterBtn_Click(object sender, RoutedEventArgs e)
            => DoAutoCenter();

        private void AntennaTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => RefreshAll();

        private void BoardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsInitialized) return;
            PopulateLayerCombo();
        }

        private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
            => DrawPreview();

        // ── Update 3D / Close ────────────────────────────────────────

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty((LayerCombo.SelectedItem as Layer)?.Name))
            {
                MessageBox.Show("Please select a conductive layer.", "Parameter Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string antennaName = AntennaNameBox.Text.Trim();
            if (string.IsNullOrEmpty(antennaName))
            {
                MessageBox.Show("Please enter a name for this antenna.", "Parameter Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = new AntennaParams
            {
                Type           = SelectedType,
                IsCarrier      = IsCarrierSelected,
                LayerName      = (LayerCombo.SelectedItem as Layer)?.Name ?? "",
                Name           = antennaName,
                FreqGHz        = Get("FreqGHz",        2.4),
                LengthL        = Get("LengthL",        24.0),
                HeightH        = Get("HeightH",         7.0),
                FeedGap        = Get("FeedGap",         3.0),
                ShortPinWidth  = Get("ShortPinWidth",   1.0),
                FeedPinWidth   = Get("FeedPinWidth",    1.0),
                MatchStubWidth = Get("MatchStubWidth",  1.0),
                RadiatorWidth  = Get("RadiatorWidth",   1.0),
                MifaHeightH    = Get("MifaHeightH",     3.9),
                MeanderHeight  = Get("MeanderHeight",   2.85),
                MeanderPitch   = Get("MeanderPitch",    5.0),
                MifaShortWidth = Get("MifaShortWidth",  0.8),
                MifaFeedWidth  = Get("MifaFeedWidth",   0.8),
                MifaHorizWidth = Get("MifaHorizWidth",  0.5),
                MifaVertWidth  = Get("MifaVertWidth",   0.5),
                CustomVertices = _customVertices.Select(v => (v.X, v.Y)).ToList(),
                AvailWidth     = double.TryParse(AvailWidthBox.Text,  out double aw) ? aw : 15.0,
                AvailHeight    = double.TryParse(AvailHeightBox.Text, out double ah) ? ah : 10.0,
                PcbOffsetX     = double.TryParse(PcbOffsetXBox.Text,  out double ox) ? ox : 0.0,
                PcbOffsetY     = double.TryParse(PcbOffsetYBox.Text,  out double oy) ? oy : 0.0,
                Clearance      = double.TryParse(ClearanceBox.Text,   out double cl) ? cl : 0.254,
            };

            // Build ManualShape from antenna trace geometry
            PushAntennaToManualShapes(Result);

            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Converts AntennaParams into a ManualShape and adds/replaces it in
        /// the view-model's ManualShapes collection so the 3D view updates live.
        /// The antenna is positioned so the PCB space maps directly to module coords.
        /// </summary>
        private void PushAntennaToManualShapes(AntennaParams ap)
        {
            var gd = ap.ToGerberData();
            if (gd.Shapes.Count == 0) return;

            // Read PCB space offset and dimensions for coordinate mapping
            double offX = double.TryParse(PcbOffsetXBox.Text, out double oxv) ? oxv : 0;
            double offY = double.TryParse(PcbOffsetYBox.Text, out double oyv) ? oyv : 0;
            double aw   = double.TryParse(AvailWidthBox.Text, out double awv) && awv > 0 ? awv : 15;
            double ah   = double.TryParse(AvailHeightBox.Text, out double ahv) && ahv > 0 ? ahv : 10;

            // Board faces Y+.  PCB space top edge → board top edge (Y+).
            // Carrier top edge at Y=0, centre X=0.
            // Module  top edge at Y=-PositionX, centre X=PositionY.
            double boardCX, boardTopY;
            if (ap.IsCarrier)
            {
                boardCX   = 0;
                boardTopY = 0;
            }
            else
            {
                boardCX   = _vm.Module.PositionX;
                boardTopY = -_vm.Module.PositionY;
            }

            (double mx, double my) ToModule(double x, double y)
            {
                return (x - offX - aw / 2.0 + boardCX,
                        y - offY - ah + boardTopY);
            }

            string shapeName = $"Antenna ({ap.Name})";

            // Build the ManualShape from the GerberData polygons
            var shape = new ManualShape
            {
                Name      = shapeName,
                IsCarrier = ap.IsCarrier,
                LayerName = ap.LayerName,
                ShowIn3D  = true
            };

            // First shape → main polygon; rest → merged polygons
            bool first = true;
            foreach (var gs in gd.Shapes)
            {
                if (first)
                {
                    foreach (var (x, y) in gs.Points)
                    {
                        var (mx, my) = ToModule(x, y);
                        shape.Vertices.Add(new ShapeVertex(mx, my));
                    }
                    if (gs.Points.Count > 1)
                    {
                        var fp = gs.Points[0];
                        var (mx, my) = ToModule(fp.X, fp.Y);
                        shape.Vertices.Add(new ShapeVertex(mx, my));
                    }
                    first = false;
                }
                else
                {
                    var poly = new List<ShapeVertex>();
                    foreach (var (x, y) in gs.Points)
                    {
                        var (mx, my) = ToModule(x, y);
                        poly.Add(new ShapeVertex(mx, my));
                    }
                    if (gs.Points.Count > 1)
                    {
                        var fp = gs.Points[0];
                        var (mx, my) = ToModule(fp.X, fp.Y);
                        poly.Add(new ShapeVertex(mx, my));
                    }
                    shape.MergedPolygons.Add(poly);
                }
            }

            // Remove only the specific antenna shape being edited (not all antennas)
            if (_antennaShape != null && _vm.ManualShapes.Contains(_antennaShape))
                _vm.ManualShapes.Remove(_antennaShape);
            else if (_editName != null)
            {
                // Fallback: find by the old name
                string oldShapeName = $"Antenna ({_editName})";
                var old = _vm.ManualShapes.FirstOrDefault(s => s.Name == oldShapeName);
                if (old != null)
                    _vm.ManualShapes.Remove(old);
            }

            // Also remove any existing shape with the same new name to prevent duplicates
            var dup = _vm.ManualShapes.FirstOrDefault(s => s.Name == shapeName);
            if (dup != null)
                _vm.ManualShapes.Remove(dup);

            _antennaShape = shape;
            _editName = ap.Name;
            _vm.ManualShapes.Add(shape);
            // CollectionChanged event automatically triggers RebuildLayerVisuals
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ── Save / Open antenna parameters (.antparam) ──────────────

        private const string AntParamFilter = "Antenna Parameters (*.antparam)|*.antparam|All Files (*.*)|*.*";

        /// <summary>DTO that mirrors every user-editable field on this window.</summary>
        private class AntennaParamFile
        {
            public string  AntennaType   { get; set; } = "InvertedF";
            public string  Board         { get; set; } = "Carrier";
            public string  LayerName     { get; set; } = "";

            // Common
            public double FreqGHz        { get; set; }

            // IFA
            public double LengthL        { get; set; }
            public double HeightH        { get; set; }
            public double FeedGap        { get; set; }
            public double ShortPinWidth  { get; set; }
            public double FeedPinWidth   { get; set; }
            public double MatchStubWidth { get; set; }
            public double RadiatorWidth  { get; set; }

            // MIFA
            public double MifaHeightH    { get; set; }
            public double MeanderHeight  { get; set; }
            public double MeanderPitch   { get; set; }
            public double MifaShortWidth { get; set; }
            public double MifaFeedWidth  { get; set; }
            public double MifaHorizWidth { get; set; }
            public double MifaVertWidth  { get; set; }

            // Custom
            public List<double[]>? CustomVertices { get; set; }

            // PCB Space
            public double AvailWidth     { get; set; }
            public double AvailHeight    { get; set; }
            public double OffsetX        { get; set; }
            public double OffsetY        { get; set; }
            public double Clearance      { get; set; }
        }

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
        };

        private AntennaParamFile CollectParamFile()
        {
            return new AntennaParamFile
            {
                AntennaType   = SelectedType.ToString(),
                Board         = BoardCombo.SelectedItem as string ?? "Carrier",
                LayerName     = (LayerCombo.SelectedItem as Layer)?.Name ?? "",

                FreqGHz        = Get("FreqGHz",        2.4),
                LengthL        = Get("LengthL",        24.0),
                HeightH        = Get("HeightH",         7.0),
                FeedGap        = Get("FeedGap",         3.0),
                ShortPinWidth  = Get("ShortPinWidth",   1.0),
                FeedPinWidth   = Get("FeedPinWidth",    1.0),
                MatchStubWidth = Get("MatchStubWidth",  1.0),
                RadiatorWidth  = Get("RadiatorWidth",   1.0),

                MifaHeightH    = Get("MifaHeightH",     3.9),
                MeanderHeight  = Get("MeanderHeight",   2.85),
                MeanderPitch   = Get("MeanderPitch",    5.0),
                MifaShortWidth = Get("MifaShortWidth",  0.8),
                MifaFeedWidth  = Get("MifaFeedWidth",   0.8),
                MifaHorizWidth = Get("MifaHorizWidth",  0.5),
                MifaVertWidth  = Get("MifaVertWidth",   0.5),

                CustomVertices = _customVertices.Count > 0
                    ? _customVertices.Select(v => new[] { v.X, v.Y }).ToList()
                    : null,

                AvailWidth  = double.TryParse(AvailWidthBox.Text,  out double aw) ? aw : 0,
                AvailHeight = double.TryParse(AvailHeightBox.Text, out double ah) ? ah : 0,
                OffsetX     = double.TryParse(PcbOffsetXBox.Text,  out double ox) ? ox : 0,
                OffsetY     = double.TryParse(PcbOffsetYBox.Text,  out double oy) ? oy : 0,
                Clearance   = double.TryParse(ClearanceBox.Text,   out double cl) ? cl : 0.254
            };
        }

        private void ApplyParamFile(AntennaParamFile pf)
        {
            // Antenna type
            var targetType = Enum.TryParse<AntennaType>(pf.AntennaType, out var at)
                ? at : AntennaType.InvertedF;
            for (int i = 0; i < AntennaTypeCombo.Items.Count; i++)
            {
                if (AntennaTypeCombo.Items[i] is ComboBoxItem ci && ci.Tag is AntennaType t && t == targetType)
                { AntennaTypeCombo.SelectedIndex = i; break; }
            }

            // Board & layer
            if (BoardCombo.Items.Contains(pf.Board))
                BoardCombo.SelectedItem = pf.Board;
            PopulateLayerCombo();
            if (!string.IsNullOrEmpty(pf.LayerName))
            {
                foreach (var item in LayerCombo.Items)
                {
                    if (item is Layer lyr && lyr.Name == pf.LayerName)
                    { LayerCombo.SelectedItem = lyr; break; }
                }
            }

            // Rebuild grids so _boxes exist for all keys
            RefreshAll();

            // Write values into text boxes
            void Set(string key, double val) { if (_boxes.TryGetValue(key, out var tb)) tb.Text = val.ToString("G6"); }

            _suppressAutoCalc = true;
            try
            {
                Set("FreqGHz",        pf.FreqGHz);
                Set("LengthL",        pf.LengthL);
                Set("HeightH",        pf.HeightH);
                Set("FeedGap",        pf.FeedGap);
                Set("ShortPinWidth",  pf.ShortPinWidth);
                Set("FeedPinWidth",   pf.FeedPinWidth);
                Set("MatchStubWidth", pf.MatchStubWidth);
                Set("RadiatorWidth",  pf.RadiatorWidth);

                Set("MifaHeightH",    pf.MifaHeightH);
                Set("MeanderHeight",  pf.MeanderHeight);
                Set("MeanderPitch",   pf.MeanderPitch);
                Set("MifaShortWidth", pf.MifaShortWidth);
                Set("MifaFeedWidth",  pf.MifaFeedWidth);
                Set("MifaHorizWidth", pf.MifaHorizWidth);
                Set("MifaVertWidth",  pf.MifaVertWidth);
            }
            finally { _suppressAutoCalc = false; }

            // Custom vertices
            _customVertices.Clear();
            if (pf.CustomVertices != null)
            {
                foreach (var arr in pf.CustomVertices)
                {
                    if (arr.Length >= 2)
                        _customVertices.Add(new ShapeVertex(arr[0], arr[1]));
                }
            }

            // PCB space
            AvailWidthBox.Text  = pf.AvailWidth.ToString("F2");
            AvailHeightBox.Text = pf.AvailHeight.ToString("F2");
            PcbOffsetXBox.Text  = pf.OffsetX.ToString("F2");
            PcbOffsetYBox.Text  = pf.OffsetY.ToString("F2");
            ClearanceBox.Text   = pf.Clearance.ToString("G6");

            UpdateWarnings();
            DrawPreview();
        }

        /// <summary>Populate UI from an AntennaParams object (used for edit mode).</summary>
        private void ApplyAntennaParamsToUI(AntennaParams ap)
        {
            // Antenna type
            for (int i = 0; i < AntennaTypeCombo.Items.Count; i++)
            {
                if (AntennaTypeCombo.Items[i] is ComboBoxItem ci && ci.Tag is AntennaType t && t == ap.Type)
                { AntennaTypeCombo.SelectedIndex = i; break; }
            }

            // Board & layer
            string board = ap.IsCarrier ? "Carrier" : "Module";
            if (BoardCombo.Items.Contains(board))
                BoardCombo.SelectedItem = board;
            PopulateLayerCombo();
            if (!string.IsNullOrEmpty(ap.LayerName))
            {
                foreach (var item in LayerCombo.Items)
                {
                    if (item is Layer lyr && lyr.Name == ap.LayerName)
                    { LayerCombo.SelectedItem = lyr; break; }
                }
            }

            // Rebuild grids so _boxes exist
            RefreshAll();

            void Set(string key, double val) { if (_boxes.TryGetValue(key, out var tb)) tb.Text = val.ToString("G6"); }

            _suppressAutoCalc = true;
            try
            {
                Set("FreqGHz",        ap.FreqGHz);
                Set("LengthL",        ap.LengthL);
                Set("HeightH",        ap.HeightH);
                Set("FeedGap",        ap.FeedGap);
                Set("ShortPinWidth",  ap.ShortPinWidth);
                Set("FeedPinWidth",   ap.FeedPinWidth);
                Set("MatchStubWidth", ap.MatchStubWidth);
                Set("RadiatorWidth",  ap.RadiatorWidth);
                Set("MifaHeightH",    ap.MifaHeightH);
                Set("MeanderHeight",  ap.MeanderHeight);
                Set("MeanderPitch",   ap.MeanderPitch);
                Set("MifaShortWidth", ap.MifaShortWidth);
                Set("MifaFeedWidth",  ap.MifaFeedWidth);
                Set("MifaHorizWidth", ap.MifaHorizWidth);
                Set("MifaVertWidth",  ap.MifaVertWidth);
            }
            finally { _suppressAutoCalc = false; }

            // Custom vertices
            _customVertices.Clear();
            foreach (var (x, y) in ap.CustomVertices)
                _customVertices.Add(new ShapeVertex(x, y));

            // PCB space
            AvailWidthBox.Text  = ap.AvailWidth.ToString("F2");
            AvailHeightBox.Text = ap.AvailHeight.ToString("F2");
            PcbOffsetXBox.Text  = ap.PcbOffsetX.ToString("F2");
            PcbOffsetYBox.Text  = ap.PcbOffsetY.ToString("F2");
            ClearanceBox.Text   = ap.Clearance.ToString("G6");

            UpdateWarnings();
            DrawPreview();
        }

        private void SaveParamsBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter           = AntParamFilter,
                DefaultExt       = ".antparam",
                FileName         = $"{SelectedType}",
                Title            = "Save Antenna Parameters"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var pf   = CollectParamFile();
                string json = JsonSerializer.Serialize(pf, _jsonOpts);
                File.WriteAllText(dlg.FileName, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save:\n{ex.Message}", "Save Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenParamsBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = AntParamFilter,
                Title  = "Open Antenna Parameters"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                string json = File.ReadAllText(dlg.FileName, Encoding.UTF8);
                var pf = JsonSerializer.Deserialize<AntennaParamFile>(json, _jsonOpts);
                if (pf == null) throw new InvalidDataException("File is empty or invalid.");
                ApplyParamFile(pf);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open:\n{ex.Message}", "Open Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Export DXF ────────────────────────────────────────────────

        private void ExportDxfBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter     = "DXF Files (*.dxf)|*.dxf",
                DefaultExt = ".dxf",
                FileName   = $"Antenna_{SelectedType}",
                Title      = "Export Antenna Drawing to DXF"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                if (SelectedType == AntennaType.InvertedF)
                    ExportIFA_Dxf(dlg.FileName);
                else if (SelectedType == AntennaType.Custom)
                    ExportCustom_Dxf(dlg.FileName);
                else
                    ExportMIFA_Dxf(dlg.FileName);

                MessageBox.Show($"DXF exported successfully:\n{dlg.FileName}", "Export DXF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export DXF:\n{ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── DXF writer helpers ──────────────────────────────────────

        private static readonly CultureInfo _inv = CultureInfo.InvariantCulture;
        private static string F(double v) => v.ToString("F6", _inv);

        /// <summary>Write a closed LWPOLYLINE rectangle on the given layer (units = mm).</summary>
        private static void DxfRect(StringBuilder sb, string layer, int color,
            double x, double y, double w, double h)
        {
            sb.AppendLine("  0"); sb.AppendLine("LWPOLYLINE");
            sb.AppendLine("  8"); sb.AppendLine(layer);
            sb.AppendLine(" 62"); sb.AppendLine(color.ToString());
            sb.AppendLine(" 90"); sb.AppendLine("4");        // vertex count
            sb.AppendLine(" 70"); sb.AppendLine("1");        // closed
            sb.AppendLine(" 10"); sb.AppendLine(F(x));
            sb.AppendLine(" 20"); sb.AppendLine(F(y));
            sb.AppendLine(" 10"); sb.AppendLine(F(x + w));
            sb.AppendLine(" 20"); sb.AppendLine(F(y));
            sb.AppendLine(" 10"); sb.AppendLine(F(x + w));
            sb.AppendLine(" 20"); sb.AppendLine(F(y + h));
            sb.AppendLine(" 10"); sb.AppendLine(F(x));
            sb.AppendLine(" 20"); sb.AppendLine(F(y + h));
        }

        /// <summary>Write a LINE entity on the given layer.</summary>
        private static void DxfLine(StringBuilder sb, string layer, int color,
            double x1, double y1, double x2, double y2)
        {
            sb.AppendLine("  0"); sb.AppendLine("LINE");
            sb.AppendLine("  8"); sb.AppendLine(layer);
            sb.AppendLine(" 62"); sb.AppendLine(color.ToString());
            sb.AppendLine(" 10"); sb.AppendLine(F(x1));
            sb.AppendLine(" 20"); sb.AppendLine(F(y1));
            sb.AppendLine(" 11"); sb.AppendLine(F(x2));
            sb.AppendLine(" 21"); sb.AppendLine(F(y2));
        }

        /// <summary>Write a TEXT entity on the given layer (height in mm).</summary>
        private static void DxfText(StringBuilder sb, string layer, int color,
            double x, double y, double height, string text, double rotation = 0)
        {
            sb.AppendLine("  0"); sb.AppendLine("TEXT");
            sb.AppendLine("  8"); sb.AppendLine(layer);
            sb.AppendLine(" 62"); sb.AppendLine(color.ToString());
            sb.AppendLine(" 10"); sb.AppendLine(F(x));
            sb.AppendLine(" 20"); sb.AppendLine(F(y));
            sb.AppendLine(" 40"); sb.AppendLine(F(height));
            sb.AppendLine("  1"); sb.AppendLine(text);
            if (Math.Abs(rotation) > 0.01)
            { sb.AppendLine(" 50"); sb.AppendLine(F(rotation)); }
        }

        /// <summary>Draw a horizontal dimension annotation (two ticks + dashed line + label).</summary>
        private static void DxfDimH(StringBuilder sb, string layer, int color,
            double x1, double x2, double y, double mmVal, string label, double textH)
        {
            double ext = textH * 0.6;
            // Extension lines (vertical ticks)
            DxfLine(sb, layer, color, x1, y - ext, x1, y + ext);
            DxfLine(sb, layer, color, x2, y - ext, x2, y + ext);
            // Dimension line
            DxfLine(sb, layer, color, x1, y, x2, y);
            // Arrowhead lines
            double ah = Math.Min((x2 - x1) * 0.08, textH * 0.8);
            DxfLine(sb, layer, color, x1, y, x1 + ah, y + ah * 0.3);
            DxfLine(sb, layer, color, x1, y, x1 + ah, y - ah * 0.3);
            DxfLine(sb, layer, color, x2, y, x2 - ah, y + ah * 0.3);
            DxfLine(sb, layer, color, x2, y, x2 - ah, y - ah * 0.3);
            // Label text
            string txt = $"{label}={mmVal:F2}";
            DxfText(sb, layer, color, (x1 + x2) / 2, y + textH * 0.3, textH, txt);
        }

        /// <summary>Draw a vertical dimension annotation.</summary>
        private static void DxfDimV(StringBuilder sb, string layer, int color,
            double x, double y1, double y2, double mmVal, string label, double textH)
        {
            double yLo = Math.Min(y1, y2), yHi = Math.Max(y1, y2);
            double ext = textH * 0.6;
            // Extension lines (horizontal ticks)
            DxfLine(sb, layer, color, x - ext, yLo, x + ext, yLo);
            DxfLine(sb, layer, color, x - ext, yHi, x + ext, yHi);
            // Dimension line
            DxfLine(sb, layer, color, x, yLo, x, yHi);
            // Arrowhead lines
            double ah = Math.Min((yHi - yLo) * 0.08, textH * 0.8);
            DxfLine(sb, layer, color, x, yLo, x + ah * 0.3, yLo + ah);
            DxfLine(sb, layer, color, x, yLo, x - ah * 0.3, yLo + ah);
            DxfLine(sb, layer, color, x, yHi, x + ah * 0.3, yHi - ah);
            DxfLine(sb, layer, color, x, yHi, x - ah * 0.3, yHi - ah);
            // Label text (rotated 90°)
            string txt = $"{label}={mmVal:F2}";
            DxfText(sb, layer, color, x + textH * 0.3, (yLo + yHi) / 2, textH, txt, 90);
        }

        /// <summary>Write DXF header + ENTITIES section start.</summary>
        private static void DxfBegin(StringBuilder sb)
        {
            sb.AppendLine("  0"); sb.AppendLine("SECTION");
            sb.AppendLine("  2"); sb.AppendLine("HEADER");
            // Units = mm (INSUNITS = 4)
            sb.AppendLine("  9"); sb.AppendLine("$INSUNITS");
            sb.AppendLine(" 70"); sb.AppendLine("4");
            sb.AppendLine("  9"); sb.AppendLine("$MEASUREMENT");
            sb.AppendLine(" 70"); sb.AppendLine("1");
            sb.AppendLine("  0"); sb.AppendLine("ENDSEC");

            // TABLES section – define layers
            sb.AppendLine("  0"); sb.AppendLine("SECTION");
            sb.AppendLine("  2"); sb.AppendLine("TABLES");
            sb.AppendLine("  0"); sb.AppendLine("TABLE");
            sb.AppendLine("  2"); sb.AppendLine("LAYER");
            void AddLayer(string name, int color)
            {
                sb.AppendLine("  0"); sb.AppendLine("LAYER");
                sb.AppendLine("  2"); sb.AppendLine(name);
                sb.AppendLine(" 70"); sb.AppendLine("0");
                sb.AppendLine(" 62"); sb.AppendLine(color.ToString());
                sb.AppendLine("  6"); sb.AppendLine("CONTINUOUS");
            }
            AddLayer("COPPER",  5);   // blue
            AddLayer("FEED",    1);   // red
            AddLayer("MATCH",   2);   // yellow
            AddLayer("GND",     8);   // gray
            AddLayer("DIM",     3);   // green
            AddLayer("TEXT",    7);   // white/default
            AddLayer("MEANDER", 6);   // magenta
            sb.AppendLine("  0"); sb.AppendLine("ENDTAB");
            sb.AppendLine("  0"); sb.AppendLine("ENDSEC");

            sb.AppendLine("  0"); sb.AppendLine("SECTION");
            sb.AppendLine("  2"); sb.AppendLine("ENTITIES");
        }

        /// <summary>Write DXF ENTITIES section end + EOF.</summary>
        private static void DxfEnd(StringBuilder sb)
        {
            sb.AppendLine("  0"); sb.AppendLine("ENDSEC");
            sb.AppendLine("  0"); sb.AppendLine("EOF");
        }

        // ── IFA DXF export ──────────────────────────────────────────

        private void ExportIFA_Dxf(string path)
        {
            double L   = Get("LengthL", 24);
            double H   = Get("HeightH", 7);
            double S   = Get("FeedGap", 3);
            double wSh = Get("ShortPinWidth", 1);
            double wFe = Get("FeedPinWidth", 1);
            double wMa = Get("MatchStubWidth", 1);
            double wRa = Get("RadiatorWidth", 1);
            double freq = Get("FreqGHz", 2.4);

            var sb = new StringBuilder();
            DxfBegin(sb);

            double textH = Math.Max(H * 0.06, 0.4);

            // GND bar (full width under the antenna)
            DxfRect(sb, "GND", 8, -1, -0.3, L + 2, 0.3);
            DxfText(sb, "GND", 8, 0, -0.8, textH, "GND");

            // Shorting stub (left vertical, from GND up to top)
            DxfRect(sb, "COPPER", 5, -wSh / 2, 0, wSh, H);
            DxfText(sb, "TEXT", 7, -wSh / 2, H + textH * 0.3, textH, "Short");

            // Feed stub (vertical at x=S, from GND up to top)
            DxfRect(sb, "FEED", 1, S - wFe / 2, 0, wFe, H);
            DxfText(sb, "TEXT", 7, S + wFe / 2 + textH * 0.2, H / 2, textH, "Feed");

            // Matching section (horizontal at y=H, from short to feed)
            DxfRect(sb, "MATCH", 2, -wSh / 2, H - wMa / 2, S + wSh / 2 + wFe / 2, wMa);
            DxfText(sb, "TEXT", 7, S / 4, H + wMa / 2 + textH * 0.3, textH, "Match");

            // Radiator (horizontal at y=H, from feed to tip)
            DxfRect(sb, "COPPER", 5, S - wFe / 2, H - wRa / 2, L - S + wFe / 2, wRa);
            DxfText(sb, "TEXT", 7, (S + L) / 2 - 2, H + wRa / 2 + textH * 0.3, textH, "Radiator");

            // ── Dimensions ──
            double dimOff = -2.0;
            DxfDimH(sb, "DIM", 3, 0, L, dimOff, L, "L", textH);
            DxfDimH(sb, "DIM", 3, 0, S, dimOff - textH * 2.5, S, "S", textH);
            DxfDimV(sb, "DIM", 3, L + 2, 0, H, H, "H", textH);

            // Trace width labels
            DxfText(sb, "DIM", 3, -wSh / 2 - textH * 6, H / 2, textH, $"W_sh={wSh:F2}");
            DxfText(sb, "DIM", 3, S + wFe, H + textH * 1.5, textH, $"W_fe={wFe:F2}");
            DxfText(sb, "DIM", 3, S / 4, H - wMa - textH * 0.5, textH, $"W_ma={wMa:F2}");
            DxfText(sb, "DIM", 3, (S + L) / 2, H - wRa - textH * 0.5, textH, $"W_ra={wRa:F2}");

            // Info text
            double iy = dimOff - textH * 6;
            DxfText(sb, "TEXT", 7, 0, iy, textH,
                $"IFA: L={L:F2} H={H:F2} S={S:F2} Freq={freq:F2}GHz");
            DxfText(sb, "TEXT", 7, 0, iy - textH * 1.5, textH,
                $"W_sh={wSh:F2} W_fe={wFe:F2} W_ma={wMa:F2} W_ra={wRa:F2} mm");
            DxfText(sb, "TEXT", 7, 0, iy - textH * 3, textH,
                $"Footprint: {L:F1} x {H:F1} mm");

            DxfEnd(sb);
            File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        }

        // ── MIFA DXF export ─────────────────────────────────────────

        private void ExportMIFA_Dxf(string path)
        {
            double L      = Get("LengthL", 24);
            double pitch  = Get("MeanderPitch", 5.0);
            double H      = Get("MifaHeightH", 3.9);
            double Hm     = Get("MeanderHeight", 2.85);
            double feedS  = Get("FeedGap", 3);
            double wSh    = Get("MifaShortWidth", 0.8);
            double wFe    = Get("MifaFeedWidth", 0.8);
            double wMh    = Get("MifaHorizWidth", 0.5);
            double wMv    = Get("MifaVertWidth", 0.5);
            double freq   = Get("FreqGHz", 2.4);
            if (Hm >= H) Hm = H * 0.8;

            int    n_full    = (pitch + Hm) > 0 ? Math.Max(1, (int)Math.Floor((L - feedS) / (pitch + Hm))) : 1;
            double remaining = L - feedS - n_full * (pitch + Hm);
            bool   hasPartial = remaining >= pitch;
            int    n          = hasPartial ? n_full + 1 : n_full;
            double tailLen    = hasPartial ? 0 : Math.Max(remaining, 0);
            double partialHm  = hasPartial ? Math.Max(remaining - pitch, 0) : 0;

            double totalW   = feedS + n * pitch + tailLen;
            double traceLen = feedS + n_full * (pitch + Hm) + (hasPartial ? pitch + partialHm : 0) + tailLen;

            var sb = new StringBuilder();
            DxfBegin(sb);

            double textH = Math.Max(H * 0.06, 0.3);

            // GND bar
            DxfRect(sb, "GND", 8, -1, -0.3, totalW + 2, 0.3);
            DxfText(sb, "GND", 8, 0, -0.8, textH, "GND");

            // Shorting stub (left vertical from GND to H)
            DxfRect(sb, "COPPER", 5, -wSh / 2, 0, wSh, H);
            DxfText(sb, "TEXT", 7, -wSh / 2, H + textH * 0.3, textH, "Short");

            // Feed stub (vertical at x=feedS from GND to H)
            DxfRect(sb, "FEED", 1, feedS - wFe / 2, 0, wFe, H);
            DxfText(sb, "TEXT", 7, feedS + wFe / 2 + textH * 0.2, H / 2, textH, "Feed");

            // Matching section (horizontal at y=H, from short to feed)
            DxfRect(sb, "MATCH", 2, -wSh / 2, H - wMh / 2, feedS + wSh / 2 + wFe / 2, wMh);

            // Meander traces
            double mx = feedS, my = H;
            for (int i = 0; i < n; i++)
            {
                double h_i   = (hasPartial && i == n - 1) ? partialHm : Hm;
                double yBot  = H - h_i;
                double nx    = mx + pitch;
                double ny    = (i % 2 == 0) ? yBot : H;
                double vy0   = Math.Min(my, ny);
                double vy1   = Math.Max(my, ny);

                // Horizontal trace
                DxfRect(sb, "COPPER", 5,
                    mx - wMv / 2, my - wMh / 2,
                    pitch + wMv, wMh);
                // Vertical trace
                if (vy1 - vy0 > 1e-6)
                    DxfRect(sb, "MEANDER", 6,
                        nx - wMv / 2, vy0 - wMh / 2,
                        wMv, (vy1 - vy0) + wMh);

                mx = nx; my = ny;
            }
            // Tail
            if (tailLen > 0)
                DxfRect(sb, "COPPER", 5,
                    mx - wMv / 2, my - wMh / 2,
                    tailLen + wMv / 2, wMh);

            // ── Dimensions ──
            double dimOff = -2.0;
            DxfDimH(sb, "DIM", 3, 0, totalW, dimOff, totalW, "Total", textH);
            DxfDimH(sb, "DIM", 3, 0, feedS, dimOff - textH * 2.5, feedS, "S", textH);
            if (n >= 1)
                DxfDimH(sb, "DIM", 3, feedS, feedS + pitch, dimOff - textH * 5, pitch, "P", textH);

            DxfDimV(sb, "DIM", 3, totalW + 2, 0, H, H, "H", textH);
            DxfDimV(sb, "DIM", 3, totalW + 2 + textH * 4, H - Hm, H, Hm, "h1", textH);

            // Trace width labels
            DxfText(sb, "DIM", 3, -wSh / 2 - textH * 6, H / 2, textH, $"W_sh={wSh:F2}");
            DxfText(sb, "DIM", 3, feedS + wFe, H + textH * 1.5, textH, $"W_fe={wFe:F2}");
            DxfText(sb, "DIM", 3, feedS + pitch / 3, H + wMh / 2 + textH * 0.3, textH, $"W_mh={wMh:F2}");
            DxfText(sb, "DIM", 3, feedS + pitch + wMv, H - Hm / 2, textH, $"W_mv={wMv:F2}");

            // N label
            DxfText(sb, "TEXT", 7, totalW / 2 - 3, H + textH * 3, textH,
                $"N={n}  trace={traceLen:F1}mm");

            // Info text
            double iy = dimOff - textH * 8;
            DxfText(sb, "TEXT", 7, 0, iy, textH,
                $"MIFA: L={L:F2} H={H:F2} h1={Hm:F2} S={feedS:F2} P={pitch:F2} Freq={freq:F2}GHz");
            DxfText(sb, "TEXT", 7, 0, iy - textH * 1.5, textH,
                $"N={n} (auto)  Trace={traceLen:F1}mm  Width={totalW:F1}mm");
            DxfText(sb, "TEXT", 7, 0, iy - textH * 3, textH,
                $"W_sh={wSh:F2} W_fe={wFe:F2} W_mh={wMh:F2} W_mv={wMv:F2} mm");
            DxfText(sb, "TEXT", 7, 0, iy - textH * 4.5, textH,
                $"Footprint: {totalW:F1} x {H:F1} mm");

            DxfEnd(sb);
            File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        }

        // ── Custom DXF export ───────────────────────────────────────

        private void ExportCustom_Dxf(string path)
        {
            if (_customVertices.Count < 3)
            {
                MessageBox.Show("Custom antenna needs at least 3 vertices.", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double minX = _customVertices.Min(v => v.X);
            double minY = _customVertices.Min(v => v.Y);
            double maxX = _customVertices.Max(v => v.X);
            double maxY = _customVertices.Max(v => v.Y);
            double polyW = maxX - minX;
            double polyH = maxY - minY;
            double freq = Get("FreqGHz", 2.4);

            var sb = new StringBuilder();
            DxfBegin(sb);
            double textH = Math.Max(Math.Max(polyW, polyH) * 0.03, 0.3);

            // Closed LWPOLYLINE for the custom polygon
            sb.AppendLine("  0"); sb.AppendLine("LWPOLYLINE");
            sb.AppendLine("  8"); sb.AppendLine("COPPER");
            sb.AppendLine(" 62"); sb.AppendLine("5");
            sb.AppendLine(" 90"); sb.AppendLine(_customVertices.Count.ToString());
            sb.AppendLine(" 70"); sb.AppendLine("1"); // closed
            foreach (var v in _customVertices)
            {
                sb.AppendLine(" 10"); sb.AppendLine(F(v.X));
                sb.AppendLine(" 20"); sb.AppendLine(F(v.Y));
            }

            // Vertex labels
            for (int i = 0; i < _customVertices.Count; i++)
            {
                var v = _customVertices[i];
                DxfText(sb, "TEXT", 7, v.X + textH * 0.3, v.Y + textH * 0.3, textH,
                    $"[{i}]({v.X:F2},{v.Y:F2})");
            }

            // Dimension annotations
            double dimOff = minY - 2;
            DxfDimH(sb, "DIM", 3, minX, maxX, dimOff, polyW, "W", textH);
            DxfDimV(sb, "DIM", 3, maxX + 2, minY, maxY, polyH, "H", textH);

            // Info text
            double iy = dimOff - textH * 3;
            DxfText(sb, "TEXT", 7, minX, iy, textH,
                $"Custom Antenna: {_customVertices.Count} vertices  Freq={freq:F2}GHz");
            DxfText(sb, "TEXT", 7, minX, iy - textH * 1.5, textH,
                $"Footprint: {polyW:F1} x {polyH:F1} mm");

            DxfEnd(sb);
            File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        }

        // ── Lifecycle ─────────────────────────────────────────────────

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            try
            {
                _boxes.Clear();

                if (_editParams != null)
                {
                    // Edit mode: rebuild grids then re-apply saved params
                    ProjectSerializer.DiagWrite($"[OnContentRendered] Re-applying _editParams: Name={_editParams.Name} Freq={_editParams.FreqGHz} L={_editParams.LengthL} H={_editParams.HeightH}");
                    RefreshAll();
                    PopulateLayerCombo();
                    ApplyAntennaParamsToUI(_editParams);
                    ProjectSerializer.DiagWrite($"[OnContentRendered] After apply: FreqGHz box={(_boxes.TryGetValue("FreqGHz", out var fb) ? fb.Text : "N/A")}, LengthL box={(_boxes.TryGetValue("LengthL", out var lb) ? lb.Text : "N/A")}");
                }
                else
                {
                    // New mode: auto-fill PCB space width from module board
                    if (_vm.HasModule)
                        AvailWidthBox.Text = _vm.Module.Height.ToString("F2");

                    RefreshAll();
                    PopulateLayerCombo();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OnContentRendered error: {ex}");
                MessageBox.Show($"Error initializing antenna window:\n{ex.Message}",
                    "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
