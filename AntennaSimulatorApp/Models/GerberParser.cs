using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AntennaSimulatorApp.Models
{
    // ── Data structures ───────────────────────────────────────────────────────

    /// <summary>A filled polygon (or clear-copper hole) in Gerber mm coordinates.</summary>
    public class GerberShape
    {
        public List<(double X, double Y)> Points { get; } = new List<(double X, double Y)>();
        /// <summary>True = subtractive (clear copper). We skip clear shapes in display.</summary>
        public bool IsClear { get; set; }
    }

    /// <summary>All parsed shapes from one Gerber layer + overall bounding box.</summary>
    public class GerberData
    {
        public List<GerberShape> Shapes { get; } = new List<GerberShape>();
        public double XMin { get; private set; } = double.MaxValue;
        public double XMax { get; private set; } = double.MinValue;
        public double YMin { get; private set; } = double.MaxValue;
        public double YMax { get; private set; } = double.MinValue;
        public bool HasBounds => XMin <= XMax;

        internal void ExpandBounds(double x, double y)
        {
            if (x < XMin) XMin = x;
            if (x > XMax) XMax = x;
            if (y < YMin) YMin = y;
            if (y > YMax) YMax = y;
        }
        internal void ExpandBounds(IEnumerable<(double X, double Y)> pts)
        {
            foreach (var p in pts) ExpandBounds(p.X, p.Y);
        }
    }

    // ── Internal aperture definitions ─────────────────────────────────────────

    internal enum ApertureKind { Circle, Rectangle, Obround, Polygon }

    internal class ApertureDef
    {
        public ApertureKind Kind { get; set; }
        public double W { get; set; }    // diameter (circle) or X size
        public double H { get; set; }    // Y size (rect / obround)
        public int    PVerts { get; set; }  // polygon vertex count
        public double PRot   { get; set; }  // polygon rotation degrees

        public List<(double X, double Y)> Flash(double cx, double cy)
        {
            const int CircleN = 24;
            var pts = new List<(double X, double Y)>();
            switch (Kind)
            {
                case ApertureKind.Circle:
                    for (int i = 0; i < CircleN; i++)
                    {
                        double a = 2 * Math.PI * i / CircleN;
                        pts.Add((cx + W / 2 * Math.Cos(a), cy + W / 2 * Math.Sin(a)));
                    }
                    break;

                case ApertureKind.Rectangle:
                    pts.Add((cx - W / 2, cy - H / 2));
                    pts.Add((cx + W / 2, cy - H / 2));
                    pts.Add((cx + W / 2, cy + H / 2));
                    pts.Add((cx - W / 2, cy + H / 2));
                    break;

                case ApertureKind.Obround:
                {
                    const int CapN = 8;
                    double r  = Math.Min(W, H) / 2.0;
                    double hw = W / 2 - (W >= H ? r : 0);
                    double hh = H / 2 - (H >  W ? r : 0);
                    for (int i = 0; i <= CapN; i++)
                    {
                        double a = Math.PI / 2 + Math.PI * i / CapN;
                        pts.Add((cx + hw + r * Math.Cos(a), cy + hh + r * Math.Sin(a)));
                    }
                    for (int i = 0; i <= CapN; i++)
                    {
                        double a = -Math.PI / 2 + Math.PI * i / CapN;
                        pts.Add((cx - hw + r * Math.Cos(a), cy - hh + r * Math.Sin(a)));
                    }
                    break;
                }

                case ApertureKind.Polygon:
                {
                    int    n   = Math.Max(3, PVerts);
                    double rot = PRot * Math.PI / 180.0;
                    for (int i = 0; i < n; i++)
                    {
                        double a = 2 * Math.PI * i / n + rot;
                        pts.Add((cx + W / 2 * Math.Cos(a), cy + W / 2 * Math.Sin(a)));
                    }
                    break;
                }
            }
            return pts;
        }

        /// <summary>Rectangle stroke outline for a line segment (D01).</summary>
        public List<(double X, double Y)> Stroke(double x1, double y1, double x2, double y2)
        {
            double r  = W / 2.0;
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-10) return Flash(x1, y1);

            double nx = -dy / len * r;
            double ny =  dx / len * r;
            return new List<(double X, double Y)>
            {
                (x1 + nx, y1 + ny),
                (x2 + nx, y2 + ny),
                (x2 - nx, y2 - ny),
                (x1 - nx, y1 - ny),
            };
        }
    }

    // ── Parser ────────────────────────────────────────────────────────────────

    public static class GerberParser
    {
        public static GerberData Parse(string filePath)
        {
            var data = new GerberData();

            // State machine
            int    xDec = 6, yDec = 6;
            bool   inMM   = true;
            double curX   = 0, curY = 0;
            bool   positionSet = false;   // true once D02/D03 has placed the pen explicitly
            bool   inRegion = false;
            bool   regionStartSet = false;  // true once D02 fires inside the current region
            bool   isDark   = true;
            int    activeD  = 10;
            var    apts     = new Dictionary<int, ApertureDef>();
            var    regPts   = new List<(double X, double Y)>();
            bool   linear   = true;

            foreach (var tok in Tokenize(File.ReadAllText(filePath)))
            {
                if (tok.StartsWith("%") && tok.EndsWith("%"))
                {
                    string inner = tok.Substring(1, tok.Length - 2);
                    ParseExtended(inner, ref xDec, ref yDec, ref inMM, apts);
                }
                else
                {
                    ProcessBlock(tok, data, apts,
                                 ref curX, ref curY, ref positionSet,
                                 ref inRegion, ref regionStartSet, ref isDark,
                                 ref activeD, regPts,
                                 ref linear,
                                 xDec, yDec, inMM);
                }
            }
            return data;
        }

        // ── Tokenizer ─────────────────────────────────────────────────────────

        private static IEnumerable<string> Tokenize(string content)
        {
            // Strip whitespace
            content = Regex.Replace(content, @"\s+", "");
            var tokens = new List<string>();
            int i = 0;
            while (i < content.Length)
            {
                if (content[i] == '%')
                {
                    int end = content.IndexOf('%', i + 1);
                    if (end < 0) break;
                    tokens.Add(content.Substring(i, end - i + 1));
                    i = end + 1;
                }
                else
                {
                    int end = content.IndexOf('*', i);
                    if (end < 0) break;
                    string blk = content.Substring(i, end - i);
                    if (blk.Length > 0) tokens.Add(blk);
                    i = end + 1;
                }
            }
            return tokens;
        }

        // ── Extended parameter handler ─────────────────────────────────────────

        private static void ParseExtended(string inner,
            ref int xDec, ref int yDec, ref bool inMM,
            Dictionary<int, ApertureDef> apts)
        {
            // May contain multiple %-terminated blocks joined: split on *
            foreach (var block in inner.Split('*'))
            {
                var s = block.Trim();
                if (s.Length == 0) continue;

                // Format spec: FSLAX{xi}{xd}Y{yi}{yd}
                var mFS = Regex.Match(s, @"^FSL[AI]X(\d)(\d)Y(\d)(\d)", RegexOptions.IgnoreCase);
                if (mFS.Success)
                {
                    xDec = int.Parse(mFS.Groups[2].Value);
                    yDec = int.Parse(mFS.Groups[4].Value);
                    continue;
                }

                // Units: MOMM or MOIN
                if (s.Equals("MOMM", StringComparison.OrdinalIgnoreCase)) { inMM = true;  continue; }
                if (s.Equals("MOIN", StringComparison.OrdinalIgnoreCase)) { inMM = false; continue; }

                // Aperture definition: ADD{d}{type},{params}
                var mAD = Regex.Match(s, @"^ADD(\d+)([CROP])(?:,(.*))?$", RegexOptions.IgnoreCase);
                if (mAD.Success)
                {
                    int    num    = int.Parse(mAD.Groups[1].Value);
                    char   kind   = char.ToUpper(mAD.Groups[2].Value[0]);
                    string parms  = mAD.Groups[3].Value;
                    var    parts  = parms.Split('X');
                    double p0 = ParseDouble(parts, 0);
                    double p1 = ParseDouble(parts, 1, p0);
                    int    p2 = parts.Length > 2 ? (int)ParseDouble(parts, 2) : 4;
                    double p3 = parts.Length > 3 ? ParseDouble(parts, 3) : 0;

                    apts[num] = kind switch
                    {
                        'C' => new ApertureDef { Kind = ApertureKind.Circle,    W = p0            },
                        'R' => new ApertureDef { Kind = ApertureKind.Rectangle, W = p0, H = p1    },
                        'O' => new ApertureDef { Kind = ApertureKind.Obround,   W = p0, H = p1    },
                        'P' => new ApertureDef { Kind = ApertureKind.Polygon,   W = p0, PVerts=p2, PRot=p3 },
                        _   => new ApertureDef { Kind = ApertureKind.Circle,    W = p0            },
                    };
                    continue;
                }

                // Load Polarity
                if (s.Equals("LPD", StringComparison.OrdinalIgnoreCase)) continue; // dark (default)
                if (s.Equals("LPC", StringComparison.OrdinalIgnoreCase)) continue; // clear (we skip clear shapes)
            }
        }

        // ── Data block handler ─────────────────────────────────────────────────

        private static readonly Regex rCoord = new Regex(
            @"(?:X([-+]?\d+))?(?:Y([-+]?\d+))?(?:I([-+]?\d+))?(?:J([-+]?\d+))?(D\d+|G\d+|M\d+)?");

        private static void ProcessBlock(string block, GerberData data,
            Dictionary<int, ApertureDef> apts,
            ref double curX, ref double curY, ref bool positionSet,
            ref bool inRegion, ref bool regionStartSet, ref bool isDark,
            ref int activeD, List<(double, double)> regPts,
            ref bool linear,
            int xDec, int yDec, bool inMM)
        {
            // G codes first
            if (block.StartsWith("G"))
            {
                var gm = Regex.Match(block, @"^G(\d+)");
                if (gm.Success)
                {
                    int gn = int.Parse(gm.Groups[1].Value);
                    switch (gn)
                    {
                        case 1: linear = true;  break;
                        case 2: case 3: linear = false; break;
                        case 36: // region start
                            inRegion = true; regionStartSet = false; regPts.Clear(); break;
                        case 37: // region end
                            inRegion = false;
                            if (regPts.Count >= 3)
                                AddShape(data, new List<(double, double)>(regPts), isDark);
                            regPts.Clear();
                            break;
                        case 70: inMM = false; break; // deprecated inch
                        case 71: inMM = true;  break; // deprecated mm
                    }
                    // If block also has coordinate data, continue processing below
                    if (!Regex.IsMatch(block, @"[XY]")) return;
                }
            }

            // Aperture select Dnn (nn >= 10)
            var dapt = Regex.Match(block, @"^D(\d+)$");
            if (dapt.Success)
            {
                int dn = int.Parse(dapt.Groups[1].Value);
                if (dn >= 10) activeD = dn;
                return;
            }

            // M02 end of file
            if (block == "M02" || block == "M00" || block == "M01") return;

            // Coordinate/operation block: [X...][Y...][D01|D02|D03]
            var m = Regex.Match(block, @"(?:X([-+]?\d+))?(?:Y([-+]?\d+))?(?:I([-+]?\d+))?(?:J([-+]?\d+))?(D\d+)?");
            if (!m.Success) return;

            double newX = curX, newY = curY;
            if (m.Groups[1].Success) newX = ToMM(long.Parse(m.Groups[1].Value), xDec, inMM);
            if (m.Groups[2].Success) newY = ToMM(long.Parse(m.Groups[2].Value), yDec, inMM);

            string dCode = m.Groups[5].Value;
            // D code can also appear earlier in block (G01D02 style)
            if (string.IsNullOrEmpty(dCode))
            {
                var dm = Regex.Match(block, @"D(\d+)");
                if (dm.Success) dCode = dm.Value;
            }

            int dNum = 1;
            if (!string.IsNullOrEmpty(dCode))
                int.TryParse(dCode.Substring(1), out dNum);
            else
                dNum = 1; // default draw if coordinates present

            switch (dNum)
            {
                case 1: // draw
                    if (inRegion)
                    {
                        // Only add the implicit start point if D02 has already
                        // positioned us inside this region.  If no D02 fired yet
                        // (regionStartSet=false), use newX/newY as the first vertex
                        // so we don't inherit a stale pre-region coordinate.
                        if (regPts.Count == 0)
                        {
                            if (regionStartSet)
                                regPts.Add((curX, curY));  // D02 set a valid start
                            else
                                regPts.Add((newX, newY));  // no D02 → treat target as start
                        }
                        regPts.Add((newX, newY));
                    }
                    else if (positionSet &&   // only draw if pen was explicitly placed first
                             apts.TryGetValue(activeD, out var apt1) && apt1.W > 0)
                    {
                        var stroke = apt1.Stroke(curX, curY, newX, newY);
                        AddShape(data, stroke, isDark);
                    }
                    curX = newX; curY = newY;
                    positionSet = true;
                    break;

                case 2: // move
                    if (inRegion)
                    {
                        if (regPts.Count == 0) regPts.Add((newX, newY));
                        regionStartSet = true;  // D02 inside region sets a valid start
                    }
                    curX = newX; curY = newY;
                    positionSet = true;   // D02 explicitly positions the pen
                    break;

                case 3: // flash
                    if (apts.TryGetValue(activeD, out var apt3))
                    {
                        var poly = apt3.Flash(newX, newY);
                        AddShape(data, poly, isDark);
                    }
                    curX = newX; curY = newY;
                    positionSet = true;
                    break;
            }
        }

        private static void AddShape(GerberData data, List<(double X, double Y)> pts, bool isDark)
        {
            if (pts.Count < 3) return;
            var s = new GerberShape { IsClear = !isDark };
            s.Points.AddRange(pts);
            data.Shapes.Add(s);
            if (!isDark) return; // don't expand bounds for clear areas
            data.ExpandBounds(pts);
        }

        private static double ToMM(long raw, int dec, bool inMM)
        {
            double val = raw / Math.Pow(10, dec);
            return inMM ? val : val * 25.4;
        }

        private static double ParseDouble(string[] arr, int idx, double def = 0)
        {
            if (idx >= arr.Length || string.IsNullOrWhiteSpace(arr[idx])) return def;
            return double.TryParse(arr[idx], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;
        }

        // ── 2D Polygon Triangulation (Ear Clipping) ────────────────────────────

        /// <summary>
        /// Triangulate a simple polygon using ear-clipping.
        /// Returns flat list of vertex indices (triples).
        /// Works correctly for both convex and concave polygons.
        /// Input must be wound CCW (use SignedArea to check/fix before calling).
        /// When the main loop gets stuck, force-clips the most convex remaining
        /// vertex instead of falling back to fan triangulation (which fails on
        /// non-convex shapes).
        /// </summary>
        public static List<int> Triangulate(List<(double X, double Y)> poly)
        {
            int n = poly.Count;
            if (n < 3) return new List<int>();
            if (n == 3) return new List<int> { 0, 1, 2 };

            // ── Ear-clipping ──
            var result = new List<int>((n - 2) * 3);
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++) idx.Add(i);

            int remaining = n;
            int failSafe = remaining * remaining;
            int cur = 0;

            while (remaining > 3 && failSafe-- > 0)
            {
                int prev = (cur - 1 + remaining) % remaining;
                int next = (cur + 1) % remaining;

                int ip = idx[prev], ic = idx[cur], inx = idx[next];
                var pP = poly[ip]; var pC = poly[ic]; var pN = poly[inx];

                double cross = CrossSign(pP, pC, pN);

                // Collinear vertex → remove it, no triangle emitted
                if (Math.Abs(cross) <= 1e-10)
                {
                    idx.RemoveAt(cur);
                    remaining--;
                    if (cur >= remaining) cur = 0;
                    failSafe = remaining * remaining;
                    continue;
                }

                if (cross > 0) // convex vertex (CCW winding)
                {
                    // Ear if no other vertex lies strictly inside
                    bool isEar = true;
                    for (int k = 0; k < remaining; k++)
                    {
                        if (k == prev || k == cur || k == next) continue;
                        if (StrictlyInTriangle(poly[idx[k]], pP, pC, pN))
                        { isEar = false; break; }
                    }
                    if (isEar)
                    {
                        result.Add(ip); result.Add(ic); result.Add(inx);
                        idx.RemoveAt(cur);
                        remaining--;
                        if (cur >= remaining) cur = 0;
                        failSafe = remaining * remaining;
                        continue;
                    }
                }
                cur = (cur + 1) % remaining;
            }

            // If the main loop got stuck (failSafe expired), force-clip the
            // most-convex remaining vertex one at a time.  This avoids the old
            // fan-triangulation fallback which produces garbage for non-convex
            // polygons.
            while (remaining > 3)
            {
                int bestK = -1;
                double bestCross = double.NegativeInfinity;
                for (int k = 0; k < remaining; k++)
                {
                    int pv = (k - 1 + remaining) % remaining;
                    int nx = (k + 1) % remaining;
                    double c = CrossSign(poly[idx[pv]], poly[idx[k]], poly[idx[nx]]);
                    if (c > bestCross) { bestCross = c; bestK = k; }
                }
                if (bestK < 0) break;

                int prevK = (bestK - 1 + remaining) % remaining;
                int nextK = (bestK + 1) % remaining;
                result.Add(idx[prevK]); result.Add(idx[bestK]); result.Add(idx[nextK]);
                idx.RemoveAt(bestK);
                remaining--;
            }

            if (remaining == 3)
                result.AddRange(new[] { idx[0], idx[1], idx[2] });

            return result;
        }

        /// <summary>Returns true only if p is strictly inside triangle (a,b,c), excluding edges. Assumes CCW.</summary>
        private static bool StrictlyInTriangle((double X, double Y) p,
            (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        {
            const double eps = 1e-10;
            double d1 = CrossSign(a, b, p);
            double d2 = CrossSign(b, c, p);
            double d3 = CrossSign(c, a, p);
            return d1 > eps && d2 > eps && d3 > eps;
        }

        private static double SignedArea(List<(double X, double Y)> p)
        {
            double a = 0;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                a += (p[j].X + p[i].X) * (p[j].Y - p[i].Y);
            return a; // positive = CW, negative = CCW
        }

        /// <summary>Signed cross-product of vectors o→a and o→b.</summary>
        private static double CrossSign((double X, double Y) o, (double X, double Y) a, (double X, double Y) b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        private static bool InTriangle((double X, double Y) p,
            (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        {
            double d1 = CrossSign(a, b, p);
            double d2 = CrossSign(b, c, p);
            double d3 = CrossSign(c, a, p);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }

        // ── 3D Mesh builder ────────────────────────────────────────────────────

        /// <summary>
        /// Compute the X/Y offset (mm) needed to centre the Gerber content
        /// on the given 3D board centre point.
        /// Uses the centroid of all shape vertices (robust against outlier corners
        /// that skew the bounding box centre).
        /// </summary>
        public static (double OffsetX, double OffsetY) ComputeAutoFitOffset(
            GerberData gd, double boardCenterX3d, double boardCenterY3d)
        {
            // Vertex centroid: average of all polygon vertices across all shapes.
            // Much more robust than bbox centre when one outlier corner (e.g. board
            // origin 0,0) stretches the bbox.
            double sx = 0, sy = 0;
            long   cnt = 0;
            foreach (var shape in gd.Shapes)
            {
                if (shape.IsClear) continue;
                foreach (var p in shape.Points) { sx += p.X; sy += p.Y; cnt++; }
            }
            if (cnt == 0)
            {
                // Fallback to bbox centre if no dark shapes
                double gcx = (gd.XMin + gd.XMax) / 2.0;
                double gcy = (gd.YMin + gd.YMax) / 2.0;
                return (boardCenterX3d - gcx, boardCenterY3d - gcy);
            }
            double cx = sx / cnt;
            double cy = sy / cnt;
            return (boardCenterX3d - cx, boardCenterY3d - cy);
        }

        /// <summary>
        /// Diagnostic helper: returns counts of shapes filtered by each step in BuildMeshes.
        /// (clearCount, fewPointsCount, triangulationFailCount)
        /// </summary>
        public static (int ClearCount, int FewPtsCount, int NoTriCount) DiagnoseShapes(GerberData gd)
        {
            int clearCount = 0, fewPts = 0, noTri = 0;
            foreach (var shape in gd.Shapes)
            {
                if (shape.IsClear)        { clearCount++; continue; }
                if (shape.Points.Count < 3) { fewPts++;    continue; }
                var pts = new List<(double X, double Y)>(shape.Points);
                if (SignedArea(pts) < 0) pts.Reverse();
                var tri = Triangulate(pts);
                if (tri.Count < 3) noTri++;
            }
            return (clearCount, fewPts, noTri);
        }

        /// <summary>
        /// Build extruded WPF3D meshes for all dark shapes in <paramref name="gd"/>.
        /// Uses 1:1 mm → 3D-unit mapping: Gerber mm coordinates are placed directly
        /// in 3D space after applying <paramref name="offsetXmm"/>/<paramref name="offsetYmm"/>.
        /// Use <see cref="ComputeAutoFitOffset"/> to centre the pattern on the board.
        /// <paramref name="zTop"/> is the top-surface Z; <paramref name="thickness"/> adds
        /// bottom face + side walls (pass 0 for a flat sheet).
        /// Non-copper areas emit no geometry → fully transparent.
        /// </summary>
        public static IEnumerable<GeometryModel3D> BuildMeshes(
            GerberData gd,
            double zTop,
            double thickness = 0,
            double offsetXmm = 0, double offsetYmm = 0, double rotationDeg = 0)
        {
            if (!gd.HasBounds) yield break;

            double zBot = zTop - thickness;   // bottom face Z

            // Pre-compute rotation (around bounding-box centre)
            double rad  = rotationDeg * Math.PI / 180.0;
            double cosA = Math.Cos(rad), sinA = Math.Sin(rad);
            double cx   = (gd.XMin + gd.XMax) / 2.0;
            double cy   = (gd.YMin + gd.YMax) / 2.0;

            // Rotate around bbox centre, then shift by offset → 3D coordinate (1:1 mm)
            (double X, double Y) Transform((double X, double Y) p)
            {
                double dx = p.X - cx, dy = p.Y - cy;
                double tx = cx + dx * cosA - dy * sinA + offsetXmm;
                double ty = cy + dx * sinA + dy * cosA + offsetYmm;
                return (tx, ty);
            }

            var copperColor = Color.FromArgb(255, 0xE8, 0xA0, 0x40); // bright copper
            var diffuse  = new DiffuseMaterial(new SolidColorBrush(copperColor));
            var emissive = new EmissiveMaterial(new SolidColorBrush(
                Color.FromArgb(255, 0xB0, 0x60, 0x10)));  // warm glow so it shows regardless of light angle
            diffuse.Freeze();
            emissive.Freeze();
            var mat = new MaterialGroup();
            mat.Children.Add(diffuse);
            mat.Children.Add(emissive);
            mat.Freeze();

            foreach (var shape in gd.Shapes)
            {
                if (shape.IsClear || shape.Points.Count < 3) continue;

                // 1:1 mm → 3D: just apply rotation + offset
                var scene = shape.Points
                    .Select(p => Transform(p))
                    .ToList();

                // Remove duplicate closing vertex (last == first)
                while (scene.Count > 3)
                {
                    var first = scene[0];
                    var last  = scene[scene.Count - 1];
                    if (Math.Abs(first.X - last.X) < 1e-6 && Math.Abs(first.Y - last.Y) < 1e-6)
                        scene.RemoveAt(scene.Count - 1);
                    else break;
                }

                // Remove consecutive near-duplicate vertices (zero-length edges)
                for (int i = scene.Count - 1; i > 0; i--)
                {
                    if (Math.Abs(scene[i].X - scene[i - 1].X) < 1e-6 &&
                        Math.Abs(scene[i].Y - scene[i - 1].Y) < 1e-6)
                        scene.RemoveAt(i);
                }
                if (scene.Count < 3) continue;

                // Normalise winding to CCW BEFORE triangulation so that the
                // returned indices reference the same list we will use for
                // mesh.Positions.
                // SignedArea > 0 means CW in this formula, so reverse to CCW.
                if (SignedArea(scene) > 0) scene.Reverse();

                var triIdx = Triangulate(scene);
                if (triIdx.Count < 3) continue;

                int n = scene.Count;

                // Vertex list = polygon vertices only (ear-clipping needs no extra centroid).
                var verts = scene;
                int nv = n;

                var mesh = new MeshGeometry3D();
                var upNormal   = new Vector3D(0, 0, 1);
                var downNormal = new Vector3D(0, 0, -1);

                // ── Top face vertices (indices 0 .. nv-1) ─────────────────────
                foreach (var pt in verts)
                {
                    mesh.Positions.Add(new Point3D(pt.X, pt.Y, zTop));
                    mesh.Normals.Add(upNormal);
                }

                // Top face triangles – skip degenerate (area < 1e-6 mm²)
                for (int i = 0; i < triIdx.Count; i += 3)
                {
                    int i0 = triIdx[i], i1 = triIdx[i + 1], i2 = triIdx[i + 2];
                    var p0 = verts[i0]; var p1 = verts[i1]; var p2 = verts[i2];
                    double area = Math.Abs((p1.X - p0.X) * (p2.Y - p0.Y)
                                        - (p2.X - p0.X) * (p1.Y - p0.Y)) * 0.5;
                    if (area < 1e-6) continue;
                    mesh.TriangleIndices.Add(i0);
                    mesh.TriangleIndices.Add(i1);
                    mesh.TriangleIndices.Add(i2);
                }

                if (thickness > 1e-9)
                {
                    // ── Bottom face vertices (indices nv .. 2*nv-1) ───────────
                    foreach (var pt in verts)
                    {
                        mesh.Positions.Add(new Point3D(pt.X, pt.Y, zBot));
                        mesh.Normals.Add(downNormal);
                    }

                    // Bottom face triangles (reversed winding) – same area filter
                    for (int i = 0; i < triIdx.Count; i += 3)
                    {
                        int i0 = triIdx[i], i1 = triIdx[i + 1], i2 = triIdx[i + 2];
                        var p0 = verts[i0]; var p1 = verts[i1]; var p2 = verts[i2];
                        double area = Math.Abs((p1.X - p0.X) * (p2.Y - p0.Y)
                                             - (p2.X - p0.X) * (p1.Y - p0.Y)) * 0.5;
                        if (area < 1e-6) continue;
                        mesh.TriangleIndices.Add(nv + i2);
                        mesh.TriangleIndices.Add(nv + i1);
                        mesh.TriangleIndices.Add(nv + i0);
                    }

                    // ── Side walls: one quad per polygon edge (boundary only) ──
                    for (int i = 0; i < n; i++)
                    {
                        int j  = (i + 1) % n;
                        int ti = i, tj = j, bi = nv + i, bj = nv + j;
                        // Compute outward-facing normal for this edge
                        double ex = verts[j].X - verts[i].X;
                        double ey = verts[j].Y - verts[i].Y;
                        double el = Math.Sqrt(ex * ex + ey * ey);
                        var sideNormal = el > 1e-12
                            ? new Vector3D(ey / el, -ex / el, 0)
                            : new Vector3D(1, 0, 0);
                        // Need 4 normals for the 2 triangles (6 index refs, but
                        // the positions are already added — we duplicate them
                        // for correct per-face normals on side walls).
                        int s0 = mesh.Positions.Count;
                        mesh.Positions.Add(mesh.Positions[ti]);  mesh.Normals.Add(sideNormal);
                        mesh.Positions.Add(mesh.Positions[tj]);  mesh.Normals.Add(sideNormal);
                        mesh.Positions.Add(mesh.Positions[bj]);  mesh.Normals.Add(sideNormal);
                        mesh.Positions.Add(mesh.Positions[bi]);  mesh.Normals.Add(sideNormal);
                        mesh.TriangleIndices.Add(s0);     mesh.TriangleIndices.Add(s0 + 1); mesh.TriangleIndices.Add(s0 + 2);
                        mesh.TriangleIndices.Add(s0);     mesh.TriangleIndices.Add(s0 + 2); mesh.TriangleIndices.Add(s0 + 3);
                    }
                }

                if (mesh.TriangleIndices.Count < 3) continue;  // all triangles were degenerate
                mesh.Freeze();
                // BackMaterial ensures both triangle faces are lit/emissive,
                // which is important for layers viewed from varied camera angles.
                yield return new GeometryModel3D(mesh, mat) { BackMaterial = mat };
            }
        }
    }
}
