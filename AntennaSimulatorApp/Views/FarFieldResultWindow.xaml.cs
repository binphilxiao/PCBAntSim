using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using HelixToolkit.Wpf;

namespace AntennaSimulatorApp.Views
{
    public partial class FarFieldResultWindow : Window
    {
        private readonly string _resultsDir;
        private double[] _thetaE   = Array.Empty<double>();
        private double[] _patternE = Array.Empty<double>();
        private double[] _thetaH   = Array.Empty<double>();
        private double[] _patternH = Array.Empty<double>();

        // 3D pattern data  (theta × phi grid)
        private double[] _theta3D   = Array.Empty<double>();
        private double[] _phi3D     = Array.Empty<double>();
        private double[,]? _pattern3D;
        private bool _3dBuilt;

        // Summary metrics
        private double _freqGHz;
        private double _directivityDbi;
        private double _efficiency;

        public FarFieldResultWindow(string resultsDir)
        {
            InitializeComponent();
            _resultsDir = resultsDir;
            LoadData();
            ComputeMetrics();
        }

        private void LoadData()
        {
            _thetaE   = Array.Empty<double>();
            _patternE = Array.Empty<double>();
            _thetaH   = Array.Empty<double>();
            _patternH = Array.Empty<double>();

            LoadPatternCsv("FarField_Eplane.csv", out _thetaE, out _patternE);
            LoadPatternCsv("FarField_Hplane.csv", out _thetaH, out _patternH);
            Load3DPatternCsv();
            LoadSummary();
        }

        private void LoadPatternCsv(string fileName, out double[] theta, out double[] pattern)
        {
            theta = Array.Empty<double>();
            pattern = Array.Empty<double>();

            string path = System.IO.Path.Combine(_resultsDir, fileName);
            if (!File.Exists(path)) return;

            var thetas = new List<double>();
            var pats   = new List<double>();

            string[] lines;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                lines = sr.ReadToEnd().Split('\n');
            }
            catch (IOException) { return; }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("Theta", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] parts = line.Split(',');
                if (parts.Length >= 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                {
                    if (double.IsFinite(t) && double.IsFinite(p))
                    {
                        thetas.Add(t);
                        pats.Add(p);
                    }
                }
            }

            theta = thetas.ToArray();
            pattern = pats.ToArray();
        }

