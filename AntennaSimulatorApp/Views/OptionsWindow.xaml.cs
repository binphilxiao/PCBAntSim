using System.Windows;
using AntennaSimulatorApp.Services;
using Microsoft.Win32;

namespace AntennaSimulatorApp.Views
{
    public partial class OptionsWindow : Window
    {
        public OptionsWindow()
        {
            InitializeComponent();
            var s = AppSettings.Instance;
            OpenEmsPathBox.Text     = s.OpenEmsPath;
            PythonPathBox.Text      = s.PythonPath;
            SimDataScratchBox.Text  = s.SimDataScratchRoot;
        }

        private void BrowseOpenEms_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select openEMS installation folder (containing openEMS.exe)"
            };
            if (dlg.ShowDialog(this) == true)
                OpenEmsPathBox.Text = dlg.FolderName;
        }

        private void BrowsePython_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select Python executable",
                Filter = "Python|python.exe|All files|*.*"
            };
            if (dlg.ShowDialog(this) == true)
                PythonPathBox.Text = dlg.FileName;
        }

        private void BrowseSimDataScratch_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select local scratch folder for openEMS sim_data (e.g. C:\\PCBAntSimRuns)"
            };
            if (dlg.ShowDialog(this) == true)
                SimDataScratchBox.Text = dlg.FolderName;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            var s = AppSettings.Instance;
            s.OpenEmsPath        = OpenEmsPathBox.Text.Trim();
            s.PythonPath         = PythonPathBox.Text.Trim();
            s.SimDataScratchRoot = SimDataScratchBox.Text.Trim();
            s.Save();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
