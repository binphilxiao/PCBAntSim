using System.Collections.Generic;

namespace AntennaSimulatorApp.Models
{
    // ── Top-level project envelope ────────────────────────────────────────────

    /// <summary>
    /// Plain DTO serialised to / from .antproj (JSON).
    /// Every collection is a <see cref="List{T}"/> so System.Text.Json round-trips cleanly.
    /// </summary>
    public class ProjectData
    {
        public int    FormatVersion  { get; set; } = 1;

        // ── Boards ────────────────────────────────────────────────────────────
        public BoardDto    CarrierBoard { get; set; } = new();
        public ModuleDto   Module       { get; set; } = new();
        public bool        HasModule    { get; set; } = true;

        // ── Copper / antenna shapes / vias ────────────────────────────────────
        public List<ManualShapeDto>   ManualShapes   { get; set; } = new();
        public List<AntennaParamsDto> DrawnAntennas  { get; set; } = new();
        public List<ViaDto>           Vias           { get; set; } = new();

        // ── Simulation ────────────────────────────────────────────────────────
        public SimSettingsDto SimSettings { get; set; } = new();
    }

    // ── Board DTOs ────────────────────────────────────────────────────────────

    public class BoardDto
    {
        public string Name        { get; set; } = "";
        public double Width       { get; set; }
        public double Height      { get; set; }
        public double Thickness   { get; set; }
        public int    LayerCount  { get; set; }
        public List<LayerDto> Layers { get; set; } = new();
    }

    public class ModuleDto : BoardDto
    {
        public double PositionX   { get; set; }
        public double PositionY   { get; set; }
        public double Rotation    { get; set; }
    }

    public class LayerDto
    {
        public string       Name              { get; set; } = "";
        public double       Thickness         { get; set; }
        public LayerType    Type              { get; set; }
        public LayerMaterial Material          { get; set; }
        public double       DielectricConstant { get; set; }
        public bool         IsVisible         { get; set; } = true;
        public string       GerberFilePath    { get; set; } = "";
        public double       GerberOffsetX     { get; set; }
        public double       GerberOffsetY     { get; set; }
        public string       GerberRotation    { get; set; } = "0";
    }

    // ── ManualShape DTO ───────────────────────────────────────────────────────

    public class ManualShapeDto
    {
        public string  Name       { get; set; } = "Shape";
        public bool    IsCarrier  { get; set; } = true;
        public string  LayerName  { get; set; } = "";
        public bool    ShowIn3D   { get; set; } = true;
        public List<VertexDto>         Vertices       { get; set; } = new();
        public List<List<VertexDto>>   MergedPolygons { get; set; } = new();
    }

    public class VertexDto
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    // ── Via DTO ──────────────────────────────────────────────────────────────

    public class ViaDto
    {
        public string  Name        { get; set; } = "Via";
        public bool    IsCarrier   { get; set; } = true;
        public string  FromLayer   { get; set; } = "";
        public string  ToLayer     { get; set; } = "";
        public double  DiameterMil { get; set; } = 10.0;
        public double  X           { get; set; }
        public double  Y           { get; set; }
        public bool    ShowIn3D    { get; set; } = true;
    }

    // ── AntennaParams DTO ─────────────────────────────────────────────────────

    public class AntennaParamsDto
    {
        public string Type           { get; set; } = "InvertedF";   // enum name
        public bool   IsCarrier      { get; set; } = true;
        public string LayerName      { get; set; } = "";
        public string Name           { get; set; } = "Antenna";
        public double OffsetX        { get; set; }
        public double OffsetY        { get; set; }

        // IFA common
        public double FreqGHz       { get; set; } = 2.4;
        public double LengthL       { get; set; } = 24.0;
        public double HeightH       { get; set; } = 7.0;
        public double FeedGap       { get; set; } = 3.0;

        // IFA widths
        public double ShortPinWidth  { get; set; } = 1.0;
        public double FeedPinWidth   { get; set; } = 1.0;
        public double MatchStubWidth { get; set; } = 1.0;
        public double RadiatorWidth  { get; set; } = 1.0;

