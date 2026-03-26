using System.Windows;
using AntennaSimulatorApp.Models;

namespace AntennaSimulatorApp.Views
{
    public partial class SimTypeDialog : Window
    {
        public AnalysisType SelectedType { get; private set; } = AnalysisType.S11Only;

        public SimTypeDialog()
        {
            InitializeComponent();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (RbFarField.IsChecked == true)
                SelectedType = AnalysisType.FarField;
            else if (RbBoth.IsChecked == true)
                SelectedType = AnalysisType.Both;
            else
                SelectedType = AnalysisType.S11Only;

            DialogResult = true;
        }
    }
}
