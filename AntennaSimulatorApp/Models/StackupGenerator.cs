using System;
using System.Collections.Generic;
using System.Linq;

namespace AntennaSimulatorApp.Models
{
    public static class StackupGenerator
    {
        public static void GenerateStackup(PcbStackup stackup, int layerCount, double totalThickness)
        {
            stackup.Layers.Clear();
            
            // Define conductive layers based on user spec
            var conductiveTypes = GetLayerTypes(layerCount);
            
            double copperThick = 0.035; // 35um standard 1oz
            int dielectricCount = layerCount - 1;
            
            // Core logic: All prepreg/dielectric layers have a default thickness (e.g., 0.1mm or 0.2mm)
            // The THICKEST part (Core) is usually in the middle. 
            // We calculate standard prepreg thickness, then dump the rest into the CENTER core.

            double defaultDielectricThick = 0.2; // 0.2mm (approx 8mil) standard prepreg
            
            // Calculate total fixed height (Copper + Standard Prepregs)
            double fixedHeight = (layerCount * copperThick); 
            
            // If we have > 2 layers, we have multiple dielectrics. The middle one takes the slack.
            // Dielectric indices: 0, 1, ... dielectricCount-1
            // Middle index: (dielectricCount - 1) / 2
            
            // For 2 layers, there is only 1 dielectric (index 0). 
            // For > 2 layers, middle one takes remaining, others take fixed 0.2
            
            int centerDielectricIndex = 0; 
            if (dielectricCount > 1)
            {
                centerDielectricIndex = (dielectricCount - 1) / 2; 
                // Add standard thickness for all NON-center dielectrics
                fixedHeight += (dielectricCount - 1) * defaultDielectricThick;
            }
            else
            {
                // only 1 dielectric (2 layer board) - no fixed prepregs added, fixedHeight is just copper
                centerDielectricIndex = 0;
            }

            double remainingForCore = totalThickness - fixedHeight;
            if (remainingForCore < 0.1) remainingForCore = 0.1; // Minimum core thickness

            for (int i = 0; i < layerCount; i++)
            {
                // Add Conductive Layer
                var layerType = conductiveTypes[i];
                string name = GetLayerName(i, layerCount, layerType);
                
                stackup.Layers.Add(new Layer 
                { 
                    Name = name, 
                    Thickness = copperThick, 
                    Type = layerType,
                    Material = layerType == LayerType.Dielectric ? LayerMaterial.FR4 : LayerMaterial.Copper 
                });

                // Add Dielectric Layer (if not the last conductive layer)
                if (i < layerCount - 1)
                {
                    double thickness = defaultDielectricThick;
                    // Name reflects the two copper layers it separates: Sub{upper}-{lower}
                    string dName = $"Sub{i + 1}-{i + 2}";

                    if (i == centerDielectricIndex)
                    {
                        thickness = remainingForCore;
                    }

                    stackup.Layers.Add(new Layer 
                    { 
                        Name = dName, 
                        Thickness = thickness, 
                        Type = LayerType.Dielectric,
                        Material = LayerMaterial.FR4,
                        DielectricConstant = 4.3 
                    });
                }
            }
            
            // Explicitly notify that the collection has changed if binding doesn't pick it up automatically
            // (Though ObservableCollection usually handles Add/Clear)
        }

        private static List<LayerType> GetLayerTypes(int count)
        {
            // G=Ground, P=Power, S=Signal
            // 2 layers: S - G (User said G2(Bottom), usually standard is S-S or S-G. Let's stick to user request S-G)
            // User: 2层 S1 (Top) - G2 (Bottom)
            if (count == 2) return new List<LayerType> { LayerType.Signal, LayerType.Ground };

            // 4 layers: S1 - G2 - P3 - S4
            if (count == 4) return new List<LayerType> { LayerType.Signal, LayerType.Ground, LayerType.Power, LayerType.Signal };

            // 6 layers: S1 - G2 - S2 - S3 - P5 - S6
            // Interpretation: L1=S, L2=G, L3=S, L4=S, L5=P, L6=S
            if (count == 6) return new List<LayerType> { LayerType.Signal, LayerType.Ground, LayerType.Signal, LayerType.Signal, LayerType.Power, LayerType.Signal };

            // 8 layers: S1 - G2 - S2 - G3 - P4 - S3 - G5 - S4
            // Interpretation: L1=S, L2=G, L3=S, L4=G, L5=P, L6=S, L7=G, L8=S
            if (count == 8) return new List<LayerType> { LayerType.Signal, LayerType.Ground, LayerType.Signal, LayerType.Ground, LayerType.Power, LayerType.Signal, LayerType.Ground, LayerType.Signal };

            // 10 layers: S1 - G2 - S2 - S3 - G4 - P5 - S4 - S5 - G6 - S6
            // L1=S, L2=G, L3=S, L4=S, L5=G, L6=P, L7=S, L8=S, L9=G, L10=S
            if (count == 10) return new List<LayerType> { LayerType.Signal, LayerType.Ground, LayerType.Signal, LayerType.Signal, LayerType.Ground, LayerType.Power, LayerType.Signal, LayerType.Signal, LayerType.Ground, LayerType.Signal };

            // 12 layers: S1 - G2 - S2 - G3 - S3 - G4 - P5 - G6 - S4 - G7 - S5 - S6
            // L1=S, L2=G, L3=S, L4=G, L5=S, L6=G, L7=P, L8=G, L9=S, L10=G, L11=S, L12=S
            if (count == 12) return new List<LayerType> { LayerType.Signal, LayerType.Ground, LayerType.Signal, LayerType.Ground, LayerType.Signal, LayerType.Ground, LayerType.Power, LayerType.Ground, LayerType.Signal, LayerType.Ground, LayerType.Signal, LayerType.Signal };

            // Default
            return Enumerable.Repeat(LayerType.Signal, count).ToList();
        }

        private static string GetLayerName(int index, int total, LayerType type)
        {
            if (index == 0) return "TOP";
            if (index == total - 1) return "BOTTOM";
            return $"L{index + 1}";
        }
    }
}