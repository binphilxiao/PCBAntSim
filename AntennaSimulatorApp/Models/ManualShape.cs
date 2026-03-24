using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace AntennaSimulatorApp.Models
{
    // ── Single editable vertex ────────────────────────────────────────────────

    public class ShapeVertex : INotifyPropertyChanged
    {
        private double _x, _y;

        public double X
        {
            get => _x;
            set { _x = value; OnPropertyChanged(); OnPropertyChanged(nameof(Label)); }
        }

        public double Y
        {
            get => _y;
            set { _y = value; OnPropertyChanged(); OnPropertyChanged(nameof(Label)); }
        }

        public string Label => $"({X:F3},  {Y:F3})";

        public ShapeVertex() { }
        public ShapeVertex(double x, double y) { _x = x; _y = y; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = "")
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Manually drawn copper shape ──────────────────────────────────────────

    /// <summary>
    /// A user-defined closed copper polygon attached to one conductive layer.
    /// Coordinates are in 3D world space (same mm units as Gerber shapes).
    /// Carrier board:  X ∈ [-Width, 0],  Y ∈ [-Height/2, Height/2], Z auto-set from layer.
    /// Module board:   add PositionX / PositionY offsets accordingly.
    /// </summary>
    public class ManualShape : INotifyPropertyChanged
    {
        // ── Identity ──────────────────────────────────────────────────────────

        private string _name = "Shape";
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        // ── Board / Layer binding ─────────────────────────────────────────────

        /// <summary>True = drawn on carrier board; False = module board.</summary>
        public bool IsCarrier { get; set; } = true;

        private string _layerName = "";
        public string LayerName
        {
            get => _layerName;
            set { _layerName = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        // ── Visibility ────────────────────────────────────────────────────────

        private bool _showIn3D = true;
        /// <summary>Whether to render this shape in the 3D viewport.</summary>
        public bool ShowIn3D
        {
            get => _showIn3D;
            set { _showIn3D = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        // ── Vertex list ───────────────────────────────────────────────────────

        public ObservableCollection<ShapeVertex> Vertices { get; } = new ObservableCollection<ShapeVertex>();

        /// <summary>
        /// Additional closed polygons appended via the Merge operation.
        /// Each sub-list is an independent closed polygon on the same layer.
        /// </summary>
        public List<List<ShapeVertex>> MergedPolygons { get; } = new List<List<ShapeVertex>>();

        // ── Display ───────────────────────────────────────────────────────────

        public string BoardLabel => IsCarrier ? "Carrier" : "Module";

        private int TotalPts => Vertices.Count + MergedPolygons.Sum(p => p.Count);

        public string DisplayName =>
            $"{Name}  [{(IsCarrier ? "Carrier" : "Module")} / {LayerName}]  {TotalPts} pts" +
            (MergedPolygons.Count > 0 ? $"  +{MergedPolygons.Count} merged" : "") +
            (!ShowIn3D ? "  [hidden]" : "");

        // ── Validation ────────────────────────────────────────────────────────

        /// <summary>
        /// Validate polygon: ≥ 3 distinct vertices, closed (first == last within 0.1 µm),
        /// no self-intersections among edges.
        /// Returns true + error="OK" on success.
        /// </summary>
        public bool Validate(out string error)
        {
            if (Vertices.Count < 3)
            {
                error = "Need at least 3 vertices."; return false;
            }

            var first = Vertices[0];
            var last  = Vertices[Vertices.Count - 1];
            bool closed = Math.Abs(first.X - last.X) < 1e-4 &&
                          Math.Abs(first.Y - last.Y) < 1e-4;
            if (!closed)
            {
                error = "Polygon is not closed – first and last vertices must coincide.";
                return false;
            }

            // Build edge list (skip the duplicated closing vertex)
            int n = Vertices.Count - 1; // effective vertex count
            if (n < 3)
            {
                error = "Need at least 3 distinct vertices."; return false;
            }

            // Self-intersection test (O(n²) segment pairs)
            for (int i = 0; i < n; i++)
            {
                var a1 = (Vertices[i].X,       Vertices[i].Y);
                var a2 = (Vertices[i + 1].X,   Vertices[i + 1].Y);

                for (int j = i + 2; j < n; j++)
                {
                    // Skip edges that share a vertex
                    if (i == 0 && j == n - 1) continue;
                    var b1 = (Vertices[j].X,     Vertices[j].Y);
                    var b2 = (Vertices[j + 1].X, Vertices[j + 1].Y);

                    if (SegmentsProperlyIntersect(a1, a2, b1, b2))
                    {
                        error = $"Edge {i}→{i + 1} and edge {j}→{j + 1} intersect.";
                        return false;
                    }
                }
            }

            error = "OK – shape is valid.";
            return true;
        }

        // ── Conversion for rendering ──────────────────────────────────────────

        /// <summary>
        /// Produce a <see cref="GerberData"/> suitable for <see cref="GerberParser.BuildMeshes"/>.
        /// Includes the main polygon and all merged sub-polygons.
        /// </summary>
        public GerberData ToGerberData()
        {
            var gd = new GerberData();
            AddPolygonToGerber(gd, Vertices);
            foreach (var poly in MergedPolygons)
                AddPolygonToGerber(gd, poly);
            return gd;
        }

        private static void AddPolygonToGerber(GerberData gd, IList<ShapeVertex> verts)
        {
            if (verts.Count < 3) return;
            var shape = new GerberShape { IsClear = false };
            foreach (var v in verts)
                shape.Points.Add((v.X, v.Y));

            // Strip duplicate closing vertex if already present
            while (shape.Points.Count > 3)
            {
                var f = shape.Points[0];
                var l = shape.Points[shape.Points.Count - 1];
                if (Math.Abs(f.X - l.X) < 1e-6 && Math.Abs(f.Y - l.Y) < 1e-6)
                    shape.Points.RemoveAt(shape.Points.Count - 1);
                else break;
            }

            if (shape.Points.Count >= 3)
            {
                gd.Shapes.Add(shape);
                gd.ExpandBounds(shape.Points);
            }
        }

        // ── Geometry helpers ──────────────────────────────────────────────────

        private static double Cross(
            (double X, double Y) o, (double X, double Y) a, (double X, double Y) b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        private static bool OnSegment(
            (double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
            => Math.Min(a.X, b.X) <= p.X && p.X <= Math.Max(a.X, b.X)
            && Math.Min(a.Y, b.Y) <= p.Y && p.Y <= Math.Max(a.Y, b.Y);

        private static bool SegmentsProperlyIntersect(
            (double X, double Y) a1, (double X, double Y) a2,
            (double X, double Y) b1, (double X, double Y) b2)
        {
            double d1 = Cross(b1, b2, a1), d2 = Cross(b1, b2, a2);
            double d3 = Cross(a1, a2, b1), d4 = Cross(a1, a2, b2);

            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                return true;

            // Collinear / endpoint cases
            const double Eps = 1e-9;
            if (Math.Abs(d1) < Eps && OnSegment(a1, b1, b2)) return true;
            if (Math.Abs(d2) < Eps && OnSegment(a2, b1, b2)) return true;
            if (Math.Abs(d3) < Eps && OnSegment(b1, a1, a2)) return true;
            if (Math.Abs(d4) < Eps && OnSegment(b2, a1, a2)) return true;

            return false;
        }

        // ── INotifyPropertyChanged ────────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = "")
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
