import os
import sys
import numpy as np

# --- 1. Load OpenEMS ---
# Adjust this path if necessary
if os.path.isdir(os.path.join(os.getcwd(), "openEMS", "openEMS")):
    openems_path = os.path.join(os.getcwd(), "openEMS", "openEMS")
else:
    openems_path = r"C:\Users\Public\openEMS\openEMS"
    
if os.path.isdir(openems_path):
    os.add_dll_directory(openems_path)
else:
    print(f"Warning: openEMS DLL directory not found at {openems_path}")

try:
    from CSXCAD import ContinuousStructure
    from openEMS import openEMS
    from openEMS.physical_constants import *
    from openEMS.ports import LumpedPort
except ImportError as e:
    print("Error: Failed to import openEMS or CSXCAD. Please check installation.")
    print("Ensure the environment is activated and DLL paths are set.")
    sys.exit(1)


# --- 2. Simulation Parameters ---
unit = 1e-3  # Mm
f_start = 2e9   # 2 GHz
f_stop  = 3e9   # 3 GHz
f0 = 2.45e9     # Wifi Center
num_threads = 0 # 0 = Auto

# --- 3. Geometry Parameters (from Image) ---
# Widths
w_trace = 0.3      # Thin trace width
w_wide  = 1.3      # Wide start segment width
spacing_d = 0.7    # Pitch (center-to-center or repeating unit)
gap_s = 0.4        # Gap between wide block and first thin line
delta = 0.3        # Bottom clearance for meander
h1 = 2.85          # Height of the main structure

# Determine geometry based on analysis:
# Wide Block: 0 to 1.3
# Space: 1.3 to 1.7 (0.4 gap)
# Thin Line 1: 1.7 to 2.0 (0.3 width)
# Space: 2.0 to 2.4 (0.4 gap) => Pitch 0.7 matches (1.7 to 2.4)
# ...
num_meanders = 9 # Increased from 6 to tune to 2.45 GHz (was 3.58 GHz)

# ...
# Update mesh strategy to be safer if needed.
# But likely instability is just due to resonance building up?
# No, increasing energy means DIVERGENCE typically.
# Or maybe just slow ringing?
# -37 to -34 is 3dB increase. That's doubling energy.
# If Gaussian pulse is disconnected, energy CANNOT increase.

# Let's ensure EndCriteria is reasonable.

# Correct Stackup Interpretation:
# Top (Antenna)
# Prepreg (0.06mm) - Dielectric
# Inner 2 (0.034mm) - Signal? Assumed floating or minimal coupling.
# Core (1.065mm) - Dielectric
# GND (Layer 3) - Ground Plane

substrate_eps = 4.3 # Typical FR4

# Distances from Top to GND:
# h_prepreg = 0.06
# h_inner2 = 0.034
# h_core = 1.065
# Total distance h = 0.06 + 0.034 + 1.065 = 1.159mm

substrate_thickness = 1.16 # Approx 1.159mm
antenna_z = substrate_thickness

# Simulation setup:
# Z=0: Ground Plane (Layer 3)
# Z=1.16: Antenna (Top Layer)
# We fill Z=[0, 1.16] with FR4. Technically Inner2 is at Z=1.065+0.034=1.1 (approx).
# We ignore Inner2 copper for now as it's not a ground plane.

# Geometry Tuning
# Original turns = 6 (approx from image). Let's keep it.
# Widths and gaps are kept.

# And we will model the dielectric layers between Top and GND.

# Geometry Setup:
# Z=0: GND Layer (Top Surface of GND Copper)
# Z_d1 = 0.06 (Dielectric between GND and Inner2? No, between Inner2 and GND)
# Let's build from Bottom of Stack of interest.
# Interest: Top Layer Antenna + Ground Reference.
# Option A: Reference is Inner2. (h=0.06mm). This is very thin! 
# Option B: Reference is GND. (h = 0.06 + 0.0343 + 0.06 = 0.154 mm).
# Given the image, "Inner2" is "Routing". "GND" is "Routing".
# Usually Layer 2 (Inner2) is solid Ground in RF designs.
# IMPORTANT: If Inner2 is NOT solid ground under the antenna, then the reference is deeper.
# But for an antenna simulation, we usually assume the "Ground Plane" (the component with the cutout) is on the SAME layer or Layer 2.
# In the previous step, we assumed a MIFA/Monopole where the Ground is on the SAME layer (Top).
# The "delta=0.3mm" clearance suggests the Ground is on the Top Layer, surrounding the antenna.
# If the antenna is a PCB Monopole/MIFA, the "Ground" is the copper pour on the Top Layer.
# The layers below (Inner2, GND) usually have a KEEPOUT (void) under the antenna area to prevent shielding/capacitance.
# Logic: If there is solid ground 0.06mm below the antenna, it will not radiate heavily (it becomes a microstrip line).
# Antennas need "clearance".
# So, we assume:
# 1. Antenna is on Top.
# 2. Ground Plane is ALSO on Top (Planar Monopole).
# 3. All layers BELOW the antenna (Inner2, GND, PWR, etc.) have copper REMOVED in the antenna region.
# 4. The dielectric stack exists.

