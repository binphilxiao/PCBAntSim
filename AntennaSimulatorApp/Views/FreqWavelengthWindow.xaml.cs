using System;
using System.Windows;
using System.Windows.Input;

namespace AntennaSimulatorApp.Views
{
    public partial class FreqWavelengthWindow : Window
    {
        private const double C0 = 299792458.0; // speed of light in vacuum (m/s)

        public FreqWavelengthWindow()
        {
            InitializeComponent();
            Loaded += (_, __) => Calculate();
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Calculate();
        }

        private void Unit_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (IsLoaded) Calculate();
        }

        private void Calculate_Click(object sender, RoutedEventArgs e) => Calculate();

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
            {
                ErBox.Text = tag;
                Calculate();
            }
        }

        private void Calculate()
        {
            if (!double.TryParse(FreqBox.Text.Trim(), out double freqVal) || freqVal <= 0)
            {
                ClearResults();
                return;
            }

            double er = 1.0;
            if (double.TryParse(ErBox.Text.Trim(), out double erVal) && erVal > 0)
                er = erVal;

            // Convert input frequency to Hz
            double freqHz = freqVal * GetFreqMultiplier();

            // Compute wavelengths
            double lambda0 = C0 / freqHz;                   // free-space
            double lambdaM = C0 / (freqHz * Math.Sqrt(er)); // in-medium
            double k = 2 * Math.PI * freqHz * Math.Sqrt(er) / C0; // wave number

            // Display frequency in best unit
            ResultFreq.Text = FormatFreq(freqHz);

            // Display wavelengths
            ResultLambda0.Text = FormatLength(lambda0);
            ResultLambdaM.Text = er == 1.0
                ? FormatLength(lambdaM) + "  (same as λ₀)"
                : FormatLength(lambdaM);
            ResultQuarter.Text = FormatLength(lambdaM / 4);
            ResultHalf.Text = FormatLength(lambdaM / 2);
            ResultK.Text = $"{k:G6} rad/m";
        }

        private double GetFreqMultiplier()
        {
            return FreqUnitCombo.SelectedIndex switch
            {
                0 => 1,
                1 => 1e3,
                2 => 1e6,
                3 => 1e9,
                _ => 1e9
            };
        }

        private void ClearResults()
        {
            ResultFreq.Text = "";
            ResultLambda0.Text = "";
            ResultLambdaM.Text = "";
            ResultQuarter.Text = "";
            ResultHalf.Text = "";
            ResultK.Text = "";
        }

        private static string FormatFreq(double hz)
        {
            if (hz >= 1e9)      return $"{hz / 1e9:G6} GHz";
            if (hz >= 1e6)      return $"{hz / 1e6:G6} MHz";
            if (hz >= 1e3)      return $"{hz / 1e3:G6} kHz";
            return $"{hz:G6} Hz";
        }

        private static string FormatLength(double meters)
        {
            double abs = Math.Abs(meters);
            if (abs >= 1.0)       return $"{meters:G6} m";
            if (abs >= 1e-3)      return $"{meters * 1e3:G6} mm";
            if (abs >= 1e-6)      return $"{meters * 1e6:G6} µm";
            return $"{meters * 1e9:G6} nm";
        }
    }
}
