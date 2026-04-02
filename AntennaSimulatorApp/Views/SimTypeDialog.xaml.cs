using System.Windows;
using AntennaSimulatorApp.Models;

namespace AntennaSimulatorApp.Views
{
    public partial class SimTypeDialog : Window
    {
        public AnalysisType SelectedType { get; private set; } = AnalysisType.S11Only;
        public bool DumpSurfaceCurrent { get; private set; }
        public bool DumpEField { get; private set; }
        public bool DumpHField { get; private set; }
        public bool OverlayOutline { get; private set; } = true;

        public SimTypeDialog()
        {
            InitializeComponent();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            bool s11 = ChkS11.IsChecked == true;
            bool ff  = ChkFarField.IsChecked == true;

            if (!s11 && !ff)
            {
                MessageBox.Show("Please select at least S11 or Far-Field.",
                    "No Analysis Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (s11 && ff)
                SelectedType = AnalysisType.Both;
            else if (ff)
                SelectedType = AnalysisType.FarField;
            else
                SelectedType = AnalysisType.S11Only;

            DumpSurfaceCurrent = ChkDumpJ.IsChecked == true;
            DumpEField         = ChkDumpE.IsChecked == true;
            DumpHField         = ChkDumpH.IsChecked == true;
            OverlayOutline     = ChkOverlayOutline.IsChecked == true;

            DialogResult = true;
        }
    }
}