# Updated Model:
# Substrate: Full Thickness (0.5 mm).
# Antenna: Top Layer.
# Ground: Top Layer (Coplanar).
# Inner Layers: We will assume they are cleared (not modeled) in the antenna physics zone.
# Material: Dielectric Eps=4.3.

copper_thickness = 0.03429
antenna_z = substrate_thickness # Top Layer

# --- 4. Setup FDTD Simulation ---
sim = openEMS(EndCriteria=1e-5)
sim.SetGaussExcite(f0, (f_stop - f_start)/2)
sim.SetBoundaryCond(['MUR', 'MUR', 'MUR', 'MUR', 'MUR', 'MUR']) 

csx = ContinuousStructure()
# FDTD = sim.GetFDTD() # Removed

# --- 5. Materials ---
# Substrate
fr4 = csx.AddMaterial('FR4', epsilon=substrate_eps, kappa=0.02)
# Metal (PEC for speed, or Copper)
copper = csx.AddMetal('Copper')

# --- 6. Build Geometry ---

# A. Ground Plane (Bottom half of PCB + Side maybe?)
# Let's assume a large ground plane at Y < 0, feeding the antenna at Y=0.
gnd_width = 40
gnd_height = 20
gnd_y_max = 0 # Ground ends at y=0

# Add Ground Plane Rect
copper.AddBox(priority=10, start=[-10, -gnd_height, antenna_z], stop=[gnd_width, gnd_y_max, antenna_z])

# B. Substrate (Entire area)
# Make it larger than ground + antenna
sub_min = [-10, -gnd_height, 0]
sub_max = [gnd_width, h1 + 5, substrate_thickness]
fr4.AddBox(priority=0, start=sub_min, stop=sub_max)

# C. Antenna Structure (Top Layer)
# Defined relative to (0,0) which is slightly above ground?
# Let's put a small feed gap. 
feed_gap = 0.5 # 0.5mm gap for the port
antenna_y_start = feed_gap 

# Helper to draw rect relative to antenna start
def add_ant_rect(x1, x2, y1, y2):
    # y1, y2 are relative to antenna base (0)
    # real y = antenna_y_start + y
    copper.AddBox(priority=10, start=[x1, antenna_y_start + y1, antenna_z], stop=[x2, antenna_y_start + y2, antenna_z])

# 1. Wide Block
# x: [0, w_wide]
# y: [0, h1]  (Height h1=2.85 is total height of block)
add_ant_rect(0, w_wide, 0, h1)

# 2. Meander Lines
# Start X for meanders
cur_x = w_wide + gap_s # 1.3 + 0.4 = 1.7
# We alternate UP and DOWN.
# Wide block ends at Top (effectively).
# So we start by going DOWN?
# Connection 1: Top Bar from Wide Block to Line 1.
# Line 1 is at [cur_x, cur_x+w_trace]
# Draw Top Bar
add_ant_rect(w_wide - 0.1, cur_x + w_trace, h1 - w_trace, h1) # Overlap left

is_at_top = True # Start state at top

for i in range(num_meanders):
    # Vertical Line coordinates
    line_x1 = cur_x
    line_x2 = cur_x + w_trace
    
    # Vertical Line Y Range
    # If delta = 0.3, the loop bottom is at y=delta.
    # Top is at h1.
    # So line goes from delta to h1. (Wait, Wide block goes to 0?)
    # If delta is clearance to ground, and ground is at 0 (feed_gap relative to real ground), 
    # then bottom of loop is at 'delta'.
    # Wide block goes down to 0 to connect to feed.
    # Loops go down to delta.
    
    # Draw Vertical Line
    add_ant_rect(line_x1, line_x2, delta, h1)
    
    # Draw Horizontal Connection to NEXT line (or previous)
    # We already drew connection TO this line (from previous).
    # Now we need connection FROM this line to NEXT, depending on top/bot.
    
    # Look ahead to next x
    next_x = cur_x + spacing_d # 0.7 pitch
    # Gap to next = spacing_d - w_trace = 0.4.
    
    # If this is the last line, do we add a tail? None visible in schematic.
    # But we need to connect current line to next line.
    
    if i < num_meanders - 1:
        if is_at_top:
            # We came from top (previous was top bar).
            # So this line is a "Down" stroke? No, lines are static. Path is meaningful.
            # Geometry is just union of rects.
            # We need alternating Top and Bottom bars.
            # Wide Block -> Top Bar -> Line 1.
            # Now we need Bottom Bar -> Line 2.
            pass
        else:
            # We need Top Bar -> Line (i+1)
            pass
            
    # Algorithm adjustment:
    # Wide Block (Left). Connect Top to Line 1.
    # Line 1. Connect Bottom to Line 2.
    # Line 2. Connect Top to Line 3.
    # Line 3. Connect Bottom to Line 4.
    # And so on.
    
    if i < num_meanders - 1:
        # Draw connection to next
        if (i % 2 == 0): 
            # Even i (0, 2, 4...) -> corresponds to Line 1, 3, 5...
            # Line 1 connects at Bottom to Line 2.
            # Draw Bottom Bar
            add_ant_rect(line_x1, next_x + w_trace, delta, delta + w_trace)
        else:
            # Odd i (1, 3, 5...) -> corresponds to Line 2, 4, 6...
            # Line 2 connects at Top to Line 3.
            # Draw Top Bar
            add_ant_rect(line_x1, next_x + w_trace, h1 - w_trace, h1)
            
    cur_x = next_x

