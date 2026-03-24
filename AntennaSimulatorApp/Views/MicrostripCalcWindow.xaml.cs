using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AntennaSimulatorApp.Models;

namespace AntennaSimulatorApp.Views
{
    public partial class MicrostripCalcWindow : Window
    {
        private readonly BoardConfig? _carrier;
        private readonly BoardConfig? _module;
        private bool _suppressUpdate;

        public MicrostripCalcWindow()
        {
            InitializeComponent();
        }

        public MicrostripCalcWindow(BoardConfig? carrier, BoardConfig? module) : this()
        {
            _carrier = carrier;
            _module = module;
            LoadFromBoard(true);
        }

        // ── Board selection ──────────────────────────────────────────────

        private void OnBoardChanged(object sender, RoutedEventArgs e)
        {
            if (TxtBoardInfo == null) return; // not yet initialized
            bool isCarrier = RbCarrier.IsChecked == true;
            LoadFromBoard(isCarrier);
        }

        private void LoadFromBoard(bool isCarrier)
        {
            if (TxtBoardInfo == null) return;
            var board = isCarrier ? _carrier : _module;
            if (board == null)
            {
                TxtBoardInfo.Text = "(board not available)";
                return;
            }

            _suppressUpdate = true;

            var layers = board.Stackup.Layers;
            // Find top copper layer (index 0) and first dielectric below it
            Layer? topCopper = layers.FirstOrDefault(l => l.IsConductive);
            Layer? firstDielectric = null;
            Layer? groundPlane = null;
            bool foundTop = false;

            foreach (var layer in layers)
            {
                if (!foundTop && layer.IsConductive) { foundTop = true; continue; }
                if (foundTop && layer.Type == LayerType.Dielectric) { firstDielectric = layer; continue; }
                if (foundTop && firstDielectric != null && layer.IsConductive) { groundPlane = layer; break; }
            }

            if (topCopper != null)
                TxtT.Text = topCopper.Thickness.ToString("F3", CultureInfo.InvariantCulture);

            if (firstDielectric != null)
            {
                TxtH.Text = firstDielectric.Thickness.ToString("F3", CultureInfo.InvariantCulture);
                TxtEr.Text = firstDielectric.DielectricConstant.ToString("F2", CultureInfo.InvariantCulture);
            }

            string info = $"{board.Name}\n"
                + $"{board.LayerCount}L, {board.Width}×{board.Height} mm\n"
                + $"Total thickness: {board.Thickness} mm";
            if (topCopper != null)
                info += $"\nTop Cu: {topCopper.Name} ({topCopper.Thickness * 1000:F1} µm)";
            if (firstDielectric != null)
                info += $"\nSubstrate: {MaterialInfo.All.FirstOrDefault(m => m.Value == firstDielectric.Material)?.DisplayName ?? firstDielectric.Material.ToString()}"
                     + $" (εr={firstDielectric.DielectricConstant:F2}, H={firstDielectric.Thickness:F3} mm)";
            if (groundPlane != null)
                info += $"\nGND plane: {groundPlane.Name}";
            TxtBoardInfo.Text = info;

            _suppressUpdate = false;
            Recalculate();
        }

        // ── Parameter change ─────────────────────────────────────────────

