using System.Windows;

namespace AntennaSimulatorApp.Views
{
    public partial class ExportOpenEmsWindow : Window
    {
        public bool IncludeDefaultCopper { get; private set; } = true;
        public bool IncludeCopperShapes  { get; private set; } = true;
        public bool IncludeAntennas      { get; private set; } = true;
        public bool IncludeVias          { get; private set; } = true;

        public ExportOpenEmsWindow()
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

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