# --- 7. Excitation (Lumped Port) ---
# Between Bottom of Wide Block and Ground Plane
# Port Box: [0, w_wide], [0, feed_gap], [antenna_z]
# Using a lumped port with 50 Ohm impedance
# Use 'AddLumpedPort'
# Start: [0, 0, antenna_z]. Stop: [w_wide, antenna_y_start, antenna_z]
# Direction: Y (from 0 to feed_gap)
port_start = [0, 0, antenna_z]
port_stop  = [w_wide, antenna_y_start, antenna_z]
# AddLumpedPort( priority, port_resistance, start, stop, direction (0=x,1=y,2=z), excite=1.0)
port = LumpedPort(csx, 100, 50, port_start, port_stop, 1, 1.0, priority=20)


# --- 8. Mesh ---
# Define mesh lines
mesh = csx.GetGrid()

# X Mesh
# Add lines at critical x boundaries
x_lines = [0, w_wide] # Wide block
x_lines.extend([-10, gnd_width]) # Substrate/GND edges
# Add lines for each meander
curr = w_wide + gap_s
for _ in range(num_meanders):
    x_lines.append(curr)
    x_lines.append(curr + w_trace)
    curr += spacing_d

mesh.AddLine('x', x_lines)
# Smooth mesh for x. Target coarse resolution 1mm. Target fine resolution 0.1mm (trace/3)
mesh.SmoothMeshLines('x', 1.0, ratio=1.5)
# Manually refine antenna area
# Need ~0.2mm resolution in antenna region
x_refined = np.arange(0, w_wide + num_meanders*spacing_d + 1, 0.4) # Slightly coarse
mesh.AddLine('x', x_refined)

# Y Mesh
y_lines = [0, delta, h1, delta+w_trace, h1-w_trace] # Critical Y
y_lines.extend([-gnd_height, 0]) # GND
y_lines.append(feed_gap)
mesh.AddLine('y', y_lines)
mesh.SmoothMeshLines('y', 1.0, ratio=1.5)

# Z Mesh
z_lines = [0, substrate_thickness, substrate_thickness + 10, -10]
z_lines.append(substrate_thickness + copper_thickness) # If utilizing thickness
mesh.AddLine('z', z_lines)
mesh.SmoothMeshLines('z', 1.0, ratio=1.5)


# --- 9. Run Simulation ---
# Define Dump Box for Field (Optional)
# box_field = csx.AddDump( 'E_Field', file_type=0, sub_sampling=[2,2,2] )
# box_field.AddBox(start=[sub_min[0], sub_min[1], substrate_thickness], stop=[sub_max[0], sub_max[1], substrate_thickness])

# Write CSX file
sim_path = os.path.join(os.getcwd(), 'Sim_Wifi_Antenna')
if not os.path.exists(sim_path):
    os.mkdir(sim_path)

csx_file = os.path.join(sim_path, 'antenna.xml')
csx.Write2XML(csx_file)

# Set CSX structure
sim.SetCSX(csx)

print("Geometry built. Starting simulation...")
# print(f"Number of cells: {mesh.GetNumberOfCells()}")

# Create Run Script
print("Running simulation...")
sim.Run(sim_path)

print("Simulation finished.")

# --- 10. Post Processing ---
# Read Reflection Coefficient (S11)
try:
    port.CalcPort(sim_path, f_start, f_stop)
    s11 = port.uf_ref
    freq = port.f
    
    # Find resonance
    s11_db = 20 * np.log10(np.abs(s11))
    min_idx = np.argmin(s11_db)
    f_res = freq[min_idx]
    s11_min = s11_db[min_idx]
    
    print(f"Resonance Frequency: {f_res/1e9:.3f} GHz")
    print(f"Return Loss (S11): {s11_min:.2f} dB")
    
    # Save Plot
    import matplotlib.pyplot as plt
    plt.figure()
    plt.plot(freq/1e9, s11_db)
    plt.title('Return Loss (S11)')
    plt.xlabel('Frequency (GHz)')
    plt.ylabel('S11 (dB)')
    plt.grid(True)
    plt.axvline(f_res/1e9, color='r', linestyle='--', label=f'Res: {f_res/1e9:.2f} GHz')
    plt.legend()
    plt.savefig(os.path.join(sim_path, 's11_plot.png'))
    print(f"S11 Plot saved to {os.path.join(sim_path, 's11_plot.png')}")
    
except Exception as e:
    print(f"Post-processing failed: {e}")
