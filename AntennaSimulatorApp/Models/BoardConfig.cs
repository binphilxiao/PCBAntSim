using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AntennaSimulatorApp.Models
{
    public class BoardConfig : INotifyPropertyChanged
    {
        private double width;
        private double height;
        private double thickness;
        private PcbStackup stackup = null!;
        private int layerCount;

        public string Name { get; set; }

        public int LayerCount
        {
            get => layerCount;
            set
            {
                if (value != layerCount)
                {
                    layerCount = value;
                    OnPropertyChanged();
                    UpdateStackup();
                }
            }
        }

        public double Width
        {
            get => width;
            set { width = value; OnPropertyChanged(); }
        }

        public double Height
        {
            get => height;
            set { height = value; OnPropertyChanged(); }
        }

        public double Thickness
        {
            get => thickness;
            set 
            { 
                if (value != thickness)
                {
                    thickness = value; 
                    OnPropertyChanged();
                    // Only adjust Core (Center) layer — do NOT regenerate the whole stackup
                    // so that user-modified prepreg thicknesses are preserved.
                    UpdateCoreThicknessOnly();
                }
            }
        }

        public PcbStackup Stackup
        {
            get => stackup;
            set { stackup = value; OnPropertyChanged(); }
        }

        private bool _isGenerating = false;

        public BoardConfig(string name)
        {
            Name = name;
            Width = 100;
            Height = 100;
            // Initialize fields directly to avoid triggering update before constructor done
            thickness = 1.6; 
            layerCount = 2;  
            Stackup = new PcbStackup();
            
            // Initialize with default stackup
            UpdateStackup();

            // Listen to stackup changes to update our local Thickness property if user edits layers
            Stackup.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(PcbStackup.TotalThickness))
                {
                   // If we are currently generating the stackup based on a Thickness change, 
                   // we DO NOT want to update the Thickness property again based on the partial layer additions.
                   if (_isGenerating) return;

                   // Otherwise, if user edited a layer manually, update the total thickness
                   if (Math.Abs(thickness - Stackup.TotalThickness) > 0.001)
                   {
                       thickness = Stackup.TotalThickness;
                       OnPropertyChanged(nameof(Thickness));
                   }
                }
            };
        }

        // Full regeneration — called only when LayerCount changes.
        private void UpdateStackup()
        {
            _isGenerating = true;
            try
            {
                StackupGenerator.GenerateStackup(Stackup, LayerCount, Thickness);
            }
            finally
            {
                _isGenerating = false;
                // If min-thickness clamping caused a mismatch, sync the Thickness field.
                if (Math.Abs(thickness - Stackup.TotalThickness) > 0.001)
                {
                    thickness = Stackup.TotalThickness;
                    OnPropertyChanged(nameof(Thickness));
                }
            }
        }

        // Lightweight update — called when only Thickness changes.
        // Finds the middle dielectric layer and adjusts it to satisfy the target total,
        // leaving every other layer (including user-edited prepregs) untouched.
        private void UpdateCoreThicknessOnly()
        {
            // If the stackup hasn't been built yet, fall back to full generation.
            if (Stackup == null || Stackup.Layers.Count == 0)
            {
                UpdateStackup();
                return;
            }

            // Collect all dielectric layers in order.
            var dielectrics = new System.Collections.Generic.List<Layer>();
            foreach (var layer in Stackup.Layers)
            {
                if (layer.Type == LayerType.Dielectric)
                    dielectrics.Add(layer);
            }

            if (dielectrics.Count == 0)
            {
                UpdateStackup();
                return;
            }

            // Pick the middle dielectric layer (center core).
            // For 1 dielectric  → index 0
            // For 3 dielectrics → index 1
            // For 5 dielectrics → index 2   etc.
            int centerIndex = (dielectrics.Count - 1) / 2;
            Layer coreLayer = dielectrics[centerIndex];

            // Sum all layers except the core (exclude Mask layers from thickness calc).
            double otherLayersSum = 0;
            foreach (var layer in Stackup.Layers)
            {
                if (layer != coreLayer && layer.Type != LayerType.Mask)
                    otherLayersSum += layer.Thickness;
            }

            double newCoreThickness = thickness - otherLayersSum;
            // Clamp: core cannot go below 0.05 mm.
            if (newCoreThickness < 0.05) newCoreThickness = 0.05;

            _isGenerating = true;
            try
            {
                coreLayer.Thickness = newCoreThickness;
            }
            finally
            {
                _isGenerating = false;
                // Sync thickness field in case clamping changed the actual total.
                if (Math.Abs(thickness - Stackup.TotalThickness) > 0.001)
                {
                    thickness = Stackup.TotalThickness;
                    OnPropertyChanged(nameof(Thickness));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class ModuleConfig : BoardConfig
    {
        private double positionX;
        private double positionY;
        private double rotation;
        
        // We need access to parent carrier to auto-position
        // But ModuleConfig is standalone. We can set defaults in ViewModel or passing carrier dimensions?
        // Let's just set reasonable defaults in constructor for now, but 
        // to implement "Default placed at right edge centered", we should do this in ViewModel initialization.

        public double PositionX
        {
            get => positionX;
            set { positionX = value; OnPropertyChanged(); }
        }

        public double PositionY
        {
            get => positionY;
            set { positionY = value; OnPropertyChanged(); }
        }

        public double Rotation
        {
            get => rotation;
            set { rotation = value; OnPropertyChanged(); }
        }

        public ModuleConfig(string name) : base(name)
        {
            Width = 20;
            Height = 15;
            Thickness = 0.8;
            // (0,0) = flush against carrier right edge, centered vertically
            PositionX = 0;
            PositionY = 0;
            // Base constructor calls UpdateStackup, so we just override default properties
            LayerCount = 6; // Default module layer count - User asked for 6
        }
    }
}