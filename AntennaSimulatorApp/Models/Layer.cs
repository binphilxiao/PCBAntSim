using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace AntennaSimulatorApp.Models
{
    public enum LayerMaterial
    {
        Copper,
        FR4,
        Rogers4350B,
        Rogers4003C,
        PolyimidePI,
        Rogers5880,
        AluminumCCL,
        Air,
        Custom
    }

    public class MaterialInfo
    {
        public LayerMaterial Value { get; set; }
        public string DisplayName { get; set; } = "";
        public double DefaultDk { get; set; }

        private static readonly System.Collections.Generic.List<MaterialInfo> _all =
            new System.Collections.Generic.List<MaterialInfo>
        {
            new MaterialInfo { Value = LayerMaterial.Copper,      DisplayName = "Copper",             DefaultDk = 0.0  },
            new MaterialInfo { Value = LayerMaterial.FR4,         DisplayName = "FR-4",               DefaultDk = 4.3  },
            new MaterialInfo { Value = LayerMaterial.Rogers4350B, DisplayName = "Rogers 4350B",       DefaultDk = 3.48 },
            new MaterialInfo { Value = LayerMaterial.Rogers4003C, DisplayName = "Rogers 4003C",       DefaultDk = 3.55 },
            new MaterialInfo { Value = LayerMaterial.PolyimidePI, DisplayName = "Polyimide / PI",     DefaultDk = 3.4  },
            new MaterialInfo { Value = LayerMaterial.Rogers5880,  DisplayName = "Rogers 5880 (PTFE)", DefaultDk = 2.2  },
            new MaterialInfo { Value = LayerMaterial.AluminumCCL, DisplayName = "Aluminum CCL",       DefaultDk = 4.0  },
            new MaterialInfo { Value = LayerMaterial.Air,         DisplayName = "Air",                DefaultDk = 1.0  },
            new MaterialInfo { Value = LayerMaterial.Custom,      DisplayName = "Custom",             DefaultDk = 1.0  },
        };

        public static System.Collections.Generic.IReadOnlyList<MaterialInfo> All => _all;
    }

    public enum LayerType
    {
        Signal,
        Ground,
        Power,
        Dielectric,
        Mask
    }

    public class Layer : INotifyPropertyChanged
    {
        private string name = "";
        private double thickness;
        private LayerType type;
        private LayerMaterial material;
        private double dielectricConstant;
        private string gerberFilePath = "";
        private double gerberOffsetX;
        private double gerberOffsetY;
        private string gerberRotation = "0";
        private bool isVisible = true;

        public string Name
        {
            get => name;
            set { name = value; OnPropertyChanged(); }
        }

        public double Thickness
        {
            get => thickness;
            set { thickness = value; OnPropertyChanged(); }
        }

        public LayerType Type
        {
            get => type;
            set { type = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsConductive)); }
        }

        public LayerMaterial Material
        {
            get => material;
            set
            {
                material = value;
                var preset = MaterialInfo.All.FirstOrDefault(m => m.Value == value);
                if (preset != null && preset.DefaultDk > 0)
                    DielectricConstant = preset.DefaultDk;
                OnPropertyChanged();
            }
        }

        public double DielectricConstant
        {
            get => dielectricConstant;
            set { dielectricConstant = value; OnPropertyChanged(); }
        }

        /// <summary>Whether this layer is rendered in the 3D view.</summary>
        public bool IsVisible
        {
            get => isVisible;
            set { isVisible = value; OnPropertyChanged(); }
        }

        public string GerberFilePath
        {
            get => gerberFilePath;
            set
            {
                gerberFilePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GerberFileName));
                OnPropertyChanged(nameof(HasGerber));
            }
        }

        /// <summary>Gerber X shift applied before coordinate mapping (mm).</summary>
        public double GerberOffsetX
        {
            get => gerberOffsetX;
            set { gerberOffsetX = value; OnPropertyChanged(); }
        }

        /// <summary>Gerber Y shift applied before coordinate mapping (mm).</summary>
        public double GerberOffsetY
        {
            get => gerberOffsetY;
            set { gerberOffsetY = value; OnPropertyChanged(); }
        }

        /// <summary>Gerber rotation in degrees (0 / 90 / 180 / 270) around pattern centre.</summary>
        public string GerberRotation
        {
            get => gerberRotation;
            set { gerberRotation = value; OnPropertyChanged(); }
        }

        /// <summary>Just the filename (no path) for display in the grid.</summary>
        public string GerberFileName => string.IsNullOrEmpty(gerberFilePath)
            ? "" : Path.GetFileName(gerberFilePath);

        /// <summary>True when a Gerber file has been assigned to this layer.</summary>
        public bool HasGerber => !string.IsNullOrEmpty(gerberFilePath);

        /// <summary>True for Signal, Ground and Power layers (i.e. copper).</summary>
        public bool IsConductive => type == LayerType.Signal
                                 || type == LayerType.Ground
                                 || type == LayerType.Power;

        public Layer()
        {
            Name = "New Layer";
            Thickness = 0.035; // default 1oz copper
            Type = LayerType.Signal;
            Material = LayerMaterial.Copper;
            DielectricConstant = 1.0;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class PcbStackup : INotifyPropertyChanged
    {
        public ObservableCollection<Layer> Layers { get; set; } = new ObservableCollection<Layer>();
        
        // Backing field for total thickness if needed, but we calculate it dynamically usually.
        // However, user wants TotalThickness to update when layers change.
        
        public double TotalThickness
        {
            get 
            {
                double sum = 0;
                foreach(var layer in Layers) sum += layer.Thickness;
                return sum;
            }
        }

        public PcbStackup()
        {
            Layers.CollectionChanged += (s, e) => 
            {
                OnPropertyChanged(nameof(TotalThickness));
                if (e.NewItems != null) RegisterLayerEvents(e.NewItems);
                if (e.OldItems != null) UnregisterLayerEvents(e.OldItems);
            };
        }

        private void RegisterLayerEvents(System.Collections.IList items)
        {
            if (items == null) return;
            foreach (Layer layer in items)
            {
                layer.PropertyChanged += Layer_PropertyChanged;
            }
        }

        private void UnregisterLayerEvents(System.Collections.IList items)
        {
            if (items == null) return;
            foreach (Layer layer in items)
            {
                layer.PropertyChanged -= Layer_PropertyChanged;
            }
        }

        private bool _isUpdating = false;

        private void Layer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Layer.Thickness))
            {
                OnPropertyChanged(nameof(TotalThickness));

                // Handle Symmetric Thickness Update
                if (!_isUpdating && sender is Layer changedLayer)
                {
                    _isUpdating = true;
                    try
                    {
                        int index = Layers.IndexOf(changedLayer);
                        int count = Layers.Count;
                        // Find symmetric index: 0 <-> N-1, 1 <-> N-2
                        int symmetricIndex = count - 1 - index;

                        if (symmetricIndex != index && symmetricIndex >= 0 && symmetricIndex < count)
                        {
                            var symmetricLayer = Layers[symmetricIndex];
                            // Only update if they are supposed to be symmetric (e.g., both Dielectric or both Copper)
                            // Usually stackups are fully symmetric in structure
                            if (Math.Abs(symmetricLayer.Thickness - changedLayer.Thickness) > 0.0001)
                            {
                                symmetricLayer.Thickness = changedLayer.Thickness;
                            }
                        }
                    }
                    finally
                    {
                        _isUpdating = false;
                    }
                }
            }

            // Sync substrate material across all non-conductive layers in this stackup.
            // One board = one substrate, so changing any dielectric layer propagates to the rest.
            // Conductive layers (Signal / Ground / Power) are intentionally excluded.
            if (e.PropertyName == nameof(Layer.Material) && !_isUpdating && sender is Layer src)
            {
                if (src.IsConductive) return;   // ignore copper-layer material changes

                _isUpdating = true;
                try
                {
                    foreach (var layer in Layers)
                    {
                        if (layer == src || layer.IsConductive) continue;
                        if (layer.Material != src.Material)
                            layer.Material = src.Material;
                    }
                }
                finally
                {
                    _isUpdating = false;
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}