using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AntennaSimulatorApp.Models;
using AntennaSimulatorApp.ViewModels;
using AntennaSimulatorApp.Views;

namespace AntennaSimulatorApp.Services
{
    /// <summary>
    /// Serialises / deserialises the entire project state as a JSON .antproj file.
    /// </summary>
    public static class ProjectSerializer
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        // ──────────────────────────────────────────────────────────────────────
        //  Save
        // ──────────────────────────────────────────────────────────────────────

        private static readonly string DiagLog = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "antenna_diag.log");

        /// <summary>Write a diagnostic line to antenna_diag.log for troubleshooting.</summary>
        public static void DiagWrite(string msg)
        {
            try { File.AppendAllText(DiagLog, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); }
            catch { /* ignore */ }
        }

        public static void Save(string path, MainViewModel vm)
        {
            var dto = ToProjectData(vm);
            DiagWrite($"[Save] DrawnAntennas count = {dto.DrawnAntennas.Count}");
            foreach (var a in dto.DrawnAntennas)
                DiagWrite($"  [{a.Name}] Type={a.Type} Freq={a.FreqGHz} L={a.LengthL} H={a.HeightH} FeedGap={a.FeedGap}");
            string json = JsonSerializer.Serialize(dto, JsonOpts);
            File.WriteAllText(path, json);
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Load
        // ──────────────────────────────────────────────────────────────────────

        public static void Load(string path, MainViewModel vm)
        {
            string json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<ProjectData>(json, JsonOpts)
                      ?? throw new InvalidOperationException("Failed to deserialise project file.");
            DiagWrite($"[Load] JSON DrawnAntennas count = {dto.DrawnAntennas.Count}");
            foreach (var a in dto.DrawnAntennas)
                DiagWrite($"  [{a.Name}] Type={a.Type} Freq={a.FreqGHz} L={a.LengthL} H={a.HeightH} FeedGap={a.FeedGap}");
            ApplyProjectData(dto, vm);
            DiagWrite($"[Load] Final vm.DrawnAntennas count = {vm.DrawnAntennas.Count}");
            foreach (var a in vm.DrawnAntennas)
                DiagWrite($"  [{a.Name}] Type={a.Type} Freq={a.FreqGHz} L={a.LengthL} H={a.HeightH} FeedGap={a.FeedGap}");
        }

        // ==================================================================
        //  ViewModel ➜ DTO
        // ==================================================================

        private static ProjectData ToProjectData(MainViewModel vm)
        {
            var pd = new ProjectData
            {
                FormatVersion = 1,
                HasModule     = vm.HasModule,
                CarrierBoard  = BoardToDto(vm.CarrierBoard),
                Module        = ModuleToDto(vm.Module),
                SimSettings   = SimToDto(vm.SimSettings),
            };

            foreach (var ms in vm.ManualShapes)
                pd.ManualShapes.Add(ManualShapeToDto(ms));

            foreach (var ap in vm.DrawnAntennas)
                pd.DrawnAntennas.Add(AntennaToDto(ap));

            foreach (var v in vm.Vias)
                pd.Vias.Add(ViaToDto(v));

            foreach (var sj in vm.SolderJoints)
                pd.SolderJoints.Add(SolderJointToDto(sj));

            return pd;
        }

        // ── Board ─────────────────────────────────────────────────────────────

        private static BoardDto BoardToDto(BoardConfig b)
        {
            var dto = new BoardDto
            {
                Name       = b.Name,
                Width      = b.Width,
                Height     = b.Height,
                Thickness  = b.Thickness,
                LayerCount = b.LayerCount,
            };
            foreach (var l in b.Stackup.Layers)
                dto.Layers.Add(LayerToDto(l));
            return dto;
        }

        private static ModuleDto ModuleToDto(ModuleConfig m)
        {
            var dto = new ModuleDto
            {
                Name       = m.Name,
                Width      = m.Width,
                Height     = m.Height,
                Thickness  = m.Thickness,
                LayerCount = m.LayerCount,
                PositionX  = m.PositionX,
                PositionY  = m.PositionY,
                Rotation   = m.Rotation,
            };
            foreach (var l in m.Stackup.Layers)
                dto.Layers.Add(LayerToDto(l));
            return dto;
        }

        private static LayerDto LayerToDto(Layer l) => new()
        {
            Name              = l.Name,
            Thickness         = l.Thickness,
            Type              = l.Type,
            Material          = l.Material,
            DielectricConstant = l.DielectricConstant,
            IsVisible         = l.IsVisible,
            GerberFilePath    = l.GerberFilePath,
            GerberOffsetX     = l.GerberOffsetX,
            GerberOffsetY     = l.GerberOffsetY,
            GerberRotation    = l.GerberRotation,
        };

        // ── ManualShape ───────────────────────────────────────────────────────

        private static ManualShapeDto ManualShapeToDto(ManualShape s)
        {
            var dto = new ManualShapeDto
            {
                Name      = s.Name,
                IsCarrier = s.IsCarrier,
                LayerName = s.LayerName,
                ShowIn3D  = s.ShowIn3D,
            };
            foreach (var v in s.Vertices)
                dto.Vertices.Add(new VertexDto { X = v.X, Y = v.Y });
            foreach (var poly in s.MergedPolygons)
            {
                var plist = new List<VertexDto>();
                foreach (var v in poly)
                    plist.Add(new VertexDto { X = v.X, Y = v.Y });
                dto.MergedPolygons.Add(plist);
            }
            return dto;
        }

        // ── Via ───────────────────────────────────────────────────────────────

        private static ViaDto ViaToDto(Via v) => new()
        {
            Name        = v.Name,
            IsCarrier   = v.IsCarrier,
            FromLayer   = v.FromLayer,
            ToLayer     = v.ToLayer,
            DiameterMil = v.DiameterMil,
            X           = v.X,
            Y           = v.Y,
            ShowIn3D    = v.ShowIn3D,
        };
        // ── SolderJoint ─────────────────────────────────────────────────────────────

        private static SolderJointDto SolderJointToDto(SolderJoint sj) => new()
        {
            Name        = sj.Name,
            DiameterMil = sj.DiameterMil,
            X           = sj.X,
            Y           = sj.Y,
            ShowIn3D    = sj.ShowIn3D,
        };
        // ── AntennaParams ─────────────────────────────────────────────────────

        private static AntennaParamsDto AntennaToDto(AntennaParams a)
        {
            var dto = new AntennaParamsDto
            {
                Type          = a.Type.ToString(),
                IsCarrier     = a.IsCarrier,
                LayerName     = a.LayerName,
                Name          = a.Name,
                OffsetX       = a.OffsetX,
                OffsetY       = a.OffsetY,
                FreqGHz       = a.FreqGHz,
                LengthL       = a.LengthL,
                HeightH       = a.HeightH,
                FeedGap       = a.FeedGap,
                ShortPinWidth = a.ShortPinWidth,
                FeedPinWidth  = a.FeedPinWidth,
                MatchStubWidth= a.MatchStubWidth,
                RadiatorWidth = a.RadiatorWidth,
                MifaHeightH   = a.MifaHeightH,
                MeanderHeight = a.MeanderHeight,
                MeanderPitch  = a.MeanderPitch,
                MifaShortWidth= a.MifaShortWidth,
                MifaFeedWidth = a.MifaFeedWidth,
                MifaHorizWidth= a.MifaHorizWidth,
                MifaVertWidth = a.MifaVertWidth,
                AvailWidth    = a.AvailWidth,
                AvailHeight   = a.AvailHeight,
                PcbOffsetX    = a.PcbOffsetX,
                PcbOffsetY    = a.PcbOffsetY,
                Clearance     = a.Clearance,
            };
            foreach (var (x, y) in a.CustomVertices)
                dto.CustomVertices.Add(new VertexDto { X = x, Y = y });
            return dto;
        }

        // ── SimSettings ───────────────────────────────────────────────────────

        private static SimSettingsDto SimToDto(SimSettings s)
        {
            var dto = new SimSettingsDto
            {
                Boundary = new BoundaryDto
                {
                    BoundaryType  = s.Boundary.Type.ToString(),
                    PmlLayers     = s.Boundary.PmlLayers,
                    SpacingXPlus  = s.Boundary.SpacingXPlus,
                    SpacingXMinus = s.Boundary.SpacingXMinus,
                    SpacingYPlus  = s.Boundary.SpacingYPlus,
                    SpacingYMinus = s.Boundary.SpacingYMinus,
                    SpacingZPlus  = s.Boundary.SpacingZPlus,
                    SpacingZMinus = s.Boundary.SpacingZMinus,
                    SymmetryX     = s.Boundary.SymmetryX.ToString(),
                    SymmetryY     = s.Boundary.SymmetryY.ToString(),
                    SymmetryZ     = s.Boundary.SymmetryZ.ToString(),
                    ManualSimArea = s.Boundary.ManualSimArea,
                    SimXMin       = s.Boundary.SimXMin,
                    SimXMax       = s.Boundary.SimXMax,
                    SimYMin       = s.Boundary.SimYMin,
                    SimYMax       = s.Boundary.SimYMax,
                    SimZMin       = s.Boundary.SimZMin,
                    SimZMax       = s.Boundary.SimZMax,
                },
                Mesh = new MeshDto
                {
                    AdaptiveMeshing    = s.Mesh.AdaptiveMeshing,
                    MeshFreqGHz        = s.Mesh.MeshFreqGHz,
                    CellsPerWavelength = s.Mesh.CellsPerWavelength,
                    MinStepMm          = s.Mesh.MinStepMm,
                    ZMaxStepMm         = s.Mesh.ZMaxStepMm,
                    MinCellsPerTrace   = s.Mesh.MinCellsPerTrace,
                    UsePecSheets       = s.Mesh.UsePecSheets,
                    MaxAdaptivePasses  = s.Mesh.MaxAdaptivePasses,
                    ConvergenceDelta   = s.Mesh.ConvergenceDelta,
                },
                Sweep = new FreqSweepDto
                {
                    SweepType = s.Sweep.Type.ToString(),
                    StartGHz  = s.Sweep.StartGHz,
                    StopGHz   = s.Sweep.StopGHz,
                    StepGHz   = s.Sweep.StepGHz,
                    NumPoints = s.Sweep.NumPoints,
                },
                Solver = new SolverDto
                {
                    MaxTimesteps = s.Solver.MaxTimesteps,
                    EndCriteria  = s.Solver.EndCriteria,
                    NumThreads   = s.Solver.NumThreads,
                },
                FieldDumps = new FieldDumpsDto
                {
                    EnableSurfaceCurrent = s.FieldDumps.EnableSurfaceCurrent,
                    EnableEField         = s.FieldDumps.EnableEField,
                    EnableHField         = s.FieldDumps.EnableHField,
                    OverlayShapeOutline  = s.FieldDumps.OverlayShapeOutline,
                },
            };

            foreach (var p in s.Ports)
                dto.Ports.Add(new FeedPointDto
                {
                    Label     = p.Label,
                    FromLayer = p.FromLayer,
                    ToLayer   = p.ToLayer,
                    X         = p.X,
                    Y         = p.Y,
                    PortType  = p.PortType.ToString(),
                    IntegLine = p.IntegLine.ToString(),
                    Impedance = p.Impedance,
                    WidthX    = p.WidthX,
                    WidthY    = p.WidthY,
                });

            foreach (var g in s.GroundPlanes)
                dto.GroundPlanes.Add(new GroundPlaneDto
                {
                    BoardName = g.BoardName,
                    LayerName = g.LayerName,
                    IsGround  = g.IsGround,
                });

            return dto;
        }

        // ==================================================================
        //  DTO ➜ ViewModel
        // ==================================================================

        private static void ApplyProjectData(ProjectData pd, MainViewModel vm)
        {
            // ── Carrier ──
            ApplyBoard(pd.CarrierBoard, vm.CarrierBoard);

            // ── Module ──
            vm.HasModule = pd.HasModule;
            ApplyModule(pd.Module, vm.Module);

            // ── ManualShapes ──
            vm.ManualShapes.Clear();
            foreach (var sDto in pd.ManualShapes)
                vm.ManualShapes.Add(DtoToManualShape(sDto));

            // ── DrawnAntennas ──
            vm.DrawnAntennas.Clear();
            foreach (var aDto in pd.DrawnAntennas)
                vm.DrawnAntennas.Add(DtoToAntenna(aDto));

            // ── Migration: create DrawnAntennas entries for orphaned antenna shapes ──
            foreach (var ms in vm.ManualShapes)
            {
                if (ms.Name == null || !ms.Name.StartsWith("Antenna (")) continue;
                string innerName = ms.Name.Substring("Antenna (".Length).TrimEnd(')');
                if (vm.DrawnAntennas.Any(a => a.Name == innerName)) continue;

                AntennaType aType = innerName switch
                {
                    "IFA"    => AntennaType.InvertedF,
                    "MIFA"   => AntennaType.MeanderedInvertedF,
                    "Custom" => AntennaType.Custom,
                    _        => AntennaType.InvertedF,
                };
                vm.DrawnAntennas.Add(new AntennaParams
                {
                    Name      = innerName,
                    Type      = aType,
                    IsCarrier = ms.IsCarrier,
                    LayerName = ms.LayerName,
                });
            }

            // ── Vias ──
            vm.Vias.Clear();
            if (pd.Vias != null)
                foreach (var vDto in pd.Vias)
                    vm.Vias.Add(DtoToVia(vDto));

            // ── Solder Joints ──
            vm.SolderJoints.Clear();
            if (pd.SolderJoints != null)
                foreach (var sjDto in pd.SolderJoints)
                    vm.SolderJoints.Add(DtoToSolderJoint(sjDto));

            // ── SimSettings ──
            ApplySimSettings(pd.SimSettings, vm.SimSettings);
        }

        // ── Board ─────────────────────────────────────────────────────────────

        private static void ApplyBoard(BoardDto dto, BoardConfig board)
        {
            board.Name       = dto.Name;
            board.Width      = dto.Width;
            board.Height     = dto.Height;
            board.LayerCount = dto.LayerCount;   // triggers stackup regeneration
            board.Thickness  = dto.Thickness;

            // Overwrite generated stackup with saved layer details
            if (dto.Layers.Count > 0)
            {
                var stackLayers = board.Stackup.Layers;
                int count = Math.Min(dto.Layers.Count, stackLayers.Count);
                for (int i = 0; i < count; i++)
                    ApplyLayer(dto.Layers[i], stackLayers[i]);

                // If saved file has more layers than generated, add extras
                for (int i = count; i < dto.Layers.Count; i++)
                    stackLayers.Add(DtoToLayer(dto.Layers[i]));
            }
        }

        private static void ApplyModule(ModuleDto dto, ModuleConfig module)
        {
            ApplyBoard(dto, module);
            module.PositionX = dto.PositionX;
            module.PositionY = dto.PositionY;
            module.Rotation  = dto.Rotation;
        }

        private static void ApplyLayer(LayerDto dto, Layer layer)
        {
            layer.Name              = dto.Name;
            layer.Thickness         = dto.Thickness;
            layer.Type              = dto.Type;
            layer.Material          = dto.Material;
            layer.DielectricConstant = dto.DielectricConstant;
            layer.IsVisible         = dto.IsVisible;
            layer.GerberFilePath    = dto.GerberFilePath;
            layer.GerberOffsetX     = dto.GerberOffsetX;
            layer.GerberOffsetY     = dto.GerberOffsetY;
            layer.GerberRotation    = dto.GerberRotation;
        }

        private static Layer DtoToLayer(LayerDto dto)
        {
            var l = new Layer();
            ApplyLayer(dto, l);
            return l;
        }

        // ── ManualShape ───────────────────────────────────────────────────────

        private static ManualShape DtoToManualShape(ManualShapeDto dto)
        {
            var ms = new ManualShape
            {
                Name      = dto.Name,
                IsCarrier = dto.IsCarrier,
                LayerName = dto.LayerName,
                ShowIn3D  = dto.ShowIn3D,
            };
            foreach (var v in dto.Vertices)
                ms.Vertices.Add(new ShapeVertex(v.X, v.Y));
            foreach (var pList in dto.MergedPolygons)
            {
                var poly = new List<ShapeVertex>();
                foreach (var v in pList)
                    poly.Add(new ShapeVertex(v.X, v.Y));
                ms.MergedPolygons.Add(poly);
            }
            return ms;
        }

        // ── Via ───────────────────────────────────────────────────────────────

        private static Via DtoToVia(ViaDto dto) => new()
        {
            Name        = dto.Name,
            IsCarrier   = dto.IsCarrier,
            FromLayer   = dto.FromLayer,
            ToLayer     = dto.ToLayer,
            DiameterMil = dto.DiameterMil,
            X           = dto.X,
            Y           = dto.Y,
            ShowIn3D    = dto.ShowIn3D,
        };

        // ── SolderJoint ───────────────────────────────────────────────────────

        private static SolderJoint DtoToSolderJoint(SolderJointDto dto) => new()
        {
            Name        = dto.Name,
            DiameterMil = dto.DiameterMil,
            X           = dto.X,
            Y           = dto.Y,
            ShowIn3D    = dto.ShowIn3D,
        };

        // ── AntennaParams ─────────────────────────────────────────────────────

        private static AntennaParams DtoToAntenna(AntennaParamsDto dto)
        {
            Enum.TryParse<AntennaType>(dto.Type, out var aType);
            var ap = new AntennaParams
            {
                Type          = aType,
                IsCarrier     = dto.IsCarrier,
                LayerName     = dto.LayerName,
                Name          = dto.Name,
                OffsetX       = dto.OffsetX,
                OffsetY       = dto.OffsetY,
                FreqGHz       = dto.FreqGHz,
                LengthL       = dto.LengthL,
                HeightH       = dto.HeightH,
                FeedGap       = dto.FeedGap,
                ShortPinWidth = dto.ShortPinWidth,
                FeedPinWidth  = dto.FeedPinWidth,
                MatchStubWidth= dto.MatchStubWidth,
                RadiatorWidth = dto.RadiatorWidth,
                MifaHeightH   = dto.MifaHeightH,
                MeanderHeight = dto.MeanderHeight,
                MeanderPitch  = dto.MeanderPitch,
                MifaShortWidth= dto.MifaShortWidth,
                MifaFeedWidth = dto.MifaFeedWidth,
                MifaHorizWidth= dto.MifaHorizWidth,
                MifaVertWidth = dto.MifaVertWidth,
                AvailWidth    = dto.AvailWidth,
                AvailHeight   = dto.AvailHeight,
                PcbOffsetX    = dto.PcbOffsetX,
                PcbOffsetY    = dto.PcbOffsetY,
                Clearance     = dto.Clearance,
            };
            foreach (var v in dto.CustomVertices)
                ap.CustomVertices.Add((v.X, v.Y));
            return ap;
        }

        // ── SimSettings ───────────────────────────────────────────────────────

        private static void ApplySimSettings(SimSettingsDto dto, SimSettings sim)
        {
            // Boundary
            Enum.TryParse<BoundaryType>(dto.Boundary.BoundaryType, out var bt);
            sim.Boundary.Type          = bt;
            sim.Boundary.PmlLayers     = dto.Boundary.PmlLayers;
            sim.Boundary.SpacingXPlus  = dto.Boundary.SpacingXPlus;
            sim.Boundary.SpacingXMinus = dto.Boundary.SpacingXMinus;
            sim.Boundary.SpacingYPlus  = dto.Boundary.SpacingYPlus;
            sim.Boundary.SpacingYMinus = dto.Boundary.SpacingYMinus;
            sim.Boundary.SpacingZPlus  = dto.Boundary.SpacingZPlus;
            sim.Boundary.SpacingZMinus = dto.Boundary.SpacingZMinus;
            Enum.TryParse<SymmetryType>(dto.Boundary.SymmetryX, out var sx); sim.Boundary.SymmetryX = sx;
            Enum.TryParse<SymmetryType>(dto.Boundary.SymmetryY, out var sy); sim.Boundary.SymmetryY = sy;
            Enum.TryParse<SymmetryType>(dto.Boundary.SymmetryZ, out var sz); sim.Boundary.SymmetryZ = sz;
            sim.Boundary.ManualSimArea = dto.Boundary.ManualSimArea;
            sim.Boundary.SimXMin       = dto.Boundary.SimXMin;
            sim.Boundary.SimXMax       = dto.Boundary.SimXMax;
            sim.Boundary.SimYMin       = dto.Boundary.SimYMin;
            sim.Boundary.SimYMax       = dto.Boundary.SimYMax;
            sim.Boundary.SimZMin       = dto.Boundary.SimZMin;
            sim.Boundary.SimZMax       = dto.Boundary.SimZMax;

            // Mesh
            sim.Mesh.AdaptiveMeshing    = dto.Mesh.AdaptiveMeshing;
            sim.Mesh.MeshFreqGHz        = dto.Mesh.MeshFreqGHz;
            sim.Mesh.CellsPerWavelength = dto.Mesh.CellsPerWavelength;
            sim.Mesh.MinStepMm          = dto.Mesh.MinStepMm;
            sim.Mesh.ZMaxStepMm         = dto.Mesh.ZMaxStepMm;
            sim.Mesh.MinCellsPerTrace   = dto.Mesh.MinCellsPerTrace;
            sim.Mesh.UsePecSheets       = dto.Mesh.UsePecSheets;
            sim.Mesh.MaxAdaptivePasses  = dto.Mesh.MaxAdaptivePasses;
            sim.Mesh.ConvergenceDelta   = dto.Mesh.ConvergenceDelta;

            // Sweep
            Enum.TryParse<SweepType>(dto.Sweep.SweepType, out var swt);
            sim.Sweep.Type      = swt;
            sim.Sweep.StartGHz  = dto.Sweep.StartGHz;
            sim.Sweep.StopGHz   = dto.Sweep.StopGHz;
            sim.Sweep.StepGHz   = dto.Sweep.StepGHz;
            sim.Sweep.NumPoints = dto.Sweep.NumPoints;

            // Solver
            if (dto.Solver != null)
            {
                sim.Solver.MaxTimesteps = dto.Solver.MaxTimesteps;
                sim.Solver.EndCriteria  = dto.Solver.EndCriteria;
                sim.Solver.NumThreads   = dto.Solver.NumThreads;
            }

            // Field dumps
            if (dto.FieldDumps != null)
            {
                sim.FieldDumps.EnableSurfaceCurrent = dto.FieldDumps.EnableSurfaceCurrent;
                sim.FieldDumps.EnableEField         = dto.FieldDumps.EnableEField;
                sim.FieldDumps.EnableHField         = dto.FieldDumps.EnableHField;
                sim.FieldDumps.OverlayShapeOutline  = dto.FieldDumps.OverlayShapeOutline;
            }

            // Ports
            sim.Ports.Clear();
            foreach (var pDto in dto.Ports)
            {
                Enum.TryParse<PortType>(pDto.PortType, out var pt);
                Enum.TryParse<IntegLine>(pDto.IntegLine, out var il);
                sim.Ports.Add(new FeedPoint
                {
                    Label     = pDto.Label,
                    FromLayer = pDto.FromLayer,
                    ToLayer   = pDto.ToLayer,
                    X         = pDto.X,
                    Y         = pDto.Y,
                    PortType  = pt,
                    IntegLine = il,
                    Impedance = pDto.Impedance,
                    WidthX    = pDto.WidthX,
                    WidthY    = pDto.WidthY,
                });
            }

            // Ground planes
            sim.GroundPlanes.Clear();
            foreach (var gDto in dto.GroundPlanes)
            {
                sim.GroundPlanes.Add(new GroundPlaneEntry
                {
                    BoardName = gDto.BoardName,
                    LayerName = gDto.LayerName,
                    IsGround  = gDto.IsGround,
                });
            }
        }
    }
}