        private void LoadSummary()
        {
            string path = System.IO.Path.Combine(_resultsDir, "FarField_Summary.csv");
            if (!File.Exists(path)) return;

            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    string[] parts = line.Split(',');
                    if (parts.Length < 2) continue;

                    string key = parts[0].Trim();
                    if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                    {
                        if (key == "Frequency_GHz") _freqGHz = val;
                        else if (key == "Directivity_dBi") _directivityDbi = val;
                        else if (key == "RadiationEfficiency") _efficiency = val;
                    }
                }
            }
            catch { }
        }

        private void ComputeMetrics()
        {
            TxtFrequency.Text   = _freqGHz > 0 ? $"{_freqGHz:F4} GHz" : "—";
            TxtDirectivity.Text = _directivityDbi != 0 ? $"{_directivityDbi:F2} dBi" : "—";
            TxtEfficiency.Text  = _efficiency > 0 ? $"{_efficiency * 100:F1}%" : "—";
        }

        // ── Chart drawing ──────────────────────────────────────────────

        private void ChartTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != ChartTabs) return;
            Dispatcher.InvokeAsync(DrawActiveChart, DispatcherPriority.Loaded);
        }

        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawActiveChart();
        }

        private void DrawActiveChart()
        {
            switch (ChartTabs.SelectedIndex)
            {
                case 0: DrawRectangularChart(); break;
                case 1: DrawPolarChart(PolarECanvas, _thetaE, _patternE, "E-Plane", Brushes.Blue); break;
                case 2: DrawPolarChart(PolarHCanvas, _thetaH, _patternH, "H-Plane", Brushes.Red); break;
                case 3: Draw3DPattern(); break;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Rectangular pattern chart (both E & H overlaid)
        // ═══════════════════════════════════════════════════════════════

        private void DrawRectangularChart()
        {
            RectCanvas.Children.Clear();
            if (_thetaE.Length < 2 && _thetaH.Length < 2) return;

            double cw = RectCanvas.ActualWidth, ch = RectCanvas.ActualHeight;
            if (cw < 100 || ch < 80) return;

            const double ml = 60, mr = 20, mt = 20, mb = 40;
            double pw = cw - ml - mr, ph = ch - mt - mb;

            double tMin = -180, tMax = 180;
            double pMin = -40,  pMax = 5;
            double tRange = tMax - tMin, pRange = pMax - pMin;

            double Xmap(double t) => ml + (t - tMin) / tRange * pw;
            double Ymap(double p) => mt + (pMax - Math.Max(p, pMin)) / pRange * ph;

            DrawGrid(RectCanvas, ml, mt, pw, ph, tMin, tMax, tRange, pMin, pMax, pRange);
            DrawAxesBorder(RectCanvas, ml, mt, pw, ph);
            DrawAxisLabels(RectCanvas, ml, mt, pw, ph, "Theta (degrees)", "Pattern (dB)");

            // -3 dB reference line
            double py3 = Ymap(-3);
            RectCanvas.Children.Add(new Line
            {
                X1 = ml, Y1 = py3, X2 = ml + pw, Y2 = py3,
                Stroke = Brushes.Gray, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 6, 3 }, Opacity = 0.6
            });
            AddLabel(RectCanvas, "-3 dB", ml + pw - 40, py3 - 14, 9, Brushes.Gray, 0.7);

            // E-plane curve
            if (_thetaE.Length >= 2)
            {
                var poly = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(21, 101, 192)),
                    StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round
                };
                for (int i = 0; i < _thetaE.Length; i++)
                    poly.Points.Add(new Point(Xmap(_thetaE[i]), Ymap(_patternE[i])));
                RectCanvas.Children.Add(poly);
            }

            // H-plane curve
            if (_thetaH.Length >= 2)
            {
                var poly = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(198, 40, 40)),
                    StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round,
                    StrokeDashArray = new DoubleCollection { 6, 3 }
                };
                for (int i = 0; i < _thetaH.Length; i++)
                    poly.Points.Add(new Point(Xmap(_thetaH[i]), Ymap(_patternH[i])));
                RectCanvas.Children.Add(poly);
            }

            // Legend
            double legX = ml + 10, legY = mt + 8;
            var blueB = new SolidColorBrush(Color.FromRgb(21, 101, 192));
            var redB  = new SolidColorBrush(Color.FromRgb(198, 40, 40));
            RectCanvas.Children.Add(new Line { X1 = legX, Y1 = legY + 5, X2 = legX + 20, Y2 = legY + 5, Stroke = blueB, StrokeThickness = 2 });
            AddLabel(RectCanvas, "E-plane", legX + 24, legY - 2, 10, blueB);
            legY += 18;
            RectCanvas.Children.Add(new Line { X1 = legX, Y1 = legY + 5, X2 = legX + 20, Y2 = legY + 5, Stroke = redB, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 6, 3 } });
            AddLabel(RectCanvas, "H-plane", legX + 24, legY - 2, 10, redB);

            // Peak marker for E-plane
            if (_thetaE.Length >= 2)
            {
                int idxMax = 0;
                for (int i = 1; i < _patternE.Length; i++)
                    if (_patternE[i] > _patternE[idxMax]) idxMax = i;
                double mx = Xmap(_thetaE[idxMax]), my = Ymap(_patternE[idxMax]);
                AddMarker(RectCanvas, mx, my, Brushes.Red);
                AddLabel(RectCanvas, $"Peak: {_thetaE[idxMax]:F0}°, {_patternE[idxMax]:F1} dB",
                    mx + 8, my - 16, 10, Brushes.Red, 1.0, FontWeights.SemiBold);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Polar pattern chart
        // ═══════════════════════════════════════════════════════════════

        private void DrawPolarChart(Canvas canvas, double[] theta, double[] pattern,
            string title, Brush curveColor)
        {
            canvas.Children.Clear();
            if (theta.Length < 2) return;

            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            if (cw < 100 || ch < 100) return;

            double margin = 40;
            double size = Math.Min(cw, ch) - 2 * margin;
            if (size < 50) return;
            double radius = size / 2.0;
            double cx = cw / 2, cy = ch / 2;

            double dbMin = -40, dbMax = 0;
            double dbRange = dbMax - dbMin;

            // Map dB value to radius
            double Rmap(double db) => Math.Max(0, (db - dbMin) / dbRange) * radius;

            // Grid circles at -10, -20, -30 dB
            var gridBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200));
            double[] dbLevels = { -30, -20, -10, 0 };
            foreach (double db in dbLevels)
            {
                double r = Rmap(db);
                if (r <= 0) continue;
                var ellipse = new Ellipse
                {
                    Width = r * 2, Height = r * 2,
                    Stroke = gridBrush, StrokeThickness = 0.7,
                    Fill = Brushes.Transparent
                };
                Canvas.SetLeft(ellipse, cx - r); Canvas.SetTop(ellipse, cy - r);
                canvas.Children.Add(ellipse);
                AddLabel(canvas, $"{db:F0} dB", cx + 2, cy - r - 14, 9, Brushes.Gray, 0.7);
            }

            // Radial lines at 0, 30, 60, 90, ... degrees
            for (int deg = 0; deg < 360; deg += 30)
            {
                double rad = deg * Math.PI / 180.0;
                double x2 = cx + radius * Math.Cos(rad);
                double y2 = cy - radius * Math.Sin(rad);
                canvas.Children.Add(new Line
                {
                    X1 = cx, Y1 = cy, X2 = x2, Y2 = y2,
                    Stroke = gridBrush, StrokeThickness = 0.5
                });
                // Angle labels
                double lx = cx + (radius + 12) * Math.Cos(rad) - 10;
                double ly = cy - (radius + 12) * Math.Sin(rad) - 7;
                AddLabel(canvas, $"{deg}°", lx, ly, 9, Brushes.Gray, 0.6);
            }

            // Data curve
            var polyline = new Polyline
            {
                Stroke = curveColor, StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            };
            for (int i = 0; i < theta.Length; i++)
            {
                double db = Math.Max(pattern[i], dbMin);
                double r = Rmap(db);
                double rad = theta[i] * Math.PI / 180.0;
                double px = cx + r * Math.Cos(rad);
                double py = cy - r * Math.Sin(rad);
                polyline.Points.Add(new Point(px, py));
            }
            canvas.Children.Add(polyline);

            // Title
            AddLabel(canvas, title, cx - 25, margin / 2 - 8, 13, Brushes.Black, 1.0, FontWeights.SemiBold);

            // Peak marker
            if (theta.Length >= 2)
            {
                int idxMax = 0;
                for (int i = 1; i < pattern.Length; i++)
                    if (pattern[i] > pattern[idxMax]) idxMax = i;

                double peakDb = Math.Max(pattern[idxMax], dbMin);
                double peakR = Rmap(peakDb);
                double peakRad = theta[idxMax] * Math.PI / 180.0;
                double pmx = cx + peakR * Math.Cos(peakRad);
                double pmy = cy - peakR * Math.Sin(peakRad);
                AddMarker(canvas, pmx, pmy, Brushes.Red, 7);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 3D pattern data loading
        // ═══════════════════════════════════════════════════════════════

        private void Load3DPatternCsv()
        {
            _pattern3D = null;
            string path = System.IO.Path.Combine(_resultsDir, "FarField_3D.csv");
            if (!File.Exists(path)) return;

            // Read raw rows: theta, phi, dB
            var rows = new List<(double theta, double phi, double dB)>();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line.StartsWith("Theta", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string[] parts = line.Split(',');
                    if (parts.Length >= 3
                        && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double p)
                        && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    {
                        if (double.IsFinite(t) && double.IsFinite(p) && double.IsFinite(d))
                            rows.Add((t, p, d));
                    }
                }
            }
            catch { return; }

            if (rows.Count == 0) return;

            // Extract unique sorted theta/phi arrays
            var thetaSet = new SortedSet<double>();
            var phiSet   = new SortedSet<double>();
            foreach (var r in rows) { thetaSet.Add(r.theta); phiSet.Add(r.phi); }

            _theta3D = thetaSet.ToArray();
            _phi3D   = phiSet.ToArray();

            int nTheta = _theta3D.Length;
            int nPhi   = _phi3D.Length;
            _pattern3D = new double[nTheta, nPhi];
            // Fill with minimum
            for (int i = 0; i < nTheta; i++)
                for (int j = 0; j < nPhi; j++)
                    _pattern3D[i, j] = -40;

            // Build lookup
            var thetaIdx = new Dictionary<double, int>();
            var phiIdx   = new Dictionary<double, int>();
            for (int i = 0; i < nTheta; i++) thetaIdx[_theta3D[i]] = i;
            for (int j = 0; j < nPhi; j++) phiIdx[_phi3D[j]] = j;

            foreach (var r in rows)
            {
                if (thetaIdx.TryGetValue(r.theta, out int ti) && phiIdx.TryGetValue(r.phi, out int pi))
                    _pattern3D[ti, pi] = r.dB;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 3D radiation pattern rendering
        // ═══════════════════════════════════════════════════════════════

        private void Draw3DPattern()
        {
            if (_3dBuilt) return;

            if (_pattern3D == null)
            {
                // Show a hint in the viewport
                var hint = new BillboardTextVisual3D
                {
                    Text = "No 3D data. Re-run simulation with far-field analysis.",
                    Position = new Point3D(0, 0, 0),
                    FontSize = 16,
                    Foreground = Brushes.Gray
                };
                Viewport3D.Children.Add(hint);
                _3dBuilt = true;
                return;
            }

            _3dBuilt = true;

            int nTheta = _theta3D.Length;
            int nPhi   = _phi3D.Length;
            if (nTheta < 2 || nPhi < 2) return;

            const double dbMin = -40.0;
            const double dbMax = 0.0;
            const double dbRange = dbMax - dbMin;

            // Build mesh positions, texture coords, and triangle indices
            var positions = new Point3DCollection(nTheta * nPhi);
            var texCoords = new PointCollection(nTheta * nPhi);
            var indices   = new Int32Collection((nTheta - 1) * (nPhi - 1) * 6);

            for (int ti = 0; ti < nTheta; ti++)
            {
                double thetaRad = _theta3D[ti] * Math.PI / 180.0;
                for (int pi = 0; pi < nPhi; pi++)
                {
                    double phiRad = _phi3D[pi] * Math.PI / 180.0;
                    double dB = Math.Max(_pattern3D[ti, pi], dbMin);
                    double r = (dB - dbMin) / dbRange; // 0..1
                    r = 0.15 + 0.85 * r; // minimum radius so we can see it

                    double x = r * Math.Sin(thetaRad) * Math.Cos(phiRad);
                    double y = r * Math.Sin(thetaRad) * Math.Sin(phiRad);
                    double z = r * Math.Cos(thetaRad);

                    positions.Add(new Point3D(x, y, z));
                    // Texture U = normalized dB (for color), V unused
                    double u = Math.Clamp((dB - dbMin) / dbRange, 0, 1);
                    texCoords.Add(new Point(u, 0.5));
                }
            }

            // Triangles
            for (int ti = 0; ti < nTheta - 1; ti++)
            {
                for (int pi = 0; pi < nPhi - 1; pi++)
                {
                    int i00 = ti * nPhi + pi;
                    int i01 = ti * nPhi + pi + 1;
                    int i10 = (ti + 1) * nPhi + pi;
                    int i11 = (ti + 1) * nPhi + pi + 1;

                    indices.Add(i00); indices.Add(i10); indices.Add(i11);
                    indices.Add(i00); indices.Add(i11); indices.Add(i01);
                }
            }

            var mesh = new MeshGeometry3D
            {
                Positions = positions,
                TextureCoordinates = texCoords,
                TriangleIndices = indices
            };

            // Create a 1D gradient texture (256×1 pixel) for color mapping
            var brush = CreateJetGradientBrush();
            var material = new DiffuseMaterial(brush);
            var backMaterial = new DiffuseMaterial(brush);

            var model = new GeometryModel3D(mesh, material)
            {
                BackMaterial = backMaterial
            };

            var visual = new ModelVisual3D { Content = model };
            Viewport3D.Children.Add(visual);

            // Add axis lines
            AddAxisLine(new Point3D(0, 0, 0), new Point3D(1.4, 0, 0), Colors.Red);
            AddAxisLine(new Point3D(0, 0, 0), new Point3D(0, 1.4, 0), Colors.Green);
            AddAxisLine(new Point3D(0, 0, 0), new Point3D(0, 0, 1.4), Colors.Blue);

            Viewport3D.ZoomExtents();
        }

        private static Brush CreateJetGradientBrush()
        {
            // Jet-like colormap: blue → cyan → green → yellow → red
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 0, 180), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 100, 255), 0.15));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 220, 220), 0.3));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 200, 0), 0.45));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 255, 0), 0.65));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 160, 0), 0.8));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(220, 0, 0), 1.0));
            brush.Freeze();

            // Render to a 256x1 bitmap for use as texture
            int w = 256, h = 1;
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var rect = new Rectangle { Width = w, Height = h, Fill = brush };
            rect.Measure(new Size(w, h));
            rect.Arrange(new Rect(0, 0, w, h));
            rtb.Render(rect);
            rtb.Freeze();

            var imgBrush = new ImageBrush(rtb)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1)
            };
            imgBrush.Freeze();
            return imgBrush;
        }

        private void AddAxisLine(Point3D from, Point3D to, Color color)
        {
            var line = new LinesVisual3D
            {
                Color = color,
                Thickness = 2
            };
            line.Points.Add(from);
            line.Points.Add(to);
            Viewport3D.Children.Add(line);
        }

        // ═══════════════════════════════════════════════════════════════
        // Shared helpers
        // ═══════════════════════════════════════════════════════════════

        private void DrawGrid(Canvas canvas, double ml, double mt, double pw, double ph,
            double xMin, double xMax, double xRange, double yMin, double yMax, double yRange)
        {
            var gridBrush = new SolidColorBrush(Color.FromRgb(230, 230, 230));

            double yStep = NiceStep(yRange, 6);
            double yStart = Math.Ceiling(yMin / yStep) * yStep;
            for (double y = yStart; y <= yMax; y += yStep)
            {
                double py = mt + (yMax - y) / yRange * ph;
                canvas.Children.Add(new Line { X1 = ml, Y1 = py, X2 = ml + pw, Y2 = py, Stroke = gridBrush, StrokeThickness = 1 });
                var lbl = new TextBlock
                {
                    Text = $"{y:F0}", FontSize = 10, Foreground = Brushes.Gray,
                    TextAlignment = TextAlignment.Right, Width = 45
                };
                Canvas.SetLeft(lbl, ml - 50); Canvas.SetTop(lbl, py - 7);
                canvas.Children.Add(lbl);
            }

            double xStep = NiceStep(xRange, 8);
            double xStart = Math.Ceiling(xMin / xStep) * xStep;
            for (double x = xStart; x <= xMax; x += xStep)
            {
                double px = ml + (x - xMin) / xRange * pw;
                canvas.Children.Add(new Line { X1 = px, Y1 = mt, X2 = px, Y2 = mt + ph, Stroke = gridBrush, StrokeThickness = 1 });
                var lbl = new TextBlock { Text = $"{x:F0}", FontSize = 10, Foreground = Brushes.Gray };
                Canvas.SetLeft(lbl, px - 10); Canvas.SetTop(lbl, mt + ph + 4);
                canvas.Children.Add(lbl);
            }
        }

        private static void DrawAxesBorder(Canvas canvas, double ml, double mt, double pw, double ph)
        {
            canvas.Children.Add(new Line { X1 = ml, Y1 = mt, X2 = ml, Y2 = mt + ph, Stroke = Brushes.DarkGray, StrokeThickness = 1 });
            canvas.Children.Add(new Line { X1 = ml, Y1 = mt + ph, X2 = ml + pw, Y2 = mt + ph, Stroke = Brushes.DarkGray, StrokeThickness = 1 });
        }

        private static void DrawAxisLabels(Canvas canvas, double ml, double mt, double pw, double ph,
            string xLabel, string yLabel)
        {
            var xLbl = new TextBlock { Text = xLabel, FontSize = 11, Foreground = Brushes.DimGray };
            Canvas.SetLeft(xLbl, ml + pw / 2 - 45); Canvas.SetTop(xLbl, mt + ph + 22);
            canvas.Children.Add(xLbl);

            var yLbl = new TextBlock
            {
                Text = yLabel, FontSize = 11, Foreground = Brushes.DimGray,
                RenderTransform = new RotateTransform(-90),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            Canvas.SetLeft(yLbl, 2); Canvas.SetTop(yLbl, mt + ph / 2 + 15);
            canvas.Children.Add(yLbl);
        }

        private static void AddMarker(Canvas canvas, double x, double y, Brush fill, double size = 8)
        {
            var m = new Ellipse { Width = size, Height = size, Fill = fill };
            Canvas.SetLeft(m, x - size / 2); Canvas.SetTop(m, y - size / 2);
            canvas.Children.Add(m);
        }

        private static void AddLabel(Canvas canvas, string text, double x, double y,
            double fontSize, Brush fg, double opacity = 1.0, FontWeight? weight = null)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = fontSize, Foreground = fg,
                Opacity = opacity, FontWeight = weight ?? FontWeights.Normal
            };
            Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
            canvas.Children.Add(tb);
        }

        private static double NiceStep(double range, int maxTicks)
        {
            double rough = range / maxTicks;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
            double residual = rough / mag;
            double nice;
            if (residual <= 1.5) nice = 1;
            else if (residual <= 3) nice = 2;
            else if (residual <= 7) nice = 5;
            else nice = 10;
            return nice * mag;
        }

        // ── Button handlers ────────────────────────────────────────────

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = _resultsDir, UseShellExecute = true }); }
            catch { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ── 3D view control buttons ─────────────────────────────────────

        private void FF_SetView(Point3D pos, Vector3D up)
        {
            var look = new Vector3D(-pos.X, -pos.Y, -pos.Z);
            look.Normalize();
            var cam = new PerspectiveCamera
            {
                Position = pos,
                LookDirection = look,
                UpDirection = up,
                FieldOfView = 45
            };
            Viewport3D.Camera = cam;
            Viewport3D.ZoomExtents(0);
        }

        private void FF_ViewTop_Click(object sender, RoutedEventArgs e)
            => FF_SetView(new Point3D(0, 0, 5), new Vector3D(0, 1, 0));
        private void FF_ViewBottom_Click(object sender, RoutedEventArgs e)
            => FF_SetView(new Point3D(0, 0, -5), new Vector3D(0, 1, 0));
        private void FF_ViewFront_Click(object sender, RoutedEventArgs e)
            => FF_SetView(new Point3D(0, 5, 0), new Vector3D(0, 0, 1));
        private void FF_ViewBack_Click(object sender, RoutedEventArgs e)
            => FF_SetView(new Point3D(0, -5, 0), new Vector3D(0, 0, 1));
        private void FF_ViewLeft_Click(object sender, RoutedEventArgs e)
            => FF_SetView(new Point3D(-5, 0, 0), new Vector3D(0, 0, 1));
        private void FF_ViewRight_Click(object sender, RoutedEventArgs e)
            => FF_SetView(new Point3D(5, 0, 0), new Vector3D(0, 0, 1));

        private void FF_ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            if (Viewport3D.Camera is PerspectiveCamera pc)
            {
                var offset = new Vector3D(pc.LookDirection.X, pc.LookDirection.Y, pc.LookDirection.Z);
                offset.Normalize();
                double step = (pc.Position - new Point3D()).Length * 0.2;
                pc.Position += offset * step;
            }
        }

        private void FF_ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            if (Viewport3D.Camera is PerspectiveCamera pc)
            {
                var offset = new Vector3D(pc.LookDirection.X, pc.LookDirection.Y, pc.LookDirection.Z);
                offset.Normalize();
                double step = (pc.Position - new Point3D()).Length * 0.2;
                pc.Position -= offset * step;
            }
        }

        private void FF_ZoomFit_Click(object sender, RoutedEventArgs e)
            => Viewport3D.ZoomExtents(200);

        // ── Keyboard arrow keys ─────────────────────────────────────

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Only handle arrow keys when 3D tab is active
            if (ChartTabs.SelectedIndex != 3) return;

            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            const double OrbitStep = 5.0;   // degrees per key press
            const double ZoomStep  = 0.12;  // fraction of distance

            switch (e.Key)
            {
                case Key.Left:  if (!shift) { FF_OrbitCamera(-OrbitStep, 0); e.Handled = true; } break;
                case Key.Right: if (!shift) { FF_OrbitCamera(+OrbitStep, 0); e.Handled = true; } break;
                case Key.Up:
                    if (!shift) { FF_OrbitCamera(0, +OrbitStep); e.Handled = true; }
                    else        { FF_ZoomCamera(1.0 - ZoomStep);  e.Handled = true; }
                    break;
                case Key.Down:
                    if (!shift) { FF_OrbitCamera(0, -OrbitStep); e.Handled = true; }
                    else        { FF_ZoomCamera(1.0 + ZoomStep);  e.Handled = true; }
                    break;
            }
        }

        private void FF_OrbitCamera(double dAz, double dEl)
        {
            if (Viewport3D.Camera is not PerspectiveCamera cam) return;

            var look = cam.LookDirection;
            double dist = look.Length;
            look.Normalize();
            var target = cam.Position + look * dist;
            var arm    = cam.Position - target;

            // Azimuth: rotate arm around world Z
            if (Math.Abs(dAz) > 1e-9)
            {
                double rad = dAz * Math.PI / 180.0;
                double c = Math.Cos(rad), s = Math.Sin(rad);
                arm = new Vector3D(arm.X * c - arm.Y * s,
                                   arm.X * s + arm.Y * c,
                                   arm.Z);
            }

            // Elevation: rotate arm around camera-right axis (Rodrigues)
            if (Math.Abs(dEl) > 1e-9)
            {
                var right = Vector3D.CrossProduct(look, cam.UpDirection);
                right.Normalize();
                double rad = dEl * Math.PI / 180.0;
                double c = Math.Cos(rad), s = Math.Sin(rad);
                double dot = Vector3D.DotProduct(right, arm);
                var cross = Vector3D.CrossProduct(right, arm);
                arm = arm * c + cross * s + right * (dot * (1 - c));

                var armN = arm; armN.Normalize();
                if (Math.Abs(armN.Z / armN.Length) > Math.Cos(3.0 * Math.PI / 180.0))
                    return;
            }

            cam.Position      = target + arm;
            cam.LookDirection = target - cam.Position;
        }

        private void FF_ZoomCamera(double factor)
        {
            if (Viewport3D.Camera is not PerspectiveCamera cam) return;

            var look = cam.LookDirection;
            double dist = look.Length;
            look.Normalize();

            double newDist = Math.Max(0.5, dist * factor);
            cam.Position      = cam.Position + look * (dist - newDist);
            cam.LookDirection = look * newDist;
        }
    }
}
