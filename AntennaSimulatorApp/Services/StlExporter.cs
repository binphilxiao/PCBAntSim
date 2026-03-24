using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media.Media3D;

namespace AntennaSimulatorApp.Services
{
    /// <summary>
    /// Exports WPF MeshGeometry3D objects to binary STL format.
    /// </summary>
    public static class StlExporter
    {
        /// <summary>
        /// Write one or more meshes as a single binary STL file.
        /// </summary>
        public static void Export(string path, IReadOnlyList<MeshGeometry3D> meshes)
        {
            int totalTriangles = 0;
            foreach (var mesh in meshes)
                totalTriangles += mesh.TriangleIndices.Count / 3;

            if (totalTriangles == 0) return;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            // 80-byte header
            var header = new byte[80];
            var title = Encoding.ASCII.GetBytes("AntennaSimulatorApp STL");
            Array.Copy(title, header, Math.Min(title.Length, 80));
            bw.Write(header);

            // Triangle count
            bw.Write((uint)totalTriangles);

            foreach (var mesh in meshes)
            {
                var positions = mesh.Positions;
                var indices = mesh.TriangleIndices;

                for (int t = 0; t + 2 < indices.Count; t += 3)
                {
                    int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
                    if (i0 >= positions.Count || i1 >= positions.Count || i2 >= positions.Count)
                        continue;

                    var p0 = positions[i0];
                    var p1 = positions[i1];
                    var p2 = positions[i2];

                    var e1 = new Vector3D(p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z);
                    var e2 = new Vector3D(p2.X - p0.X, p2.Y - p0.Y, p2.Z - p0.Z);
                    var n = Vector3D.CrossProduct(e1, e2);
                    double len = n.Length;
                    if (len > 1e-12) { n.X /= len; n.Y /= len; n.Z /= len; }

                    bw.Write((float)n.X); bw.Write((float)n.Y); bw.Write((float)n.Z);
                    bw.Write((float)p0.X); bw.Write((float)p0.Y); bw.Write((float)p0.Z);
                    bw.Write((float)p1.X); bw.Write((float)p1.Y); bw.Write((float)p1.Z);
                    bw.Write((float)p2.X); bw.Write((float)p2.Y); bw.Write((float)p2.Z);
                    bw.Write((ushort)0);
                }
            }
        }

        /// <summary>Single mesh convenience overload.</summary>
        public static void Export(string path, MeshGeometry3D mesh)
            => Export(path, new[] { mesh });
    }
}
