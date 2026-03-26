using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AntennaSimulatorApp.Views
{
    public partial class S11ResultWindow : Window
    {
        private readonly string _resultsDir;
        private double[] _freqGHz = Array.Empty<double>();
        private double[] _s11dB   = Array.Empty<double>();
        private double[] _s11Real = Array.Empty<double>();
        private double[] _s11Imag = Array.Empty<double>();

        // Derived data
        private double[] _vswr  = Array.Empty<double>();
        private double[] _zReal = Array.Empty<double>();
        private double[] _zImag = Array.Empty<double>();

        private readonly DispatcherTimer? _refreshTimer;
        private DateTime _lastCsvWrite = DateTime.MinValue;

        // Bandwidth annotation data
        private double _bwFLow, _bwFHigh;
        private bool _hasBw;
        private int _idxMin;

        private const double Z0 = 50.0;

        public S11ResultWindow(string resultsDir, int liveRefreshSeconds = 0)
        {
            InitializeComponent();
            _resultsDir = resultsDir;
            LoadCsv();
            ComputeDerived();
            ComputeMetrics();

            if (liveRefreshSeconds > 0)
            {
                TxtLiveStatus.Text = "⟳ Live";
                _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(liveRefreshSeconds) };
                _refreshTimer.Tick += (_, __) => TryRefreshFromFile();
                _refreshTimer.Start();
            }
        }

        public void StopLiveRefresh()
        {
            _refreshTimer?.Stop();
            TxtLiveStatus.Text = "";
        }

        public void Refresh()
        {
            LoadCsv();
            ComputeDerived();
            ComputeMetrics();
            DrawActiveChart();
            string pts = _freqGHz.Length > 0 ? $" ({_freqGHz.Length} pts)" : "";
            TxtLiveStatus.Text = _refreshTimer?.IsEnabled == true
                ? $"⟳ Live {DateTime.Now:HH:mm:ss}{pts}"
                : $"Updated {DateTime.Now:HH:mm:ss}{pts}";
        }

        private void TryRefreshFromFile()
        {
            string csvPath = System.IO.Path.Combine(_resultsDir, "S11.csv");
            if (!File.Exists(csvPath)) return;

            var lastWrite = File.GetLastWriteTime(csvPath);
            if (lastWrite <= _lastCsvWrite) return;
            _lastCsvWrite = lastWrite;

            Refresh();
        }

        // ── Data loading ───────────────────────────────────────────────

        private void LoadCsv()
        {
            string csvPath = System.IO.Path.Combine(_resultsDir, "S11.csv");
            if (!File.Exists(csvPath)) return;

            var freqs = new List<double>();
            var vals  = new List<double>();
            var reals = new List<double>();
            var imags = new List<double>();

            string[] lines;
            try
            {
                using var fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                lines = sr.ReadToEnd().Split('\n');
            }
            catch (IOException) { return; }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("Freq", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] parts = line.Split(',');
                if (parts.Length >= 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double f)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                {
                    if (!double.IsFinite(f) || !double.IsFinite(s)) continue;

                    freqs.Add(f);
                    vals.Add(s);

                    double re = 0, im = 0;
                    if (parts.Length >= 4
                        && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out re)
                        && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out im))
                    {
                        reals.Add(re);
                        imags.Add(im);
                    }
                    else
                    {
                        // Fallback: reconstruct magnitude from dB (phase = 0)
                        double mag = Math.Pow(10, s / 20.0);
                        reals.Add(mag);
                        imags.Add(0);
                    }
                }
            }

            _freqGHz = freqs.ToArray();
            _s11dB   = vals.ToArray();
            _s11Real = reals.ToArray();
            _s11Imag = imags.ToArray();
        }

        private void ComputeDerived()
        {
            int n = _freqGHz.Length;
            if (n == 0) return;

            _vswr  = new double[n];
            _zReal = new double[n];
            _zImag = new double[n];

            for (int i = 0; i < n; i++)
            {
                double re = _s11Real[i];
                double im = _s11Imag[i];
                double mag = Math.Sqrt(re * re + im * im);

                // VSWR = (1 + |Γ|) / (1 - |Γ|)
                if (mag >= 1.0) mag = 0.9999;
                _vswr[i] = (1 + mag) / (1 - mag);

                // Z_in = Z0 * (1 + S11) / (1 - S11)
                double numRe = 1 + re, numIm = im;
                double denRe = 1 - re, denIm = -im;
                double denMag2 = denRe * denRe + denIm * denIm;
                if (denMag2 < 1e-12) denMag2 = 1e-12;

                _zReal[i] = Z0 * (numRe * denRe + numIm * denIm) / denMag2;
                _zImag[i] = Z0 * (numIm * denRe - numRe * denIm) / denMag2;
            }
        }

        private void ComputeMetrics()
        {
            if (_freqGHz.Length == 0) return;

            _idxMin = 0;
            double minVal = _s11dB[0];
            for (int i = 1; i < _s11dB.Length; i++)
            {
                if (_s11dB[i] < minVal) { minVal = _s11dB[i]; _idxMin = i; }
            }

            TxtResonance.Text = $"{_freqGHz[_idxMin]:F4} GHz";
            TxtMinS11.Text    = $"{minVal:F2} dB";

            if (_vswr.Length > _idxMin)
                TxtVswr.Text = $"{_vswr[_idxMin]:F2}";

            double threshold = -10.0;
            _hasBw = false;
            if (minVal < threshold)
            {
                int left = _idxMin;
                while (left > 0 && _s11dB[left - 1] < threshold) left--;
                int right = _idxMin;
                while (right < _s11dB.Length - 1 && _s11dB[right + 1] < threshold) right++;

                _bwFLow = _freqGHz[left];
                if (left > 0)
                {
                    double t = (threshold - _s11dB[left - 1]) / (_s11dB[left] - _s11dB[left - 1]);
                    _bwFLow = _freqGHz[left - 1] + t * (_freqGHz[left] - _freqGHz[left - 1]);
                }

                _bwFHigh = _freqGHz[right];
                if (right < _s11dB.Length - 1)
                {
                    double t = (threshold - _s11dB[right]) / (_s11dB[right + 1] - _s11dB[right]);
                    _bwFHigh = _freqGHz[right] + t * (_freqGHz[right + 1] - _freqGHz[right]);
                }

                double bwMHz = (_bwFHigh - _bwFLow) * 1000.0;
                double center = _freqGHz[_idxMin];
                double pct = center > 0 ? bwMHz / (center * 1000) * 100 : 0;
                TxtBandwidth.Text = $"{bwMHz:F1} MHz ({_bwFLow:F3}–{_bwFHigh:F3} GHz, {pct:F1}%)";
                _hasBw = true;
            }
            else
            {
                TxtBandwidth.Text = "N/A (S11 > -10 dB)";
            }
        }

        // ── Chart tab switching ────────────────────────────────────────

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
            if (_freqGHz.Length < 2) return;
            switch (ChartTabs.SelectedIndex)
            {
                case 0: DrawS11Chart(); break;
                case 1: DrawVswrChart(); break;
                case 2: DrawImpedanceChart(); break;
                case 3: DrawSmithChart(); break;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // S11 Chart
        // ═══════════════════════════════════════════════════════════════

        private void DrawS11Chart()
        {
            S11Canvas.Children.Clear();
            if (_freqGHz.Length < 2) return;

            double cw = S11Canvas.ActualWidth, ch = S11Canvas.ActualHeight;
            if (cw < 100 || ch < 80) return;

            const double ml = 60, mr = 20, mt = 20, mb = 40;
            double pw = cw - ml - mr, ph = ch - mt - mb;

            double fMin = _freqGHz.Min(), fMax = _freqGHz.Max();
            double sMin = _s11dB.Min(), sMax = _s11dB.Max();
            double sRange = sMax - sMin;
            if (sRange < 1) sRange = 1;
            sMin -= sRange * 0.05; sMax += sRange * 0.05;
            if (sMax < 0) sMax = 2;
            double fRange = fMax - fMin;
            if (fRange < 1e-9) fRange = 1;
            sRange = sMax - sMin;

            double Xmap(double f) => ml + (f - fMin) / fRange * pw;
            double Ymap(double s) => mt + (sMax - s) / sRange * ph;

            DrawGrid(S11Canvas, ml, mt, pw, ph, fMin, fMax, fRange, sMin, sMax, sRange);
            DrawAxesBorder(S11Canvas, ml, mt, pw, ph);
            DrawAxisLabels(S11Canvas, ml, mt, pw, ph, "Frequency (GHz)", "S11 (dB)");

            // -10 dB reference line
            if (sMin < -10 && sMax > -10)
            {
                double py10 = Ymap(-10);
                S11Canvas.Children.Add(new Line
                {
                    X1 = ml, Y1 = py10, X2 = ml + pw, Y2 = py10,
                    Stroke = Brushes.Red, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 6, 3 }, Opacity = 0.6
                });
                AddLabel(S11Canvas, "-10 dB", ml + pw - 45, py10 - 14, 9, Brushes.Red, 0.7);
            }

            // Bandwidth shading
            if (_hasBw && sMin < -10)
            {
                double xL = Xmap(_bwFLow), xR = Xmap(_bwFHigh);
                var rect = new Rectangle
                {
                    Width = xR - xL, Height = ph,
                    Fill = new SolidColorBrush(Color.FromArgb(25, 0, 180, 0))
                };
                Canvas.SetLeft(rect, xL); Canvas.SetTop(rect, mt);
                S11Canvas.Children.Add(rect);

                var dash = new DoubleCollection { 4, 2 };
                S11Canvas.Children.Add(new Line { X1 = xL, Y1 = mt, X2 = xL, Y2 = mt + ph, Stroke = Brushes.Green, StrokeThickness = 1, StrokeDashArray = dash, Opacity = 0.6 });
                S11Canvas.Children.Add(new Line { X1 = xR, Y1 = mt, X2 = xR, Y2 = mt + ph, Stroke = Brushes.Green, StrokeThickness = 1, StrokeDashArray = dash, Opacity = 0.6 });

                double bwMHz = (_bwFHigh - _bwFLow) * 1000.0;
                AddLabel(S11Canvas, $"BW={bwMHz:F1}MHz", (xL + xR) / 2 - 30, mt + 4, 9, Brushes.Green, 0.9, FontWeights.SemiBold);
            }

            // S11 curve
            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(21, 101, 192)),
                StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round
            };
            for (int i = 0; i < _freqGHz.Length; i++)
                polyline.Points.Add(new Point(Xmap(_freqGHz[i]), Ymap(_s11dB[i])));
            S11Canvas.Children.Add(polyline);

            // Resonance marker
            double mx = Xmap(_freqGHz[_idxMin]), my = Ymap(_s11dB[_idxMin]);
            AddMarker(S11Canvas, mx, my, Brushes.Red);
            string txt = $"{_freqGHz[_idxMin]:F3} GHz\n{_s11dB[_idxMin]:F1} dB";
            double lblX = mx + 8, lblY = my - 24;
            if (lblX + 80 > cw) lblX = mx - 90;
            if (lblY < mt) lblY = my + 8;
            AddLabel(S11Canvas, txt, lblX, lblY, 10, Brushes.Red, 1.0, FontWeights.SemiBold);
        }

        // ═══════════════════════════════════════════════════════════════
        // VSWR Chart
        // ═══════════════════════════════════════════════════════════════

        private void DrawVswrChart()
        {
            VswrCanvas.Children.Clear();
            if (_vswr.Length < 2) return;

            double cw = VswrCanvas.ActualWidth, ch = VswrCanvas.ActualHeight;
            if (cw < 100 || ch < 80) return;

            const double ml = 60, mr = 20, mt = 20, mb = 40;
            double pw = cw - ml - mr, ph = ch - mt - mb;

            double fMin = _freqGHz.Min(), fMax = _freqGHz.Max();
            double fRange = fMax - fMin;
            if (fRange < 1e-9) fRange = 1;

            double vMin = 1.0;
            double vMax = Math.Min(_vswr.Max(), 10);
            double vRange = vMax - vMin;
            if (vRange < 0.5) vRange = 1;
            vMax = vMin + vRange * 1.1;
            vRange = vMax - vMin;

            double Xmap(double f) => ml + (f - fMin) / fRange * pw;
            double Ymap(double v) => mt + (vMax - v) / vRange * ph;

            DrawGrid(VswrCanvas, ml, mt, pw, ph, fMin, fMax, fRange, vMin, vMax, vRange);
            DrawAxesBorder(VswrCanvas, ml, mt, pw, ph);
            DrawAxisLabels(VswrCanvas, ml, mt, pw, ph, "Frequency (GHz)", "VSWR");

            // VSWR = 2 reference
            if (vMin < 2 && vMax > 2)
            {
                double py2 = Ymap(2);
                VswrCanvas.Children.Add(new Line
                {
                    X1 = ml, Y1 = py2, X2 = ml + pw, Y2 = py2,
                    Stroke = Brushes.Red, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 6, 3 }, Opacity = 0.6
                });
                AddLabel(VswrCanvas, "VSWR=2", ml + pw - 55, py2 - 14, 9, Brushes.Red, 0.7);
            }

            // Bandwidth shading
            if (_hasBw)
            {
                double xL = Xmap(_bwFLow), xR = Xmap(_bwFHigh);
                var rect = new Rectangle
                {
                    Width = xR - xL, Height = ph,
                    Fill = new SolidColorBrush(Color.FromArgb(25, 0, 180, 0))
                };
                Canvas.SetLeft(rect, xL); Canvas.SetTop(rect, mt);
                VswrCanvas.Children.Add(rect);
            }

            // VSWR curve
            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(106, 27, 154)),
                StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round
            };
            for (int i = 0; i < _freqGHz.Length; i++)
                polyline.Points.Add(new Point(Xmap(_freqGHz[i]), Ymap(Math.Min(_vswr[i], vMax))));
            VswrCanvas.Children.Add(polyline);

            // Resonance marker
            double mx = Xmap(_freqGHz[_idxMin]);
            double my = Ymap(Math.Min(_vswr[_idxMin], vMax));
            AddMarker(VswrCanvas, mx, my, Brushes.Red);
            AddLabel(VswrCanvas, $"{_freqGHz[_idxMin]:F3} GHz\nVSWR={_vswr[_idxMin]:F2}",
                     mx + 8, my - 24, 10, Brushes.Red, 1.0, FontWeights.SemiBold);
        }

        // ═══════════════════════════════════════════════════════════════
        // Input Impedance Chart
        // ═══════════════════════════════════════════════════════════════

        private void DrawImpedanceChart()
        {
            ImpedanceCanvas.Children.Clear();
            if (_zReal.Length < 2) return;

            double cw = ImpedanceCanvas.ActualWidth, ch = ImpedanceCanvas.ActualHeight;
            if (cw < 100 || ch < 80) return;

            const double ml = 60, mr = 20, mt = 20, mb = 40;
            double pw = cw - ml - mr, ph = ch - mt - mb;

            double fMin = _freqGHz.Min(), fMax = _freqGHz.Max();
            double fRange = fMax - fMin;
            if (fRange < 1e-9) fRange = 1;

            double zAllMin = Math.Min(_zReal.Min(), _zImag.Min());
            double zAllMax = Math.Max(_zReal.Max(), _zImag.Max());
            zAllMin = Math.Min(zAllMin, -10);
            zAllMax = Math.Max(zAllMax, 60);
            double zRange = zAllMax - zAllMin;
            if (zRange < 10) zRange = 10;
            zAllMin -= zRange * 0.05; zAllMax += zRange * 0.05;
            zRange = zAllMax - zAllMin;

            double Xmap(double f) => ml + (f - fMin) / fRange * pw;
            double Ymap(double z) => mt + (zAllMax - z) / zRange * ph;

            DrawGrid(ImpedanceCanvas, ml, mt, pw, ph, fMin, fMax, fRange, zAllMin, zAllMax, zRange);
            DrawAxesBorder(ImpedanceCanvas, ml, mt, pw, ph);
            DrawAxisLabels(ImpedanceCanvas, ml, mt, pw, ph, "Frequency (GHz)", "Impedance (Ω)");

            // 50 Ω reference
            if (zAllMin < 50 && zAllMax > 50)
            {
                double py50 = Ymap(50);
                ImpedanceCanvas.Children.Add(new Line
                {
                    X1 = ml, Y1 = py50, X2 = ml + pw, Y2 = py50,
                    Stroke = Brushes.Gray, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 6, 3 }, Opacity = 0.5
                });
                AddLabel(ImpedanceCanvas, "50 Ω", ml + pw - 40, py50 - 14, 9, Brushes.Gray, 0.7);
            }

            // 0 Ω reference
            if (zAllMin < 0 && zAllMax > 0)
            {
                double py0 = Ymap(0);
                ImpedanceCanvas.Children.Add(new Line
                {
                    X1 = ml, Y1 = py0, X2 = ml + pw, Y2 = py0,
                    Stroke = Brushes.Gray, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 2 }, Opacity = 0.4
                });
            }

            // R (real) curve
            var polyR = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(21, 101, 192)),
                StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round
            };
            for (int i = 0; i < _freqGHz.Length; i++)
                polyR.Points.Add(new Point(Xmap(_freqGHz[i]), Ymap(_zReal[i])));
            ImpedanceCanvas.Children.Add(polyR);

            // X (imaginary) curve
            var polyX = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(198, 40, 40)),
                StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round
            };
            for (int i = 0; i < _freqGHz.Length; i++)
                polyX.Points.Add(new Point(Xmap(_freqGHz[i]), Ymap(_zImag[i])));
            ImpedanceCanvas.Children.Add(polyX);

            // Legend
            double legX = ml + 10, legY = mt + 8;
            var blueB = new SolidColorBrush(Color.FromRgb(21, 101, 192));
            var redB  = new SolidColorBrush(Color.FromRgb(198, 40, 40));
            ImpedanceCanvas.Children.Add(new Line { X1 = legX, Y1 = legY + 5, X2 = legX + 20, Y2 = legY + 5, Stroke = blueB, StrokeThickness = 2 });
            AddLabel(ImpedanceCanvas, "R (Real)", legX + 24, legY - 2, 10, blueB);
            legY += 18;
            ImpedanceCanvas.Children.Add(new Line { X1 = legX, Y1 = legY + 5, X2 = legX + 20, Y2 = legY + 5, Stroke = redB, StrokeThickness = 2 });
            AddLabel(ImpedanceCanvas, "X (Imag)", legX + 24, legY - 2, 10, redB);

            // Resonance marker
            double mrx = Xmap(_freqGHz[_idxMin]), mry = Ymap(_zReal[_idxMin]);
            AddMarker(ImpedanceCanvas, mrx, mry, Brushes.Red);
            AddLabel(ImpedanceCanvas,
                $"{_freqGHz[_idxMin]:F3} GHz\nR={_zReal[_idxMin]:F1}Ω  X={_zImag[_idxMin]:F1}Ω",
                mrx + 8, mry - 30, 10, Brushes.Red, 1.0, FontWeights.SemiBold);
        }

        // ═══════════════════════════════════════════════════════════════
        // Smith Chart
        // ═══════════════════════════════════════════════════════════════

        private void DrawSmithChart()
        {
            SmithCanvas.Children.Clear();
            if (_s11Real.Length < 2) return;

            double cw = SmithCanvas.ActualWidth, ch = SmithCanvas.ActualHeight;
            if (cw < 100 || ch < 100) return;

            double margin = 40;
            double size = Math.Min(cw, ch) - 2 * margin;
            if (size < 50) return;
            double radius = size / 2.0;
            double cx = cw / 2, cy = ch / 2;

            double Sx(double gRe) => cx + gRe * radius;
            double Sy(double gIm) => cy - gIm * radius;

            // Unit circle
            var unitCircle = new Ellipse
            {
                Width = size, Height = size,
                Stroke = Brushes.DarkGray, StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(8, 0, 0, 255))
            };
            Canvas.SetLeft(unitCircle, cx - radius);
            Canvas.SetTop(unitCircle, cy - radius);
            SmithCanvas.Children.Add(unitCircle);

            var gridBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180));

            // Constant-R circles
            double[] rVals = { 0, 0.2, 0.5, 1.0, 2.0, 5.0 };
            foreach (double r in rVals)
            {
                double cR = r / (1 + r);
                double rR = 1.0 / (1 + r);
                DrawSmithCircle(SmithCanvas, cx, cy, radius, cR, 0, rR, gridBrush, 0.7);
            }

            // Constant-X arcs
            double[] xVals = { 0.2, 0.5, 1.0, 2.0, 5.0 };
            foreach (double x in xVals)
            {
                DrawSmithArc(SmithCanvas, cx, cy, radius, x, gridBrush, 0.5);
                DrawSmithArc(SmithCanvas, cx, cy, radius, -x, gridBrush, 0.5);
            }

            // Horizontal axis
            SmithCanvas.Children.Add(new Line
            {
                X1 = Sx(-1), Y1 = cy, X2 = Sx(1), Y2 = cy,
                Stroke = gridBrush, StrokeThickness = 0.7
            });

            // S11 data curve
            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(21, 101, 192)),
                StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round
            };
            for (int i = 0; i < _s11Real.Length; i++)
            {
                double gRe = _s11Real[i], gIm = _s11Imag[i];
                double mag = Math.Sqrt(gRe * gRe + gIm * gIm);
                if (mag > 1) { gRe /= mag; gIm /= mag; }
                polyline.Points.Add(new Point(Sx(gRe), Sy(gIm)));
            }
            SmithCanvas.Children.Add(polyline);

            // Start marker
            if (_s11Real.Length > 0)
            {
                AddMarker(SmithCanvas, Sx(_s11Real[0]), Sy(_s11Imag[0]), Brushes.Green, 6);
                AddLabel(SmithCanvas, $"Start {_freqGHz[0]:F2}G",
                    Sx(_s11Real[0]) + 6, Sy(_s11Imag[0]) - 16, 9, Brushes.Green);
            }

            // End marker
            if (_s11Real.Length > 1)
            {
                int last = _s11Real.Length - 1;
                AddMarker(SmithCanvas, Sx(_s11Real[last]), Sy(_s11Imag[last]), Brushes.Blue, 6);
                AddLabel(SmithCanvas, $"End {_freqGHz[last]:F2}G",
                    Sx(_s11Real[last]) + 6, Sy(_s11Imag[last]) + 4, 9, Brushes.Blue);
            }

            // Resonance marker
            {
                double rx = Sx(_s11Real[_idxMin]), ry = Sy(_s11Imag[_idxMin]);
                AddMarker(SmithCanvas, rx, ry, Brushes.Red, 7);
                double zr = _zReal[_idxMin], zi = _zImag[_idxMin];
                string sign = zi >= 0 ? "+" : "-";
                AddLabel(SmithCanvas,
                    $"Res: {_freqGHz[_idxMin]:F3} GHz\nZ={zr:F1}{sign}j{Math.Abs(zi):F1}Ω",
                    rx + 8, ry - 30, 10, Brushes.Red, 1.0, FontWeights.SemiBold);
            }

            // Center dot (50 Ω match)
            var dot = new Ellipse { Width = 4, Height = 4, Fill = Brushes.Black };
            Canvas.SetLeft(dot, cx - 2); Canvas.SetTop(dot, cy - 2);
            SmithCanvas.Children.Add(dot);
        }

        private void DrawSmithCircle(Canvas canvas, double cx, double cy, double unitR,
            double cGRe, double cGIm, double cR, Brush stroke, double thickness)
        {
            double d = cR * unitR * 2;
            var ellipse = new Ellipse
            {
                Width = d, Height = d,
                Stroke = stroke, StrokeThickness = thickness,
                Fill = Brushes.Transparent
            };
            double px = cx + cGRe * unitR - cR * unitR;
            double py = cy - cGIm * unitR - cR * unitR;
            ellipse.Clip = new EllipseGeometry(new Point(cx - px, cy - py), unitR, unitR);
            Canvas.SetLeft(ellipse, px); Canvas.SetTop(ellipse, py);
            canvas.Children.Add(ellipse);
        }

        private void DrawSmithArc(Canvas canvas, double cx, double cy, double unitR,
            double x, Brush stroke, double thickness)
        {
            double cGRe = 1.0, cGIm = 1.0 / x;
            double r = 1.0 / Math.Abs(x);
            double d = r * unitR * 2;
            var ellipse = new Ellipse
            {
                Width = d, Height = d,
                Stroke = stroke, StrokeThickness = thickness,
                Fill = Brushes.Transparent
            };
            double px = cx + cGRe * unitR - r * unitR;
            double py = cy - cGIm * unitR - r * unitR;
            ellipse.Clip = new EllipseGeometry(new Point(cx - px, cy - py), unitR, unitR);
            Canvas.SetLeft(ellipse, px); Canvas.SetTop(ellipse, py);
            canvas.Children.Add(ellipse);
        }

        // ═══════════════════════════════════════════════════════════════
        // Shared drawing helpers
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
                    Text = FormatTick(y), FontSize = 10, Foreground = Brushes.Gray,
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
                var lbl = new TextBlock { Text = $"{x:F2}", FontSize = 10, Foreground = Brushes.Gray };
                Canvas.SetLeft(lbl, px - 15); Canvas.SetTop(lbl, mt + ph + 4);
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

        private static string FormatTick(double v)
        {
            if (Math.Abs(v) >= 100) return $"{v:F0}";
            if (Math.Abs(v) >= 1) return $"{v:F1}";
            return $"{v:F2}";
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
    }
}
