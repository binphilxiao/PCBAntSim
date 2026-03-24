using System.Windows;

namespace AntennaSimulatorApp.Views
{
    public partial class ExportStepWindow : Window
    {
        public bool IncludeDefaultCopper { get; private set; } = true;
        public bool IncludeCopperShapes  { get; private set; } = true;
        public bool IncludeAntennas      { get; private set; } = true;
        public bool IncludeVias          { get; private set; } = true;

        public ExportStepWindow()
        {
            InitializeComponent();
        }

        private void ExportBtn_Click(object sender, RoutedEventArgs e)
        {
            IncludeDefaultCopper = ChkDefaultCopper.IsChecked == true;
            IncludeCopperShapes  = ChkCopperShapes.IsChecked  == true;
            IncludeAntennas      = ChkAntennas.IsChecked      == true;
            IncludeVias          = ChkVias.IsChecked           == true;
            DialogResult = true;
        }
    }
}
