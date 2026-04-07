using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace AntennaSimulatorApp.Views
{
    public partial class SimConsoleWindow : Window
    {
        private readonly string _simDir;       // Sim/ root
        private readonly string _scriptPath;   // Sim/scripts/run_simulation.py
        private readonly int _maxTimesteps;
        private string? _pythonExe;            // cached Python path for post-processing
        private Process? _process;
        private bool _isRunning;
        private readonly DispatcherTimer _timer;
        private DateTime _startTime;

        // Live result refresh
        private DispatcherTimer? _postProcessTimer;
        private bool _postProcessRunning;
        private S11ResultWindow? _liveResultWindow;

        // Log file
        private StreamWriter? _logWriter;

        // Regex to match openEMS timestep output: "[@ 1234]" or "Timestep: 1234"
        private static readonly Regex _tsRegex = new(
            @"(?:\[@\s*(\d+)\]|Timestep:\s*(\d+))",
            RegexOptions.Compiled);

        public SimConsoleWindow(string simDir, int maxTimesteps = 200000)
        {
            InitializeComponent();
            _simDir        = simDir;
            _scriptPath    = Path.Combine(simDir, "scripts", "run_simulation.py");
            _maxTimesteps  = maxTimesteps > 0 ? maxTimesteps : 200000;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, __) =>
            {
                if (_isRunning)
                {
                    var elapsed = DateTime.Now - _startTime;
                    TxtElapsed.Text = elapsed.ToString(@"hh\:mm\:ss");
                }
            };
        }

        public void StartSimulation()
        {
            string? pythonExe = FindPython();
            if (pythonExe == null)
            {
                AppendLine("[ERROR] Cannot find Python. Please install Python or create a .venv.");
                TxtStatus.Text = "Error: Python not found";
                return;
            }
            _pythonExe = pythonExe;

            if (!File.Exists(_scriptPath))
            {
                AppendLine($"[ERROR] Script not found: {_scriptPath}");
                TxtStatus.Text = "Error: Script not found";
                return;
            }

            // Create log file in project log/ folder
            try
            {
                string projectDir = Path.GetDirectoryName(_simDir)!;
                string logDir = Path.Combine(projectDir, "log");
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "simulation.log");
                _logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
            }
            catch { /* ignore log file errors */ }

            AppendLine($"Python:  {pythonExe}");
            AppendLine($"Script:  {_scriptPath}");
            AppendLine($"WorkDir: {_simDir}");
            AppendLine(new string('─', 60));
            AppendLine("");

            var psi = new ProcessStartInfo
            {
                FileName               = pythonExe,
                Arguments              = $"-u \"{_scriptPath}\"",
                WorkingDirectory       = _simDir,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            // Ensure non-interactive matplotlib
            psi.Environment["MPLBACKEND"] = "Agg";

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutputData;
            _process.ErrorDataReceived  += OnOutputData;
            _process.Exited             += OnProcessExited;

            _isRunning = true;
            _startTime = DateTime.Now;
            _timer.Start();

            TxtStatus.Text = "Running simulation...";
            ProgressSim.Visibility = Visibility.Visible;
            BtnStop.IsEnabled = true;
            BtnOpenFolder.IsEnabled = false;

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            BtnViewResults.IsEnabled = true;

            // Start periodic post-processing for live results (every 10s)
            _postProcessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _postProcessTimer.Tick += (_, __) => RunLivePostProcess();
            _postProcessTimer.Start();
        }

        private void OnOutputData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
                Dispatcher.BeginInvoke(() =>
                {
                    AppendLine(e.Data);
                    TryUpdateProgress(e.Data);
                });
        }

        private void TryUpdateProgress(string line)
        {
            var match = _tsRegex.Match(line);
            if (!match.Success) return;

            string val = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!int.TryParse(val, out int currentStep)) return;

            double pct = Math.Min(100.0, (double)currentStep / _maxTimesteps * 100.0);
            ProgressSim.Value = pct;
            TxtProgress.Text = $"{currentStep:N0} / {_maxTimesteps:N0}  ({pct:F1}%)";
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _isRunning = false;
                _timer.Stop();
                _postProcessTimer?.Stop();

                int exitCode = _process?.ExitCode ?? -1;
                BtnStop.IsEnabled = false;
                BtnOpenFolder.IsEnabled = true;
                BtnViewResults.IsEnabled = true;

                AppendLine("");
                AppendLine(new string('─', 60));

                if (exitCode == 0)
                {
                    ProgressSim.Value = 100;
                    TxtProgress.Text = $"{_maxTimesteps:N0} / {_maxTimesteps:N0}  (100.0%)";
                    TxtStatus.Text = "Simulation completed successfully";
                    AppendLine("[DONE] Simulation finished successfully.");
                    FinalRefreshResults();
                }
                else
                {
                    ProgressSim.Visibility = Visibility.Collapsed;
                    TxtProgress.Text = "";
                    TxtStatus.Text = exitCode == -1
                        ? "Simulation stopped"
                        : $"Simulation exited (code {exitCode})";
                    AppendLine(exitCode == -1
                        ? "[STOPPED] Simulation was terminated."
                        : $"[EXIT] Process exited with code {exitCode}.");

                    // Run final post-processing on partial data
                    RunPostProcessAsync(isFinal: true);
                }

                // Close log file
                try { _logWriter?.Close(); _logWriter = null; } catch { }
            });
        }

        /// <summary>
        /// Periodic live post-processing: run --post-only in background,
        /// then refresh the live result window.
        /// </summary>
        private void RunLivePostProcess()
        {
            if (_postProcessRunning) return;
            if (_pythonExe == null || !File.Exists(_scriptPath)) return;

            string simDataDir = Path.Combine(_simDir, "sim_data");
            if (!Directory.Exists(simDataDir) || Directory.GetFiles(simDataDir).Length == 0)
                return;

            RunPostProcessAsync(isFinal: false);
        }

        /// <summary>
        /// Run --post-only, then open or refresh the S11 result window.
        /// </summary>
        private void RunPostProcessAsync(bool isFinal)
        {
            if (_postProcessRunning) return;
            if (_pythonExe == null || !File.Exists(_scriptPath)) return;

            string simDataDir = Path.Combine(_simDir, "sim_data");
            if (!Directory.Exists(simDataDir) || Directory.GetFiles(simDataDir).Length == 0)
            {
                if (isFinal) AppendLine("[INFO] No simulation data found — cannot generate results.");
                return;
            }

            _postProcessRunning = true;
            if (isFinal)
            {
                AppendLine("[INFO] Running post-processing...");
                TxtStatus.Text = "Post-processing...";
            }

            var psi = new ProcessStartInfo
            {
                FileName               = _pythonExe,
                Arguments              = $"-u \"{_scriptPath}\" --post-only",
                WorkingDirectory       = _simDir,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            psi.Environment["MPLBACKEND"] = "Agg";

            string s11Csv = Path.Combine(_simDir, "results", "S11.csv");
            var ppProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            ppProcess.OutputDataReceived += (_, ea) =>
            {
                if (ea.Data != null)
                    Dispatcher.BeginInvoke(() => AppendLine(ea.Data));
            };
            ppProcess.ErrorDataReceived += (_, ea) =>
            {
                if (ea.Data != null)
                    Dispatcher.BeginInvoke(() => AppendLine(ea.Data));
            };
            ppProcess.Exited += (_, __) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    int ppExit = ppProcess.ExitCode;
                    ppProcess.Dispose();
                    _postProcessRunning = false;

                    if (ppExit == 0 && File.Exists(s11Csv))
                    {
                        OpenOrRefreshResultWindow(isFinal);
                    }
                    else if (isFinal)
                    {
                        AppendLine("[WARN] Post-processing did not produce results.");
                        TxtStatus.Text = "Post-processing failed — no results";
                    }
                });
            };

            ppProcess.Start();
            ppProcess.BeginOutputReadLine();
            ppProcess.BeginErrorReadLine();
        }

        /// <summary>
        /// After a successful simulation (exit 0), results were already written by the script.
        /// Just open/refresh the window.
        /// </summary>
        private void FinalRefreshResults()
        {
            string resultsDir = Path.Combine(_simDir, "results");

            string s11Csv = Path.Combine(resultsDir, "S11.csv");
            if (File.Exists(s11Csv))
            {
                OpenOrRefreshResultWindow(isFinal: true);
            }

            // Auto-open field distribution window if any field dump images exist
            bool hasFieldPng = File.Exists(Path.Combine(resultsDir, "Jf_surface.png"))
                            || File.Exists(Path.Combine(resultsDir, "Ef_surface.png"))
                            || File.Exists(Path.Combine(resultsDir, "Hf_surface.png"));
            if (hasFieldPng)
            {
                var fieldWin = new FieldResultWindow(resultsDir);
                if (this.IsLoaded) fieldWin.Owner = this;
                fieldWin.Show();
            }
        }

        /// <summary>
        /// Open the S11 result window (reuse if already open), refresh data.
        /// </summary>
        private void OpenOrRefreshResultWindow(bool isFinal)
        {
            string resultsDir = Path.Combine(_simDir, "results");

            if (_liveResultWindow != null && _liveResultWindow.IsLoaded)
            {
                // Existing window — just refresh data
                _liveResultWindow.Refresh();
                if (isFinal) _liveResultWindow.StopLiveRefresh();
            }
            else
            {
                // Open new window with auto-refresh (file-based, every 5s)
                int refreshInterval = isFinal ? 0 : 5;
                _liveResultWindow = new S11ResultWindow(resultsDir, refreshInterval);
                if (this.IsLoaded) _liveResultWindow.Owner = this;
                _liveResultWindow.Closed += (_, __) => _liveResultWindow = null;
                _liveResultWindow.Show();
            }
        }

        private void AppendLine(string text)
        {
            TxtConsole.AppendText(text + Environment.NewLine);
            TxtConsole.ScrollToEnd();
            try { _logWriter?.WriteLine(text); } catch { }
        }

        private string? FindPython()
        {
            // 0. User-configured path from Tools → Options
            string userPython = Services.AppSettings.Instance.PythonPath;
            if (!string.IsNullOrWhiteSpace(userPython) && File.Exists(userPython) && TestPython(userPython))
                return userPython;

            // Candidates to try in order
            string projectDir = Path.GetDirectoryName(_simDir)!;
            string parentDir  = Path.GetDirectoryName(projectDir) ?? "";

            var candidates = new[]
            {
                // 1. .venv next to Sim/ (project folder)
                Path.Combine(projectDir, ".venv", "Scripts", "python.exe"),
                // 2. workspace-level .venv (one more level up)
                Path.Combine(parentDir, ".venv", "Scripts", "python.exe"),
                // 3. System Python on PATH
                "python"
            };

            foreach (var candidate in candidates)
            {
                if (candidate != "python" && !File.Exists(candidate))
                    continue;
                if (TestPython(candidate))
                    return candidate;
            }

            return null;
        }

        private static bool TestPython(string pythonPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_process != null && !_process.HasExited)
            {
                var result = MessageBox.Show(
                    "Are you sure you want to stop the simulation?",
                    "Stop Simulation", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                        AppendLine("");
                        AppendLine("[STOPPED] Simulation was cancelled by user.");
                        TxtStatus.Text = "Simulation stopped by user";
                    }
                    catch (Exception ex)
                    {
                        AppendLine($"[WARN] Could not stop process: {ex.Message}");
                    }
                }
            }
        }

        private void BtnViewResults_Click(object sender, RoutedEventArgs e)
        {
            RunLivePostProcess();
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _simDir,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_isRunning)
            {
                var result = MessageBox.Show(
                    "Simulation is still running. Stop it and close?",
                    "Close", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }

                try { _process?.Kill(entireProcessTree: true); }
                catch { }
            }

            _timer.Stop();
            _postProcessTimer?.Stop();
        }
    }
}
