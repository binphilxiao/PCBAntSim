using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AntennaSimulatorApp.Services
{
    /// <summary>
    /// Exports WPF 3D mesh geometry to STEP AP214 format.
    /// Each mesh becomes a MANIFOLD_SOLID_BREP built from a CLOSED_SHELL
    /// of triangular ADVANCED_FACEs, with colour styling.
    /// </summary>
    public static class StepExporter
    {
        private static int _id;
        private static StringBuilder _data = null!;

        private static int Next() => ++_id;
        private static string F(double v) => v.ToString("G15", CultureInfo.InvariantCulture);

        /// <summary>
        /// Collect meshes from one or more ModelVisual3D trees and write a STEP file.
        /// Each entry is (label, visual).
        /// </summary>
        public static void Export(string path, List<(string Label, ModelVisual3D Visual)> groups)
        {
            _id = 0;
            _data = new StringBuilder(1 << 20);

            // ── Units ──
            int luId = Next();
            _data.AppendLine($"#{luId}=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));");
            int auId = Next();
            _data.AppendLine($"#{auId}=(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.));");
            int suId = Next();
            _data.AppendLine($"#{suId}=(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT());");
            int uaId = Next();
            _data.AppendLine($"#{uaId}=UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-07),#{luId}," +
                "'distance_accuracy_value','confusion accuracy');");

            // ── Shared representation context ──
            int repCtxId = Next();
            _data.AppendLine($"#{repCtxId}=(" +
                "GEOMETRIC_REPRESENTATION_CONTEXT(3)" +
                $"GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#{uaId}))" +
                $"GLOBAL_UNIT_ASSIGNED_CONTEXT((#{luId},#{auId},#{suId}))" +
                "REPRESENTATION_CONTEXT('Context3D','3D Context with 1 mm units'));");

            // ── Application context (shared) ──
            int appCtxId = Next();
            _data.AppendLine($"#{appCtxId}=APPLICATION_CONTEXT(" +
                "'core data for automotive mechanical design processes');");
            int appProtoId = Next();
            _data.AppendLine($"#{appProtoId}=APPLICATION_PROTOCOL_DEFINITION('international standard'," +
                $"'automotive_design',2000,#{appCtxId});");

            bool anyGeometry = false;

            foreach (var (label, visual) in groups)
            {
                var meshes = new List<(MeshGeometry3D Mesh, Color Color)>();
                CollectMeshes(visual, meshes);
                if (meshes.Count == 0) continue;

                // Build BREPs for all meshes in this group
                var brepIds = new List<int>();
                foreach (var (mesh, color) in meshes)
                {
                    int brepId = WriteMeshAsBrep(mesh);
                    if (brepId > 0)
                    {
                        brepIds.Add(brepId);
                        WriteColourStyle(brepId, color);
                    }
                }
                if (brepIds.Count == 0) continue;
                anyGeometry = true;

                // SHAPE_REPRESENTATION
                int shapeRepId = Next();
                _data.AppendLine($"#{shapeRepId}=SHAPE_REPRESENTATION('{Esc(label)}'," +
                    $"({IdList(brepIds)}),#{repCtxId});");

                // Product hierarchy
                int prodCtxId = Next();
                _data.AppendLine($"#{prodCtxId}=PRODUCT_CONTEXT('',#{appCtxId},'mechanical');");

                int productId = Next();
                _data.AppendLine($"#{productId}=PRODUCT('{Esc(label)}','{Esc(label)}','',(#{prodCtxId}));");

                int pdfId = Next();
                _data.AppendLine($"#{pdfId}=PRODUCT_DEFINITION_FORMATION('',' ',#{productId});");

                int pdCtxId = Next();
                _data.AppendLine($"#{pdCtxId}=PRODUCT_DEFINITION_CONTEXT('part definition',#{appCtxId},'design');");

                int pdsId = Next();
                _data.AppendLine($"#{pdsId}=PRODUCT_DEFINITION('design','',#{pdfId},#{pdCtxId});");

                int pdsShapeId = Next();
                _data.AppendLine($"#{pdsShapeId}=PRODUCT_DEFINITION_SHAPE('','',#{pdsId});");

                Next(); // SDR
                _data.AppendLine($"#{_id}=SHAPE_DEFINITION_REPRESENTATION(#{pdsShapeId},#{shapeRepId});");
            }

            if (!anyGeometry)
                throw new InvalidOperationException("No geometry to export.");

            // ── Write complete file ──
            var sb = new StringBuilder(_data.Length + 512);
            sb.AppendLine("ISO-10303-21;");
            sb.AppendLine("HEADER;");
            sb.AppendLine("FILE_DESCRIPTION(('PCB Antenna Simulator STEP export'),'2;1');");
            sb.AppendLine($"FILE_NAME('{Esc(Path.GetFileName(path))}','{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}'," +
                "('PCB Antenna Simulator'),(''),''," +
                "'AntennaSimulatorApp','');");
            sb.AppendLine("FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));");
            sb.AppendLine("ENDSEC;");
            sb.AppendLine("DATA;");
            sb.Append(_data);
            sb.AppendLine("ENDSEC;");
            sb.AppendLine("END-ISO-10303-21;");

            File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        }

        // ── Mesh → BREP ──────────────────────────────────────────────────────

        private static int WriteMeshAsBrep(MeshGeometry3D mesh)
        {
            var positions = mesh.Positions;
            var indices = mesh.TriangleIndices;
            if (positions.Count < 3 || indices.Count < 3) return 0;

            // 1. Deduplicate vertices by position (meshes may have split vertices for normals)
            var uniquePos = new List<Point3D>();
            var posMap = new int[positions.Count]; // mesh index → unique index
            var posDict = new Dictionary<string, int>();

            for (int i = 0; i < positions.Count; i++)
            {
                var p = positions[i];
                string key = $"{F(p.X)}|{F(p.Y)}|{F(p.Z)}";
                if (!posDict.TryGetValue(key, out int idx))
                {
                    idx = uniquePos.Count;
                    uniquePos.Add(p);
                    posDict[key] = idx;
                }
                posMap[i] = idx;
            }

            // 2. Write CARTESIAN_POINT and VERTEX_POINT for unique positions
            var ptIds  = new int[uniquePos.Count];
            var vtxIds = new int[uniquePos.Count];

            for (int i = 0; i < uniquePos.Count; i++)
            {
                var p = uniquePos[i];
                ptIds[i] = Next();
                _data.AppendLine($"#{ptIds[i]}=CARTESIAN_POINT('',({F(p.X)},{F(p.Y)},{F(p.Z)}));");
            }
            for (int i = 0; i < uniquePos.Count; i++)
            {
                vtxIds[i] = Next();
                _data.AppendLine($"#{vtxIds[i]}=VERTEX_POINT('',#{ptIds[i]});");
            }

            // 3. Build shared edge map: canonical key (lo,hi) → EDGE_CURVE id
            var edgeMap = new Dictionary<(int, int), int>();

            int GetOrCreateEdge(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                if (edgeMap.TryGetValue(key, out int existing))
                    return existing;

                int lo = key.Item1, hi = key.Item2;
                var pA = uniquePos[lo];
                var pB = uniquePos[hi];
                var dir = new Vector3D(pB.X - pA.X, pB.Y - pA.Y, pB.Z - pA.Z);
                double len = dir.Length;
                if (len < 1e-12) { dir = new Vector3D(1, 0, 0); len = 1; }
                else dir.Normalize();

                int dirId = Next();
                _data.AppendLine($"#{dirId}=DIRECTION('',({F(dir.X)},{F(dir.Y)},{F(dir.Z)}));");
                int vecId = Next();
                _data.AppendLine($"#{vecId}=VECTOR('',#{dirId},{F(len)});");
                int lineId = Next();
                _data.AppendLine($"#{lineId}=LINE('',#{ptIds[lo]},#{vecId});");
                int edgeId = Next();
                _data.AppendLine($"#{edgeId}=EDGE_CURVE('',#{vtxIds[lo]},#{vtxIds[hi]},#{lineId},.T.);");

                edgeMap[key] = edgeId;
                return edgeId;
            }

            // 4. Build triangular ADVANCED_FACEs
            var faceIds = new List<int>();
            for (int t = 0; t + 2 < indices.Count; t += 3)
            {
                int mi0 = indices[t], mi1 = indices[t + 1], mi2 = indices[t + 2];
                if (mi0 >= positions.Count || mi1 >= positions.Count || mi2 >= positions.Count)
                    continue;

                // Map to deduplicated indices
                int i0 = posMap[mi0], i1 = posMap[mi1], i2 = posMap[mi2];
                if (i0 == i1 || i1 == i2 || i2 == i0) continue; // degenerate

                var p0 = uniquePos[i0]; var p1 = uniquePos[i1]; var p2 = uniquePos[i2];

                var e1 = new Vector3D(p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z);
                var e2 = new Vector3D(p2.X - p0.X, p2.Y - p0.Y, p2.Z - p0.Z);
                var n = Vector3D.CrossProduct(e1, e2);
                if (n.Length < 1e-12) continue;
                n.Normalize();
                var u = e1; u.Normalize();

                int dirN = Next();
                _data.AppendLine($"#{dirN}=DIRECTION('',({F(n.X)},{F(n.Y)},{F(n.Z)}));");
                int dirU = Next();
                _data.AppendLine($"#{dirU}=DIRECTION('',({F(u.X)},{F(u.Y)},{F(u.Z)}));");

                int axId = Next();
                _data.AppendLine($"#{axId}=AXIS2_PLACEMENT_3D('',#{ptIds[i0]},#{dirN},#{dirU});");
                int plId = Next();
                _data.AppendLine($"#{plId}=PLANE('',#{axId});");

                // Shared edges with correct orientation sense
                int ec01 = GetOrCreateEdge(i0, i1);
                string s01 = i0 < i1 ? ".T." : ".F.";
                int ec12 = GetOrCreateEdge(i1, i2);
                string s12 = i1 < i2 ? ".T." : ".F.";
                int ec20 = GetOrCreateEdge(i2, i0);
                string s20 = i2 < i0 ? ".T." : ".F.";

                int oe01 = Next(); _data.AppendLine($"#{oe01}=ORIENTED_EDGE('',*,*,#{ec01},{s01});");
                int oe12 = Next(); _data.AppendLine($"#{oe12}=ORIENTED_EDGE('',*,*,#{ec12},{s12});");
                int oe20 = Next(); _data.AppendLine($"#{oe20}=ORIENTED_EDGE('',*,*,#{ec20},{s20});");

                int loopId = Next();
                _data.AppendLine($"#{loopId}=EDGE_LOOP('',(#{oe01},#{oe12},#{oe20}));");

                int boundId = Next();
                _data.AppendLine($"#{boundId}=FACE_OUTER_BOUND('',#{loopId},.T.);");

                int faceId = Next();
                _data.AppendLine($"#{faceId}=ADVANCED_FACE('',(#{boundId}),#{plId},.T.);");

                faceIds.Add(faceId);
            }

            if (faceIds.Count == 0) return 0;

            int shellId = Next();
            _data.AppendLine($"#{shellId}=CLOSED_SHELL('',({IdList(faceIds)}));");

            int brepId = Next();
            _data.AppendLine($"#{brepId}=MANIFOLD_SOLID_BREP('',#{shellId});");

            return brepId;
        }

        // ── Colour styling ───────────────────────────────────────────────────

        private static void WriteColourStyle(int brepId, Color color)
        {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;

            int colId = Next();
            _data.AppendLine($"#{colId}=COLOUR_RGB('',{F(r)},{F(g)},{F(b)});");

            int facId = Next();
            _data.AppendLine($"#{facId}=FILL_AREA_STYLE_COLOUR('',#{colId});");

            int fasId = Next();
            _data.AppendLine($"#{fasId}=FILL_AREA_STYLE('',(#{facId}));");

            int sfaId = Next();
            _data.AppendLine($"#{sfaId}=SURFACE_STYLE_FILL_AREA(#{fasId});");

            int sssId = Next();
            _data.AppendLine($"#{sssId}=SURFACE_SIDE_STYLE('',(#{sfaId}));");

            int ssuId = Next();
            _data.AppendLine($"#{ssuId}=SURFACE_STYLE_USAGE(.BOTH.,#{sssId});");

            int psaId = Next();
            _data.AppendLine($"#{psaId}=PRESENTATION_STYLE_ASSIGNMENT((#{ssuId}));");

            Next();
            _data.AppendLine($"#{_id}=STYLED_ITEM('',(#{psaId}),#{brepId});");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Walk a ModelVisual3D tree and collect all MeshGeometry3D + colour.</summary>
        public static void CollectMeshes(ModelVisual3D root, List<(MeshGeometry3D, Color)> results)
        {
            CollectFromModel(root.Content, results);
            foreach (var child in root.Children)
            {
                if (child is ModelVisual3D mv)
                    CollectMeshes(mv, results);
            }
        }

        private static void CollectFromModel(Model3D? model, List<(MeshGeometry3D, Color)> results)
        {
            if (model is GeometryModel3D gm && gm.Geometry is MeshGeometry3D mesh)
            {
                var color = ExtractColor(gm.Material) ?? Color.FromRgb(0xB0, 0xB0, 0xB0);
                results.Add((mesh, color));
            }
            else if (model is Model3DGroup group)
            {
                foreach (var child in group.Children)
                    CollectFromModel(child, results);
            }
        }

        private static Color? ExtractColor(Material? mat)
        {
            if (mat is DiffuseMaterial dm && dm.Brush is SolidColorBrush scb)
                return scb.Color;
            if (mat is MaterialGroup mg)
            {
                foreach (var child in mg.Children)
                {
                    var c = ExtractColor(child);
                    if (c.HasValue) return c;
                }
            }
            return null;
        }

        private static string IdList(List<int> ids) =>
            string.Join(",", ids.Select(i => $"#{i}"));

        private static string Esc(string s) =>
            s.Replace("'", "''").Replace("\\", "\\\\");
    }
}
