using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AntennaSimulatorApp.Models
{
    /// <summary>
    /// A single plated through-hole via connecting two conductive layers.
    /// Position is in mm, diameter is stored in mil for user convenience.
    /// </summary>
    public class Via : INotifyPropertyChanged
    {
        private string _name = "Via";
        private bool _isCarrier = true;
        private string _fromLayer = "";
        private string _toLayer = "";
        private double _diameterMil = 10.0;
        private double _x;
        private double _y;
        private bool _showIn3D = true;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        /// <summary>True = on carrier board; False = on module board.</summary>
        public bool IsCarrier
        {
            get => _isCarrier;
            set { _isCarrier = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        /// <summary>Starting layer name.</summary>
        public string FromLayer
        {
            get => _fromLayer;
            set { _fromLayer = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        /// <summary>Ending layer name.</summary>
        public string ToLayer
        {
            get => _toLayer;
            set { _toLayer = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        /// <summary>Via diameter in mil.</summary>
        public double DiameterMil
        {
            get => _diameterMil;
            set { _diameterMil = value; OnPropertyChanged(); OnPropertyChanged(nameof(DiameterMm)); }
        }

        /// <summary>Via diameter in mm (computed).</summary>
        public double DiameterMm => DiameterMil * 0.0254;

        /// <summary>Via centre X position in mm.</summary>
        public double X
        {
            get => _x;
            set { _x = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        /// <summary>Via centre Y position in mm.</summary>
        public double Y
        {
            get => _y;
            set { _y = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        /// <summary>Whether to render this via in the 3D viewport.</summary>
        public bool ShowIn3D
        {
            get => _showIn3D;
            set { _showIn3D = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
        }

        public string DisplayName =>
            $"{Name}  [{(IsCarrier ? "Carrier" : "Module")} {FromLayer}→{ToLayer}]  ⌀{DiameterMil} mil  ({X:F2}, {Y:F2})" +
            (!ShowIn3D ? "  [hidden]" : "");

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