        // MIFA
        public double MifaHeightH   { get; set; } = 3.9;
        public double MeanderHeight  { get; set; } = 2.85;
        public double MeanderPitch   { get; set; } = 5.0;

        // MIFA widths
        public double MifaShortWidth { get; set; } = 0.8;
        public double MifaFeedWidth  { get; set; } = 0.8;
        public double MifaHorizWidth { get; set; } = 0.5;
        public double MifaVertWidth  { get; set; } = 0.5;

        // PCB space (coordinate mapping)
        public double AvailWidth     { get; set; } = 15.0;
        public double AvailHeight    { get; set; } = 10.0;
        public double PcbOffsetX     { get; set; }
        public double PcbOffsetY     { get; set; }
        public double Clearance      { get; set; } = 0.254;

        // Custom antenna vertices
        public List<VertexDto> CustomVertices { get; set; } = new();
    }

    // ── SimSettings DTOs ──────────────────────────────────────────────────────

    public class SimSettingsDto
    {
        public List<FeedPointDto>      Ports        { get; set; } = new();
        public List<GroundPlaneDto>    GroundPlanes { get; set; } = new();
        public BoundaryDto             Boundary     { get; set; } = new();
        public MeshDto                 Mesh         { get; set; } = new();
        public FreqSweepDto            Sweep        { get; set; } = new();
        public SolverDto               Solver       { get; set; } = new();
    }

    public class FeedPointDto
    {
        public string Label      { get; set; } = "";
        public string FromLayer  { get; set; } = "";
        public string ToLayer    { get; set; } = "";
        public double X          { get; set; }
        public double Y          { get; set; }
        public string PortType   { get; set; } = "LumpedPort";
        public string IntegLine  { get; set; } = "ZPlus";
        public double Impedance  { get; set; } = 50.0;
        public double WidthX     { get; set; } = 0.5;
        public double WidthY     { get; set; } = 0.5;
    }

    public class GroundPlaneDto
    {
        public string BoardName  { get; set; } = "";
        public string LayerName  { get; set; } = "";
        public bool   IsGround   { get; set; }
    }

    public class BoundaryDto
    {
        public string BoundaryType   { get; set; } = "PML";
        public int    PmlLayers      { get; set; } = 8;
        public double SpacingXPlus   { get; set; } = 30;
        public double SpacingXMinus  { get; set; } = 30;
        public double SpacingYPlus   { get; set; } = 30;
        public double SpacingYMinus  { get; set; } = 30;
        public double SpacingZPlus   { get; set; } = 30;
        public double SpacingZMinus  { get; set; } = 30;
        public string SymmetryX      { get; set; } = "None";
        public string SymmetryY      { get; set; } = "None";
        public string SymmetryZ      { get; set; } = "None";
        public bool   ManualSimArea  { get; set; } = false;
        public double SimXMin        { get; set; } = -80;
        public double SimXMax        { get; set; } =  80;
        public double SimYMin        { get; set; } = -130;
        public double SimYMax        { get; set; } =  30;
        public double SimZMin        { get; set; } = -30;
        public double SimZMax        { get; set; } =  30;
    }

    public class MeshDto
    {
        public bool   AdaptiveMeshing    { get; set; } = true;
        public double MeshFreqGHz        { get; set; } = 2.4;
        public int    CellsPerWavelength { get; set; } = 20;
        public double MinStepMm          { get; set; } = 0.05;
        public int    MaxAdaptivePasses  { get; set; } = 10;
        public double ConvergenceDelta   { get; set; } = 0.01;
    }

    public class FreqSweepDto
    {
        public string SweepType { get; set; } = "Interpolating";
        public double StartGHz  { get; set; } = 1.0;
        public double StopGHz   { get; set; } = 6.0;
        public double StepGHz   { get; set; } = 0.01;
        public int    NumPoints { get; set; } = 501;
    }

    public class SolverDto
    {
        public int    MaxTimesteps { get; set; } = 200000;
        public double EndCriteria  { get; set; } = 1e-5;
    }
}
