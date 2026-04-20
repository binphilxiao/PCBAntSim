using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AntennaSimulatorApp.Models;
using AntennaSimulatorApp.Views;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;

namespace AntennaSimulatorApp.Services
{
    /// <summary>
    /// Generates a PDF simulation report after a successful FDTD run.
    /// </summary>
    public static class SimReportGenerator
    {
        public class ReportContext
        {
            public string ProjectName { get; set; } = "";
            public string ProjectPath { get; set; } = "";
            public AnalysisType AnalysisType { get; set; }
            public List<AntennaParams> Antennas { get; set; } = new();
            public SimSettings? SimSettings { get; set; }
        }

        public static string? Generate(string simDir, ReportContext ctx, TimeSpan elapsed)
        {
            QuestPDF.Settings.License = LicenseType.Community;

            string resultsDir = Path.Combine(simDir, "results");
            if (!Directory.Exists(resultsDir)) return null;

            string projectDir = Path.GetDirectoryName(simDir)!;
            string outputDir = Path.Combine(projectDir, "Output");
            Directory.CreateDirectory(outputDir);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"SimReport_{ctx.ProjectName}_{timestamp}.pdf";
            string reportPath = Path.Combine(outputDir, fileName);

            // Parse result data up-front
            var s11 = ParseS11Csv(Path.Combine(resultsDir, "S11.csv"));
            var farField = ParseFarFieldSummary(Path.Combine(resultsDir, "FarField_Summary.csv"));
            bool hasFarField = (ctx.AnalysisType == AnalysisType.FarField || ctx.AnalysisType == AnalysisType.Both)
                             && farField.Count > 0;

            int idxMin = 0;
            double resFreq = 0, minS11 = 0, vswr = 0, zReal = 0, zImag = 0;
            double bwLow = double.NaN, bwHigh = double.NaN;
            if (s11.Freq.Length > 0)
            {
                for (int i = 1; i < s11.S11dB.Length; i++)
                    if (s11.S11dB[i] < s11.S11dB[idxMin]) idxMin = i;
                resFreq = s11.Freq[idxMin];
                minS11 = s11.S11dB[idxMin];

                double gRe = s11.S11Real.Length > idxMin ? s11.S11Real[idxMin] : 0;
                double gIm = s11.S11Imag.Length > idxMin ? s11.S11Imag[idxMin] : 0;
                double gMag = Math.Sqrt(gRe * gRe + gIm * gIm);
                if (gMag >= 1) gMag = 0.999;
                vswr = (1 + gMag) / (1 - gMag);

                double dRe = 1 - gRe, dIm = -gIm;
                double dMag2 = dRe * dRe + dIm * dIm;
                double nRe = 1 + gRe, nIm = gIm;
                zReal = dMag2 > 0 ? 50.0 * (nRe * dRe + nIm * dIm) / dMag2 : 50.0;
                zImag = dMag2 > 0 ? 50.0 * (nIm * dRe - nRe * dIm) / dMag2 : 0;

                for (int i = idxMin; i >= 1; i--)
                    if (s11.S11dB[i - 1] >= -10 && s11.S11dB[i] < -10)
                    { bwLow = Lerp(s11.Freq[i - 1], s11.Freq[i], s11.S11dB[i - 1], s11.S11dB[i], -10); break; }
                for (int i = idxMin; i < s11.S11dB.Length - 1; i++)
                    if (s11.S11dB[i] < -10 && s11.S11dB[i + 1] >= -10)
                    { bwHigh = Lerp(s11.Freq[i], s11.Freq[i + 1], s11.S11dB[i], s11.S11dB[i + 1], -10); break; }
            }

            // Captured locals for lambdas
            var capturedS11 = s11;
            var capturedIdxMin = idxMin;
            var capturedFarField = farField;

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.MarginHorizontal(40);
                    page.MarginVertical(30);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header().Text("PCB Antenna Simulation Report")
                        .FontSize(20).Bold().FontColor(Colors.Blue.Darken2);

                    page.Content().Column(col =>
                    {
                        col.Spacing(8);

                        // ── Project Info ──
                        col.Item().PaddingTop(5).Table(t =>
                        {
                            t.ColumnsDefinition(c => { c.RelativeColumn(1); c.RelativeColumn(2); });
                            InfoRow(t, "Project", ctx.ProjectName);
                            InfoRow(t, "Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                            InfoRow(t, "Analysis", ctx.AnalysisType.ToString());
                            InfoRow(t, "Elapsed", elapsed.ToString(@"hh\:mm\:ss"));
                        });

                        // ── Simulation Settings ──
                        if (ctx.SimSettings != null)
                        {
                            col.Item().PaddingTop(10).Text("Simulation Settings").FontSize(14).Bold().FontColor(Colors.Blue.Darken1);
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(c => { c.RelativeColumn(1); c.RelativeColumn(2); });
                                var ss = ctx.SimSettings;
                                InfoRow(t, "Frequency Sweep", $"{ss.Sweep.StartGHz} \u2013 {ss.Sweep.StopGHz} GHz ({ss.Sweep.Type})");
                                InfoRow(t, "Max Timesteps", $"{ss.Solver.MaxTimesteps:N0}");
                                InfoRow(t, "End Criteria", $"{ss.Solver.EndCriteria:E1}");
                                InfoRow(t, "Boundary", $"{ss.Boundary.Type}" + (ss.Boundary.Type == BoundaryType.PML ? $" ({ss.Boundary.PmlLayers} layers)" : ""));
                                InfoRow(t, "Mesh Freq", $"{ss.Mesh.MeshFreqGHz} GHz, {ss.Mesh.CellsPerWavelength} cells/\u03bb");
                            });
                        }

                        // ── Antenna Parameters ──
                        if (ctx.Antennas.Count > 0)
                        {
                            col.Item().PaddingTop(10).Text("Antenna Parameters").FontSize(14).Bold().FontColor(Colors.Blue.Darken1);
                            foreach (var ant in ctx.Antennas)
                            {
                                col.Item().PaddingTop(4).Text($"{ant.Name} ({ant.Type})").FontSize(12).SemiBold();
                                col.Item().Table(t =>
                                {
                                    t.ColumnsDefinition(c => { c.RelativeColumn(1); c.RelativeColumn(2); });
                                    InfoRow(t, "Board", ant.IsCarrier ? "Carrier" : "Module");
                                    InfoRow(t, "Layer", ant.LayerName);
                                    InfoRow(t, "Target Freq", $"{ant.FreqGHz} GHz");
                                    InfoRow(t, "Offset", $"X={ant.OffsetX} mm, Y={ant.OffsetY} mm");

                                    if (ant.Type == AntennaType.InvertedF)
                                    {
                                        InfoRow(t, "Length (L)", $"{ant.LengthL} mm");
                                        InfoRow(t, "Height (H)", $"{ant.HeightH} mm");
                                        InfoRow(t, "Feed Gap", $"{ant.FeedGap} mm");
                                        InfoRow(t, "Short Pin Width", $"{ant.ShortPinWidth} mm");
                                        InfoRow(t, "Feed Pin Width", $"{ant.FeedPinWidth} mm");
                                        InfoRow(t, "Radiator Width", $"{ant.RadiatorWidth} mm");
                                    }
                                    else if (ant.Type == AntennaType.MeanderedInvertedF)
                                    {
                                        InfoRow(t, "Length (L)", $"{ant.LengthL} mm");
                                        InfoRow(t, "Height (H)", $"{ant.MifaHeightH} mm");
                                        InfoRow(t, "Feed Gap", $"{ant.FeedGap} mm");
                                        InfoRow(t, "Meander Height", $"{ant.MeanderHeight} mm");
                                        InfoRow(t, "Meander Pitch", $"{ant.MeanderPitch} mm");
                                        InfoRow(t, "Meander Count", $"{ant.MeanderCount}");
                                        InfoRow(t, "Short Width", $"{ant.MifaShortWidth} mm");
                                        InfoRow(t, "Feed Width", $"{ant.MifaFeedWidth} mm");
                                        InfoRow(t, "Horiz Width", $"{ant.MifaHorizWidth} mm");
                                        InfoRow(t, "Vert Width", $"{ant.MifaVertWidth} mm");
                                    }
                                    else if (ant.Type == AntennaType.Custom)
                                    {
                                        InfoRow(t, "Vertices", $"{ant.CustomVertices.Count} points");
                                    }
                                });

                                // Antenna schematic diagram
                                col.Item().PaddingTop(4).AlignCenter().Width(400).Image(
                                    RenderChart(400, 200, (c, cw, ch) => DrawAntennaSchematic(c, cw, ch, ant)));
                            }
                        }

                        // ── S11 Results ──
                        if (capturedS11.Freq.Length > 0)
                        {
                            col.Item().PaddingTop(10).Text("S11 / Return Loss").FontSize(14).Bold().FontColor(Colors.Blue.Darken1);
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(c => { c.RelativeColumn(1); c.RelativeColumn(2); });
                                InfoRow(t, "Resonant Frequency", $"{resFreq:F4} GHz");
                                InfoRow(t, "Min S11", $"{minS11:F2} dB");
                                InfoRow(t, "VSWR at Resonance", $"{vswr:F2}");
                                InfoRow(t, "Input Impedance", $"{zReal:F1} {(zImag >= 0 ? "+" : "\u2212")} j{Math.Abs(zImag):F1} \u03a9");
                                if (!double.IsNaN(bwLow) && !double.IsNaN(bwHigh))
                                {
                                    double bwMHz = (bwHigh - bwLow) * 1000;
                                    double center = (bwLow + bwHigh) / 2;
                                    double pctBW = center > 0 ? bwMHz / (center * 1000) * 100 : 0;
                                    InfoRow(t, "-10 dB Bandwidth", $"{bwMHz:F1} MHz ({pctBW:F1}%)");
                                    InfoRow(t, "-10 dB Range", $"{bwLow:F4} \u2013 {bwHigh:F4} GHz");
                                }
                            });

                            // S11 Chart
                            col.Item().PaddingTop(5).Image(RenderChart(520, 220, (c, cw, ch) =>
                                DrawS11Chart(c, cw, ch, capturedS11, capturedIdxMin)));

                            // Smith Chart
                            if (capturedS11.S11Real.Length > 0)
                            {
                                col.Item().PaddingTop(5).Text("Smith Chart").FontSize(12).SemiBold();
                                col.Item().AlignCenter().Width(350).Image(RenderChart(350, 350, (c, cw, ch) =>
                                    DrawSmithChart(c, cw, ch, capturedS11)));
                            }

                            // VSWR Chart
                            col.Item().PaddingTop(5).Text("VSWR").FontSize(12).SemiBold();
                            col.Item().Image(RenderChart(520, 180, (c, cw, ch) =>
                                DrawVswrChart(c, cw, ch, capturedS11)));
                        }

