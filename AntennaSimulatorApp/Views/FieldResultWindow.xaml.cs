using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AntennaSimulatorApp.Views
{
    public partial class FieldResultWindow : Window
    {
        private readonly string _resultsDir;

        public FieldResultWindow(string resultsDir)
        {
            InitializeComponent();
            _resultsDir = resultsDir;
            LoadImages();
        }

        public void Refresh() => LoadImages();

        private void LoadImages()
        {
            bool anyLoaded = false;
            anyLoaded |= TryLoadImage("Jf_surface.png", ImgJ, TabJ);
            anyLoaded |= TryLoadImage("Ef_surface.png", ImgE, TabE);
            anyLoaded |= TryLoadImage("Hf_surface.png", ImgH, TabH);

            if (!anyLoaded)
            {
                TxtInfo.Text = "No field dump images found. Run the simulation with field dumps enabled.";
            }
            else
            {
                TxtInfo.Text = "Field distribution at center frequency (log scale)";
                // Select first visible tab
                foreach (var item in FieldTabs.Items)
                {
                    if (item is System.Windows.Controls.TabItem tab && tab.Visibility == Visibility.Visible)
                    {
                        tab.IsSelected = true;
                        break;
                    }
                }
            }
        }

        private bool TryLoadImage(string filename, System.Windows.Controls.Image imgCtrl,
            System.Windows.Controls.TabItem tab)
        {
            string path = Path.Combine(_resultsDir, filename);
            if (!File.Exists(path))
            {
                tab.Visibility = Visibility.Collapsed;
                return false;
            }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                imgCtrl.Source = bmp;
                tab.Visibility = Visibility.Visible;
                return true;
            }
            catch
            {
                tab.Visibility = Visibility.Collapsed;
                return false;
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadImages();
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
