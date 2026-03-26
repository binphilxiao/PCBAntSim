using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.Windows.Media.Media3D;
using AntennaSimulatorApp.Models;
using AntennaSimulatorApp.Views;   // AntennaParams lives in DrawAntennaWindow.xaml.cs

namespace AntennaSimulatorApp.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private BoardConfig carrierBoard = null!;
        private ModuleConfig moduleConfig = null!;

        public BoardConfig CarrierBoard
        {
            get => carrierBoard;
            set { carrierBoard = value; OnPropertyChanged(); }
        }

        public ModuleConfig Module
        {
            get => moduleConfig;
            set { moduleConfig = value; OnPropertyChanged(); }
        }

        private bool _hasModule = true;
        public bool HasModule
        {
            get => _hasModule;
            set { _hasModule = value; OnPropertyChanged(); }
        }

        public ObservableCollection<int> AvailableLayerCounts { get; } = new ObservableCollection<int> { 2, 4, 6, 8, 10, 12 };

        /// <summary>Manually drawn copper shapes added via Draw → Copper Shapes…</summary>
        public ObservableCollection<AntennaSimulatorApp.Models.ManualShape> ManualShapes { get; }
            = new ObservableCollection<AntennaSimulatorApp.Models.ManualShape>();

        /// <summary>Parametric antenna traces added via Draw → Draw Antenna…</summary>
        public ObservableCollection<AntennaParams> DrawnAntennas { get; }
            = new ObservableCollection<AntennaParams>();

        /// <summary>Vias added via Draw → Draw Via…</summary>
        public ObservableCollection<AntennaSimulatorApp.Models.Via> Vias { get; }
            = new ObservableCollection<AntennaSimulatorApp.Models.Via>();

        /// <summary>Solder joints connecting module bottom to carrier top, added via Draw → Draw Solder Joint…</summary>
        public ObservableCollection<AntennaSimulatorApp.Models.SolderJoint> SolderJoints { get; }
            = new ObservableCollection<AntennaSimulatorApp.Models.SolderJoint>();

        /// <summary>Simulation settings (ports, boundary, mesh, sweep, ground planes).</summary>
        public AntennaSimulatorApp.Models.SimSettings SimSettings { get; }
            = new AntennaSimulatorApp.Models.SimSettings();

        // ── 3D computed centers ──────────────────────────────────────────────
        // Coordinate convention:
        //   X=0  → carrier right edge
        //   Y=0  → carrier vertical centre
        //   Z=0  → carrier top surface (module sits above this)

        /// <summary>Geometric centre of the carrier board box in 3D scene.</summary>
        public Point3D CarrierCenter3D => new Point3D(
            -CarrierBoard.Width  / 2.0,    // right edge at X=0, left edge at X=-Width
             0,                             // centred vertically
            -CarrierBoard.Thickness / 2.0   // top face at Z=0
        );

        /// <summary>Geometric centre of the module box in 3D scene.
        /// PositionX=0 / PositionY=0  →  flush against carrier right edge, centred.
        /// PositionX>0 moves module LEFT (inward); PositionY shifts vertically.
        /// </summary>
        public Point3D ModuleCenter3D => new Point3D(
            -Module.PositionX - Module.Width / 2.0,   // right edge = X=0 when PositionX=0
             Module.PositionY,                          // Y offset from carrier centre
             Module.Thickness / 2.0                     // sits on top of carrier
        );

        public MainViewModel()
        {
            CarrierBoard = new BoardConfig("Main PCB Carrier")
            {
                Width    = 100,
                Height   = 80,
                Thickness = 1.6,
                LayerCount = 4
            };

            Module = new ModuleConfig("RF Module")
            {
                Width    = 20,
                Height   = 15,
                Thickness = 0.8,
                LayerCount = 6
                // PositionX = 0, PositionY = 0 already set in ModuleConfig ctor
            };

            // Propagate dimension changes → refresh 3D centres
            CarrierBoard.PropertyChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(CarrierCenter3D));
                OnPropertyChanged(nameof(ModuleCenter3D));
            };

            Module.PropertyChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(ModuleCenter3D));
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}