using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AntennaSimulatorApp.Models
{
    /// <summary>
    /// A solder joint connecting the module board's bottom layer (top surface)
    /// to the carrier board's top layer (bottom surface).
    /// Position is in mm, diameter is stored in mil for user convenience.
    /// </summary>
    public class SolderJoint : INotifyPropertyChanged
    {
        private string _name = "SJ";
        private double _diameterMil = 20.0;
        private double _x;
        private double _y;
        private bool _showIn3D = true;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        /// <summary>Solder joint diameter in mil.</summary>
        public double DiameterMil
        {
            get => _diameterMil;
            set { _diameterMil = value; OnPropertyChanged(); OnPropertyChanged(nameof(DiameterMm)); }
        }

        /// <summary>Solder joint diameter in mm (computed).</summary>
        public double DiameterMm => DiameterMil * 0.0254;

        /// <summary>Centre X position in mm.</summary>
        public double X
        {
            get => _x;
            set { _x = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        /// <summary>Centre Y position in mm.</summary>
        public double Y
        {
            get => _y;
            set { _y = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        /// <summary>Whether to render this solder joint in the 3D viewport.</summary>
        public bool ShowIn3D
        {
            get => _showIn3D;
            set { _showIn3D = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        public string DisplayName =>
            $"{Name}  ⌀{DiameterMil} mil  ({X:F2}, {Y:F2})" +
            (!ShowIn3D ? "  [hidden]" : "");

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
