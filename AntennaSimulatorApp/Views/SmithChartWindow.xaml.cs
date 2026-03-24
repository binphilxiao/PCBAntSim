using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AntennaSimulatorApp.Views
{
    public partial class SmithChartWindow : Window
    {
        private bool _suppressUpdate;
        private List<MatchingSolution> _solutions = new();
        private string _lastTopologyName = "";  // remember selected topology across redraws

        public SmithChartWindow()
        {
            InitializeComponent();
        }

        // ── Chart drawing ────────────────────────────────────────────────

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

        private void OnParamChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressUpdate) return;
            Redraw();
        }

        private void Redraw()
        {
            if (ChartCanvas == null) return;
            ChartCanvas.Children.Clear();

            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            if (w < 10 || h < 10) return;

            double radius = Math.Min(w, h) / 2.0 - 20;
            double cx = w / 2.0, cy = h / 2.0;

            // Unit circle (|Γ|=1)
            DrawCircle(cx, cy, radius, Brushes.Black, 1.5);

            // Constant-r circles
            double[] rVals = { 0, 0.2, 0.5, 1.0, 2.0, 5.0 };
            foreach (double r in rVals)
                DrawConstantRCircle(cx, cy, radius, r);

            // Constant-x arcs
            double[] xVals = { 0.2, 0.5, 1.0, 2.0, 5.0 };
            foreach (double x in xVals)
            {
                DrawConstantXArc(cx, cy, radius, x);
                DrawConstantXArc(cx, cy, radius, -x);
            }

            // Horizontal axis (x=0 line)
            ChartCanvas.Children.Add(new Line
            {
                X1 = cx - radius, Y1 = cy, X2 = cx + radius, Y2 = cy,
                Stroke = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                StrokeThickness = 0.5
            });

            // Labels on real axis
            foreach (double r in rVals)
            {
                double gx = (r / (r + 1));
                double sx = cx + gx * radius;
                var tb = new TextBlock
                {
                    Text = r.ToString("G3"),
                    FontSize = 9,
                    Foreground = Brushes.Gray
                };
                Canvas.SetLeft(tb, sx - 5);
                Canvas.SetTop(tb, cy + 3);
                ChartCanvas.Children.Add(tb);
            }

            // Parse inputs and plot load point
            if (!TryParseInputs(out double z0, out double rl, out double xl, out double freqGHz))
                return;

            Complex zL = new Complex(rl, xl);
            Complex zNorm = zL / z0;
            Complex gamma = (zNorm - 1) / (zNorm + 1);
            Complex yNorm = 1.0 / zNorm;

            // Update readout
            double gammaMag = gamma.Magnitude;
            double gammaAngleDeg = gamma.Phase * 180.0 / Math.PI;
            double vswr = gammaMag < 1 ? (1 + gammaMag) / (1 - gammaMag) : double.PositiveInfinity;
            double returnLoss = gammaMag > 0 ? -20 * Math.Log10(gammaMag) : double.PositiveInfinity;
            double mismatchLoss = -10 * Math.Log10(1 - gammaMag * gammaMag);

            TxtGammaMag.Text = $"|Γ| = {gammaMag:F4}";
            TxtGammaAngle.Text = $"∠Γ = {gammaAngleDeg:F1}°";
            TxtVSWR.Text = vswr < 1e6 ? $"VSWR = {vswr:F2}" : "VSWR = ∞";
            TxtReturnLoss.Text = double.IsInfinity(returnLoss)
                ? "Return Loss = ∞ dB" : $"Return Loss = {returnLoss:F2} dB";
            TxtMismatchLoss.Text = $"Mismatch Loss = {mismatchLoss:F3} dB";

            TxtNormZ.Text = $"z = {zNorm.Real:F4} + j{zNorm.Imaginary:F4}";
            TxtNormY.Text = $"y = {yNorm.Real:F4} + j{yNorm.Imaginary:F4}";

            // Plot load point on Smith chart
            double ptX = cx + gamma.Real * radius;
            double ptY = cy - gamma.Imaginary * radius;
            DrawDot(ptX, ptY, 6, Brushes.Red);

            // Label load point
            AddText(ChartCanvas, "ZL", ptX + 5, ptY - 14, 10, Brushes.Red, true);

            // Plot Z0 centre point
            DrawDot(cx, cy, 5, Brushes.Green);
            AddText(ChartCanvas, "Z₀", cx + 5, cy - 14, 10, Brushes.Green, true);

            // VSWR circle
            DrawCircle(cx, cy, gammaMag * radius,
                new SolidColorBrush(Color.FromArgb(80, 255, 0, 0)), 1.2, dashed: true);

            // L-network matching
            ComputeLNetwork(z0, zL, freqGHz);

            // Draw impedance trajectory for the selected solution
            DrawSelectedSolutionPath(cx, cy, radius, z0);
        }

        // ── Constant-r circle ────────────────────────────────────────────

        private void DrawConstantRCircle(double cx, double cy, double unitR, double r)
        {
            // In Γ-plane: center at (r/(r+1), 0), radius 1/(r+1)
            double circR = unitR / (r + 1);
            double circCx = cx + unitR * r / (r + 1);
            DrawCircle(circCx, cy, circR,
                new SolidColorBrush(Color.FromRgb(0xAA, 0xCC, 0xEE)), 0.6);
        }

        // ── Constant-x arc (clipped to unit circle) ─────────────────────

        private void DrawConstantXArc(double cx, double cy, double unitR, double x)
        {
            // Center at (1, 1/x) in Γ-plane, radius |1/x|
            if (Math.Abs(x) < 1e-12) return;

            double arcR = unitR / Math.Abs(x);
            double arcCx = cx + unitR;
            double arcCy = cy - unitR / x;

            // Clip: find intersections with unit circle
            // Unit circle: (gx-cx)^2 + (gy-cy)^2 = unitR^2
            // Arc circle: (gx-arcCx)^2 + (gy-arcCy)^2 = arcR^2
            // We parametrically sample and draw polyline inside unit circle
            int N = 200;
            var points = new List<Point>();
            for (int i = 0; i <= N; i++)
            {
                double angle = 2 * Math.PI * i / N;
                double px = arcCx + arcR * Math.Cos(angle);
                double py = arcCy + arcR * Math.Sin(angle);

                double dx = px - cx, dy = py - cy;
                if (dx * dx + dy * dy <= unitR * unitR + 1)
                    points.Add(new Point(px, py));
            }

            if (points.Count < 2) return;

            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xEE, 0xBB, 0xAA)),
                StrokeThickness = 0.5,
                StrokeLineJoin = PenLineJoin.Round
            };
            foreach (var p in points)
                polyline.Points.Add(p);
            ChartCanvas.Children.Add(polyline);
        }

        // ── Drawing helpers ──────────────────────────────────────────────

        private void DrawCircle(double cx, double cy, double r, Brush stroke,
            double thickness, bool dashed = false)
        {
            var ell = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Stroke = stroke, StrokeThickness = thickness,
                Fill = Brushes.Transparent,
            };
            if (dashed)
                ell.StrokeDashArray = new DoubleCollection { 4, 3 };
            Canvas.SetLeft(ell, cx - r);
            Canvas.SetTop(ell, cy - r);
            ChartCanvas.Children.Add(ell);
        }

        private void DrawDot(double x, double y, double size, Brush fill)
        {
            var ell = new Ellipse
            {
                Width = size, Height = size,
                Fill = fill, Stroke = Brushes.DarkRed, StrokeThickness = 1
            };
            Canvas.SetLeft(ell, x - size / 2);
            Canvas.SetTop(ell, y - size / 2);
            ChartCanvas.Children.Add(ell);
        }

        // ── L-Network matching ───────────────────────────────────────────

        private void ComputeLNetwork(double z0, Complex zL, double freqGHz)
        {
            double rl = zL.Real, xl = zL.Imaginary;
            double omega = 2 * Math.PI * freqGHz * 1e9;

            _solutions.Clear();
            CmbSolution.Items.Clear();
            SchematicCanvas.Children.Clear();
            TxtComp1.Text = ""; TxtComp2.Text = ""; TxtComp3.Text = "";
            TxtMatchInfo.Text = "";

            if (Math.Abs(rl - z0) < 0.01 && Math.Abs(xl) < 0.01)
            {
                CmbSolution.Items.Add("Already matched!");
                CmbSolution.SelectedIndex = 0;
                TxtMatchInfo.Text = "ZL ≈ Z0, no matching network needed.";
                return;
            }
            if (rl <= 0)
            {
                CmbSolution.Items.Add("R must be > 0");
                CmbSolution.SelectedIndex = 0;
                return;
            }

            // L-network solutions (up to 4)
            ComputeLNetworkSolutions(z0, zL, omega, _solutions);

            // π-network solutions
            double qFactor = 5.0;
            if (double.TryParse(TxtQFactor?.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double qParsed) && qParsed > 0)
                qFactor = qParsed;
            ComputePiNetworkSolutions(z0, zL, omega, qFactor, _solutions);

            if (_solutions.Count == 0)
            {
                CmbSolution.Items.Add("No solution found");
                CmbSolution.SelectedIndex = 0;
                return;
            }

            _suppressUpdate = true;
            for (int i = 0; i < _solutions.Count; i++)
                CmbSolution.Items.Add($"Solution {i + 1}: {_solutions[i].TopologyName}");

            // Try to restore previously selected topology type
            int restoreIdx = 0;
            if (!string.IsNullOrEmpty(_lastTopologyName))
            {
                for (int i = 0; i < _solutions.Count; i++)
                {
                    if (_solutions[i].TopologyName == _lastTopologyName)
                    { restoreIdx = i; break; }
                }
            }
            CmbSolution.SelectedIndex = restoreIdx;
            _suppressUpdate = false;

            ShowSelectedSolution();
        }

        private void CmbSolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUpdate) return;
            ShowSelectedSolution();
        }

        private void ShowSelectedSolution()
        {
            int idx = CmbSolution.SelectedIndex;
            if (idx < 0 || idx >= _solutions.Count)
            {
                SchematicCanvas.Children.Clear();
                TxtComp1.Text = ""; TxtComp2.Text = ""; TxtComp3.Text = ""; TxtMatchInfo.Text = "";
                return;
            }

            var sol = _solutions[idx];
            _lastTopologyName = sol.TopologyName;  // remember for next redraw
            TxtComp1.Text = sol.Comp1Desc;
            TxtComp2.Text = sol.Comp2Desc;
            TxtComp3.Text = sol.Comp3Desc;
            TxtMatchInfo.Text = sol.InfoText;
            DrawSchematic(sol);

            // Redraw only the impedance path on the chart (not full Redraw to avoid recursion)
            RedrawSolutionPathOnly();
        }

        /// <summary>
        /// Redraws just the matching path on the Smith chart without triggering a full Redraw.
        /// Removes old path elements and adds new ones.
        /// </summary>
        private void RedrawSolutionPathOnly()
        {
            // Remove previous path elements (tagged with "PathElement")
            for (int i = ChartCanvas.Children.Count - 1; i >= 0; i--)
            {
                if (ChartCanvas.Children[i] is FrameworkElement fe && fe.Tag as string == "PathElement")
                    ChartCanvas.Children.RemoveAt(i);
            }

            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            if (w < 10 || h < 10) return;
            double radius = Math.Min(w, h) / 2.0 - 20;
            double cx = w / 2.0, cy = h / 2.0;

            if (!TryParseInputs(out double z0, out _, out _, out _)) return;
            DrawSelectedSolutionPath(cx, cy, radius, z0);
        }

        // ── Impedance trajectory drawing ─────────────────────────────────

        private static readonly Color InductorColor  = Color.FromRgb(0x00, 0x70, 0xC0);
        private static readonly Color CapacitorColor = Color.FromRgb(0xC0, 0x40, 0x00);

        private void DrawSelectedSolutionPath(double cx, double cy, double radius, double z0)
        {
            int idx = CmbSolution.SelectedIndex;
            if (idx < 0 || idx >= _solutions.Count) return;
            var sol = _solutions[idx];
            if (sol.PathPoints.Count < 2) return;

            for (int seg = 0; seg < sol.PathPoints.Count - 1; seg++)
            {
                Complex zStart = sol.PathPoints[seg];
                Complex zEnd   = sol.PathPoints[seg + 1];
                bool isL = seg < sol.SegmentIsInductor.Count && sol.SegmentIsInductor[seg];
                var segColor = isL ? InductorColor : CapacitorColor;
                DrawSmithArc(cx, cy, radius, z0, zStart, zEnd, segColor);

                // Draw intermediate waypoint dot
                if (seg > 0)
                {
                    Complex gS = (zStart / z0 - 1) / (zStart / z0 + 1);
                    double dx = cx + gS.Real * radius;
                    double dy = cy - gS.Imaginary * radius;
                    var dot = new Ellipse
                    {
                        Width = 5, Height = 5,
                        Fill = new SolidColorBrush(segColor),
                        Tag = "PathElement"
                    };
                    Canvas.SetLeft(dot, dx - 2.5);
                    Canvas.SetTop(dot, dy - 2.5);
                    ChartCanvas.Children.Add(dot);
                }
            }
        }

        /// <summary>
        /// Draw an arc on the Smith Chart between two impedance points.
        /// Interpolates in the Γ-plane with 80 sample points.
        /// </summary>
        private void DrawSmithArc(double cx, double cy, double radius,
            double z0, Complex zFrom, Complex zTo, Color color)
        {
            Complex gFrom = (zFrom / z0 - 1) / (zFrom / z0 + 1);
            Complex gTo   = (zTo   / z0 - 1) / (zTo   / z0 + 1);

            // Interpolate impedance linearly in reactance/susceptance,
            // generating the correct arc on the Smith chart.
            // Determine if this is a series element (same R, different X)
            // or shunt element (same G, different B).
            const int N = 80;
            var pts = new PointCollection(N + 1);

            Complex yFrom = 1.0 / zFrom;
            Complex yTo   = 1.0 / zTo;
            bool isShunt = Math.Abs(yFrom.Real - yTo.Real) < Math.Abs(zFrom.Real - zTo.Real) * 0.01 + 1e-9;

            for (int i = 0; i <= N; i++)
            {
                double t = (double)i / N;
                Complex zMid;
                if (isShunt)
                {
                    // Shunt element: interpolate susceptance B at constant G
                    Complex yMid = new Complex(
                        yFrom.Real * (1 - t) + yTo.Real * t,
                        yFrom.Imaginary * (1 - t) + yTo.Imaginary * t);
                    zMid = 1.0 / yMid;
                }
                else
                {
                    // Series element: interpolate reactance X at constant R
                    zMid = new Complex(
                        zFrom.Real * (1 - t) + zTo.Real * t,
                        zFrom.Imaginary * (1 - t) + zTo.Imaginary * t);
                }
                Complex gMid = (zMid / z0 - 1) / (zMid / z0 + 1);
                if (gMid.Magnitude > 1.01) continue;
                pts.Add(new Point(cx + gMid.Real * radius, cy - gMid.Imaginary * radius));
            }

            if (pts.Count < 2) return;

            var polyline = new Polyline
            {
                Points = pts,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round,
                Tag = "PathElement"
            };
            ChartCanvas.Children.Add(polyline);

            // Draw arrowhead at the end
            if (pts.Count >= 2)
            {
                var p1 = pts[pts.Count - 2];
                var p2 = pts[pts.Count - 1];
                DrawArrowhead(p2, p1, color);
            }
        }

        private void DrawArrowhead(Point tip, Point from, Color color)
        {
            double dx = tip.X - from.X;
            double dy = tip.Y - from.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return;
            dx /= len; dy /= len;
            double arrowLen = 8;
            double arrowWidth = 4;
            double bx = tip.X - dx * arrowLen;
            double by = tip.Y - dy * arrowLen;
            var brush = new SolidColorBrush(color);
            var poly = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    tip,
                    new Point(bx + dy * arrowWidth, by - dx * arrowWidth),
                    new Point(bx - dy * arrowWidth, by + dx * arrowWidth)
                },
                Tag = "PathElement"
            };
            ChartCanvas.Children.Add(poly);
        }

        // ── Schematic drawing ────────────────────────────────────────────

        private void DrawSchematic(MatchingSolution sol)
        {
            var c = SchematicCanvas;
            c.Children.Clear();
            double w = c.ActualWidth > 0 ? c.ActualWidth : 250;
            double h = c.ActualHeight > 0 ? c.ActualHeight : 120;

            var wirePen = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            double wireY = h * 0.38;
            double gndY = h * 0.82;

            double leftX = 16;
            double rightX = w - 16;
            double midX = w / 2.0;

            // Source label
            AddText(c, "Z₀", leftX - 2, wireY - 20, 11, Brushes.Black, true);
            // Load label
            AddText(c, "ZL", rightX - 8, wireY - 20, 11, Brushes.Black, true);

            if (sol.Topology == MatchTopology.ShuntThenSeries)
            {
                // Source ──[wire]──┬──[series comp]──── Load
                //                  │
                //              [shunt comp]
                //                  │
                //                 GND

                double juncX = midX - 20;
                double seriesStartX = juncX + 4;
                double seriesEndX = rightX - 30;

                // Wire: source to junction
                AddLine(c, leftX, wireY, juncX, wireY, wirePen, 1.5);
                // Junction dot
                AddDot(c, juncX, wireY, 4, wirePen);
                // Wire: after series comp to load
                AddLine(c, seriesEndX, wireY, rightX, wireY, wirePen, 1.5);

                // Series component (horizontal)
                DrawComponent(c, seriesStartX, wireY, seriesEndX, wireY,
                    sol.SeriesIsInductor, sol.Comp2Short, false);

                // Shunt component (vertical, junction to ground)
                double shuntTop = wireY + 4;
                double shuntBot = gndY - 12;
                DrawComponent(c, juncX, shuntTop, juncX, shuntBot,
                    sol.ShuntIsInductor, sol.Comp1Short, true);

                // Ground
                AddLine(c, juncX, shuntBot, juncX, gndY, wirePen, 1.5);
                DrawGround(c, juncX, gndY);

                // Ground wire from source side
                AddLine(c, leftX, gndY, juncX - 12, gndY, wirePen, 0.8);
                // Ground wire to load side
                AddLine(c, juncX + 12, gndY, rightX, gndY, wirePen, 0.8);
            }
            else if (sol.Topology == MatchTopology.PiNetwork)
            {
                // Source ──┬──[series comp]──┬── Load
                //          │                 │
                //      [shunt1]          [shunt2]
                //          │                 │
                //         GND              GND

                double junc1X = leftX + (midX - leftX) * 0.45;
                double junc2X = rightX - (rightX - midX) * 0.45;
                double seriesStartX = junc1X + 4;
                double seriesEndX = junc2X - 4;

                // Wires
                AddLine(c, leftX, wireY, junc1X, wireY, wirePen, 1.5);
                AddLine(c, junc2X, wireY, rightX, wireY, wirePen, 1.5);
                // Junction dots
                AddDot(c, junc1X, wireY, 4, wirePen);
                AddDot(c, junc2X, wireY, 4, wirePen);

                // Series component (horizontal, between junctions)
                DrawComponent(c, seriesStartX, wireY, seriesEndX, wireY,
                    sol.SeriesIsInductor, sol.Comp2Short, false);

                // Shunt 1 (vertical, source side)
                double shuntTop = wireY + 4;
                double shuntBot = gndY - 12;
                DrawComponent(c, junc1X, shuntTop, junc1X, shuntBot,
                    sol.ShuntIsInductor, sol.Comp1Short, true);
                AddLine(c, junc1X, shuntBot, junc1X, gndY, wirePen, 1.5);
                DrawGround(c, junc1X, gndY);

                // Shunt 2 (vertical, load side)
                DrawComponent(c, junc2X, shuntTop, junc2X, shuntBot,
                    sol.Shunt2IsInductor, sol.Comp3Short, true);
                AddLine(c, junc2X, shuntBot, junc2X, gndY, wirePen, 1.5);
                DrawGround(c, junc2X, gndY);

                // Ground wires
                AddLine(c, leftX, gndY, junc1X - 12, gndY, wirePen, 0.8);
                AddLine(c, junc1X + 12, gndY, junc2X - 12, gndY, wirePen, 0.8);
                AddLine(c, junc2X + 12, gndY, rightX, gndY, wirePen, 0.8);
            }
            else // SeriesThenShunt
            {
                // Source ──[series comp]──┬──[wire]──── Load
                //                         │
                //                     [shunt comp]
                //                         │
                //                        GND

                double seriesStartX = leftX + 30;
                double juncX = midX + 20;
                double seriesEndX = juncX - 4;

                // Wire: source to series comp
                AddLine(c, leftX, wireY, seriesStartX, wireY, wirePen, 1.5);
                // Wire: junction to load
                AddLine(c, juncX, wireY, rightX, wireY, wirePen, 1.5);
                // Junction dot
                AddDot(c, juncX, wireY, 4, wirePen);

                // Series component (horizontal)
                DrawComponent(c, seriesStartX, wireY, seriesEndX, wireY,
                    sol.SeriesIsInductor, sol.Comp1Short, false);

                // Shunt component (vertical)
                double shuntTop = wireY + 4;
                double shuntBot = gndY - 12;
                DrawComponent(c, juncX, shuntTop, juncX, shuntBot,
                    sol.ShuntIsInductor, sol.Comp2Short, true);

                // Ground
                AddLine(c, juncX, shuntBot, juncX, gndY, wirePen, 1.5);
                DrawGround(c, juncX, gndY);

                AddLine(c, leftX, gndY, juncX - 12, gndY, wirePen, 0.8);
                AddLine(c, juncX + 12, gndY, rightX, gndY, wirePen, 0.8);
            }
        }

        private static void DrawComponent(Canvas c,
            double x1, double y1, double x2, double y2,
            bool isInductor, string label, bool isVertical)
        {
            var compBrush = new SolidColorBrush(
                isInductor ? Color.FromRgb(0x00, 0x70, 0xC0) : Color.FromRgb(0xC0, 0x40, 0x00));

            if (isVertical)
            {
                double mx = x1;
                double top = Math.Min(y1, y2);
                double bot = Math.Max(y1, y2);
                double len = bot - top;
                double compTop = top + len * 0.15;
                double compBot = bot - len * 0.15;

                // Lead wires
                AddLine(c, mx, top, mx, compTop, Brushes.DimGray, 1.2);
                AddLine(c, mx, compBot, mx, bot, Brushes.DimGray, 1.2);

                if (isInductor)
                    DrawInductorSymbol(c, mx, compTop, mx, compBot, compBrush, true);
                else
                    DrawCapacitorSymbol(c, mx, compTop, mx, compBot, compBrush, true);

                AddText(c, label, mx + 6, (compTop + compBot) / 2 - 7, 9.5, compBrush, false);
            }
            else
            {
                double my = y1;
                double left = Math.Min(x1, x2);
                double right = Math.Max(x1, x2);
                double len = right - left;
                double compL = left + len * 0.15;
                double compR = right - len * 0.15;

                AddLine(c, left, my, compL, my, Brushes.DimGray, 1.2);
                AddLine(c, compR, my, right, my, Brushes.DimGray, 1.2);

                if (isInductor)
                    DrawInductorSymbol(c, compL, my, compR, my, compBrush, false);
                else
                    DrawCapacitorSymbol(c, compL, my, compR, my, compBrush, false);

                AddText(c, label, (compL + compR) / 2 - 15, my - 18, 9.5, compBrush, false);
            }
        }

        private static void DrawInductorSymbol(Canvas c,
            double x1, double y1, double x2, double y2, Brush stroke, bool vertical)
        {
            // Draw 3-4 arcs (coil symbol)
            int nLoops = 4;
            if (vertical)
            {
                double len = Math.Abs(y2 - y1);
                double step = len / nLoops;
                double mx = x1;
                double top = Math.Min(y1, y2);
                for (int i = 0; i < nLoops; i++)
                {
                    double yc = top + step * (i + 0.5);
                    var arc = new Path
                    {
                        Stroke = stroke, StrokeThickness = 1.8,
                        Data = new PathGeometry(new[]
                        {
                            new PathFigure(new Point(mx, top + step * i), new PathSegment[]
                            {
                                new ArcSegment(new Point(mx, top + step * (i + 1)),
                                    new Size(6, step / 2), 0, false, SweepDirection.Clockwise, true)
                            }, false)
                        })
                    };
                    c.Children.Add(arc);
                }
            }
            else
            {
                double len = Math.Abs(x2 - x1);
                double step = len / nLoops;
                double my = y1;
                double left = Math.Min(x1, x2);
                for (int i = 0; i < nLoops; i++)
                {
                    var arc = new Path
                    {
                        Stroke = stroke, StrokeThickness = 1.8,
                        Data = new PathGeometry(new[]
                        {
                            new PathFigure(new Point(left + step * i, my), new PathSegment[]
                            {
                                new ArcSegment(new Point(left + step * (i + 1), my),
                                    new Size(step / 2, 6), 0, false, SweepDirection.Counterclockwise, true)
                            }, false)
                        })
                    };
                    c.Children.Add(arc);
                }
            }
        }

        private static void DrawCapacitorSymbol(Canvas c,
            double x1, double y1, double x2, double y2, Brush stroke, bool vertical)
        {
            double plateLen = 12;
            if (vertical)
            {
                double mx = x1;
                double mid = (y1 + y2) / 2;
                double gap = 4;
                // Lead to plate
                AddLine(c, mx, y1, mx, mid - gap, Brushes.DimGray, 1.2);
                AddLine(c, mx, mid + gap, mx, y2, Brushes.DimGray, 1.2);
                // Two plates
                AddLine(c, mx - plateLen / 2, mid - gap, mx + plateLen / 2, mid - gap, stroke, 2.5);
                AddLine(c, mx - plateLen / 2, mid + gap, mx + plateLen / 2, mid + gap, stroke, 2.5);
            }
            else
            {
                double my = y1;
                double mid = (x1 + x2) / 2;
                double gap = 4;
                AddLine(c, x1, my, mid - gap, my, Brushes.DimGray, 1.2);
                AddLine(c, mid + gap, my, x2, my, Brushes.DimGray, 1.2);
                AddLine(c, mid - gap, my - plateLen / 2, mid - gap, my + plateLen / 2, stroke, 2.5);
                AddLine(c, mid + gap, my - plateLen / 2, mid + gap, my + plateLen / 2, stroke, 2.5);
            }
        }

        private static void DrawGround(Canvas c, double x, double y)
        {
            var gnd = Brushes.DimGray;
            AddLine(c, x - 10, y, x + 10, y, gnd, 1.5);
            AddLine(c, x - 6, y + 4, x + 6, y + 4, gnd, 1.2);
            AddLine(c, x - 3, y + 8, x + 3, y + 8, gnd, 1.0);
        }

        private static void AddLine(Canvas c, double x1, double y1, double x2, double y2,
            Brush stroke, double thickness)
        {
            c.Children.Add(new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = stroke, StrokeThickness = thickness
            });
        }

        private static void AddDot(Canvas c, double x, double y, double size, Brush fill)
        {
            var ell = new Ellipse { Width = size, Height = size, Fill = fill };
            Canvas.SetLeft(ell, x - size / 2);
            Canvas.SetTop(ell, y - size / 2);
            c.Children.Add(ell);
        }

        private static void AddText(Canvas c, string text, double x, double y,
            double fontSize, Brush foreground, bool bold)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = fontSize, Foreground = foreground,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            c.Children.Add(tb);
        }

        /// <summary>
        /// L-network impedance matching – always tries BOTH topologies
        /// to find all valid solutions (up to 4).
        ///
        /// Topology A  (code: SeriesThenShunt):
        ///   Source ──[jXs]──┬── ZL
        ///                   │
        ///                [1/jBp]
        ///                   GND
        ///   Series element toward source, shunt element at load.
        ///   Quadratic in Bp:  Z0·|ZL|²·Bp²  − 2·Z0·XL·Bp  + (Z0 − RL) = 0
        ///   Discriminant ≥ 0 when  RL² + XL² ≥ Z0·RL.
        ///
        /// Topology B  (code: ShuntThenSeries):
        ///   Source ──┬──[jXs]── ZL
        ///            │
        ///         [1/jBp]
        ///            GND
        ///   Shunt element toward source, series element at load.
        ///   Condition: (XL+Xs)² = RL·(Z0−RL),  Bp = (XL+Xs)/(Z0·RL).
        ///   Requires Z0 ≥ RL.
        /// </summary>
        private static void ComputeLNetworkSolutions(double z0, Complex zL, double omega,
            List<MatchingSolution> solutions)
        {
            double rl = zL.Real, xl = zL.Imaginary;

            // ═══ Topology A (SeriesThenShunt) ════════════════════════════
            // Discriminant: 4·Z0·RL·(RL²+XL²−Z0·RL)
            {
                double discTerm = rl * rl + xl * xl - z0 * rl;
                if (discTerm >= 0)
                {
                    double a = z0 * (rl * rl + xl * xl);
                    double b = -2 * z0 * xl;
                    double c = z0 - rl;
                    double disc = b * b - 4 * a * c;
                    if (disc >= 0 && Math.Abs(a) > 1e-20)
                    {
                        double sqrtDisc = Math.Sqrt(disc);
                        for (int sign = -1; sign <= 1; sign += 2)
                        {
                            double bp = (-b + sign * sqrtDisc) / (2 * a);
                            if (Math.Abs(bp) < 1e-15) continue;

                            double D = 1 - bp * xl;
                            double E = bp * rl;
                            double denom = D * D + E * E;
                            if (denom < 1e-20) continue;
                            double imZp = (xl * D - rl * E) / denom;
                            double xs = -imZp;

                            // Shunt impedance = 1/(jBp) = −j/Bp  →  reactance = −1/Bp
                            double shuntReactance = -1.0 / bp;

                            AddLSolution(solutions, MatchTopology.SeriesThenShunt,
                                shuntReactance, xs, bp, omega, z0, zL);
                        }
                    }
                }
            }

            // ═══ Topology B (ShuntThenSeries) ════════════════════════════
            // (XL+Xs)² = RL·(Z0−RL),  requires Z0 ≥ RL
            {
                double discTerm = rl * (z0 - rl);
                if (discTerm >= 0)
                {
                    double sqrtTerm = Math.Sqrt(discTerm);
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        double xs = -xl + sign * sqrtTerm;
                        double xlxs = xl + xs;          // = sign * sqrtTerm
                        double bp = xlxs / (z0 * rl);   // Bp = (XL+Xs)/(Z0·RL)
                        if (Math.Abs(bp) < 1e-15) continue;

                        double shuntReactance = -1.0 / bp;

                        AddLSolution(solutions, MatchTopology.ShuntThenSeries,
                            shuntReactance, xs, bp, omega, z0, zL);
                    }
                }
            }
        }

        private static void AddLSolution(List<MatchingSolution> solutions,
            MatchTopology topology, double shuntReactance, double xs, double bp, double omega,
            double z0, Complex zL)
        {
            bool shuntIsL = shuntReactance > 0;
            bool seriesIsL = xs > 0;
            string sT = shuntIsL ? "L" : "C";
            string rT = seriesIsL ? "L" : "C";

            string shuntVal = ReactanceToComponentValue(shuntReactance, omega);
            string seriesVal = ReactanceToComponentValue(xs, omega);

            if (topology == MatchTopology.ShuntThenSeries)
            {
                        // ShuntThenSeries: shunt(source) → series(load) → ZL
                        // Path on chart: ZL → +jXs(series) → +jBp(shunt) → Z0
                        Complex zAfterSeries = new Complex(zL.Real, zL.Imaginary + xs);

                        solutions.Add(new MatchingSolution
                        {
                            Topology = topology,
                            TopologyName = $"Shunt {sT} + Series {rT}",
                            ShuntIsInductor = shuntIsL,
                            SeriesIsInductor = seriesIsL,
                            Comp1Short = shuntVal,
                            Comp2Short = seriesVal,
                            Comp1Desc = $"Shunt: {ReactanceToComponent(shuntReactance, omega)}",
                            Comp2Desc = $"Series: {ReactanceToComponent(xs, omega)}",
                            InfoText = $"Shunt-first topology\n" +
                                       $"Bp = {bp * 1e3:F4} mS, Xs = {xs:F2} Ω",
                            PathPoints = new List<Complex> { zL, zAfterSeries, new Complex(z0, 0) },
                            SegmentIsInductor = new List<bool> { seriesIsL, shuntIsL }
                        });
            }
            else
            {
                        // SeriesThenShunt: series(source) → shunt(load) → ZL
                        // Path on chart: ZL → +jBp(shunt) → +jXs(series) → Z0
                        Complex yL2 = 1.0 / zL;
                        Complex yAfterShunt = new Complex(yL2.Real, yL2.Imaginary + bp);
                        Complex zAfterShunt = 1.0 / yAfterShunt;

                        solutions.Add(new MatchingSolution
                        {
                            Topology = topology,
                            TopologyName = $"Series {rT} + Shunt {sT}",
                            ShuntIsInductor = shuntIsL,
                            SeriesIsInductor = seriesIsL,
                            Comp1Short = seriesVal,
                            Comp2Short = shuntVal,
                            Comp1Desc = $"Series: {ReactanceToComponent(xs, omega)}",
                            Comp2Desc = $"Shunt: {ReactanceToComponent(shuntReactance, omega)}",
                            InfoText = $"Series-first topology\n" +
                                       $"Xs = {xs:F2} Ω, Bp = {bp * 1e3:F4} mS",
                            PathPoints = new List<Complex> { zL, zAfterShunt, new Complex(z0, 0) },
                            SegmentIsInductor = new List<bool> { shuntIsL, seriesIsL }
                        });
            }
        }

        // ── π-Network matching ───────────────────────────────────────────
        //
        // Topology:
        //   Source (Z0) ──┬──[jXs]──┬── Load (ZL)
        //                 │          │
        //              [Shunt1]   [Shunt2]
        //                 │          │
        //                GND        GND
        //
        // Decomposed into two back-to-back L-networks sharing a virtual
        // resistance Rv, where Rv = max(Z0, RL) / (1 + Q²).

        private static void ComputePiNetworkSolutions(double z0, Complex zL, double omega,
            double qFactor, List<MatchingSolution> solutions)
        {
            double rl = zL.Real, xl = zL.Imaginary;
            if (rl <= 0) return;

            double gl = rl / (rl * rl + xl * xl);   // load conductance
            double bl = -xl / (rl * rl + xl * xl);   // load susceptance

            double rHigh = Math.Max(z0, rl);
            double rLow = Math.Min(z0, rl);
            double qMin = Math.Sqrt(Math.Max(rHigh / rLow - 1, 0));
            double q = Math.Max(qFactor, qMin + 0.1);

            double rv = rHigh / (1 + q * q);

            // Need Rv < Z0 and Rv < 1/GL (= |ZL|²/RL)
            if (rv >= z0 - 1e-12 || rv >= 1.0 / gl - 1e-12) return;

            // Load side: (BL + B2)² = GL·(1 − GL·Rv) / Rv
            double loadTerm = gl * (1.0 - gl * rv) / rv;
            if (loadTerm < 0) return;
            double sqrtLoad = Math.Sqrt(loadTerm);

            // Source side: |Im_mid| = sqrt(Rv·(Z0 − Rv))
            double srcTerm = rv * (z0 - rv);
            if (srcTerm < 0) return;
            double sqrtSrc = Math.Sqrt(srcTerm);

            // Enumerate: 2 signs for B2 × 2 signs for Im_mid = up to 4 solutions
            var seen = new HashSet<string>();
            for (int s2 = -1; s2 <= 1; s2 += 2)
            {
                double b2 = -bl + s2 * sqrtLoad;
                double bSum = bl + b2;                 // = s2 * sqrtLoad
                double dLoad = gl / rv;                // GL² + bSum² = GL/Rv

                for (int s1 = -1; s1 <= 1; s1 += 2)
                {
                    double imMid = s1 * sqrtSrc;
                    double xs = bSum / dLoad + imMid;  // = bSum·Rv/GL + imMid
                    double b1 = imMid / (rv * z0);

                    if (Math.Abs(b1) < 1e-15 || Math.Abs(b2) < 1e-15) continue;

                    double xShunt1 = -1.0 / b1;       // source-side shunt reactance
                    double xShunt2 = -1.0 / b2;       // load-side shunt reactance

                    // De-duplicate (reactance values rounded to 2 decimals)
                    string key = $"{xShunt1:F2}|{xs:F2}|{xShunt2:F2}";
                    if (!seen.Add(key)) continue;

                    bool s1IsL = xShunt1 > 0;
                    bool serIsL = xs > 0;
                    bool s2IsL = xShunt2 > 0;
                    string t1 = s1IsL ? "L" : "C";
                    string t2 = serIsL ? "L" : "C";
                    string t3 = s2IsL ? "L" : "C";

                    // π-network path: ZL → +jB2(shunt2) → +jXs(series) → +jB1(shunt1) → Z0
                    Complex yL3 = 1.0 / zL;
                    Complex yAfterS2 = new Complex(yL3.Real, yL3.Imaginary + b2);
                    Complex zAfterS2 = 1.0 / yAfterS2;
                    Complex zAfterSer = new Complex(zAfterS2.Real, zAfterS2.Imaginary + xs);

                    solutions.Add(new MatchingSolution
                    {
                        Topology = MatchTopology.PiNetwork,
                        TopologyName = $"π: Shunt {t1} – Series {t2} – Shunt {t3}",
                        ShuntIsInductor = s1IsL,
                        SeriesIsInductor = serIsL,
                        Shunt2IsInductor = s2IsL,
                        Comp1Short = ReactanceToComponentValue(xShunt1, omega),
                        Comp2Short = ReactanceToComponentValue(xs, omega),
                        Comp3Short = ReactanceToComponentValue(xShunt2, omega),
                        Comp1Desc = $"Shunt 1: {ReactanceToComponent(xShunt1, omega)}",
                        Comp2Desc = $"Series:  {ReactanceToComponent(xs, omega)}",
                        Comp3Desc = $"Shunt 2: {ReactanceToComponent(xShunt2, omega)}",
                        InfoText = $"π-network (Q = {q:F2}, Rv = {rv:F2} Ω)\n" +
                                   $"B1 = {b1 * 1e3:F4} mS, Xs = {xs:F2} Ω, B2 = {b2 * 1e3:F4} mS",
                        PathPoints = new List<Complex> { zL, zAfterS2, zAfterSer, new Complex(z0, 0) },
                        SegmentIsInductor = new List<bool> { s2IsL, serIsL, s1IsL }
                    });
                }
            }
        }

        private static string ReactanceToComponent(double reactance, double omega)
        {
            if (Math.Abs(reactance) < 1e-12)
                return "Wire (0 Ω)";
            if (reactance > 0)
            {
                double L = reactance / omega;
                return $"Inductor L = {FormatEngineering(L)}H  (X = {reactance:F2} Ω)";
            }
            else
            {
                double C = -1.0 / (omega * reactance);
                return $"Capacitor C = {FormatEngineering(C)}F  (X = {reactance:F2} Ω)";
            }
        }

        private static string ReactanceToComponentValue(double reactance, double omega)
        {
            if (Math.Abs(reactance) < 1e-12)
                return "0";
            if (reactance > 0)
            {
                double L = reactance / omega;
                return $"{FormatEngineering(L)}H";
            }
            else
            {
                double C = -1.0 / (omega * reactance);
                return $"{FormatEngineering(C)}F";
            }
        }

        private static string FormatEngineering(double val)
        {
            double abs = Math.Abs(val);
            if (abs >= 1) return $"{val:F3} ";
            if (abs >= 1e-3) return $"{val * 1e3:F3} m";
            if (abs >= 1e-6) return $"{val * 1e6:F3} µ";
            if (abs >= 1e-9) return $"{val * 1e9:F3} n";
            if (abs >= 1e-12) return $"{val * 1e12:F3} p";
            return $"{val:E3} ";
        }

        // ── Mouse interaction ────────────────────────────────────────────

        private void ChartCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (ChartCanvas == null) return;
            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            double radius = Math.Min(w, h) / 2.0 - 20;
            double cx = w / 2.0, cy = h / 2.0;

            var pos = e.GetPosition(ChartCanvas);
            double gx = (pos.X - cx) / radius;
            double gy = -(pos.Y - cy) / radius;

            Complex gamma = new Complex(gx, gy);
            if (gamma.Magnitude > 1.001)
            {
                TxtCursorZ.Text = "Z = (outside chart)";
                TxtCursorGamma.Text = $"Γ = {gx:F3} + j{gy:F3}";
                return;
            }

            if (!double.TryParse(TxtZ0.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double z0) || z0 <= 0)
                z0 = 50;

            Complex zNorm = (1 + gamma) / (1 - gamma);
            Complex z = zNorm * z0;

            TxtCursorZ.Text = $"Z = {z.Real:F2} {(z.Imaginary >= 0 ? "+" : "-")} j{Math.Abs(z.Imaginary):F2} Ω";
            TxtCursorGamma.Text = $"Γ = {gx:F3} + j{gy:F3}  (|Γ|={gamma.Magnitude:F3})";
        }

        private void ChartCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Click on chart to set load impedance
            if (ChartCanvas == null) return;
            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            double radius = Math.Min(w, h) / 2.0 - 20;
            double cx = w / 2.0, cy = h / 2.0;

            var pos = e.GetPosition(ChartCanvas);
            double gx = (pos.X - cx) / radius;
            double gy = -(pos.Y - cy) / radius;

            Complex gamma = new Complex(gx, gy);
            if (gamma.Magnitude > 1.0) return;

            if (!double.TryParse(TxtZ0.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double z0) || z0 <= 0)
                z0 = 50;

            Complex zNorm = (1 + gamma) / (1 - gamma);
            Complex z = zNorm * z0;

            _suppressUpdate = true;
            TxtR.Text = z.Real.ToString("F2", CultureInfo.InvariantCulture);
            TxtX.Text = z.Imaginary.ToString("F2", CultureInfo.InvariantCulture);
            _suppressUpdate = false;

            Redraw();
        }

        // ── Input parsing ────────────────────────────────────────────────

        private bool TryParseInputs(out double z0, out double r, out double x, out double freqGHz)
        {
            z0 = 50; r = 0; x = 0; freqGHz = 2.45;
            var ci = CultureInfo.InvariantCulture;
            if (!double.TryParse(TxtZ0?.Text, NumberStyles.Float, ci, out z0) || z0 <= 0) return false;
            if (!double.TryParse(TxtR?.Text, NumberStyles.Float, ci, out r)) return false;
            if (!double.TryParse(TxtX?.Text, NumberStyles.Float, ci, out x)) return false;
            if (!double.TryParse(TxtFreq?.Text, NumberStyles.Float, ci, out freqGHz) || freqGHz <= 0) return false;
            if (r < 0) return false;
            return true;
        }
    }

    public enum MatchTopology { ShuntThenSeries, SeriesThenShunt, PiNetwork }

    public class MatchingSolution
    {
        public MatchTopology Topology { get; set; }
        public string TopologyName { get; set; } = "";
        public bool ShuntIsInductor { get; set; }
        public bool SeriesIsInductor { get; set; }
        /// <summary>For π-network: whether second shunt element is inductor</summary>
        public bool Shunt2IsInductor { get; set; }
        /// <summary>Short label for component drawn first in schematic (e.g. "3.26 nH")</summary>
        public string Comp1Short { get; set; } = "";
        /// <summary>Short label for component drawn second</summary>
        public string Comp2Short { get; set; } = "";
        /// <summary>Short label for component drawn third (π-network)</summary>
        public string Comp3Short { get; set; } = "";
        /// <summary>Full description for component 1</summary>
        public string Comp1Desc { get; set; } = "";
        /// <summary>Full description for component 2</summary>
        public string Comp2Desc { get; set; } = "";
        /// <summary>Full description for component 3 (π-network)</summary>
        public string Comp3Desc { get; set; } = "";
        /// <summary>Extra info (topology type, raw values)</summary>
        public string InfoText { get; set; } = "";
        /// <summary>Impedance waypoints for the matching path on the Smith chart.
        /// Each entry is a complex impedance (un-normalised). The first point is ZL,
        /// the last is Z0. Intermediate points show how each element transforms Z.</summary>
        public List<Complex> PathPoints { get; set; } = new();
        /// <summary>Per-segment flag: true = inductor, false = capacitor.
        /// Matches path segments (PathPoints has N+1 entries → N segments).</summary>
        public List<bool> SegmentIsInductor { get; set; } = new();
    }
}
