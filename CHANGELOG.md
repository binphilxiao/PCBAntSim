# Changelog

## [1.0.1] - 2026-04-15

### Changed
- OpenEMS simulation now uses the multithreaded engine (`engine='multithreaded'`) by default for faster FDTD runs
- Updated MIFA_onModule_Rev.B simulation script with expanded configuration

## [1.0.0] - 2026-04-13

### Initial Release

PCB Antenna Simulator — a WPF desktop application (.NET 8) for designing, simulating, and analyzing PCB-mounted antennas using the openEMS FDTD engine.

### Features

#### Project Management
- JSON-based project format (`.antproj`) with full save/load support
- Save As for project duplication
- Keyboard shortcuts: Ctrl+O (Open), Ctrl+S (Save), Ctrl+Shift+S (Save As), F5 (Run Simulation)

#### PCB Board Setup
- Dual-board architecture: main carrier PCB + optional RF module with XY offset and rotation
- Dynamic layer configuration: 2 / 4 / 6 / 8 / 10 / 12-layer stackups
- Automatic stackup generation with dielectric thickness distribution and solder mask layers
- Pre-configured material presets: FR-4, Rogers 4350B / 4003C / 5880, Polyimide, Aluminum CCL, and custom Dk
- Per-layer properties: thickness, material, layer type (Signal / Ground / Power / Dielectric / Mask), visibility toggle

#### Geometry & Drawing
- **Copper Shapes** — Manual polygon drawing on any conductive layer with vertex editing, polygon validation (self-intersection detection), merged polygon support, and visibility control
- **Parametric Antennas** — Built-in generators for Inverted-F (IFA), Meandered Inverted-F (MIFA), and custom antennas; configurable frequency, dimensions, trace widths, and layer assignment; live canvas preview
- **Plated Through-Hole Vias** — Add/edit/delete vias with diameter, start/end layer, XY position, and board assignment; 2D drawing canvas with zoom/pan
- **Solder Joints** — Module-to-carrier connection points with diameter and position control; 3D visualization in assembly view
- **Gerber Import** — Import copper layer artwork from Gerber (RS274X) files with per-layer XY offset and rotation; supports circular, rectangular, obround, and polygon apertures, fills, and arcs

#### Simulation (openEMS FDTD Integration)
- One-click export of complete openEMS simulation project (Python script + STL geometry + folder structure)
- Feed point / port configuration: lumped and waveguide ports with layer pair, position, integration direction, reference impedance, and extent
- Ground plane selection for multi-plane structures
- Boundary conditions: PML, open boundary with padding, PEC / PMC symmetry per axis
- Manual simulation area override
- Mesh density and cell-size control
- Frequency sweep modes: fast, discrete, and interpolating
- Analysis type selection: S11 only, far-field only, or both
- Configurable FDTD max timesteps
- Simulation console with real-time output, timestep progress, elapsed time, and auto-detected Python path
- Simulation log saved to `project/log/simulation.log`

#### Results Visualization
- **S11 Return Loss** — Frequency response plot with S11 (dB), real/imaginary impedance, VSWR, -10 dB bandwidth marker, and resonance detection; CSV export; live auto-refresh during simulation
- **Smith Chart** — Interactive impedance display with load point marker, reflection coefficient (Γ), normalized impedance, and built-in L-network impedance matcher with topology selection and automatic component calculation
- **Far-Field Radiation Pattern** — 2D polar plots (E-plane / H-plane, linear / dB scale) and 3D hemispherical mesh surface colored by gain; directivity (dBi), efficiency (%), peak gain, and sidelobe levels
- **Field Distribution** — Surface current (Jf), electric field (Ef), and magnetic field (Hf) heatmap plots in tabbed interface

#### Import / Export
- **STEP Export** — Full 3D model to STEP AP214 (`.stp` / `.step`) for CAD tools (Fusion 360, SolidWorks, FreeCAD, etc.)
- **STL Export** — Binary STL for 3D printing and mesh-based workflows
- **openEMS Export** — Complete simulation project with auto-generated Python script and geometry files

#### Tools & Utilities
- **Microstrip Impedance Calculator** — Microstrip and coplanar waveguide modes; calculates trace width, effective permittivity, phase velocity, and attenuation from target impedance, substrate Dk, and thickness; auto-loads board properties
- **Frequency ↔ Wavelength Converter** — Bidirectional conversion with free-space and dielectric-medium wavelengths; λ/4, λ/2, wave number; flexible unit support (Hz–GHz, nm–m); material presets

#### 3D Visualization (HelixToolkit WPF)
- Full 3D scene: carrier PCB, RF module, solder gap, copper shapes, antenna traces, via cylinders, solder joints, and port markers
- Semi-transparent dielectric layers with proper depth sorting
- Gerber-imported geometry rendered directly as meshes
- Interactive navigation: rotate, zoom, pan, keyboard view presets (Top / Bottom / Front / Right / Isometric)
- Perspective camera with auto zoom-to-fit
- Per-layer and per-shape visibility toggles with real-time updates

#### Settings
- Configurable openEMS installation path and Python executable path
- Auto-detection for common installation directories
