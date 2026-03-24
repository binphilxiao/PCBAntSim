import os
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

sim_path = 'Sim_Wifi_Antenna_Snapshot'
port_ut_file = os.path.join(sim_path, 'port_ut_100')
port_it_file = os.path.join(sim_path, 'port_it_100')

print(f"Reading {port_ut_file}...")
try:
    # Use python manual parsing for robustness against oddities
    ut_data = []
    with open(port_ut_file, 'r', encoding='utf-8', errors='ignore') as f:
        for i, line in enumerate(f):
             if line.startswith('%'): continue
             try:
                 parts = line.split()
                 if len(parts) >= 2:
                     ut_data.append([float(parts[0]), float(parts[1])])
             except ValueError:
                 print(f"Skipping bad line {i}: {line[:20]}...")
                 continue

    it_data = []
    with open(port_it_file, 'r', encoding='utf-8', errors='ignore') as f:
        for i, line in enumerate(f):
             if line.startswith('%'): continue
             try:
                 parts = line.split()
                 if len(parts) >= 2:
                     it_data.append([float(parts[0]), float(parts[1])])
             except ValueError:
                 continue
                 
    ut_data = np.array(ut_data)
    it_data = np.array(it_data)
    print("Data loaded.")
except Exception as e:
    print(f"Error loading data: {e}")
    exit()

# Trim to min length
n = min(len(ut_data), len(it_data))
ut = ut_data[:n, 1]
it = it_data[:n, 1]
t = ut_data[:n, 0]

dt = t[1] - t[0]
print(f"Time steps: {n}, dt: {dt}")

# FFT
U = np.fft.rfft(ut)
I = np.fft.rfft(it)
freq = np.fft.rfftfreq(n, d=dt)

# Compute Z_in and S11
# Z_ref usually 50 Ohm
Z_ref = 50.0
# Avoid division by zero
mask = np.abs(I) > 1e-10
Z_in = np.zeros_like(U)
Z_in[mask] = U[mask] / I[mask]
S11 = (Z_in - Z_ref) / (Z_in + Z_ref)

# Convert to dB
S11_db = 20 * np.log10(np.abs(S11) + 1e-12)

# Find Resonance
idx = np.logical_and(freq >= 0.5e9, freq <= 6e9) # 0.5 to 6 GHz
f_band = freq[idx]
s11_band = S11_db[idx]
min_idx = np.argmin(s11_band)
f_res = f_band[min_idx]
s11_min = s11_band[min_idx]

print(f"Resonance Frequency: {f_res/1e9:.3f} GHz")
print(f"Return Loss (S11): {s11_min:.2f} dB")

# Plot
plt.figure()
plt.plot(freq/1e9, S11_db)
plt.xlim(1, 4) # Zoom in 1-4 GHz
plt.ylim(-40, 5)
plt.grid(True)
plt.title(f"S11 Parameter (Res: {f_res/1e9:.2f} GHz @ {s11_min:.1f} dB)")
plt.xlabel("Frequency (GHz)")
plt.ylabel("S11 (dB)")
output_png = os.path.join(sim_path, 's11_result.png')
plt.savefig(output_png)
print(f"Saved plot to {output_png}")
