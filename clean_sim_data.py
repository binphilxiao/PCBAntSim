import os

def clean_file(path):
    print(f"Cleaning {path}...")
    good_lines = []
    try:
        with open(path, 'rb') as f:
            lines = f.readlines()
            for line in lines:
                # Check for null bytes or bad chars
                if b'\x00' in line:
                    break
                try:
                    text = line.decode('utf-8')
                    # verify it looks like numbers
                    parts = text.strip().split()
                    if len(parts) >= 2 or text.startswith('%'):
                        good_lines.append(line)
                except:
                    break
    except Exception as e:
        print(f"Error reading {path}: {e}")
        return

    print(f"Kept {len(good_lines)} lines out of {len(lines)}.")
    
    # Save back
    with open(path, 'wb') as f:
        f.writelines(good_lines)
    print(f"Saved clean file.")

clean_file(os.path.join('Sim_Wifi_Antenna', 'port_ut_100'))
clean_file(os.path.join('Sim_Wifi_Antenna', 'port_it_100'))
