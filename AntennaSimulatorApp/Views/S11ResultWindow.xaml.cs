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

        private readonly DispatcherTimer? _refreshTimer;
        private DateTime _lastCsvWrite = DateTime.MinValue;

        /// <param name="resultsDir">Path to results/ folder</param>
        /// <param name="liveRefreshSeconds">If > 0, auto-reload CSV every N seconds</param>
        public S11ResultWindow(string resultsDir, int liveRefreshSeconds = 0)
        {
            InitializeComponent();
            _resultsDir = resultsDir;
            LoadCsv();
            ComputeMetrics();

            if (liveRefreshSeconds > 0)
            {
                TxtLiveStatus.Text = "⟳ Live";
                _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(liveRefreshSeconds) };
                _refreshTimer.Tick += (_, __) => TryRefreshFromFile();
                _refreshTimer.Start();
            }
        }

        /// <summary>Stop live-updating and mark as final.</summary>
        public void StopLiveRefresh()
        {
            _refreshTimer?.Stop();
            TxtLiveStatus.Text = "";
        }

        /// <summary>Reload data from CSV and redraw. Called by external code after post-processing.</summary>
        public void Refresh()
        {
            LoadCsv();
            ComputeMetrics();
            DrawChart();
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
            if (lastWrite <= _lastCsvWrite) return; // no change
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
                    if (double.IsFinite(f) && double.IsFinite(s))
                    {
                        freqs.Add(f);
                        vals.Add(s);
                    }
                }
            }

            _freqGHz = freqs.ToArray();
            _s11dB   = vals.ToArray();
        }

        private void ComputeMetrics()
        {
            if (_freqGHz.Length == 0) return;

            int idxMin = 0;
            double minVal = _s11dB[0];
            for (int i = 1; i < _s11dB.Length; i++)
            {
                if (_s11dB[i] < minVal) { minVal = _s11dB[i]; idxMin = i; }
            }

            TxtResonance.Text = $"{_freqGHz[idxMin]:F4} GHz";
            TxtMinS11.Text    = $"{minVal:F2} dB";

            // -10 dB bandwidth
            double threshold = -10.0;
            if (minVal < threshold)
            {
                // Find left edge
                int left = idxMin;
                while (left > 0 && _s11dB[left - 1] < threshold) left--;
                // Find right edge
                int right = idxMin;
                while (right < _s11dB.Length - 1 && _s11dB[right + 1] < threshold) right++;

                double fLow  = _freqGHz[left];
                double fHigh = _freqGHz[right];
                double bwMHz = (fHigh - fLow) * 1000.0;
                TxtBandwidth.Text = $"{bwMHz:F1} MHz ({fLow:F3}–{fHigh:F3} GHz)";
            }
            else
            {
                TxtBandwidth.Text = "N/A (S11 > -10 dB)";
            }
        }

        // ── Chart drawing ──────────────────────────────────────────────

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawChart();
        }

        private void DrawChart()
        {
            ChartCanvas.Children.Clear();
            if (_freqGHz.Length < 2) return;

            double cw = ChartCanvas.ActualWidth;
            double ch = ChartCanvas.ActualHeight;
            if (cw < 100 || ch < 80) return;

            // Margins for axis labels
            const double ml = 60, mr = 20, mt = 20, mb = 40;
            double pw = cw - ml - mr;   // plot area width
            double ph = ch - mt - mb;   // plot area height

            // Data ranges
            double fMin = _freqGHz.Min();
            double fMax = _freqGHz.Max();
            double sMin = _s11dB.Min();
            double sMax = _s11dB.Max();

            // Add padding to Y
            double sRange = sMax - sMin;
            if (sRange < 1) sRange = 1;
            sMin -= sRange * 0.05;
            sMax += sRange * 0.05;

            // Clamp Y so 0 dB is visible
            if (sMax < 0) sMax = 2;

            double fRange = fMax - fMin;
            if (fRange < 1e-9) fRange = 1;
            sRange = sMax - sMin;

            // Mapping functions
            double Xmap(double f) => ml + (f - fMin) / fRange * pw;
            double Ymap(double s) => mt + (sMax - s) / sRange * ph;

            // ── Background grid ──
            var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(230, 230, 230)), 1);

            // Y grid & labels
            double yStep = NiceStep(sRange, 6);
            double yStart = Math.Ceiling(sMin / yStep) * yStep;
            for (double y = yStart; y <= sMax; y += yStep)
            {
                double py = Ymap(y);
                var line = new Line { X1 = ml, Y1 = py, X2 = ml + pw, Y2 = py, Stroke = gridPen.Brush, StrokeThickness = 1 };
                ChartCanvas.Children.Add(line);
                var lbl = new TextBlock
                {
                    Text = $"{y:F0}",
                    FontSize = 10,
                    Foreground = Brushes.Gray,
                    TextAlignment = TextAlignment.Right,
                    Width = 45
                };
                Canvas.SetLeft(lbl, ml - 50);
                Canvas.SetTop(lbl, py - 7);
                ChartCanvas.Children.Add(lbl);
            }

            // X grid & labels
            double xStep = NiceStep(fRange, 8);
            double xStart = Math.Ceiling(fMin / xStep) * xStep;
            for (double x = xStart; x <= fMax; x += xStep)
            {
                double px = Xmap(x);
                var line = new Line { X1 = px, Y1 = mt, X2 = px, Y2 = mt + ph, Stroke = gridPen.Brush, StrokeThickness = 1 };
                ChartCanvas.Children.Add(line);
                var lbl = new TextBlock
                {
                    Text = $"{x:F2}",
                    FontSize = 10,
                    Foreground = Brushes.Gray
                };
                Canvas.SetLeft(lbl, px - 15);
                Canvas.SetTop(lbl, mt + ph + 4);
                ChartCanvas.Children.Add(lbl);
            }

            // ── Axes border ──
            var axisPen = Brushes.DarkGray;
            ChartCanvas.Children.Add(new Line { X1 = ml, Y1 = mt, X2 = ml, Y2 = mt + ph, Stroke = axisPen, StrokeThickness = 1 });
            ChartCanvas.Children.Add(new Line { X1 = ml, Y1 = mt + ph, X2 = ml + pw, Y2 = mt + ph, Stroke = axisPen, StrokeThickness = 1 });

            // ── -10 dB reference line ──
            if (sMin < -10 && sMax > -10)
            {
                double py10 = Ymap(-10);
                var refLine = new Line
                {
                    X1 = ml, Y1 = py10, X2 = ml + pw, Y2 = py10,
                    Stroke = Brushes.Red, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 6, 3 },
                    Opacity = 0.6
                };
                ChartCanvas.Children.Add(refLine);
                var refLbl = new TextBlock { Text = "-10 dB", FontSize = 9, Foreground = Brushes.Red, Opacity = 0.7 };
                Canvas.SetLeft(refLbl, ml + pw - 45);
                Canvas.SetTop(refLbl, py10 - 14);
                ChartCanvas.Children.Add(refLbl);
            }

            // ── S11 curve ──
            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(21, 101, 192)),
                StrokeThickness = 1.8,
                StrokeLineJoin = PenLineJoin.Round
            };

            for (int i = 0; i < _freqGHz.Length; i++)
            {
                polyline.Points.Add(new Point(Xmap(_freqGHz[i]), Ymap(_s11dB[i])));
            }
            ChartCanvas.Children.Add(polyline);

            // ── Resonance marker ──
            int idxMin = 0;
            for (int i = 1; i < _s11dB.Length; i++)
                if (_s11dB[i] < _s11dB[idxMin]) idxMin = i;

            double mx = Xmap(_freqGHz[idxMin]);
            double my = Ymap(_s11dB[idxMin]);
            var marker = new Ellipse { Width = 8, Height = 8, Fill = Brushes.Red };
            Canvas.SetLeft(marker, mx - 4);
            Canvas.SetTop(marker, my - 4);
            ChartCanvas.Children.Add(marker);

            var markerLbl = new TextBlock
            {
                Text = $"{_freqGHz[idxMin]:F3} GHz\n{_s11dB[idxMin]:F1} dB",
                FontSize = 10,
                Foreground = Brushes.Red,
                FontWeight = FontWeights.SemiBold
            };
            // Place label avoiding edge clipping
            double lblX = mx + 8;
            double lblY = my - 24;
            if (lblX + 80 > cw) lblX = mx - 90;
            if (lblY < mt) lblY = my + 8;
            Canvas.SetLeft(markerLbl, lblX);
            Canvas.SetTop(markerLbl, lblY);
            ChartCanvas.Children.Add(markerLbl);

            // ── Axis labels ──
            var xAxisLabel = new TextBlock
            {
                Text = "Frequency (GHz)",
                FontSize = 11,
                Foreground = Brushes.DimGray
            };
            Canvas.SetLeft(xAxisLabel, ml + pw / 2 - 45);
            Canvas.SetTop(xAxisLabel, mt + ph + 22);
            ChartCanvas.Children.Add(xAxisLabel);

            var yAxisLabel = new TextBlock
            {
                Text = "S11 (dB)",
                FontSize = 11,
                Foreground = Brushes.DimGray,
                RenderTransform = new RotateTransform(-90),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            Canvas.SetLeft(yAxisLabel, 2);
            Canvas.SetTop(yAxisLabel, mt + ph / 2 + 15);
            ChartCanvas.Children.Add(yAxisLabel);
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
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _resultsDir,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
