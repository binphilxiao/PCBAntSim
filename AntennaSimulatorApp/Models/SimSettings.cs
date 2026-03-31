using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AntennaSimulatorApp.Models
{
    // ── Enumerations ─────────────────────────────────────────────────────────

    public enum PortType        { LumpedPort, WaveguidePort }
    public enum IntegLine       { XPlus, XMinus, YPlus, YMinus, ZPlus, ZMinus }
    public enum BoundaryType    { PML, OpenAddSpace }
    public enum SymmetryType    { None, PEC, PMC }
    public enum SweepType       { Fast, Discrete, Interpolating }
    public enum AnalysisType    { S11Only, FarField, Both }

    // ── Feed point / port ─────────────────────────────────────────────────────

    public class FeedPoint : INotifyPropertyChanged
    {
        private string   _fromLayer  = "";
        private string   _toLayer    = "";
        private double   _x          = 0.0;
        private double   _y          = 0.0;
        private PortType _portType   = PortType.LumpedPort;
        private IntegLine _integLine = IntegLine.ZPlus;
        private double   _impedance  = 50.0;
        private double   _widthX     = 0.5;
        private double   _widthY     = 0.5;
        private string   _label      = "Port 1";

        public string    Label       { get => _label;     set { _label     = value; OnPropertyChanged(); } }
        /// <summary>Conductive layer that forms the + terminal (e.g. feed trace).</summary>
        public string    FromLayer   { get => _fromLayer; set { _fromLayer = value; OnPropertyChanged(); } }
        /// <summary>Conductive layer that forms the − terminal (e.g. ground plane).</summary>
        public string    ToLayer     { get => _toLayer;   set { _toLayer   = value; OnPropertyChanged(); } }
        /// <summary>Feed point X coordinate (mm, in board coordinate system).</summary>
        public double    X           { get => _x;         set { _x         = value; OnPropertyChanged(); } }
        /// <summary>Feed point Y coordinate (mm).</summary>
        public double    Y           { get => _y;         set { _y         = value; OnPropertyChanged(); } }
        public PortType  PortType    { get => _portType;  set { _portType  = value; OnPropertyChanged(); } }
        /// <summary>Integration line direction for lumped port voltage definition.</summary>
        public IntegLine IntegLine   { get => _integLine; set { _integLine = value; OnPropertyChanged(); } }
        /// <summary>Reference impedance in Ohm (default 50).</summary>
        public double    Impedance   { get => _impedance; set { _impedance = value; OnPropertyChanged(); } }
        /// <summary>Port extent in X direction (mm, default 0.5).</summary>
        public double    WidthX      { get => _widthX;    set { _widthX    = value; OnPropertyChanged(); } }
        /// <summary>Port extent in Y direction (mm, default 0.5).</summary>
        public double    WidthY      { get => _widthY;    set { _widthY    = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Ground plane selection ────────────────────────────────────────────────

    /// <summary>Tracks whether a named layer is designated as a ground reference plane.</summary>
    public class GroundPlaneEntry : INotifyPropertyChanged
    {
        private bool _isGround;

        public string BoardName  { get; set; } = "";
        public string LayerName  { get; set; } = "";

        /// <summary>Display string shown in the list: "BoardName – LayerName".</summary>
        public string DisplayName => $"{BoardName} – {LayerName}";

        public bool IsGround
        {
            get => _isGround;
            set { _isGround = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Boundary conditions ───────────────────────────────────────────────────

    public class BoundarySettings : INotifyPropertyChanged
    {
        private BoundaryType _type    = BoundaryType.OpenAddSpace;
        private int    _pmlLayers     = 8;
        private double _spacingXp     = 30.0;
        private double _spacingXm     = 30.0;
        private double _spacingYp     = 30.0;
        private double _spacingYm     = 30.0;
        private double _spacingZp     = 30.0;
        private double _spacingZm     = 30.0;
        private SymmetryType _symX    = SymmetryType.None;
        private SymmetryType _symY    = SymmetryType.None;
        private SymmetryType _symZ    = SymmetryType.None;
        private bool   _manualSimArea = false;
        private double _simXMin       = -80;
        private double _simXMax       =  80;
        private double _simYMin       = -130;
        private double _simYMax       =  30;
        private double _simZMin       = -30;
        private double _simZMax       =  30;

        public BoundaryType  Type       { get => _type;       set { _type       = value; OnPropertyChanged(); } }
        /// <summary>Number of PML absorbing layers (typically 4–16, default 8).</summary>
        public int    PmlLayers      { get => _pmlLayers;  set { _pmlLayers  = value; OnPropertyChanged(); } }
        /// <summary>Extra air-box padding on each side (mm). Only used for OpenAddSpace mode.</summary>
        public double SpacingXPlus     { get => _spacingXp;   set { _spacingXp  = value; OnPropertyChanged(); } }
        public double SpacingXMinus    { get => _spacingXm;   set { _spacingXm  = value; OnPropertyChanged(); } }
        public double SpacingYPlus     { get => _spacingYp;   set { _spacingYp  = value; OnPropertyChanged(); } }
        public double SpacingYMinus    { get => _spacingYm;   set { _spacingYm  = value; OnPropertyChanged(); } }
        public double SpacingZPlus     { get => _spacingZp;   set { _spacingZp  = value; OnPropertyChanged(); } }
        public double SpacingZMinus    { get => _spacingZm;   set { _spacingZm  = value; OnPropertyChanged(); } }
        public SymmetryType SymmetryX  { get => _symX;        set { _symX       = value; OnPropertyChanged(); } }
        public SymmetryType SymmetryY  { get => _symY;        set { _symY       = value; OnPropertyChanged(); } }
        public SymmetryType SymmetryZ  { get => _symZ;        set { _symZ       = value; OnPropertyChanged(); } }

        /// <summary>When true, use manually specified sim area; when false, auto-compute from board geometry + padding.</summary>
        public bool   ManualSimArea    { get => _manualSimArea; set { _manualSimArea = value; OnPropertyChanged(); } }
        public double SimXMin          { get => _simXMin;     set { _simXMin    = value; OnPropertyChanged(); } }
        public double SimXMax          { get => _simXMax;     set { _simXMax    = value; OnPropertyChanged(); } }
        public double SimYMin          { get => _simYMin;     set { _simYMin    = value; OnPropertyChanged(); } }
        public double SimYMax          { get => _simYMax;     set { _simYMax    = value; OnPropertyChanged(); } }
        public double SimZMin          { get => _simZMin;     set { _simZMin    = value; OnPropertyChanged(); } }
        public double SimZMax          { get => _simZMax;     set { _simZMax    = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Mesh settings ─────────────────────────────────────────────────────────

    public class MeshSettings : INotifyPropertyChanged
    {
        private bool   _adaptive         = true;
        private double _meshFreqGHz      = 2.4;
        private int    _cellsPerWL       = 20;
        private double _minStepMm        = 0.05;
        private int    _maxPasses        = 10;
        private double _convergenceDelta = 0.01;

        public bool   AdaptiveMeshing   { get => _adaptive;         set { _adaptive         = value; OnPropertyChanged(); } }
        public double MeshFreqGHz       { get => _meshFreqGHz;      set { _meshFreqGHz      = value; OnPropertyChanged(); } }
        public int    CellsPerWavelength{ get => _cellsPerWL;       set { _cellsPerWL       = value; OnPropertyChanged(); } }
        public double MinStepMm         { get => _minStepMm;        set { _minStepMm        = value; OnPropertyChanged(); } }
        public int    MaxAdaptivePasses { get => _maxPasses;        set { _maxPasses        = value; OnPropertyChanged(); } }
        /// <summary>Convergence delta for S-parameter between passes (e.g. 0.01 → 1%).</summary>
        public double ConvergenceDelta  { get => _convergenceDelta; set { _convergenceDelta = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Frequency sweep ───────────────────────────────────────────────────────

    public class FreqSweepSettings : INotifyPropertyChanged
    {
        private SweepType _type     = SweepType.Interpolating;
        private double _startGHz    = 1.0;
        private double _stopGHz     = 6.0;
        private double _stepGHz     = 0.01;
        private int    _numPoints   = 501;

        public SweepType Type       { get => _type;       set { _type       = value; OnPropertyChanged(); } }
        public double   StartGHz    { get => _startGHz;   set { _startGHz   = value; OnPropertyChanged(); } }
        public double   StopGHz     { get => _stopGHz;    set { _stopGHz    = value; OnPropertyChanged(); } }
        /// <summary>Step size for Discrete sweep (GHz).</summary>
        public double   StepGHz     { get => _stepGHz;    set { _stepGHz    = value; OnPropertyChanged(); } }
        /// <summary>Number of interpolation points for Interpolating/Fast sweep.</summary>
        public int      NumPoints   { get => _numPoints;  set { _numPoints  = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Solver settings ───────────────────────────────────────────────────

    public class SolverSettings : INotifyPropertyChanged
    {
        private int    _maxTimesteps = 200000;
        private double _endCriteria  = 1e-5;
        private int    _numThreads   = 0;  // 0 = auto (all cores)

        public int    MaxTimesteps { get => _maxTimesteps; set { _maxTimesteps = value; OnPropertyChanged(); } }
        public double EndCriteria  { get => _endCriteria;  set { _endCriteria  = value; OnPropertyChanged(); } }
        /// <summary>Number of OpenMP threads for FDTD engine. 0 = use all available cores.</summary>
        public int    NumThreads   { get => _numThreads;   set { _numThreads   = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Top-level container ───────────────────────────────────────────────────

    public class SimSettings : INotifyPropertyChanged
    {
        public ObservableCollection<FeedPoint>      Ports         { get; } = new();
        public ObservableCollection<GroundPlaneEntry> GroundPlanes { get; } = new();
        public BoundarySettings                     Boundary      { get; } = new();
        public MeshSettings                         Mesh          { get; } = new();
        public FreqSweepSettings                    Sweep         { get; } = new();
        public SolverSettings                       Solver        { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
