using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using AntennaSimulatorApp.Services;

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
        /// <summary>True while a simulation (FDTD or post-only) is active.</summary>
        public bool IsSimRunning => _isRunning;
        /// <summary>Fired whenever <see cref="IsSimRunning"/> changes.</summary>
        public event EventHandler? RunningStateChanged;
        private void SetRunning(bool value)
        {
            if (_isRunning == value) return;
            _isRunning = value;
            try { RunningStateChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }
        private readonly DispatcherTimer _timer;
        private DateTime _startTime;

        // Live result refresh
        private DispatcherTimer? _postProcessTimer;
        private bool _postProcessRunning;
        private S11ResultWindow? _liveResultWindow;

        /// <summary>
        /// Fired right after a new <see cref="S11ResultWindow"/> is created
        /// during live post-processing. A host (e.g. <c>MainWindow</c>) can
        /// subscribe to re-parent the result window's content into an
        /// embedded pane instead of showing it as a top-level window.
        /// If no subscribers are attached, the result window is shown
        /// normally as a separate window.
        /// </summary>
        public event Action<S11ResultWindow>? ResultWindowReady;

        // Tracks the total size of sim_data/ as seen by the last post-process
        // pass. If nothing has grown since then, skip running post-only.
        private long _lastSimDataBytes;

        // Log file
        private StreamWriter? _logWriter;

        // Report context (captured before run)
        private SimReportGenerator.ReportContext? _reportContext;

        // Regex to match openEMS timestep output: "[@ 1234]" or "Timestep: 1234"
        private static readonly Regex _tsRegex = new(
            @"(?:\[@\s*(\d+)\]|Timestep:\s*(\d+))",
            RegexOptions.Compiled);

        public SimConsoleWindow(string simDir, int maxTimesteps = 200000, SimReportGenerator.ReportContext? reportContext = null)
        {
            InitializeComponent();
            _simDir        = simDir;
            _scriptPath    = Path.Combine(simDir, "scripts", "run_simulation.py");
            _maxTimesteps  = maxTimesteps > 0 ? maxTimesteps : 200000;
            _reportContext = reportContext;

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

        /// <summary>
        /// Detach this window's root <see cref="Content"/> so it can be
        /// hosted inside another control (e.g. an embedded pane in
        /// <c>MainWindow</c>). The window itself stays alive for its timers,
        /// process handle and post-processing logic, but must not be shown.
        /// Returns the detached root element, or <c>null</c> if already
        /// detached.
        /// </summary>
        public FrameworkElement? DetachContentForEmbedding()
        {
            if (this.Content is not FrameworkElement root) return null;
            this.Content = null;
            // Make absolutely sure this window never pops up on screen.
            this.ShowInTaskbar = false;
            this.Visibility = Visibility.Collapsed;
            this.WindowStyle = WindowStyle.None;
            this.Width = 0;
            this.Height = 0;
            // Hide the Close button — the host window owns visibility now.
            try { BtnClose.Visibility = Visibility.Collapsed; } catch { }
            return root;
        }

        /// <summary>
        /// Re-run only the post-processing stage of an existing simulation
        /// (skips the FDTD engine). Requires <c>Sim/sim_data/</c> to already
        /// contain time-domain port data from a previous successful run.
        /// </summary>
        public void StartPostOnly()
        {
            Title = "openEMS Post-Processing";

            string? pythonExe = FindPython();
            if (pythonExe == null)
            {
                AppendLine("[ERROR] Cannot find system Python. Please install Python or set Tools -> Options -> Python path.");
                TxtStatus.Text = "Error: Python not found";
                return;
            }
            _pythonExe = pythonExe;

            if (!File.Exists(_scriptPath))
            {
                AppendLine($"[ERROR] Script not found: {_scriptPath}");
                AppendLine("        Run a full simulation at least once to generate it.");
                TxtStatus.Text = "Error: Script not found";
                return;
            }

            string simDataDir = AntennaSimulatorApp.Services.OpenEmsExporter.FindExistingSimDataDir(_simDir);
            if (!Directory.Exists(simDataDir) || Directory.GetFiles(simDataDir).Length == 0)
            {
                AppendLine($"[ERROR] No time-domain data in: {simDataDir}");
                AppendLine("        Run a full simulation at least once before using post-only mode.");
                TxtStatus.Text = "Error: No sim_data";
                return;
            }

            AppendLine($"Python:  {pythonExe}");
            AppendLine($"Script:  {_scriptPath} --post-only");
            AppendLine($"WorkDir: {_simDir}");
            AppendLine(new string('─', 60));
            AppendLine("[INFO] Re-running post-processing only (FDTD skipped)...");
            AppendLine("");

            // Hide the FDTD progress bar — post-only has no timestep loop.
            ProgressSim.Visibility = Visibility.Collapsed;
            TxtProgress.Text = "";
            BtnStop.IsEnabled = false;

            TxtStatus.Text = "Post-processing...";
            _startTime = DateTime.Now;
            _timer.Start();
            SetRunning(true);

            RunPostProcessAsync(isFinal: true);
        }

        public void StartSimulation()
        {
            string? pythonExe = FindPython();
            if (pythonExe == null)
            {
                AppendLine("[ERROR] Cannot find system Python. Please install Python or set Tools -> Options -> Python path.");
                TxtStatus.Text = "Error: Python not found";
                return;
            }
            _pythonExe = pythonExe;

            // Clear stale data from the previous run so the live result window
            // does not briefly show the old S11 curve while FDTD warms up.
            TryCleanDir(AntennaSimulatorApp.Services.OpenEmsExporter.ResolveSimDataDir(_simDir));
            TryCleanDir(Path.Combine(_simDir, "results"));

            // Close any result window left open from a previous run so it
            // cannot silently re-display outdated data while live-refresh
            // timers race with the new simulation.
            try { _liveResultWindow?.Close(); } catch { }
            _liveResultWindow = null;

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

            SetRunning(true);
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

            _lastSimDataBytes = 0;

            // Periodically re-run post-processing so the result window shows
            // intermediate S11/far-field. Interval is intentionally long to
            // avoid stealing CPU from the FDTD engine; the actual work is
            // skipped entirely if sim_data/ hasn't grown since last pass.
            _postProcessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
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
                SetRunning(false);
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
                    MirrorScratchToProjectAsync();
                    FinalRefreshResults();
                    GenerateReport();
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
        /// then refresh the live result window. Skipped if sim_data/ has
        /// not grown since the previous pass, so it does not waste CPU
        /// when the FDTD engine is between flushes.
        /// </summary>
        private void RunLivePostProcess()
        {
            if (_postProcessRunning) return;
            if (_pythonExe == null || !File.Exists(_scriptPath)) return;

            string simDataDir = AntennaSimulatorApp.Services.OpenEmsExporter.ResolveSimDataDir(_simDir);
            if (!Directory.Exists(simDataDir) || Directory.GetFiles(simDataDir).Length == 0)
                return;

            long total = GetDirSize(simDataDir);
            if (total <= _lastSimDataBytes) return;   // no new time-domain samples
            _lastSimDataBytes = total;

            RunPostProcessAsync(isFinal: false);
        }

        private static long GetDirSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        /// <summary>
        /// After a successful simulation, copy the scratch sim_data folder
        /// (typically on a local SSD, e.g. C:\PCBAntSimData) back into the
        /// project's <c>Sim/sim_data</c> so that re-post-processing remains
        /// possible later when the project is opened on a different machine
        /// or the scratch disk is wiped. This is a one-shot bulk copy of
        /// final data only — it does NOT happen during the run, so the
        /// cloud-sync provider only sees a single write at the end instead
        /// of GB-scale streaming HDF5 churn.
        /// </summary>
        private void MirrorScratchToProjectAsync()
        {
            string scratch = AntennaSimulatorApp.Services.OpenEmsExporter.ResolveSimDataDir(_simDir);
            string project = AntennaSimulatorApp.Services.OpenEmsExporter.GetProjectSimDataDir(_simDir);

            // Same path → nothing to mirror (scratch root not configured).
            if (string.Equals(Path.GetFullPath(scratch), Path.GetFullPath(project),
                              StringComparison.OrdinalIgnoreCase))
                return;

            if (!Directory.Exists(scratch)) return;

            AppendLine($"[INFO] Mirroring sim_data to project folder: {project}");
            TxtStatus.Text = "Copying sim_data to project...";

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(project);
                    CopyDirectory(scratch, project);
                    Dispatcher.BeginInvoke(() =>
                    {
                        AppendLine("[INFO] sim_data mirrored successfully.");
                        TxtStatus.Text = "Simulation completed successfully";
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        AppendLine($"[WARN] Failed to mirror sim_data: {ex.Message}");
                    });
                }
            });
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string file in Directory.EnumerateFiles(sourceDir))
            {
                string dest = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }
            foreach (string sub in Directory.EnumerateDirectories(sourceDir))
            {
                string dest = Path.Combine(destDir, Path.GetFileName(sub));
                CopyDirectory(sub, dest);
            }
        }

        /// <summary>
        /// Run --post-only, then open or refresh the S11 result window.
        /// </summary>
        private void RunPostProcessAsync(bool isFinal)
        {
            if (_postProcessRunning) return;
            if (_pythonExe == null || !File.Exists(_scriptPath)) return;

            string simDataDir = AntennaSimulatorApp.Services.OpenEmsExporter.ResolveSimDataDir(_simDir);
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

                        // Attach auxiliary tabs (Far-Field / Field Distribution)
                        // whenever new files exist — works for both live and
                        // final passes. Attach* is a no-op when files are
                        // absent or already attached.
                        if (_liveResultWindow != null && _liveResultWindow.IsLoaded)
                        {
                            string resultsDir = Path.Combine(_simDir, "results");
                            _liveResultWindow.AttachFarField(resultsDir);
                            _liveResultWindow.AttachFieldDump(resultsDir);
                        }

                        if (isFinal)
                        {
                            if (_isRunning)
                            {
                                _isRunning = false;
                                _timer.Stop();
                                TxtStatus.Text = "Post-processing completed";
                                BtnOpenFolder.IsEnabled = true;
                            }
                        }
                    }
                    else if (isFinal)
                    {
                        AppendLine("[WARN] Post-processing did not produce results.");
                        TxtStatus.Text = "Post-processing failed — no results";
                        if (_isRunning) { _isRunning = false; _timer.Stop(); }
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

            // Attach auxiliary result tabs (Far-Field + Field Distribution)
            // into the unified S11 result window rather than opening extra
            // top-level windows.
            if (_liveResultWindow != null)
            {
                _liveResultWindow.AttachFarField(resultsDir);
                _liveResultWindow.AttachFieldDump(resultsDir);
            }
        }

        /// <summary>
        /// Open the S11 result window (reuse if already open), refresh data.
        /// </summary>
        private void OpenOrRefreshResultWindow(bool isFinal)
        {
            string resultsDir = Path.Combine(_simDir, "results");

            if (_liveResultWindow != null)
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
                if (ResultWindowReady != null)
                {
                    // Let the host (MainWindow) embed the result window's content.
                    ResultWindowReady.Invoke(_liveResultWindow);
                }
                else
                {
                    _liveResultWindow.Show();
                }
            }
        }

        private void GenerateReport()
        {
            if (_reportContext == null) return;
            try
            {
                var elapsed = DateTime.Now - _startTime;
                string? path = SimReportGenerator.Generate(_simDir, _reportContext, elapsed);
                if (path != null)
                {
                    AppendLine($"[REPORT] {path}");
                    // Open the report in the default browser
                    Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                AppendLine($"[WARN] Report generation failed: {ex.Message}");
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

            // Default policy: force system Python on PATH.
            // Do not auto-pick project/workspace .venv to avoid accidental dependency mismatch.
            var candidates = new[] { "python" };

            foreach (var candidate in candidates)
            {
                if (candidate != "python" && !File.Exists(candidate))
                    continue;
                if (TestPython(candidate))
                    return candidate;
            }

            return null;
        }

        private static void TryCleanDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    try { File.Delete(f); } catch { /* best effort */ }
                }
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    try { Directory.Delete(sub, recursive: true); } catch { /* best effort */ }
                }
            }
            catch { /* best effort */ }
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
            RequestStop();
        }

        /// <summary>
        /// Public entry point so an external host (e.g. the toolbar Stop
        /// button on <c>MainWindow</c>) can stop the running simulation.
        /// Asks for confirmation first; no-op if nothing is running.
        /// </summary>
        public void RequestStop()
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

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // After the console closes, Windows may pick an unrelated top-level
            // window (e.g. a lingering openEMS/Python process) to focus. Force
            // focus back to our owner (MainWindow) if it's still alive.
            try
            {
                var owner = Owner;
                if (owner != null && owner.IsLoaded)
                {
                    if (owner.WindowState == WindowState.Minimized)
                        owner.WindowState = WindowState.Normal;
                    owner.Activate();
                    owner.Focus();
                }
                else
                {
                    Application.Current?.MainWindow?.Activate();
                }
            }
            catch { }
        }
    }
}
