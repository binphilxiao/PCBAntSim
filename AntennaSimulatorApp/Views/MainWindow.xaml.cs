using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Navigation;
using System.Windows.Shapes;
using HelixToolkit.Wpf;
using AntennaSimulatorApp.Models;
using AntennaSimulatorApp.ViewModels;
using AntennaSimulatorApp.Services;

namespace AntennaSimulatorApp.Views;

public partial class MainWindow : Window
{
    // -- Routed commands (referenced by Window.InputBindings in XAML) ----------

    public static readonly RoutedUICommand OpenCommand     = new RoutedUICommand("Open",     "Open",     typeof(MainWindow));
    public static readonly RoutedUICommand SaveCommand     = new RoutedUICommand("Save",     "Save",     typeof(MainWindow));
    public static readonly RoutedUICommand SaveAsCommand   = new RoutedUICommand("Save As",  "SaveAs",   typeof(MainWindow));
    public static readonly RoutedUICommand SimulateCommand = new RoutedUICommand("Simulate", "Simulate", typeof(MainWindow));

    /// <summary>Path to the currently loaded / last-saved .antproj file (null = untitled).</summary>
    private string? _currentProjectPath;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"PCB Antenna Simulator  v{AppVersion.Current}";

        // Wire keyboard shortcut handlers
        CommandBindings.Add(new CommandBinding(OpenCommand,     (_, e) => MenuOpen_Click(this, e)));
        CommandBindings.Add(new CommandBinding(SaveCommand,     (_, e) => MenuSave_Click(this, e)));
        CommandBindings.Add(new CommandBinding(SaveAsCommand,   (_, e) => MenuSaveAs_Click(this, e)));
        CommandBindings.Add(new CommandBinding(SimulateCommand, (_, e) => MenuSimulate_Click(this, e)));
    }

    // -- Camera helpers ----------------------------------------------------

    /// <summary>
    /// Set a perspective camera by specifying which world axis faces the viewer.
    /// The camera is placed at <paramref name="pos"/> looking toward the origin.
    /// </summary>
    private void SetView(Point3D pos, Vector3D up)
    {
        var look = new Vector3D(-pos.X, -pos.Y, -pos.Z);
        look.Normalize();
        var cam = new PerspectiveCamera
        {
            Position      = pos,
            LookDirection = look,
            UpDirection   = up,
            FieldOfView   = 45
        };
        Viewport3D.Camera = cam;
        Viewport3D.ZoomExtents(0);   // fit geometry instantly
    }

    private void Viewport3D_Loaded(object sender, RoutedEventArgs e)
    {
        // Default isometric-ish view: +X ? right screen, +Z ? up screen
        var cam = new PerspectiveCamera
        {
            Position      = new Point3D(120, -180, 140),
            LookDirection = new Vector3D(-120, 180, -140),
            UpDirection   = new Vector3D(0, 0, 1),
            FieldOfView   = 45
        };
        Viewport3D.Camera = cam;
        Viewport3D.DefaultCamera = cam;

        // Build initial layer visuals
        RebuildLayerVisuals();
        Viewport3D.ZoomExtents(0);

        // Subscribe to stackup / dimension changes so visuals stay in sync
        if (DataContext is MainViewModel vm)
        {
            vm.CarrierBoard.PropertyChanged += (s, _) => RebuildLayerVisuals();
            vm.CarrierBoard.Stackup.PropertyChanged += (s, _) => RebuildLayerVisuals();
            SubscribeLayerList(vm.CarrierBoard.Stackup.Layers);
            vm.CarrierBoard.Stackup.Layers.CollectionChanged += (_, ce) =>
            {
                if (ce.NewItems != null)
                    foreach (Layer l in ce.NewItems) l.PropertyChanged += (__, ___) => RebuildLayerVisuals();
                RebuildLayerVisuals();
            };

            vm.Module.PropertyChanged += (s, _) => RebuildLayerVisuals();
            vm.Module.Stackup.PropertyChanged += (s, _) => RebuildLayerVisuals();
            SubscribeLayerList(vm.Module.Stackup.Layers);
            vm.Module.Stackup.Layers.CollectionChanged += (_, ce) =>
            {
                if (ce.NewItems != null)
                    foreach (Layer l in ce.NewItems) l.PropertyChanged += (__, ___) => RebuildLayerVisuals();
                RebuildLayerVisuals();
            };

            vm.PropertyChanged += (s, _) => RebuildLayerVisuals();

            // Re-render when manually drawn shapes are added/removed/changed
            vm.ManualShapes.CollectionChanged += (_, __) =>
            {
                RebuildLayerVisuals();
                RefreshDrawnObjectGridFilters();
            };

            // Re-render when vias are added/removed
            vm.Vias.CollectionChanged += (_, __) => RebuildLayerVisuals();

            // Re-render when solder joints are added/removed
            vm.SolderJoints.CollectionChanged += (_, __) => RebuildLayerVisuals();

            // Re-render when ports are added/removed/changed
            vm.SimSettings.Ports.CollectionChanged += (_, __) => RebuildLayerVisuals();

            // Set up DataGrid filters: Copper vs Antenna
            CopperShapesGrid.ItemsSource  = _copperShapes;
            AntennasGrid.ItemsSource      = _antennaShapes;
            RefreshDrawnObjectGridFilters();
        }
    }

    /// <summary>
    /// Rebuild separate copper / antenna display collections from the master
    /// ManualShapes list so each DataGrid shows only its category.
    /// </summary>
    private void RefreshDrawnObjectGridFilters()
    {
        if (DataContext is not MainViewModel vm2) return;

        _copperShapes.Clear();
        _antennaShapes.Clear();

        foreach (var s in vm2.ManualShapes)
        {
            if (s.Name != null && s.Name.StartsWith("Antenna ("))
                _antennaShapes.Add(s);
            else
                _copperShapes.Add(s);
        }
    }

    /// <summary>Rebuild 3D view when a drawn object visibility checkbox changes.</summary>
    private void DrawnObjectVisibility_Changed(object sender, RoutedEventArgs e)
    {
        RebuildLayerVisuals();
    }

    private void SubscribeLayerList(IEnumerable<Layer> layers)
    {
        foreach (var l in layers)
            l.PropertyChanged += (_, __) => RebuildLayerVisuals();
    }

    // -- Layer visual builders ------------------------------------------

    private void RebuildLayerVisuals()
    {
        if (!(DataContext is MainViewModel vm)) return;

        try
        {
        const double MountGap = 0.1;   // mm � solder/adhesive layer thickness
        double cWidth  = vm.CarrierBoard.Width;
        double cHeight = vm.CarrierBoard.Height;
        // Board faces Y+ direction: center at (0, -Width/2), extends Y?[-Width, 0], X?[-Height/2, Height/2]
        double cx      = 0;
        double cy_c    = -cWidth / 2.0;

        double mWidth  = vm.Module.Width;
        double mHeight = vm.Module.Height;
        double mx      =  vm.Module.PositionX;
        double my      = -vm.Module.PositionY - mWidth / 2.0;

        // - Carrier board ----------------------------------------------------
        // Rendering strategy:
        //   CarrierVisual3D  � opaque copper (solid box fallback or Gerber mesh)
        //   ModuleVisual3D   � opaque copper for module
        //   TransparentVisual3D � ALL semi-transparent dielectric boxes, added LAST
        //     WPF 3D composites transparent geometry correctly only when it appears
        //     after ALL opaque nodes in the scene graph.  A separate sibling node
        //     that is declared after CarrierVisual3D/ModuleVisual3D in the XAML
        //     guarantees this regardless of camera angle.
        CarrierVisual3D.Children.Clear();
        ModuleVisual3D.Children.Clear();
        CopperShapeVisual3D.Children.Clear();
        AntennaVisual3D.Children.Clear();
        ViaVisual3D.Children.Clear();
        SolderJointVisual3D.Children.Clear();
        PortVisual3D.Children.Clear();
        TransparentVisual3D.Children.Clear();

        // -- DEBUG: log which layers are actually visible --
        var _dbgLines = new System.Collections.Generic.List<string>();
        _dbgLines.Add("=== RebuildLayerVisuals ===");
        foreach (var dl in vm.CarrierBoard.Stackup.Layers)
            _dbgLines.Add($"  Carrier {dl.Name}: IsVisible={dl.IsVisible}, Type={dl.Type}");
        if (vm.HasModule)
            foreach (var dl in vm.Module.Stackup.Layers)
                _dbgLines.Add($"  Module  {dl.Name}: IsVisible={dl.IsVisible}, Type={dl.Type}");

        double zFace = 0.0;
        foreach (var layer in vm.CarrierBoard.Stackup.Layers)
        {
            if (layer.Thickness <= 0) continue;
            if (!layer.IsVisible) { zFace -= layer.Thickness; continue; }
            double zc = zFace - layer.Thickness / 2.0;

            if (layer.IsConductive && layer.HasGerber)
            {
                var gd = GetGerberData(layer.GerberFilePath);
                if (gd != null)
                {
                    var grp = new Model3DGroup();
                    // +0.001 mm lift: avoid z-fighting with the substrate bottom
                    // face which sits at exactly the same Z as this copper top face.
                    foreach (var geom in GerberParser.BuildMeshes(gd, zFace + 0.001,
                        layer.Thickness,
                        layer.GerberOffsetX, layer.GerberOffsetY,
                        double.TryParse(layer.GerberRotation, out double rotC) ? rotC : 0))
                    {
                        grp.Children.Add(geom);
                    }
                    _dbgLines.Add($"  >> ADD Carrier Gerber mesh: {layer.Name} Z={zFace:F4}");
                    CarrierVisual3D.Children.Add(new ModelVisual3D { Content = grp });
                    zFace -= layer.Thickness;
                    continue;
                }
            }

            // Suppress default flood-fill if THIS layer has hand-drawn shapes
            bool carrierLayerHasShapes = vm.ManualShapes.Any(
                s => s.IsCarrier && s.LayerName == layer.Name);

            var box = new BoxVisual3D
            {
                Center = new Point3D(cx, cy_c, zc),
                Length = cHeight, Width = cWidth, Height = layer.Thickness,
                Fill   = GetLayerBrush(layer, isCarrier: true)
            };
            if (layer.IsConductive)
            {
                if (!carrierLayerHasShapes)
                {
                    _dbgLines.Add($"  >> ADD Carrier default copper: {layer.Name} Z={zc:F4} hasShapes=false");
                    CarrierVisual3D.Children.Add(box);
                }
                else
                    _dbgLines.Add($"  >> SKIP Carrier default copper: {layer.Name} (has shapes)");
            }
            else
            {
                _dbgLines.Add($"  >> ADD Carrier dielectric: {layer.Name} Z={zc:F4}");
                TransparentVisual3D.Children.Add(box);
            }

            zFace -= layer.Thickness;
        }

        // - Module -----------------------------------------------------------
        if (vm.HasModule)
        {
            bool anyModuleLayerVisible = vm.Module.Stackup.Layers.Any(l => l.IsVisible);

            _dbgLines.Add($"  Module anyLayerVisible={anyModuleLayerVisible}");
            // Solder layer � only show when at least one module layer is visible
            if (anyModuleLayerVisible)
            {
                _dbgLines.Add($"  >> ADD solder gap");
                TransparentVisual3D.Children.Add(new BoxVisual3D
                {
                    Center = new Point3D(mx, my, MountGap / 2.0),
                    Length = mHeight, Width = mWidth, Height = MountGap,
                    Fill   = new SolidColorBrush(Color.FromArgb(120, 0xC0, 0xB0, 0x80))
                });
            }

            double zBase = MountGap;
            var moduleLayers  = vm.Module.Stackup.Layers.ToList();
            Layer? moduleTopLayer = moduleLayers.FirstOrDefault(l => l.Type == LayerType.Signal);
            foreach (var layer in moduleLayers.AsEnumerable().Reverse())
            {
                if (layer.Thickness <= 0) continue;
                if (!layer.IsVisible) { zBase += layer.Thickness; continue; }
                double top = zBase + layer.Thickness;
                double zc  = zBase + layer.Thickness / 2.0;

                if (layer.IsConductive && layer.HasGerber)
                {
                    var gd = GetGerberData(layer.GerberFilePath);
                    if (gd != null)
                    {
                        var grp = new Model3DGroup();
                        // Clip to module board XY footprint.
                        foreach (var geom in GerberParser.BuildMeshes(gd, top,
                            layer.Thickness,
                            layer.GerberOffsetX, layer.GerberOffsetY,
                            double.TryParse(layer.GerberRotation, out double rotM) ? rotM : 0))
                            grp.Children.Add(geom);
                        _dbgLines.Add($"  >> ADD Module Gerber mesh: {layer.Name}");
                        ModuleVisual3D.Children.Add(new ModelVisual3D { Content = grp });
                        zBase = top;
                        continue;
                    }
                }

                // Suppress default flood-fill if THIS layer has hand-drawn shapes
                bool moduleLayerHasShapes = vm.ManualShapes.Any(
                    s => !s.IsCarrier && s.LayerName == layer.Name);

                var box = new BoxVisual3D
                {
                    Center = new Point3D(mx, my, zc),
                    Length = mHeight, Width = mWidth, Height = layer.Thickness,
                    Fill   = GetLayerBrush(layer, isCarrier: false, isModuleTop: layer == moduleTopLayer)
                };
                if (layer.IsConductive)
                {
                    if (!moduleLayerHasShapes)
                    {
                        _dbgLines.Add($"  >> ADD Module default copper: {layer.Name} Z={zc:F4}");
                        ModuleVisual3D.Children.Add(box);
                    }
                }
                else
                {
                    _dbgLines.Add($"  >> ADD Module dielectric: {layer.Name} Z={zc:F4}");
                    TransparentVisual3D.Children.Add(box);
                }

                zBase = top;
            }
        }

        // - Manually drawn copper shapes & antennas -----------------------
        // Z-offset priority: PCB +0.001, copper shapes +0.002, antennas +0.003
        _dbgLines.Add($"  Total ManualShapes={vm.ManualShapes.Count}");
        foreach (var s in vm.ManualShapes)
            _dbgLines.Add($"    shape='{s.Name}' board={( s.IsCarrier ? "Carrier" : "Module")} layer={s.LayerName} show3D={s.ShowIn3D} merged={s.MergedPolygons.Count} verts={s.Vertices.Count}");
        foreach (var shape in vm.ManualShapes)
        {
            if (!shape.ShowIn3D) continue;
            if (!shape.IsCarrier && !vm.HasModule) continue;
            // Find which stackup this shape targets
            var stackup = shape.IsCarrier ? vm.CarrierBoard.Stackup : vm.Module.Stackup;
            var targetLayer = stackup.Layers.FirstOrDefault(l => l.Name == shape.LayerName);
            if (targetLayer == null) continue;
            // Respect the layer's visibility toggle (same checkbox that controls Gerber + flood fill)
            if (!targetLayer.IsVisible) continue;

            // Compute the Z top face of the target layer (same logic as loop above)
            double shapeZ = ComputeLayerZFace(stackup.Layers, targetLayer, shape.IsCarrier, MountGap);

            var gd = shape.ToGerberData();
            if (gd.Shapes.Count == 0) continue;

            _dbgLines.Add($"  Shape '{shape.Name}': GerberShapes={gd.Shapes.Count}, MergedPolygons={shape.MergedPolygons.Count}, Vertices={shape.Vertices.Count}");
            for (int si = 0; si < gd.Shapes.Count; si++)
                _dbgLines.Add($"    GerberShape[{si}]: pts={gd.Shapes[si].Points.Count}, isClear={gd.Shapes[si].IsClear}");

            bool isAntenna = shape.Name != null && shape.Name.StartsWith("Antenna (");
            double zOff = isAntenna ? 0.003 : 0.002;

            var grp = new Model3DGroup();
            foreach (var geom in GerberParser.BuildMeshes(gd, shapeZ + zOff,
                targetLayer.Thickness, offsetXmm: 0, offsetYmm: 0, rotationDeg: 0))
                grp.Children.Add(geom);

            _dbgLines.Add($"  >> grp.Children.Count={grp.Children.Count} (3D meshes from shape)");

            var visual = new ModelVisual3D { Content = grp };
            if (isAntenna)
            {
                _dbgLines.Add($"  >> ADD Antenna shape: {shape.Name} layer={shape.LayerName}");
                AntennaVisual3D.Children.Add(visual);
            }
            else
            {
                _dbgLines.Add($"  >> ADD Copper shape: {shape.Name} layer={shape.LayerName}");
                CopperShapeVisual3D.Children.Add(visual);
            }
        }

        // - Vias (highest priority — rendered on top) ----------------------
        foreach (var via in vm.Vias)
        {
            if (!via.ShowIn3D) continue;
            if (!via.IsCarrier && !vm.HasModule) continue;
            var stackup = via.IsCarrier ? vm.CarrierBoard.Stackup : vm.Module.Stackup;
            var fromLayer = stackup.Layers.FirstOrDefault(l => l.Name == via.FromLayer);
            var toLayer   = stackup.Layers.FirstOrDefault(l => l.Name == via.ToLayer);
            if (fromLayer == null || toLayer == null) continue;

            double z1Top = ComputeLayerZFace(stackup.Layers, fromLayer, via.IsCarrier, MountGap);
            double z1Bot = z1Top - fromLayer.Thickness;
            double z2Top = ComputeLayerZFace(stackup.Layers, toLayer, via.IsCarrier, MountGap);
            double z2Bot = z2Top - toLayer.Thickness;

            double cylTop = Math.Max(z1Top, z2Top) + 0.004;
            double cylBot = Math.Min(z1Bot, z2Bot) - 0.004;
            double radius = via.DiameterMm / 2.0;

            var mesh = BuildCylinderMesh(via.X, via.Y, cylBot, cylTop, radius, 16);
            var mat = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(255, 0xC0, 0xC0, 0xC0)));
            var model = new GeometryModel3D(mesh, mat) { BackMaterial = mat };
            ViaVisual3D.Children.Add(new ModelVisual3D { Content = model });
        }

        // - Solder joints (module bottom → carrier top, in the mount gap) --
        {
            var carrierTopLayer = vm.CarrierBoard.Stackup.Layers.FirstOrDefault(l => l.IsConductive);
            if (carrierTopLayer != null)
            {
                double zCarrierTopFace = ComputeLayerZFace(vm.CarrierBoard.Stackup.Layers, carrierTopLayer, true, MountGap);
                double zCarrierBot = zCarrierTopFace - carrierTopLayer.Thickness;

                double sjTop;
                if (vm.HasModule)
                {
                    var moduleLayers = vm.Module.Stackup.Layers.ToList();
                    var moduleBottomLayer = moduleLayers.LastOrDefault(l => l.IsConductive);
                    sjTop = moduleBottomLayer != null
                        ? ComputeLayerZFace(vm.Module.Stackup.Layers, moduleBottomLayer, false, MountGap)
                        : zCarrierTopFace + MountGap;
                }
                else
                {
                    sjTop = zCarrierTopFace + MountGap;
                }
                double sjBot = zCarrierBot;

                foreach (var sj in vm.SolderJoints)
                {
                    if (!sj.ShowIn3D) continue;
                    double radius = sj.DiameterMm / 2.0;

                    var sjMesh = BuildCylinderMesh(sj.X, sj.Y, sjBot, sjTop, radius, 16);
                    var sjMat = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(255, 0xC0, 0xB0, 0x60)));
                    var sjModel = new GeometryModel3D(sjMesh, sjMat) { BackMaterial = sjMat };
                    SolderJointVisual3D.Children.Add(new ModelVisual3D { Content = sjModel });
                }
            }
        }

        // - Ports (rendered as coloured boxes spanning from/to layers) ------
        foreach (var port in vm.SimSettings.Ports)
        {
            string fromName = StripBoardPrefix(port.FromLayer);
            string toName   = StripBoardPrefix(port.ToLayer);
            bool fromIsModule = port.FromLayer.StartsWith("Module", System.StringComparison.OrdinalIgnoreCase);

            PcbStackup? pStackup = null;
            bool pIsCarrier = true;
            Layer? fromL = null, toL = null;

            if (fromIsModule && vm.HasModule)
            {
                fromL = vm.Module.Stackup.Layers.FirstOrDefault(l => l.Name == fromName);
                toL   = vm.Module.Stackup.Layers.FirstOrDefault(l => l.Name == toName);
                if (fromL != null && toL != null) { pStackup = vm.Module.Stackup; pIsCarrier = false; }
            }
            if (pStackup == null)
            {
                fromL = vm.CarrierBoard.Stackup.Layers.FirstOrDefault(l => l.Name == fromName);
                toL   = vm.CarrierBoard.Stackup.Layers.FirstOrDefault(l => l.Name == toName);
                if (fromL != null && toL != null) { pStackup = vm.CarrierBoard.Stackup; pIsCarrier = true; }
            }
            if (pStackup == null && vm.HasModule)
            {
                fromL = vm.Module.Stackup.Layers.FirstOrDefault(l => l.Name == fromName);
                toL   = vm.Module.Stackup.Layers.FirstOrDefault(l => l.Name == toName);
                if (fromL != null && toL != null) { pStackup = vm.Module.Stackup; pIsCarrier = false; }
            }
            if (pStackup == null || fromL == null || toL == null) continue;

            double zFromTop = ComputeLayerZFace(pStackup.Layers, fromL, pIsCarrier, MountGap);
            double zFromBot = zFromTop - fromL.Thickness;
            double zToTop   = ComputeLayerZFace(pStackup.Layers, toL,   pIsCarrier, MountGap);
            double zToBot   = zToTop - toL.Thickness;

            double pZMin = Math.Min(Math.Min(zFromBot, zFromTop), Math.Min(zToBot, zToTop));
            double pZMax = Math.Max(Math.Max(zFromBot, zFromTop), Math.Max(zToBot, zToTop));
            double hwx = port.WidthX / 2.0;
            double hwy = port.WidthY / 2.0;

            var portBox = new BoxVisual3D
            {
                Center = new Point3D(port.X, port.Y, (pZMin + pZMax) / 2.0),
                Length = port.WidthX,
                Width  = port.WidthY,
                Height = pZMax - pZMin + 0.01,
                Fill   = new SolidColorBrush(Color.FromArgb(200, 0xFF, 0x40, 0x40))
            };
            PortVisual3D.Children.Add(portBox);
        }

        // Write debug log to file
        try { File.WriteAllLines(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rebuild3d_log.txt"), _dbgLines); } catch { }
        }
        catch (Exception ex)
        {
            try
            {
                var errLines = new[] { $"RebuildLayerVisuals error: {ex}" };
                File.WriteAllLines(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rebuild3d_log.txt"), errLines);
            } catch { }
        }
    }

    /// <summary>Strip "Carrier – " or "Module – " prefix from a port layer name.</summary>
    private static string StripBoardPrefix(string layerName)
    {
        if (string.IsNullOrEmpty(layerName)) return layerName;
        foreach (var prefix in new[] { "Carrier – ", "Carrier - ", "Module – ", "Module - " })
        {
            if (layerName.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                return layerName.Substring(prefix.Length);
        }
        return layerName;
    }

    /// <summary>Build a solid cylinder mesh centred at (cx,cy) from zBot to zTop.</summary>
    private static MeshGeometry3D BuildCylinderMesh(
        double cx, double cy, double zBot, double zTop, double radius, int segments)
    {
        var mesh = new MeshGeometry3D();
        var pts = mesh.Positions;
        var idx = mesh.TriangleIndices;

        // Top ring [0..segments-1], bottom ring [segments..2*segments-1]
        for (int i = 0; i < segments; i++)
        {
            double angle = 2 * Math.PI * i / segments;
            double px = cx + radius * Math.Cos(angle);
            double py = cy + radius * Math.Sin(angle);
            pts.Add(new Point3D(px, py, zTop));
        }
        for (int i = 0; i < segments; i++)
        {
            double angle = 2 * Math.PI * i / segments;
            double px = cx + radius * Math.Cos(angle);
            double py = cy + radius * Math.Sin(angle);
            pts.Add(new Point3D(px, py, zBot));
        }
        // Centre points for top and bottom caps
        int cTop = pts.Count; pts.Add(new Point3D(cx, cy, zTop));
        int cBot = pts.Count; pts.Add(new Point3D(cx, cy, zBot));

        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            // Side faces (two triangles per quad)
            idx.Add(i);              idx.Add(next);            idx.Add(i + segments);
            idx.Add(next);           idx.Add(next + segments); idx.Add(i + segments);
            // Top cap
            idx.Add(cTop); idx.Add(i); idx.Add(next);
            // Bottom cap
            idx.Add(cBot); idx.Add(next + segments); idx.Add(i + segments);
        }
        return mesh;
    }

    /// <summary>
    /// Returns the Z-world coordinate of the TOP face of <paramref name="targetLayer"/>
    /// within <paramref name="layers"/>. If not found, returns 0.
    /// </summary>
    private static double ComputeLayerZFace(
        IEnumerable<Layer> layers, Layer targetLayer, bool isCarrier, double mountGap)
    {
        if (isCarrier)
        {
            // Carrier: Z starts at 0 (board top), decrements going downward
            double z = 0.0;
            foreach (var l in layers)
            {
                if (l == targetLayer) return z;    // z is the top face of l
                z -= l.Thickness;
            }
        }
        else
        {
            // Module: iterated in reverse (bottom ? top), zBase starts at mountGap and increments
            double zBase = mountGap;
            foreach (var l in layers.Reverse())
            {
                double top = zBase + l.Thickness;  // top face of l
                if (l == targetLayer) return top;
                zBase = top;
            }
        }
        return 0.0;
    }

    private GerberData? GetGerberData(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_gerberCache.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path))
        {
            MessageBox.Show($"Gerber file not found:\n{path}", "Gerber Load Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        try
        {
            var gd = GerberParser.Parse(path);
            if (!gd.HasBounds || gd.Shapes.Count == 0)
            {
                MessageBox.Show(
                    $"Gerber file parsed, but no displayable copper shapes were found:\n{path}\n\n"
                    + "Please confirm this file is a copper layer (Top/Bottom Copper), not a silkscreen or solder-mask layer.",
                    "Gerber \u2013 No Valid Content", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            _gerberCache[path] = gd;
            return gd;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error parsing Gerber file:\n{path}\n\nMessage: {ex.Message}",
                "Gerber Parse Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>
    /// Returns the display brush for a PCB layer.
    /// Carrier:  dielectric = FR4 green  |  Signal/Ground/Power = copper
    /// Module:   dielectric = FR4 gray   |  Signal/Ground/Power = copper
    /// </summary>
    private static Brush GetLayerBrush(Layer layer, bool isCarrier, bool isModuleTop = false)
    {
        bool isConductive = layer.Type == LayerType.Signal
                         || layer.Type == LayerType.Ground
                         || layer.Type == LayerType.Power;

        if (isConductive)
            return new SolidColorBrush(Color.FromArgb(230, 0xB8, 0x73, 0x33));  // copper brown

        // Dielectric: colour matched to real PCB substrate appearance
        // Dielectric layers are rendered semi-transparent so copper on inner
        // layers remains visible through the substrate stack.
        const byte A = 90;   // ~35% opaque � see-through
        return layer.Material switch
        {
            LayerMaterial.FR4         => new SolidColorBrush(Color.FromArgb(A, 0xB8, 0xC8, 0x48)),
            LayerMaterial.Rogers4350B => new SolidColorBrush(Color.FromArgb(A, 0xDC, 0xDC, 0xD4)),
            LayerMaterial.Rogers4003C => new SolidColorBrush(Color.FromArgb(A, 0xD8, 0xD8, 0xCC)),
            LayerMaterial.PolyimidePI => new SolidColorBrush(Color.FromArgb(A, 0xD4, 0x88, 0x18)),
            LayerMaterial.Rogers5880  => new SolidColorBrush(Color.FromArgb(A, 0xE4, 0xEC, 0xE0)),
            // Aluminum CCL: metal backing � keep mostly opaque
            LayerMaterial.AluminumCCL => new SolidColorBrush(Color.FromArgb(200, 0xC0, 0xC8, 0xD0)),
            LayerMaterial.Air         => new SolidColorBrush(Color.FromArgb(20,  0xB0, 0xD0, 0xFF)),
            LayerMaterial.SolderMask  => new SolidColorBrush(Color.FromArgb(120, 0x00, 0x80, 0x00)),  // green solder mask
            _                         => new SolidColorBrush(isCarrier
                                            ? Color.FromArgb(A, 0xB8, 0xC8, 0x48)
                                            : Color.FromArgb(A, 0xDC, 0xDC, 0xD4))
        };
    }

    // -- Gerber file browse / clear --------------------------------------------

    private void GerberBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (!((sender as Button)?.Tag is Layer layer)) return;
        if (!(DataContext is MainViewModel vm)) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select Gerber file for " + layer.Name,
            Filter = "Gerber files|*.gbr;*.ger;*.gtl;*.gbl;*.gts;*.gbs;*.gto;*.gbo;*.gm1;*.g2;*.g3;*.art|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        _gerberCache.Remove(layer.GerberFilePath ?? "");  // invalidate old cache
        layer.GerberFilePath = dlg.FileName;

        // -- Auto-fit: centre Gerber content on its board ---------------------
        try
        {
            var gd = GerberParser.Parse(dlg.FileName);
            if (gd.HasBounds && gd.Shapes.Count > 0)
            {
                // Determine which board this layer belongs to
                bool isCarrierLayer = vm.CarrierBoard.Stackup.Layers.Contains(layer);

                double boardCX, boardCY;
                if (isCarrierLayer)
                {
                    boardCX = 0.0;
                    boardCY = -vm.CarrierBoard.Width / 2.0;
                }
                else
                {
                    boardCX =  vm.Module.PositionX;
                    boardCY = -vm.Module.PositionY - vm.Module.Width / 2.0;
                }

                var (ox, oy) = GerberParser.ComputeAutoFitOffset(gd, boardCX, boardCY);
                // Cache BEFORE setting offset properties so the PropertyChanged
                // ? RebuildLayerVisuals chain uses the cached data (no double parse).
                _gerberCache[dlg.FileName] = gd;
                layer.GerberOffsetX = Math.Round(ox, 4);
                layer.GerberOffsetY = Math.Round(oy, 4);
            }
        }
        catch { /* parse errors are reported in GetGerberData; skip auto-fit silently */ }

        RebuildLayerVisuals();
    }

    private void GerberClear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is Layer layer)
        {
            _gerberCache.Remove(layer.GerberFilePath ?? "");
            layer.GerberFilePath = "";
            RebuildLayerVisuals();
        }
    }

    // -- View preset buttons ----------------------------------------------

    // Top view: looking down Z axis
    private void ViewTop_Click(object sender, RoutedEventArgs e)
        => SetView(new Point3D(0, 0, 300), new Vector3D(0, 1, 0));

    // Bottom view: looking up Z axis
    private void ViewBottom_Click(object sender, RoutedEventArgs e)
        => SetView(new Point3D(0, 0, -300), new Vector3D(0, 1, 0));

    // Front view: camera on +Y side, looking toward �Y, Z is up
    private void ViewFront_Click(object sender, RoutedEventArgs e)
        => SetView(new Point3D(0, 300, 0), new Vector3D(0, 0, 1));

    // Back view: camera on �Y side, looking toward +Y, Z is up
    private void ViewBack_Click(object sender, RoutedEventArgs e)
        => SetView(new Point3D(0, -300, 0), new Vector3D(0, 0, 1));

    // Left view: camera on �X side, looking toward +X, Z is up
    private void ViewLeft_Click(object sender, RoutedEventArgs e)
        => SetView(new Point3D(-300, 0, 0), new Vector3D(0, 0, 1));

    // Right view: camera on +X side, looking toward �X, Z is up
    private void ViewRight_Click(object sender, RoutedEventArgs e)
        => SetView(new Point3D(300, 0, 0), new Vector3D(0, 0, 1));

    // -- Zoom buttons -----------------------------------------------------

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (Viewport3D.Camera is PerspectiveCamera pc)
        {
            var offset = new Vector3D(pc.LookDirection.X, pc.LookDirection.Y, pc.LookDirection.Z);
            offset.Normalize();
            double step = (pc.Position - new Point3D()).Length * 0.2;
            pc.Position += offset * step;
        }
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (Viewport3D.Camera is PerspectiveCamera pc)
        {
            var offset = new Vector3D(pc.LookDirection.X, pc.LookDirection.Y, pc.LookDirection.Z);
            offset.Normalize();
            double step = (pc.Position - new Point3D()).Length * 0.2;
            pc.Position -= offset * step;
        }
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
        => Viewport3D.ZoomExtents(200);

    // -- Z-scale toggle ---------------------------------------------------

    private bool _zScaleX10 = true;   // default: 10� exaggeration
    private readonly Dictionary<string, GerberData> _gerberCache = new();
    private readonly ObservableCollection<ManualShape> _copperShapes  = new();
    private readonly ObservableCollection<ManualShape> _antennaShapes = new();

    private void ZScaleToggle_Click(object sender, RoutedEventArgs e)
    {
        _zScaleX10 = !_zScaleX10;
        ZScaleTransform.ScaleZ = _zScaleX10 ? 10.0 : 1.0;
        ZScaleButton.Content   = _zScaleX10 ? "Z �10" : "Z 1:1";
        Viewport3D.ZoomExtents(200);
    }

    private void Refresh3D_Click(object sender, RoutedEventArgs e)
    {
        // Commit any TextBox that still has focus (covers UpdateSourceTrigger=LostFocus bindings)
        var focused = FocusManager.GetFocusedElement(this) as DependencyObject;
        if (focused != null)
        {
            // Walk up to find a TextBox and force its binding to update
            var tb = focused as System.Windows.Controls.TextBox
                  ?? FindVisualParent<System.Windows.Controls.TextBox>(focused);
            tb?.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        }

        // Force all TextBox bindings in the whole window to flush (catches DataGrid edit cells too)
        foreach (var textBox in FindVisualChildren<System.Windows.Controls.TextBox>(this))
            textBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();

        // Invalidate Gerber cache so changed files are re-parsed
        _gerberCache.Clear();

        // Full 3D rebuild from current ViewModel state
        RebuildLayerVisuals();

        // Reset to initial isometric viewing angle
        var cam = new PerspectiveCamera
        {
            Position      = new Point3D(120, -180, 140),
            LookDirection = new Vector3D(-120, 180, -140),
            UpDirection   = new Vector3D(0, 0, 1),
            FieldOfView   = 45
        };
        Viewport3D.Camera = cam;
        Viewport3D.ZoomExtents(200);
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T t) return t;
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) yield break;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var desc in FindVisualChildren<T>(child))
                yield return desc;
        }
    }

    // ?? TextBox ??? Enter ?,?????????????
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is TextBox tb)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        // -- 3D viewport hotkeys (ignore when a TextBox has focus) ------------
        if (e.OriginalSource is TextBox) return;

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        const double OrbitStep = 5.0;   // degrees per key press
        const double ZoomStep  = 0.12;  // fraction of distance per key press
        const double PanStep   = 2.0;   // mm per key press

        switch (e.Key)
        {
            case Key.Left:
                if (shift) { OrbitCamera(-OrbitStep, 0); }
                else       { PanCamera(-PanStep, 0); }
                e.Handled = true;
                break;
            case Key.Right:
                if (shift) { OrbitCamera(+OrbitStep, 0); }
                else       { PanCamera(+PanStep, 0); }
                e.Handled = true;
                break;
            case Key.Up:
                if (shift) { ZoomCamera(1.0 - ZoomStep); }
                else       { PanCamera(0, +PanStep); }
                e.Handled = true;
                break;
            case Key.Down:
                if (shift) { ZoomCamera(1.0 + ZoomStep); }
                else       { PanCamera(0, -PanStep); }
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Orbit the camera around the scene look-at point.
    /// <paramref name="dAz"/> rotates left/right (around world Z).
    /// <paramref name="dEl"/> rotates up/down (around camera right axis).
    /// </summary>
    private void OrbitCamera(double dAz, double dEl)
    {
        if (!(Viewport3D.Camera is PerspectiveCamera cam)) return;

        var look = cam.LookDirection;
        double dist = look.Length;
        look.Normalize();
        var target = cam.Position + look * dist;   // look-at point
        var arm    = cam.Position - target;         // target ? camera vector

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

            // Clamp: don't flip past the poles (within 3� of vertical)
            var armN = arm; armN.Normalize();
            if (Math.Abs(armN.Z / armN.Length) > Math.Cos(3.0 * Math.PI / 180.0))
                return;
        }

        cam.Position      = target + arm;
        cam.LookDirection = target - cam.Position;
    }

    /// <summary>
    /// Pan the camera perpendicular to the look direction.
    /// <paramref name="dx"/> moves along the camera-right axis (positive = right).
    /// <paramref name="dy"/> moves along the camera-up axis (positive = up).
    /// </summary>
    private void PanCamera(double dx, double dy)
    {
        if (!(Viewport3D.Camera is PerspectiveCamera cam)) return;

        var look = cam.LookDirection;
        look.Normalize();

        // Camera right = look × up
        var right = Vector3D.CrossProduct(look, cam.UpDirection);
        right.Normalize();

        // Camera true-up = right × look
        var up = Vector3D.CrossProduct(right, look);
        up.Normalize();

        var offset = right * dx + up * dy;
        cam.Position += offset;
    }

    /// <summary>
    /// Zoom by moving the camera along its look direction.
    /// factor &lt; 1 ? zoom in (Shift+Up); factor &gt; 1 ? zoom out (Shift+Down).
    /// </summary>
    private void ZoomCamera(double factor)
    {
        if (!(Viewport3D.Camera is PerspectiveCamera cam)) return;

        var look = cam.LookDirection;
        double dist = look.Length;
        look.Normalize();

        double newDist = Math.Max(5.0, dist * factor);
        cam.Position      = cam.Position + look * (dist - newDist);
        cam.LookDirection = look * newDist;
    }

    private void Viewport3D_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        const double OrbitStep = 5.0;

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (shift)
        {
            // Shift + wheel → orbit left/right (azimuth)
            double dir = e.Delta > 0 ? -OrbitStep : OrbitStep;
            OrbitCamera(dir, 0);
        }
        else if (ctrl)
        {
            // Ctrl + wheel → orbit up/down (elevation)
            double dir = e.Delta > 0 ? OrbitStep : -OrbitStep;
            OrbitCamera(0, dir);
        }
        else if (Viewport3D.Camera is PerspectiveCamera pc)
        {
            // Plain wheel → zoom (same approach as ZoomIn/ZoomOut buttons)
            var offset = new Vector3D(pc.LookDirection.X, pc.LookDirection.Y, pc.LookDirection.Z);
            offset.Normalize();
            // Scale step by number of notches (each notch = Delta 120)
            double notches = e.Delta / 120.0;
            double step = (pc.Position - new Point3D()).Length * 0.08 * Math.Abs(notches);
            if (notches > 0)
                pc.Position += offset * step;   // zoom in
            else
                pc.Position -= offset * step;   // zoom out
        }
        e.Handled = true;
    }

    // -- Menu handlers ---------------------------------------------------------

    // -- ?? ------------------------------------------------------------------

    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Open Project File",
            Filter = "Antenna Simulator Project|*.antproj|All Files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        if (!(DataContext is MainViewModel vm)) return;
        try
        {
            ProjectSerializer.Load(dlg.FileName, vm);
            _currentProjectPath = dlg.FileName;
            Title = $"PCB Antenna Simulator  v{AppVersion.Current}  �  {System.IO.Path.GetFileName(dlg.FileName)}";
            RebuildLayerVisuals();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open project.\n\n{ex.Message}",
                "Open Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MenuSave_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectPath != null)
            SaveToFile(_currentProjectPath);
        else
            MenuSaveAs_Click(sender, e);
    }

    private void MenuSaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Save As",
            Filter     = "Antenna Simulator Project|*.antproj|All Files|*.*",
            DefaultExt = ".antproj"
        };
        if (dlg.ShowDialog() == true)
            SaveToFile(dlg.FileName);
    }

    private void SaveToFile(string path)
    {
        if (!(DataContext is MainViewModel vm)) return;
        try
        {
            ProjectSerializer.Save(path, vm);
            _currentProjectPath = path;
            Title = $"PCB Antenna Simulator  v{AppVersion.Current}  �  {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save project.\n\n{ex.Message}",
                "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
        => Application.Current.Shutdown();

    // -- Export openEMS ---------------------------------------------------------

    private void MenuExportOpenEms_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is MainViewModel vm)) return;

        var optWin = new ExportOpenEmsWindow { Owner = this };
        if (optWin.ShowDialog() != true) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export openEMS Simulation Script",
            Filter     = "Python script|*.py",
            FileName   = "run_simulation.py",
            DefaultExt = ".py"
        };
        if (dlg.ShowDialog() != true) return;

        string outputDir = System.IO.Path.GetDirectoryName(dlg.FileName)!;
        // The exporter creates a scripts/ subfolder inside outputDir.
        // If the user picked a file already inside a "scripts" folder,
        // step up so we don't double-nest (scripts/scripts/).
        if (System.IO.Path.GetFileName(outputDir)
                .Equals("scripts", StringComparison.OrdinalIgnoreCase))
            outputDir = System.IO.Path.GetDirectoryName(outputDir)!;

        try
        {
            OpenEmsExporter.Export(
                outputDir, vm, GetGerberData,
                optWin.IncludeDefaultCopper,
                optWin.IncludeCopperShapes,
                optWin.IncludeAntennas,
                optWin.IncludeVias);

            MessageBox.Show(
                $"openEMS simulation files exported successfully.\n\n" +
                $"Output: {outputDir}\n\n" +
                $"Run:  python run_simulation.py",
                "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export openEMS files.\n\n{ex.Message}",
                "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // -- Export STEP -----------------------------------------------------------

    private void MenuExportStep_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is MainViewModel vm)) return;

        var optWin = new ExportStepWindow { Owner = this };
        if (optWin.ShowDialog() != true) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export STEP File",
            Filter     = "STEP files|*.step;*.stp|All files|*.*",
            DefaultExt = ".step"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var groups = BuildExportGroups(vm, optWin);
            if (groups.Count == 0)
            {
                MessageBox.Show("No geometry to export with the selected options.",
                    "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            StepExporter.Export(dlg.FileName, groups);
            MessageBox.Show($"STEP file exported successfully.\n\n{dlg.FileName}",
                "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export STEP file.\n\n{ex.Message}",
                "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Rebuild 3D geometry into categorised groups for STEP export.
    /// Each category is a separate ModelVisual3D so it becomes a distinct STEP product.
    /// </summary>
    private List<(string Label, ModelVisual3D Visual)> BuildExportGroups(
        MainViewModel vm, ExportStepWindow opts)
    {
        const double MountGap = 0.1;
        var result = new List<(string, ModelVisual3D)>();

        var pcbVisual         = new ModelVisual3D();
        var defaultCopperVis  = new ModelVisual3D();
        var copperShapeVis    = new ModelVisual3D();
        var antennaVis        = new ModelVisual3D();
        var viaVis            = new ModelVisual3D();
        var solderJointVis    = new ModelVisual3D();

        double cWidth  = vm.CarrierBoard.Width;
        double cHeight = vm.CarrierBoard.Height;
        double cx = 0, cy_c = -cWidth / 2.0;

        // - Carrier stackup ---------------------------------------------
        double zFace = 0.0;
        foreach (var layer in vm.CarrierBoard.Stackup.Layers)
        {
            if (layer.Thickness <= 0) { continue; }
            double zc = zFace - layer.Thickness / 2.0;

            if (layer.IsConductive)
            {
                if (layer.HasGerber)
                {
                    var gd = GetGerberData(layer.GerberFilePath);
                    if (gd != null)
                    {
                        var grp = new Model3DGroup();
                        foreach (var geom in GerberParser.BuildMeshes(gd, zFace + 0.001,
                            layer.Thickness, layer.GerberOffsetX, layer.GerberOffsetY,
                            double.TryParse(layer.GerberRotation, out double rotC) ? rotC : 0))
                            grp.Children.Add(geom);
                        defaultCopperVis.Children.Add(new ModelVisual3D { Content = grp });
                        zFace -= layer.Thickness;
                        continue;
                    }
                }

                bool hasShapes = vm.ManualShapes.Any(s => s.IsCarrier && s.LayerName == layer.Name);
                if (!hasShapes)
                {
                    defaultCopperVis.Children.Add(MakeBox(cx, cy_c, zc, cHeight, cWidth,
                        layer.Thickness, GetLayerBrush(layer, true)));
                }
            }
            else
            {
                // Dielectric ? PCB
                pcbVisual.Children.Add(MakeBox(cx, cy_c, zc, cHeight, cWidth,
                    layer.Thickness, GetExportDielectricBrush(layer, true)));
            }
            zFace -= layer.Thickness;
        }

        // - Module stackup ----------------------------------------------
        if (vm.HasModule)
        {
            double mWidth  = vm.Module.Width;
            double mHeight = vm.Module.Height;
            double mx      =  vm.Module.PositionX;
            double my      = -vm.Module.PositionY - mWidth / 2.0;

            // Solder gap
            pcbVisual.Children.Add(MakeBox(mx, my, MountGap / 2.0,
                mHeight, mWidth, MountGap,
                new SolidColorBrush(Color.FromRgb(0xC0, 0xB0, 0x80))));

            double zBase = MountGap;
            var moduleLayers = vm.Module.Stackup.Layers.ToList();
            foreach (var layer in moduleLayers.AsEnumerable().Reverse())
            {
                if (layer.Thickness <= 0) { continue; }
                double top = zBase + layer.Thickness;
                double zc  = zBase + layer.Thickness / 2.0;

                if (layer.IsConductive)
                {
                    if (layer.HasGerber)
                    {
                        var gd = GetGerberData(layer.GerberFilePath);
                        if (gd != null)
                        {
                            var grp = new Model3DGroup();
                            foreach (var geom in GerberParser.BuildMeshes(gd, top,
                                layer.Thickness, layer.GerberOffsetX, layer.GerberOffsetY,
                                double.TryParse(layer.GerberRotation, out double rotM) ? rotM : 0))
                                grp.Children.Add(geom);
                            defaultCopperVis.Children.Add(new ModelVisual3D { Content = grp });
                            zBase = top;
                            continue;
                        }
                    }

                    bool hasShapes = vm.ManualShapes.Any(s => !s.IsCarrier && s.LayerName == layer.Name);
                    if (!hasShapes)
                    {
                        defaultCopperVis.Children.Add(MakeBox(mx, my, zc,
                            mHeight, mWidth, layer.Thickness, GetLayerBrush(layer, false)));
                    }
                }
                else
                {
                    pcbVisual.Children.Add(MakeBox(mx, my, zc,
                        mHeight, mWidth, layer.Thickness, GetExportDielectricBrush(layer, false)));
                }
                zBase = top;
            }
        }

        // - Copper shapes & antennas ------------------------------------
        foreach (var shape in vm.ManualShapes)
        {
            if (!shape.ShowIn3D) continue;
            if (!shape.IsCarrier && !vm.HasModule) continue;
            var stackup = shape.IsCarrier ? vm.CarrierBoard.Stackup : vm.Module.Stackup;
            var targetLayer = stackup.Layers.FirstOrDefault(l => l.Name == shape.LayerName);
            if (targetLayer == null) continue;

            double shapeZ = ComputeLayerZFace(stackup.Layers, targetLayer, shape.IsCarrier, MountGap);
            var gd = shape.ToGerberData();
            if (gd.Shapes.Count == 0) continue;

            bool isAntenna = shape.Name != null && shape.Name.StartsWith("Antenna (");
            double zOff = isAntenna ? 0.003 : 0.002;

            var grp = new Model3DGroup();
            foreach (var geom in GerberParser.BuildMeshes(gd, shapeZ + zOff,
                targetLayer.Thickness, 0, 0, 0))
                grp.Children.Add(geom);

            var vis = new ModelVisual3D { Content = grp };
            if (isAntenna)
                antennaVis.Children.Add(vis);
            else
                copperShapeVis.Children.Add(vis);
        }

        // - Vias --------------------------------------------------------
        foreach (var via in vm.Vias)
        {
            if (!via.ShowIn3D) continue;
            if (!via.IsCarrier && !vm.HasModule) continue;
            var stackup = via.IsCarrier ? vm.CarrierBoard.Stackup : vm.Module.Stackup;
            var fromLayer = stackup.Layers.FirstOrDefault(l => l.Name == via.FromLayer);
            var toLayer   = stackup.Layers.FirstOrDefault(l => l.Name == via.ToLayer);
            if (fromLayer == null || toLayer == null) continue;

            double z1Top = ComputeLayerZFace(stackup.Layers, fromLayer, via.IsCarrier, MountGap);
            double z1Bot = z1Top - fromLayer.Thickness;
            double z2Top = ComputeLayerZFace(stackup.Layers, toLayer, via.IsCarrier, MountGap);
            double z2Bot = z2Top - toLayer.Thickness;

            double cylTop = Math.Max(z1Top, z2Top) + 0.004;
            double cylBot = Math.Min(z1Bot, z2Bot) - 0.004;
            double radius = via.DiameterMm / 2.0;

            var mesh = BuildCylinderMesh(via.X, via.Y, cylBot, cylTop, radius, 16);
            var mat  = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)));
            var model = new GeometryModel3D(mesh, mat) { BackMaterial = mat };
            viaVis.Children.Add(new ModelVisual3D { Content = model });
        }

        // - Solder Joints -----------------------------------------------
        {
            var carrierTopLayer = vm.CarrierBoard.Stackup.Layers.FirstOrDefault(l => l.IsConductive);
            if (carrierTopLayer != null)
            {
                double zCarrierTopFace = ComputeLayerZFace(vm.CarrierBoard.Stackup.Layers, carrierTopLayer, true, MountGap);
                double zCarrierBot = zCarrierTopFace - carrierTopLayer.Thickness;

                double sjTop;
                if (vm.HasModule)
                {
                    var expModuleLayers = vm.Module.Stackup.Layers.ToList();
                    var moduleBottomLayer = expModuleLayers.LastOrDefault(l => l.IsConductive);
                    sjTop = moduleBottomLayer != null
                        ? ComputeLayerZFace(vm.Module.Stackup.Layers, moduleBottomLayer, false, MountGap)
                        : zCarrierTopFace + MountGap;
                }
                else
                {
                    sjTop = zCarrierTopFace + MountGap;
                }

                foreach (var sj in vm.SolderJoints)
                {
                    if (!sj.ShowIn3D) continue;
                    double radius = sj.DiameterMm / 2.0;
                    var sjMesh = BuildCylinderMesh(sj.X, sj.Y, zCarrierBot, sjTop, radius, 16);
                    var sjMat  = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xC0, 0xB0, 0x60)));
                    var sjModel = new GeometryModel3D(sjMesh, sjMat) { BackMaterial = sjMat };
                    solderJointVis.Children.Add(new ModelVisual3D { Content = sjModel });
                }
            }
        }

        // - Assemble selected groups ------------------------------------
        // PCB is always included
        result.Add(("PCB Substrate", pcbVisual));

        if (opts.IncludeDefaultCopper)
            result.Add(("Default Copper", defaultCopperVis));
        if (opts.IncludeCopperShapes)
            result.Add(("Copper Shapes", copperShapeVis));
        if (opts.IncludeAntennas)
            result.Add(("Antennas", antennaVis));
        if (opts.IncludeVias)
            result.Add(("Vias", viaVis));

        // Solder joints always included when present (they are part of the board connection)
        result.Add(("Solder Joints", solderJointVis));

        // Remove empty groups
        result.RemoveAll(g =>
        {
            var meshes = new List<(MeshGeometry3D, Color)>();
            StepExporter.CollectMeshes(g.Item2, meshes);
            return meshes.Count == 0;
        });

        return result;
    }

    /// <summary>Create a BoxVisual3D (ModelVisual3D) for export.</summary>
    private static ModelVisual3D MakeBox(double cx, double cy, double cz,
        double length, double width, double height, Brush fill)
    {
        return new BoxVisual3D
        {
            Center = new Point3D(cx, cy, cz),
            Length = length, Width = width, Height = height,
            Fill   = fill
        };
    }

    /// <summary>
    /// Opaque dielectric brush for STEP export (rendering uses semi-transparent,
    /// but STEP files look better with solid colours).
    /// </summary>
    private static Brush GetExportDielectricBrush(Layer layer, bool isCarrier)
    {
        return layer.Material switch
        {
            LayerMaterial.FR4         => new SolidColorBrush(Color.FromRgb(0xB8, 0xC8, 0x48)),
            LayerMaterial.Rogers4350B => new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xD4)),
            LayerMaterial.Rogers4003C => new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xCC)),
            LayerMaterial.PolyimidePI => new SolidColorBrush(Color.FromRgb(0xD4, 0x88, 0x18)),
            LayerMaterial.Rogers5880  => new SolidColorBrush(Color.FromRgb(0xE4, 0xEC, 0xE0)),
            LayerMaterial.AluminumCCL => new SolidColorBrush(Color.FromRgb(0xC0, 0xC8, 0xD0)),
            LayerMaterial.Air         => new SolidColorBrush(Color.FromRgb(0xE0, 0xF0, 0xFF)),
            _                         => new SolidColorBrush(isCarrier
                                            ? Color.FromRgb(0xB8, 0xC8, 0x48)
                                            : Color.FromRgb(0xDC, 0xDC, 0xD4)),
        };
    }

    // -- ?? ------------------------------------------------------------------

    private void MenuDrawCopper_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is MainViewModel vm)) return;
        var win = new ManageShapesWindow(vm) { Owner = this };
        win.ShowDialog();
        RebuildLayerVisuals();
    }

    private void MenuDrawAntenna_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is MainViewModel vm)) return;
        var win = new ManageAntennasWindow(vm) { Owner = this };
        win.ShowDialog();
        RebuildLayerVisuals();
    }

    private void MenuDrawVia_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is MainViewModel vm)) return;
        var win = new DrawViaWindow(vm) { Owner = this };
        if (win.ShowDialog() == true)
            RebuildLayerVisuals();
    }

    private void MenuDrawSolderJoint_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is MainViewModel vm)) return;
        if (!vm.HasModule)
        {
            MessageBox.Show("Solder joints require a module board.\nPlease enable the module first.",
                "No Module", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var win = new DrawSolderJointWindow(vm) { Owner = this };
        if (win.ShowDialog() == true)
            RebuildLayerVisuals();
    }

    private void MenuManageShapes_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is MainViewModel vm)) return;
        var win = new ManageShapesWindow(vm) { Owner = this };
        win.ShowDialog();
        RebuildLayerVisuals();
    }

    // -- Simulation ------------------------------------------------------------------

    private void MenuSimulate_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is MainViewModel vm)) return;

        // ── Pre-flight validation ──
        var errors = ValidateBeforeSimulation(vm);
        if (errors.Count > 0)
        {
            string msg = "Cannot start simulation. Please fix the following:\n\n"
                       + string.Join("\n", errors.Select(e2 => "• " + e2));
            MessageBox.Show(msg, "Simulation Pre-check",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ── Determine output directory: <project>/Sim ──
        if (string.IsNullOrEmpty(_currentProjectPath))
        {
            MessageBox.Show("Please save the project first before running simulation.",
                "Project Not Saved", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ── Ask user for analysis type ──
        var typeDlg = new SimTypeDialog { Owner = this };
        if (typeDlg.ShowDialog() != true) return;
        var analysisType = typeDlg.SelectedType;

        // Apply field dump selections to settings
        vm.SimSettings.FieldDumps.EnableSurfaceCurrent = typeDlg.DumpSurfaceCurrent;
        vm.SimSettings.FieldDumps.EnableEField         = typeDlg.DumpEField;
        vm.SimSettings.FieldDumps.EnableHField         = typeDlg.DumpHField;
        vm.SimSettings.FieldDumps.OverlayShapeOutline  = typeDlg.OverlayOutline;

        string projectDir = System.IO.Path.GetDirectoryName(_currentProjectPath)!;
        string outputDir = System.IO.Path.Combine(projectDir, "Sim");
        System.IO.Directory.CreateDirectory(outputDir);

        // ── Export simulation files ──
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;

            OpenEmsExporter.Export(
                outputDir, vm, GetGerberData,
                includeDefaultCopper: false,
                includeCopperShapes:  true,
                includeAntennas:      true,
                includeVias:          true,
                analysisType:         analysisType);

            Mouse.OverrideCursor = null;
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show($"Failed to export simulation files.\n\n{ex.Message}",
                "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // ── Launch simulation console ──
        var simWin = new SimConsoleWindow(outputDir, vm.SimSettings.Solver.MaxTimesteps) { Owner = this };
        simWin.Show();
        simWin.StartSimulation();
    }

    /// <summary>
    /// Checks all prerequisites for a valid openEMS simulation export.
    /// Returns an empty list if everything is ready.
    /// </summary>
    private List<string> ValidateBeforeSimulation(MainViewModel vm)
    {
        var err = new List<string>();
        var ss  = vm.SimSettings;

        // 1. At least one antenna drawn
        bool hasAntenna = vm.DrawnAntennas.Count > 0
            || vm.ManualShapes.Any(s => s.Name != null && s.Name.StartsWith("Antenna ("));
        if (!hasAntenna)
            err.Add("No antenna has been drawn. Use Draw → Antenna to create one.");

        // 2. At least one ground plane designated
        bool hasGround = ss.GroundPlanes.Any(gp => gp.IsGround);
        if (!hasGround)
            err.Add("No ground plane is designated. Open Simulation Settings → Ground Planes and mark at least one layer as ground.");

        // 3. At least one port defined
        if (ss.Ports.Count == 0)
            err.Add("No excitation port defined. Open Simulation Settings → Ports / Feed and add at least one port.");

        // 4. Port layer assignments
        foreach (var port in ss.Ports)
        {
            if (string.IsNullOrWhiteSpace(port.FromLayer))
                err.Add($"Port \"{port.Label}\": Signal layer (+) is not set.");
            if (string.IsNullOrWhiteSpace(port.ToLayer))
                err.Add($"Port \"{port.Label}\": Ground layer (−) is not set.");
            if (port.FromLayer == port.ToLayer && !string.IsNullOrEmpty(port.FromLayer))
                err.Add($"Port \"{port.Label}\": Signal and ground layers are the same.");
            if (port.Impedance <= 0)
                err.Add($"Port \"{port.Label}\": Reference impedance must be > 0 Ω.");
        }

        // 5. Frequency sweep
        if (ss.Sweep.StartGHz <= 0)
            err.Add("Frequency sweep start must be > 0 GHz.");
        if (ss.Sweep.StopGHz <= ss.Sweep.StartGHz)
            err.Add("Frequency sweep stop must be greater than start.");
        if (ss.Sweep.Type == SweepType.Discrete && ss.Sweep.StepGHz <= 0)
            err.Add("Discrete sweep step size must be > 0 GHz.");
        if (ss.Sweep.Type != SweepType.Discrete && ss.Sweep.NumPoints < 2)
            err.Add("Frequency sweep must have at least 2 points.");

        // 6. Mesh settings
        if (ss.Mesh.MeshFreqGHz <= 0)
            err.Add("Mesh reference frequency must be > 0 GHz.");
        if (ss.Mesh.CellsPerWavelength < 5)
            err.Add("Cells per wavelength must be at least 5 (recommended ≥ 15).");

        // 7. Board dimensions
        if (vm.CarrierBoard.Width <= 0 || vm.CarrierBoard.Height <= 0)
            err.Add("Carrier board dimensions (Width/Height) must be > 0.");

        // 8. Boundary – manual sim area
        if (ss.Boundary.ManualSimArea)
        {
            if (ss.Boundary.SimXMin >= ss.Boundary.SimXMax)
                err.Add("Manual sim area: X Min must be less than X Max.");
            if (ss.Boundary.SimYMin >= ss.Boundary.SimYMax)
                err.Add("Manual sim area: Y Min must be less than Y Max.");
            if (ss.Boundary.SimZMin >= ss.Boundary.SimZMax)
                err.Add("Manual sim area: Z Min must be less than Z Max.");
        }

        return err;
    }

    private void MenuSimSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is MainViewModel vm)) return;
        var win = new SimSettingsWindow(vm) { Owner = this };
        win.ShowDialog();
    }

    // -- ???? ??? -------------------------------------------------------

    private void MenuViewS11_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select S11.csv",
            Filter = "S11 CSV|S11.csv|All CSV|*.csv",
            FileName = "S11.csv"
        };
        if (dlg.ShowDialog(this) != true) return;

        string resultsDir = System.IO.Path.GetDirectoryName(dlg.FileName)!;
        var win = new S11ResultWindow(resultsDir) { Owner = this };
        win.Show();
    }

    private void MenuViewFarField_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select FarField_Eplane.csv",
            Filter = "Far-Field CSV|FarField_Eplane.csv|All CSV|*.csv",
            FileName = "FarField_Eplane.csv"
        };
        if (dlg.ShowDialog(this) != true) return;

        string resultsDir = System.IO.Path.GetDirectoryName(dlg.FileName)!;
        var win = new FarFieldResultWindow(resultsDir) { Owner = this };
        win.Show();
    }

    private void MenuViewGain_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "??????(Realized Gain / Peak Gain vs. Frequency)??????????",
            "???? � ??", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuViewEfficiency_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "????(???? / ???)??????????",
            "???? � ??", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuViewCurrentDist_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a field dump image (e.g. Jf_surface.png)",
            Filter = "Field PNG|*_surface.png|All files|*.*",
            FileName = "Jf_surface.png"
        };
        if (dlg.ShowDialog(this) != true) return;

        string resultsDir = System.IO.Path.GetDirectoryName(dlg.FileName)!;
        var win = new FieldResultWindow(resultsDir) { Owner = this };
        win.Show();
    }

    private void MenuViewSmith_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Smith ????????????",
            "???? � Smith ??", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // -- ?? ------------------------------------------------------------------

    // -- ?? ------------------------------------------------------------------

    private void MenuToolImpedance_Click(object sender, RoutedEventArgs e)
    {
        BoardConfig? carrier = null;
        BoardConfig? module = null;
        if (DataContext is ViewModels.MainViewModel vm)
        {
            carrier = vm.CarrierBoard;
            module = vm.Module;
        }
        var win = new MicrostripCalcWindow(carrier, module) { Owner = this };
        win.Show();
    }

    private void MenuToolFreqWL_Click(object sender, RoutedEventArgs e)
    {
        var win = new FreqWavelengthWindow { Owner = this };
        win.Show();
    }

    private void MenuToolSkinDepth_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Skin Depth Calculator is not yet implemented.\n\n"
            + "Planned: Enter frequency and conductor conductivity; output skin depth d = sqrt(2 / (? � � � s)).",
            "Tools � Skin Depth Calculator", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuToolSmithChart_Click(object sender, RoutedEventArgs e)
    {
        var win = new SmithChartWindow { Owner = this };
        win.Show();
    }

    private void MenuToolOptions_Click(object sender, RoutedEventArgs e)
    {
        var win = new OptionsWindow { Owner = this };
        win.ShowDialog();
    }

    // -- ?? ------------------------------------------------------------------

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"PCB Antenna Simulator\nVersion  {AppVersion.Current}\n\n"
            + "3D PCB stackup visualiser with Gerber import and manual copper drawing.",
            "About / Version Info", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Online update check is not yet implemented.", "Check for Updates",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuCopyright_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"PCB Antenna Simulator  v{AppVersion.Current}\n\n"
            + "Copyright \u00a9 2024\u20132026  Bin Xiao. All rights reserved.\n\n"
            + "This software is for educational and research use only.\n"
            + "The author assumes no liability for any consequences arising from its use.",
            "Copyright Notice", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
