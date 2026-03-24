import matplotlib
matplotlib.use('Agg')
import os
import numpy as np
import matplotlib.pyplot as plt

sim_path = 'Sim_Wifi_Antenna'
port_prefix = 'port_ut_100'

try:
    # Read port voltage and current
    # Format: time, value
    import numpy as np
    
    def load_data(file_path):
        data = []
        with open(file_path, 'r') as f:
            for line in f:
                if line.startswith('%'):
                    continue
                parts = line.split()
                if len(parts) >= 2:
                    data.append([float(parts[0]), float(parts[1])])
        return np.array(data)
        
    ut_data = load_data(os.path.join(sim_path, 'port_ut_100'))
    it_data = load_data(os.path.join(sim_path, 'port_it_100'))
    
    # Check if we have data
    if len(ut_data) == 0:
        print("No data yet.")
        exit()

    print(f"Loaded {len(ut_data)} time points.")
    print(f"First 5 lines of ut_data: {ut_data[:5]}")
        
    # Trim to min length
    min_len = min(len(ut_data), len(it_data))
    ut_data = ut_data[:min_len]
    it_data = it_data[:min_len]
    
    t = ut_data[:,0]
    ut = ut_data[:,1]
    it = it_data[:,1]
    
    # Calculate Frequency Response manually (DFT)
    # Simple DFT
    dt = t[1] - t[0]
    Fs = 1.0 / dt
    N = len(t)
    
    # Frequencies
    freq = np.fft.rfftfreq(N, d=dt)
    
    # FFT
    U_f = np.fft.rfft(ut)
    I_f = np.fft.rfft(it)
    
    # Calculate Incident and Reflected
    # Z_ref = 50 Ohm
    Z0 = 50.0
    
    # Definition: 
    # V_inc = 0.5 * (V_tot + Z0 * I_tot)
    # V_ref = V_tot - V_inc
    
    U_inc = 0.5 * (U_f + Z0 * I_f)
    U_ref = U_f - U_inc
    
    # S11 = U_ref / U_inc
    # Handle division by zero
    with np.errstate(divide='ignore', invalid='ignore'):
        s11 = U_ref / U_inc
        s11_db = 20 * np.log10(np.abs(s11))
    
    # Plot S11
    plt.figure(figsize=(10,6))
    plt.plot(freq/1e9, s11_db)
    plt.xlim(1, 4) # 1-4 GHz
    plt.ylim(-40, 0)
    plt.grid(True)
    plt.xlabel('Frequency (GHz)')
    plt.ylabel('S11 (dB)')
    plt.title('Return Loss (Preliminary)')
    
    # Find resonance in 2-3 GHz band
    mask = (freq >= 2e9) & (freq <= 3e9)
    if np.any(mask):
        subset_s11 = s11_db[mask]
        subset_freq = freq[mask]
        min_idx = np.argmin(subset_s11)
        res_freq = subset_freq[min_idx]
        res_val = subset_s11[min_idx]
        plt.plot(res_freq/1e9, res_val, 'ro')
        plt.text(res_freq/1e9, res_val+2, f"{res_freq/1e9:.3f} GHz\n{res_val:.1f} dB", color='red')
        
    output_file = os.path.join(sim_path, 's11_preview.png')
    plt.savefig(output_file)
    print(f"Preview saved to {output_file}")
    
except Exception as e:
    print(f"Error plotting: {e}")