        private void OnParamChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressUpdate) return;
            Recalculate();
        }

        private void DiagramCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Recalculate();
        }

        // ── Core calculation: Hammerstad–Jensen microstrip formulas ──────

        private void Recalculate()
        {
            if (TxtResultW == null || DiagramCanvas == null) return;

            var ci = CultureInfo.InvariantCulture;
            if (!double.TryParse(TxtEr?.Text, NumberStyles.Float, ci, out double er) || er < 1) return;
            if (!double.TryParse(TxtH?.Text, NumberStyles.Float, ci, out double h_mm) || h_mm <= 0) return;
            if (!double.TryParse(TxtT?.Text, NumberStyles.Float, ci, out double t_mm) || t_mm < 0) return;
            if (!double.TryParse(TxtTargetZ?.Text, NumberStyles.Float, ci, out double z0Target) || z0Target <= 0) return;
            if (!double.TryParse(TxtFreq?.Text, NumberStyles.Float, ci, out double freqGHz) || freqGHz <= 0) return;

            // Synthesize W/H from target Z0 using Hammerstad equations
            double wh = SynthesizeMicrostrip(z0Target, er);
            double w_mm = wh * h_mm;

            // Verify: compute Z0 back from W/H (with thickness correction)
            double weH = EffectiveWidthRatio(wh, t_mm / h_mm);
            double eEff = EffectiveEr(er, weH);
            double z0Actual = MicrostripZ0(weH, eEff);

            // Phase velocity & wavelength
            double c0 = 299.792458; // mm/ns = same as m/µs
            double vp = c0 / Math.Sqrt(eEff); // mm/ns
            double freqHz = freqGHz * 1e9;
            double lambda_mm = (c0 * 1e6 / freqHz) / Math.Sqrt(eEff); // in mm
            double quarterWave = lambda_mm / 4.0;

            // Display results
            double w_mil = w_mm / 0.0254;
            TxtResultW.Text = $"W = {w_mil:F2} mil  ({w_mm:F4} mm)";
            TxtResultZ.Text = $"Z₀ = {z0Actual:F2} Ω";
            TxtResultEeff.Text = $"εeff = {eEff:F4}";
            TxtResultVp.Text = $"vp = {vp:F2} mm/ns ({vp / c0 * 100:F1}% c)";
            TxtResultLambda.Text = $"λ/4 = {FormatLength(quarterWave)}";
            TxtResultWH.Text = $"W/H = {wh:F4}";

            DrawDiagram(w_mm, h_mm, t_mm, er, z0Actual);
        }

        /// <summary>
        /// Synthesize W/H ratio from target impedance Z0 and substrate εr.
        /// Hammerstad–Jensen (1980) synthesis equations.
        /// </summary>
        private static double SynthesizeMicrostrip(double z0, double er)
        {
            double A = z0 / 60.0 * Math.Sqrt((er + 1) / 2.0)
                      + (er - 1) / (er + 1) * (0.23 + 0.11 / er);
            double B = 377.0 * Math.PI / (2.0 * z0 * Math.Sqrt(er));

            // Try narrow strip (W/H < 2)
            double wh_narrow = 8.0 * Math.Exp(A) / (Math.Exp(2.0 * A) - 2.0);

            // Try wide strip (W/H > 2)
            double wh_wide = 2.0 / Math.PI * (B - 1 - Math.Log(2 * B - 1)
                + (er - 1) / (2 * er) * (Math.Log(B - 1) + 0.39 - 0.61 / er));

            return wh_narrow < 2 ? wh_narrow : wh_wide;
        }

        /// <summary>Effective width ratio We/H accounting for finite conductor thickness.</summary>
        private static double EffectiveWidthRatio(double wh, double th)
        {
            if (th <= 0) return wh;
            // Wheeler correction
            if (wh >= 0.5 / Math.PI)
                return wh + th / Math.PI * (1 + Math.Log(4 * Math.PI * wh / th));
            else
                return wh + th / Math.PI * (1 + Math.Log(2 * 0.5 / th));
        }

        /// <summary>Effective dielectric constant for microstrip.</summary>
        private static double EffectiveEr(double er, double weH)
        {
            return (er + 1) / 2.0
                + (er - 1) / 2.0 / Math.Sqrt(1 + 12.0 / weH);
        }

        /// <summary>Microstrip impedance from We/H and effective εr.</summary>
        private static double MicrostripZ0(double weH, double eEff)
        {
            if (weH <= 1)
                return 60.0 / Math.Sqrt(eEff) * Math.Log(8.0 / weH + weH / 4.0);
            else
                return 120.0 * Math.PI / Math.Sqrt(eEff)
                    / (weH + 1.393 + 0.667 * Math.Log(weH + 1.444));
        }

        private static string FormatLength(double mm)
        {
            if (mm >= 1) return $"{mm:F3} mm";
            if (mm >= 0.001) return $"{mm * 1000:F2} µm";
            return $"{mm * 1e6:F1} nm";
        }

        // ── Diagram drawing ─────────────────────────────────────────────

        private void DrawDiagram(double w_mm, double h_mm, double t_mm, double er, double z0)
        {
            var c = DiagramCanvas;
            c.Children.Clear();

            double cw = c.ActualWidth, ch = c.ActualHeight;
            if (cw < 60 || ch < 60) return;

            double margin = 40;
            double drawW = cw - 2 * margin;
            double drawH = ch - 2 * margin - 40; // leave room for labels at bottom

            // Scale: map physical dimensions to drawing
            // Total height = t (top copper) + h (dielectric) + t (ground copper)
            double totalPhysH = t_mm + h_mm + t_mm;
            double scale = Math.Min(drawW / (w_mm * 3), drawH / totalPhysH);
            // Clamp so trace is visible
            scale = Math.Max(scale, 2);

            double tracePixW = Math.Max(w_mm * scale, 8);
            double dielectricPixH = Math.Max(h_mm * scale, 30);
            double copperPixH = Math.Max(t_mm * scale, 4);
            double substratePixW = Math.Max(tracePixW * 2.5, drawW * 0.8);

            double centerX = cw / 2;
            double topY = margin + 30; // space for dimension arrows above

            // ── Draw substrate (dielectric) ──
            double subLeft = centerX - substratePixW / 2;
            double subRight = centerX + substratePixW / 2;
            double subTop = topY + copperPixH;
            double subBot = subTop + dielectricPixH;

            var subRect = new System.Windows.Shapes.Rectangle
            {
                Width = substratePixW,
                Height = dielectricPixH,
                Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xD5, 0x70)),     // FR4-ish yellow
                Stroke = new SolidColorBrush(Color.FromRgb(0xB0, 0x98, 0x40)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(subRect, subLeft);
            Canvas.SetTop(subRect, subTop);
            c.Children.Add(subRect);

            // εr label in center of substrate
            AddCenteredText(c, $"εr = {er:F2}", centerX, (subTop + subBot) / 2 - 7, 12,
                new SolidColorBrush(Color.FromRgb(0x66, 0x55, 0x00)), true);

            // ── Draw top copper trace ──
            double traceLeft = centerX - tracePixW / 2;
            var traceRect = new System.Windows.Shapes.Rectangle
            {
                Width = tracePixW,
                Height = copperPixH,
                Fill = new SolidColorBrush(Color.FromRgb(0xC8, 0x70, 0x33)),     // copper
                Stroke = new SolidColorBrush(Color.FromRgb(0x90, 0x50, 0x20)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(traceRect, traceLeft);
            Canvas.SetTop(traceRect, topY);
            c.Children.Add(traceRect);

            // ── Draw ground plane (bottom copper) ──
            var gndRect = new System.Windows.Shapes.Rectangle
            {
                Width = substratePixW,
                Height = copperPixH,
                Fill = new SolidColorBrush(Color.FromRgb(0xC8, 0x70, 0x33)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x90, 0x50, 0x20)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(gndRect, subLeft);
            Canvas.SetTop(gndRect, subBot);
            c.Children.Add(gndRect);

            // GND hatching lines
            double hatchStep = 8;
            var hatchBrush = new SolidColorBrush(Color.FromArgb(80, 0x60, 0x30, 0x10));
            for (double hx = subLeft; hx < subRight; hx += hatchStep)
            {
                c.Children.Add(new Line
                {
                    X1 = hx, Y1 = subBot,
                    X2 = Math.Min(hx + copperPixH, subRight), Y2 = subBot + copperPixH,
                    Stroke = hatchBrush, StrokeThickness = 0.8
                });
            }

            // ── Dimension arrows & labels ──
            var dimBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0xCC));
            var dimPen = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0xCC));

            // W dimension (above trace)
            double dimWY = topY - 8;
            DrawDimensionH(c, traceLeft, traceLeft + tracePixW, dimWY, dimPen,
                $"W = {FormatLength(w_mm)}", dimBrush);

            // H dimension (right side)
            double dimHX = subRight + 14;
            DrawDimensionV(c, subTop, subBot, dimHX, dimPen,
                $"H = {FormatLength(h_mm)}", dimBrush);

            // T dimension (left side of trace)
            double dimTX = traceLeft - 14;
            DrawDimensionV(c, topY, topY + copperPixH, dimTX, dimPen,
                $"T = {FormatLength(t_mm)}", dimBrush);

            // ── Labels ──
            AddCenteredText(c, "Trace (Signal)", centerX, topY - 36, 10,
                Brushes.DarkRed, false);
            AddCenteredText(c, "Ground Plane", centerX, subBot + copperPixH + 4, 10,
                Brushes.DarkRed, false);

            // Z0 result at bottom
            double bottomLabelY = subBot + copperPixH + 24;
            AddCenteredText(c, $"Z₀ = {z0:F2} Ω", centerX, bottomLabelY, 14,
                Brushes.Black, true);

            // ── E-field lines (dashed, between trace and ground) ──
            var fieldBrush = new SolidColorBrush(Color.FromArgb(60, 0x00, 0x80, 0x00));
            double fieldTop = topY + copperPixH + 3;
            double fieldBot = subBot - 3;
            int nLines = Math.Max(3, (int)(tracePixW / 12));
            double fieldStep = tracePixW / (nLines + 1);
            for (int i = 1; i <= nLines; i++)
            {
                double fx = traceLeft + fieldStep * i;
                var fieldLine = new Line
                {
                    X1 = fx, Y1 = fieldTop,
                    X2 = fx, Y2 = fieldBot,
                    Stroke = fieldBrush,
                    StrokeThickness = 1.2,
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                };
                c.Children.Add(fieldLine);
                // Add arrow head at bottom
                DrawArrowDown(c, fx, fieldBot, fieldBrush);
            }

            // Fringe field lines (curved, at edges)
            DrawFringeLine(c, traceLeft, fieldTop, subLeft + (traceLeft - subLeft) * 0.3, fieldBot, fieldBrush);
            DrawFringeLine(c, traceLeft + tracePixW, fieldTop,
                subRight - (subRight - traceLeft - tracePixW) * 0.3, fieldBot, fieldBrush);
        }

        // ── Drawing helpers ──────────────────────────────────────────────

        private static void DrawDimensionH(Canvas c, double x1, double x2, double y,
            Brush lineBrush, string label, Brush textBrush)
        {
            // Horizontal dimension line with end caps
            c.Children.Add(new Line { X1 = x1, Y1 = y, X2 = x2, Y2 = y, Stroke = lineBrush, StrokeThickness = 1 });
            c.Children.Add(new Line { X1 = x1, Y1 = y - 4, X2 = x1, Y2 = y + 4, Stroke = lineBrush, StrokeThickness = 1 });
            c.Children.Add(new Line { X1 = x2, Y1 = y - 4, X2 = x2, Y2 = y + 4, Stroke = lineBrush, StrokeThickness = 1 });
            AddCenteredText(c, label, (x1 + x2) / 2, y - 16, 10, textBrush, false);
        }

        private static void DrawDimensionV(Canvas c, double y1, double y2, double x,
            Brush lineBrush, string label, Brush textBrush)
        {
            c.Children.Add(new Line { X1 = x, Y1 = y1, X2 = x, Y2 = y2, Stroke = lineBrush, StrokeThickness = 1 });
            c.Children.Add(new Line { X1 = x - 4, Y1 = y1, X2 = x + 4, Y2 = y1, Stroke = lineBrush, StrokeThickness = 1 });
            c.Children.Add(new Line { X1 = x - 4, Y1 = y2, X2 = x + 4, Y2 = y2, Stroke = lineBrush, StrokeThickness = 1 });

            var tb = new TextBlock
            {
                Text = label, FontSize = 9.5, Foreground = textBrush,
                RenderTransform = new RotateTransform(-90)
            };
            Canvas.SetLeft(tb, x - 14);
            Canvas.SetTop(tb, (y1 + y2) / 2 + 20);
            c.Children.Add(tb);
        }

        private static void DrawArrowDown(Canvas c, double x, double y, Brush fill)
        {
            var poly = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(x - 3, y - 5),
                    new Point(x + 3, y - 5),
                    new Point(x, y)
                },
                Fill = fill
            };
            c.Children.Add(poly);
        }

        private static void DrawFringeLine(Canvas c, double x1, double y1, double x2, double y2, Brush stroke)
        {
            // Simple quadratic Bezier fringe field line
            double cpx = x2;
            double cpy = y1;
            var path = new Path
            {
                Stroke = stroke,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                Data = new PathGeometry(new[]
                {
                    new PathFigure(new Point(x1, y1), new PathSegment[]
                    {
                        new QuadraticBezierSegment(new Point(cpx, cpy), new Point(x2, y2), true)
                    }, false)
                })
            };
            c.Children.Add(path);
        }

        private static void AddCenteredText(Canvas c, string text, double cx, double y,
            double fontSize, Brush foreground, bool bold)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                Foreground = foreground,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
            };
            // Measure to center
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, cx - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, y);
            c.Children.Add(tb);
        }
    }
}