                        // ── Far-Field ──
                        if (hasFarField)
                        {
                            col.Item().PaddingTop(10).Text("Far-Field Results").FontSize(14).Bold().FontColor(Colors.Blue.Darken1);
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(c => { c.RelativeColumn(1); c.RelativeColumn(2); });
                                if (capturedFarField.ContainsKey("Frequency_GHz"))
                                    InfoRow(t, "Frequency", $"{capturedFarField["Frequency_GHz"]:F4} GHz");
                                if (capturedFarField.ContainsKey("Directivity_dBi"))
                                    InfoRow(t, "Directivity", $"{capturedFarField["Directivity_dBi"]:F2} dBi");
                                if (capturedFarField.ContainsKey("RadiationEfficiency"))
                                    InfoRow(t, "Radiation Efficiency", $"{capturedFarField["RadiationEfficiency"] * 100:F1}%");
                            });

                            // E-plane pattern
                            var ePlane = ParsePatternCsv(Path.Combine(resultsDir, "FarField_Eplane.csv"));
                            if (ePlane.theta.Length > 0)
                            {
                                col.Item().PaddingTop(5).Text("E-Plane Radiation Pattern").FontSize(12).SemiBold();
                                col.Item().AlignCenter().Width(250).Image(RenderChart(250, 250, (c, cw, ch) =>
                                    DrawPolarPattern(c, cw, ch, ePlane.theta, ePlane.pattern)));
                            }
                            // H-plane pattern
                            var hPlane = ParsePatternCsv(Path.Combine(resultsDir, "FarField_Hplane.csv"));
                            if (hPlane.theta.Length > 0)
                            {
                                col.Item().PaddingTop(5).Text("H-Plane Radiation Pattern").FontSize(12).SemiBold();
                                col.Item().AlignCenter().Width(250).Image(RenderChart(250, 250, (c, cw, ch) =>
                                    DrawPolarPattern(c, cw, ch, hPlane.theta, hPlane.pattern)));
                            }
                        }

                        // ── Field Distribution ──
                        var fieldImages = new[] {
                            ("Jf_surface.png", "Surface Current Distribution"),
                            ("Ef_surface.png", "E-Field Distribution"),
                            ("Hf_surface.png", "H-Field Distribution")
                        };
                        bool fieldHeaderDone = false;
                        foreach (var (file, title) in fieldImages)
                        {
                            string imgPath = Path.Combine(resultsDir, file);
                            if (!File.Exists(imgPath)) continue;
                            if (!fieldHeaderDone)
                            {
                                col.Item().PaddingTop(10).Text("Field Distribution").FontSize(14).Bold().FontColor(Colors.Blue.Darken1);
                                fieldHeaderDone = true;
                            }
                            col.Item().PaddingTop(4).Text(title).FontSize(12).SemiBold();
                            col.Item().Image(imgPath).FitWidth();
                        }
                    });

                    page.Footer().AlignCenter()
                        .Text($"Generated by PCB Antenna Simulator v{AppVersion.Current}")
                        .FontSize(8).FontColor(Colors.Grey.Medium);
                });
            }).GeneratePdf(reportPath);

            return reportPath;
        }

        // ── Chart-to-PNG helper ─────────────────────────────────────────

        private static byte[] RenderChart(int width, int height, Action<SKCanvas, float, float> draw)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            draw(surface.Canvas, width, height);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        // ── Table helpers ───────────────────────────────────────────────

        private static void InfoRow(TableDescriptor t, string label, string value)
        {
            t.Cell().PaddingVertical(2).Text(label).SemiBold().FontSize(10).FontColor(Colors.Grey.Darken2);
            t.Cell().PaddingVertical(2).Text(value).FontSize(10);
        }

        // ── SkiaSharp chart drawing ─────────────────────────────────────

        private static void DrawS11Chart(SKCanvas canvas, float w, float h, S11Data s11, int idxMin)
        {
            const float ML = 45, MR = 15, MT = 15, MB = 30;
            float pw = w - ML - MR, ph = h - MT - MB;

            double fMin = s11.Freq.Min(), fMax = s11.Freq.Max();
            double sMin = Math.Min(s11.S11dB.Min(), -40), sMax = Math.Max(s11.S11dB.Max(), 0);
            if (fMin == fMax) fMax = fMin + 1;
            if (sMin == sMax) sMax = sMin + 10;

            using var bgPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            using var gridPaint = new SKPaint { Color = new SKColor(0xDD, 0xDD, 0xDD), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
            using var borderPaint = new SKPaint { Color = new SKColor(0x99, 0x99, 0x99), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
            using var textPaint = new SKPaint { Color = new SKColor(0x33, 0x33, 0x33), IsAntialias = true };
            using var textFont = new SKFont { Size = 9 };
            using var curvePaint = new SKPaint { Color = new SKColor(0x21, 0x96, 0xF3), StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var refPaint = new SKPaint { Color = SKColors.Red.WithAlpha(128), StrokeWidth = 1, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0) };
            using var markerPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill, IsAntialias = true };
            using var markerTextPaint = new SKPaint { Color = SKColors.Red, IsAntialias = true };
            using var markerFont = new SKFont { Size = 8 };

            // Grid
            for (int i = 0; i <= 4; i++)
            {
                float y = MT + ph * i / 4f;
                double val = sMax - (sMax - sMin) * i / 4.0;
                canvas.DrawLine(ML, y, ML + pw, y, gridPaint);
                canvas.DrawText($"{val:F0}", ML - 4, y + 3, SKTextAlign.Right, textFont, textPaint);
            }
            for (int i = 0; i <= 5; i++)
            {
                float x = ML + pw * i / 5f;
                double val = fMin + (fMax - fMin) * i / 5.0;
                canvas.DrawLine(x, MT, x, MT + ph, gridPaint);
                canvas.DrawText($"{val:F2}", x, h - 5, SKTextAlign.Center, textFont, textPaint);
            }

            // -10 dB line
            float y10 = MT + ph * (float)((sMax - (-10)) / (sMax - sMin));
            if (y10 >= MT && y10 <= MT + ph)
                canvas.DrawLine(ML, y10, ML + pw, y10, refPaint);

            // S11 curve
            using var path = new SKPath();
            for (int i = 0; i < s11.Freq.Length; i++)
            {
                float x = ML + (float)((s11.Freq[i] - fMin) / (fMax - fMin)) * pw;
                float y = MT + (float)((sMax - s11.S11dB[i]) / (sMax - sMin)) * ph;
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            canvas.DrawPath(path, curvePaint);

            // Resonance marker
            {
                float mx = ML + (float)((s11.Freq[idxMin] - fMin) / (fMax - fMin)) * pw;
                float my = MT + (float)((sMax - s11.S11dB[idxMin]) / (sMax - sMin)) * ph;
                canvas.DrawCircle(mx, my, 4, markerPaint);
                canvas.DrawText($"{s11.Freq[idxMin]:F3} GHz, {s11.S11dB[idxMin]:F1} dB", mx, my - 7, SKTextAlign.Center, markerFont, markerTextPaint);
            }

            // Axis labels
            canvas.DrawText("Frequency (GHz)", ML + pw / 2, h - 15, SKTextAlign.Center, textFont, textPaint);
            canvas.Save();
            canvas.RotateDegrees(-90, 10, MT + ph / 2);
            canvas.DrawText("S11 (dB)", 10, MT + ph / 2 + 3, SKTextAlign.Center, textFont, textPaint);
            canvas.Restore();

            // Border
            canvas.DrawRect(ML, MT, pw, ph, borderPaint);
        }

        private static void DrawSmithChart(SKCanvas canvas, float w, float h, S11Data s11)
        {
            float cx = w / 2, cy = h / 2, r = Math.Min(w, h) / 2 - 20;

            using var bgPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            using var circlePaint = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC), StrokeWidth = 1, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var rCirclePaint = new SKPaint { Color = new SKColor(0xE0, 0xE0, 0xE0), StrokeWidth = 0.5f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var curvePaint = new SKPaint { Color = new SKColor(0x21, 0x96, 0xF3), StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var labelPaint = new SKPaint { Color = new SKColor(0x33, 0x33, 0x33), IsAntialias = true };
            using var labelFont = new SKFont { Size = 7 };

            // Unit circle
            canvas.DrawCircle(cx, cy, r, circlePaint);
            canvas.DrawLine(cx - r, cy, cx + r, cy, circlePaint);

            // Constant-r circles
            foreach (double rr in new[] { 0.2, 0.5, 1.0, 2.0, 5.0 })
            {
                float cr = r / (float)(1 + rr);
                float ccx = cx + r * (float)(rr / (1 + rr));
                canvas.DrawCircle(ccx, cy, cr, rCirclePaint);
            }

            int n = Math.Min(s11.S11Real.Length, s11.S11Imag.Length);
            if (n > 0)
            {
                using var path = new SKPath();
                for (int i = 0; i < n; i++)
                {
                    float px = cx + (float)s11.S11Real[i] * r;
                    float py = cy - (float)s11.S11Imag[i] * r;
                    if (i == 0) path.MoveTo(px, py); else path.LineTo(px, py);
                }
                canvas.DrawPath(path, curvePaint);

                using var startPaint = new SKPaint { Color = SKColors.Green, Style = SKPaintStyle.Fill, IsAntialias = true };
                using var endPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill, IsAntialias = true };
                canvas.DrawCircle(cx + (float)s11.S11Real[0] * r, cy - (float)s11.S11Imag[0] * r, 3, startPaint);
                canvas.DrawCircle(cx + (float)s11.S11Real[n - 1] * r, cy - (float)s11.S11Imag[n - 1] * r, 3, endPaint);

                // Impedance labels at key frequency points (start, resonance, end)
                var labelIndices = new HashSet<int> { 0, n - 1 };
                // Find resonance (min S11)
                if (s11.S11dB.Length == n)
                {
                    int minIdx = 0;
                    for (int i = 1; i < n; i++)
                        if (s11.S11dB[i] < s11.S11dB[minIdx]) minIdx = i;
                    labelIndices.Add(minIdx);
                }

                using var markerPaint = new SKPaint { Color = new SKColor(0x88, 0x30, 0xCC), Style = SKPaintStyle.Fill, IsAntialias = true };
                foreach (int idx in labelIndices)
                {
                    if (idx < 0 || idx >= n) continue;
                    double gRe = s11.S11Real[idx], gIm = s11.S11Imag[idx];
                    float ptx = cx + (float)gRe * r;
                    float pty = cy - (float)gIm * r;
                    canvas.DrawCircle(ptx, pty, 2.5f, markerPaint);

                    // Compute impedance Z = Z0 * (1+Gamma)/(1-Gamma)
                    double dRe = 1 - gRe, dIm = -gIm;
                    double dMag2 = dRe * dRe + dIm * dIm;
                    double zR = dMag2 > 0 ? 50.0 * ((1 + gRe) * dRe + gIm * dIm) / dMag2 : 50.0;
                    double zI = dMag2 > 0 ? 50.0 * (gIm * dRe - (1 + gRe) * dIm) / dMag2 : 0;
                    double freq = idx < s11.Freq.Length ? s11.Freq[idx] : 0;

                    string zStr = $"{freq:F2}G: {zR:F0}{(zI >= 0 ? "+" : "\u2212")}j{Math.Abs(zI):F0}\u03a9";
                    // Offset label to avoid overlapping the curve
                    float lx = ptx + (ptx > cx ? 4 : -4);
                    float ly = pty + (pty > cy ? 10 : -4);
                    var align = ptx > cx ? SKTextAlign.Left : SKTextAlign.Right;
                    canvas.DrawText(zStr, lx, ly, align, labelFont, labelPaint);
                }
            }
        }

        private static void DrawVswrChart(SKCanvas canvas, float w, float h, S11Data s11)
        {
            const float ML = 45, MR = 15, MT = 15, MB = 30;
            float pw = w - ML - MR, ph = h - MT - MB;

            int n = s11.Freq.Length;
            var vswrArr = new double[n];
            for (int i = 0; i < n; i++)
            {
                double re = i < s11.S11Real.Length ? s11.S11Real[i] : 0;
                double im = i < s11.S11Imag.Length ? s11.S11Imag[i] : 0;
                double mag = Math.Sqrt(re * re + im * im);
                if (mag >= 1) mag = 0.999;
                vswrArr[i] = (1 + mag) / (1 - mag);
            }

            double fMin = s11.Freq.Min(), fMax = s11.Freq.Max();
            double vMax = Math.Min(vswrArr.Max(), 10);
            if (fMin == fMax) fMax = fMin + 1;
            if (vMax <= 1) vMax = 5;

            using var bgPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            using var gridPaint = new SKPaint { Color = new SKColor(0xDD, 0xDD, 0xDD), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
            using var borderPaint = new SKPaint { Color = new SKColor(0x99, 0x99, 0x99), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
            using var textPaint = new SKPaint { Color = new SKColor(0x33, 0x33, 0x33), IsAntialias = true };
            using var textFont = new SKFont { Size = 9 };
            using var curvePaint = new SKPaint { Color = new SKColor(0xFF, 0x98, 0x00), StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var refPaint = new SKPaint { Color = SKColors.Orange.WithAlpha(128), StrokeWidth = 1, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0) };

            for (int i = 0; i <= 4; i++)
            {
                float y = MT + ph * i / 4f;
                double val = vMax - (vMax - 1) * i / 4.0;
                canvas.DrawLine(ML, y, ML + pw, y, gridPaint);
                canvas.DrawText($"{val:F1}", ML - 4, y + 3, SKTextAlign.Right, textFont, textPaint);
            }
            for (int i = 0; i <= 5; i++)
            {
                float x = ML + pw * i / 5f;
                double val = fMin + (fMax - fMin) * i / 5.0;
                canvas.DrawLine(x, MT, x, MT + ph, gridPaint);
                canvas.DrawText($"{val:F2}", x, h - 5, SKTextAlign.Center, textFont, textPaint);
            }

            // VSWR=2 line
            float y2 = MT + ph * (float)((vMax - 2) / (vMax - 1));
            if (y2 >= MT && y2 <= MT + ph)
                canvas.DrawLine(ML, y2, ML + pw, y2, refPaint);

            using var path = new SKPath();
            for (int i = 0; i < n; i++)
            {
                float x = ML + (float)((s11.Freq[i] - fMin) / (fMax - fMin)) * pw;
                double v = Math.Min(vswrArr[i], vMax);
                float y = MT + (float)((vMax - v) / (vMax - 1)) * ph;
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            canvas.DrawPath(path, curvePaint);

            canvas.DrawText("Frequency (GHz)", ML + pw / 2, h - 15, SKTextAlign.Center, textFont, textPaint);
            canvas.Save();
            canvas.RotateDegrees(-90, 10, MT + ph / 2);
            canvas.DrawText("VSWR", 10, MT + ph / 2 + 3, SKTextAlign.Center, textFont, textPaint);
            canvas.Restore();

            canvas.DrawRect(ML, MT, pw, ph, borderPaint);
        }

        private static void DrawPolarPattern(SKCanvas canvas, float w, float h, double[] theta, double[] pattern)
        {
            float cx = w / 2, cy = h / 2, r = Math.Min(w, h) / 2 - 15;
            double pMin = pattern.Min(), pMax = pattern.Max();
            double range = pMax - pMin;
            if (range < 1) range = 1;

            using var bgPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            using var gridPaint = new SKPaint { Color = new SKColor(0xE0, 0xE0, 0xE0), StrokeWidth = 1, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var axisPaint = new SKPaint { Color = new SKColor(0xDD, 0xDD, 0xDD), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
            using var curvePaint = new SKPaint { Color = new SKColor(0x4C, 0xAF, 0x50), StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };

            for (int i = 1; i <= 3; i++)
                canvas.DrawCircle(cx, cy, r * i / 3f, gridPaint);
            canvas.DrawLine(cx - r, cy, cx + r, cy, axisPaint);
            canvas.DrawLine(cx, cy - r, cx, cy + r, axisPaint);

            using var path = new SKPath();
            for (int i = 0; i < theta.Length; i++)
            {
                double norm = (pattern[i] - pMin) / range;
                double rad = theta[i] * Math.PI / 180.0;
                float px = cx + (float)(norm * r * Math.Sin(rad));
                float py = cy - (float)(norm * r * Math.Cos(rad));
                if (i == 0) path.MoveTo(px, py); else path.LineTo(px, py);
            }
            canvas.DrawPath(path, curvePaint);
        }

        // ── Antenna Schematic Drawing ───────────────────────────────────

        private static void DrawAntennaSchematic(SKCanvas canvas, float w, float h, AntennaParams ant)
        {
            using var bgPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            canvas.DrawRect(0, 0, w, h, bgPaint);

            if (ant.Type == AntennaType.InvertedF)
                DrawIFASchematic(canvas, w, h, ant);
            else if (ant.Type == AntennaType.MeanderedInvertedF)
                DrawMIFASchematic(canvas, w, h, ant);
            else if (ant.Type == AntennaType.Custom)
                DrawCustomSchematic(canvas, w, h, ant);
        }

        private static void DrawIFASchematic(SKCanvas canvas, float w, float h, AntennaParams ant)
        {
            double L = ant.LengthL, H = ant.HeightH, S = ant.FeedGap;
            double wSh = ant.ShortPinWidth, wFe = ant.FeedPinWidth, wRa = ant.RadiatorWidth;

            float margin = 40;
            float pw = w - 2 * margin, ph = h - 2 * margin;
            float sc = Math.Min(pw / (float)Math.Max(L, 1), ph / (float)Math.Max(H, 1));
            float drawW = (float)L * sc, drawH = (float)H * sc;
            float ox = (w - drawW) / 2, oy = (h + drawH) / 2 + 5; // GND y position

            float px(double mm) => ox + (float)mm * sc;
            float py(double mm) => oy - (float)mm * sc;

            using var gndPaint = new SKPaint { Color = new SKColor(0x80, 0x80, 0x80), StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var bluePaint = new SKPaint { Color = new SKColor(0x20, 0x60, 0xCC), StrokeWidth = Math.Max(2, (float)wSh * sc * 0.5f), Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var blueFill = new SKPaint { Color = new SKColor(0x20, 0x60, 0xCC, 0x40), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var redPaint = new SKPaint { Color = new SKColor(0xCC, 0x20, 0x20), StrokeWidth = Math.Max(2, (float)wFe * sc * 0.5f), Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var dimPaint = new SKPaint { Color = new SKColor(0x20, 0xAA, 0x40), IsAntialias = true };
            using var dimLinePaint = new SKPaint { Color = new SKColor(0x20, 0xAA, 0x40), StrokeWidth = 0.8f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var dimFont = new SKFont { Size = 8 };
            using var labelPaint = new SKPaint { Color = new SKColor(0x33, 0x33, 0x33), IsAntialias = true };
            using var labelFont = new SKFont { Size = 8 };

            // GND line
            canvas.DrawLine(ox - 8, oy, px(L) + 8, oy, gndPaint);
            canvas.DrawText("GND", ox - 8, oy + 11, SKTextAlign.Left, labelFont, labelPaint);

            // Shorting stub (vertical from GND to top)
            if (ant.HasGroundStub)
            {
                canvas.DrawLine(px(0), oy, px(0), py(H), bluePaint);
                canvas.DrawText("Short", px(0) - 2, py(H) - 4, SKTextAlign.Center, labelFont, labelPaint);

                // Match section (horizontal at top from short to feed)
                canvas.DrawLine(px(0), py(H), px(S), py(H), bluePaint);
            }

            // Feed stub (vertical from GND to top)
            canvas.DrawLine(px(S), oy, px(S), py(H), redPaint);
            canvas.DrawText("Feed", px(S) + 3, py(H / 2), SKTextAlign.Left, labelFont, new SKPaint { Color = new SKColor(0xCC, 0x20, 0x20), IsAntialias = true });

            // Radiating arm (horizontal from feed to end)
            float radW = Math.Max(2.5f, (float)wRa * sc * 0.5f);
            using var radPaint = new SKPaint { Color = new SKColor(0x20, 0x60, 0xCC), StrokeWidth = radW, Style = SKPaintStyle.Stroke, IsAntialias = true };
            canvas.DrawLine(px(S), py(H), px(L), py(H), radPaint);
            canvas.DrawText("Radiator", px((S + L) / 2), py(H) - 5, SKTextAlign.Center, labelFont, labelPaint);

            // Dimension: L
            float dimY = oy + 14;
            canvas.DrawLine(px(0), dimY, px(L), dimY, dimLinePaint);
            canvas.DrawText($"L={L:F1}", px(L / 2), dimY + 10, SKTextAlign.Center, dimFont, dimPaint);

            // Dimension: S
            canvas.DrawLine(px(0), dimY - 5, px(S), dimY - 5, dimLinePaint);
            canvas.DrawText($"S={S:F1}", px(S / 2), dimY - 7, SKTextAlign.Center, dimFont, dimPaint);

            // Dimension: H
            float dimX = px(L) + 10;
            canvas.DrawLine(dimX, oy, dimX, py(H), dimLinePaint);
            canvas.DrawText($"H={H:F1}", dimX + 3, py(H / 2) + 3, SKTextAlign.Left, dimFont, dimPaint);
        }

        private static void DrawMIFASchematic(SKCanvas canvas, float w, float h, AntennaParams ant)
        {
            double L = ant.LengthL, H = ant.MifaHeightH, Hm = ant.MeanderHeight;
            double pitch = ant.MeanderPitch, feedS = ant.FeedGap;
            if (Hm >= H) Hm = H * 0.8;
            int nFull = (pitch + Hm) > 0 ? Math.Max(1, (int)Math.Floor((L - feedS) / (pitch + Hm))) : 1;
            double remaining = L - feedS - nFull * (pitch + Hm);
            bool hasPartial = remaining >= pitch;
            int nTurns = hasPartial ? nFull + 1 : nFull;
            double tailLen = hasPartial ? 0 : Math.Max(remaining, 0);
            double partialHm = hasPartial ? Math.Max(remaining - pitch, 0) : 0;
            double totalW = feedS + nTurns * pitch + tailLen;

            float margin = 40;
            float pw = w - 2 * margin, ph = h - 2 * margin;
            float sc = Math.Min(pw / (float)Math.Max(totalW, 1), ph / (float)Math.Max(H, 1));
            float drawW = (float)totalW * sc, drawH = (float)H * sc;
            float ox = (w - drawW) / 2, oy = (h + drawH) / 2 + 5;

            float px(double mm) => ox + (float)mm * sc;
            float py(double mm) => oy - (float)mm * sc;

            using var gndPaint = new SKPaint { Color = new SKColor(0x80, 0x80, 0x80), StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var bluePaint = new SKPaint { Color = new SKColor(0x20, 0x60, 0xCC), StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var purpPaint = new SKPaint { Color = new SKColor(0x88, 0x30, 0xCC), StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var redPaint = new SKPaint { Color = new SKColor(0xCC, 0x20, 0x20), StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var oranPaint = new SKPaint { Color = new SKColor(0xDD, 0x88, 0x00), StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var dimPaint = new SKPaint { Color = new SKColor(0x20, 0xAA, 0x40), IsAntialias = true };
            using var dimLinePaint = new SKPaint { Color = new SKColor(0x20, 0xAA, 0x40), StrokeWidth = 0.8f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var dimFont = new SKFont { Size = 8 };
            using var labelPaint = new SKPaint { Color = new SKColor(0x33, 0x33, 0x33), IsAntialias = true };
            using var labelFont = new SKFont { Size = 8 };

            // GND line
            canvas.DrawLine(ox - 8, oy, px(totalW) + 8, oy, gndPaint);
            canvas.DrawText("GND", ox - 8, oy + 11, SKTextAlign.Left, labelFont, labelPaint);

            // Shorting stub
            if (ant.HasGroundStub)
            {
                canvas.DrawLine(px(0), oy, px(0), py(H), bluePaint);
                canvas.DrawText("Short", px(0) - 2, py(H) - 4, SKTextAlign.Center, labelFont, labelPaint);

                // Match section at top
                canvas.DrawLine(px(0), py(H), px(feedS), py(H), oranPaint);
            }

            // Feed stub
            canvas.DrawLine(px(feedS), oy, px(feedS), py(H), redPaint);
            canvas.DrawText("Feed", px(feedS) + 3, py(H / 2), SKTextAlign.Left, labelFont,
                new SKPaint { Color = new SKColor(0xCC, 0x20, 0x20), IsAntialias = true });

            // Meander traces
            double mx = feedS, myMm = H;
            for (int i = 0; i < nTurns; i++)
            {
                double hI = (hasPartial && i == nTurns - 1) ? partialHm : Hm;
                double yBot = H - hI;
                double nyMm = (i % 2 == 0) ? yBot : H;
                double nx = mx + pitch;

                // Horizontal trace
                canvas.DrawLine(px(mx), py(myMm), px(nx), py(myMm), bluePaint);
                // Vertical connection
                if (Math.Abs(myMm - nyMm) > 0.01)
                    canvas.DrawLine(px(nx), py(myMm), px(nx), py(nyMm), purpPaint);

                mx = nx;
                myMm = nyMm;
            }
            // Tail
            if (tailLen > 0)
                canvas.DrawLine(px(mx), py(myMm), px(mx + tailLen), py(myMm), bluePaint);

            // Dimensions
            float dimY = oy + 14;
            canvas.DrawLine(px(0), dimY, px(totalW), dimY, dimLinePaint);
            canvas.DrawText($"Total={totalW:F1}", px(totalW / 2), dimY + 10, SKTextAlign.Center, dimFont, dimPaint);

            float dimX = px(totalW) + 10;
            canvas.DrawLine(dimX, oy, dimX, py(H), dimLinePaint);
            canvas.DrawText($"H={H:F1}", dimX + 3, py(H / 2) + 3, SKTextAlign.Left, dimFont, dimPaint);

            // h1 dimension
            float dimX2 = dimX + 30;
            canvas.DrawLine(dimX2, py(H), dimX2, py(H - Hm), dimLinePaint);
            canvas.DrawText($"h1={Hm:F1}", dimX2 + 3, py(H - Hm / 2) + 3, SKTextAlign.Left, dimFont, dimPaint);

            // S dimension
            canvas.DrawLine(px(0), dimY - 5, px(feedS), dimY - 5, dimLinePaint);
            canvas.DrawText($"S={feedS:F1}", px(feedS / 2), dimY - 7, SKTextAlign.Center, dimFont, dimPaint);

            // N label
            canvas.DrawText($"N={nTurns}", px(totalW / 2), py(H) - 5, SKTextAlign.Center, labelFont, labelPaint);
        }

        private static void DrawCustomSchematic(SKCanvas canvas, float w, float h, AntennaParams ant)
        {
            if (ant.CustomVertices.Count < 2) return;

            double minX = ant.CustomVertices.Min(v => v.X);
            double minY = ant.CustomVertices.Min(v => v.Y);
            double maxX = ant.CustomVertices.Max(v => v.X);
            double maxY = ant.CustomVertices.Max(v => v.Y);
            double polyW = maxX - minX, polyH = maxY - minY;
            if (polyW < 1e-6) polyW = 1;
            if (polyH < 1e-6) polyH = 1;

            float margin = 30;
            float pw = w - 2 * margin, ph = h - 2 * margin;
            float sc = Math.Min(pw / (float)polyW, ph / (float)polyH);
            float drawW = (float)polyW * sc, drawH = (float)polyH * sc;
            float ox = (w - drawW) / 2 - (float)minX * sc;
            float oy = (h + drawH) / 2 + (float)minY * sc;

            float px(double mm) => ox + (float)mm * sc;
            float py(double mm) => oy - (float)mm * sc;

            using var gndPaint = new SKPaint { Color = new SKColor(0x80, 0x80, 0x80), StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var polyPaint = new SKPaint { Color = new SKColor(0x20, 0x60, 0xCC), StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var fillPaint = new SKPaint { Color = new SKColor(0x20, 0x60, 0xCC, 0x40), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var dotPaint = new SKPaint { Color = new SKColor(0x20, 0x60, 0xCC), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var dimPaint = new SKPaint { Color = new SKColor(0x20, 0xAA, 0x40), IsAntialias = true };
            using var dimLinePaint = new SKPaint { Color = new SKColor(0x20, 0xAA, 0x40), StrokeWidth = 0.8f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var dimFont = new SKFont { Size = 8 };

            // GND line at y=0
            canvas.DrawLine(px(minX - 1), py(0), px(maxX + 1), py(0), gndPaint);

            // Filled polygon
            using var polyPath = new SKPath();
            for (int i = 0; i < ant.CustomVertices.Count; i++)
            {
                var v = ant.CustomVertices[i];
                if (i == 0) polyPath.MoveTo(px(v.X), py(v.Y));
                else polyPath.LineTo(px(v.X), py(v.Y));
            }
            polyPath.Close();
            canvas.DrawPath(polyPath, fillPaint);
            canvas.DrawPath(polyPath, polyPaint);

            // Vertex dots
            foreach (var v in ant.CustomVertices)
                canvas.DrawCircle(px(v.X), py(v.Y), 2.5f, dotPaint);

            // Dimensions
            float dimY = py(minY) + 12;
            canvas.DrawLine(px(minX), dimY, px(maxX), dimY, dimLinePaint);
            canvas.DrawText($"W={polyW:F1}", px((minX + maxX) / 2), dimY + 10, SKTextAlign.Center, dimFont, dimPaint);

            float dimX = px(maxX) + 8;
            canvas.DrawLine(dimX, py(minY), dimX, py(maxY), dimLinePaint);
            canvas.DrawText($"H={polyH:F1}", dimX + 3, py((minY + maxY) / 2) + 3, SKTextAlign.Left, dimFont, dimPaint);
        }

        // ── CSV Parsing ─────────────────────────────────────────────────

        private class S11Data
        {
            public double[] Freq = Array.Empty<double>();
            public double[] S11dB = Array.Empty<double>();
            public double[] S11Real = Array.Empty<double>();
            public double[] S11Imag = Array.Empty<double>();
        }

        private static S11Data ParseS11Csv(string path)
        {
            if (!File.Exists(path)) return new S11Data();
            var freqs = new List<double>();
            var vals = new List<double>();
            var reals = new List<double>();
            var imags = new List<double>();

            string[] lines;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                lines = sr.ReadToEnd().Split('\n');
            }
            catch { return new S11Data(); }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("Freq", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = line.Split(',');
                if (parts.Length >= 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double f)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                {
                    freqs.Add(f);
                    vals.Add(s);
                    if (parts.Length >= 4
                        && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double re)
                        && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double im))
                    { reals.Add(re); imags.Add(im); }
                }
            }
            return new S11Data { Freq = freqs.ToArray(), S11dB = vals.ToArray(), S11Real = reals.ToArray(), S11Imag = imags.ToArray() };
        }

        private static Dictionary<string, double> ParseFarFieldSummary(string path)
        {
            var dict = new Dictionary<string, double>();
            if (!File.Exists(path)) return dict;
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string[] parts = raw.Trim().Split(',');
                    if (parts.Length >= 2
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                        dict[parts[0].Trim()] = val;
                }
            }
            catch { }
            return dict;
        }

        private static (double[] theta, double[] pattern) ParsePatternCsv(string path)
        {
            if (!File.Exists(path)) return (Array.Empty<double>(), Array.Empty<double>());
            var thetas = new List<double>();
            var patterns = new List<double>();
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("Theta", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] parts = line.Split(',');
                    if (parts.Length >= 2
                        && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                    { thetas.Add(t); patterns.Add(p); }
                }
            }
            catch { }
            return (thetas.ToArray(), patterns.ToArray());
        }

        private static double Lerp(double x0, double x1, double y0, double y1, double yTarget)
        {
            if (Math.Abs(y1 - y0) < 1e-12) return (x0 + x1) / 2;
            return x0 + (yTarget - y0) * (x1 - x0) / (y1 - y0);
        }
    }
}
