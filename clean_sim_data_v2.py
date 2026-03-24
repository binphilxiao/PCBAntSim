import os

def clean_file_strict(path):
    print(f"Cleaning {path}...")
    good_lines = []
    try:
        with open(path, 'rb') as f:
            lines = f.readlines()
            for line in lines:
                if b'\x00' in line:
                    break # Stop at first binary garbage
                
                try:
                    text = line.decode('utf-8').strip()
                    if not text:
                        continue # Skip empty lines
                    
                    if text.startswith('%'):
                       good_lines.append(line)
                       continue
                    
                    # Must be numbers
                    parts = text.split()
                    if len(parts) < 2:
                        continue
                        
                    # Validate they are floats
                    float(parts[0])
                    float(parts[1])
                    
                    good_lines.append(line)
                except ValueError:
                    # Not a float line, likely garbage or header
                    continue
                except UnicodeDecodeError:
                    break
                    
    except Exception as e:
        print(f"Error: {e}")
        return

    lines = good_lines[:20000]
    print(f"Forcing truncate to 20000 lines. Original good: {len(good_lines)}")
    
    # Debug last few lines
    print(f"Line 19995: {lines[19995]}")
    print(f"Line 19999: {lines[19999]}")

    
    with open(path, 'wb') as f:
        f.writelines(lines)

clean_file_strict(os.path.join('Sim_Wifi_Antenna_Snapshot', 'port_ut_100'))
clean_file_strict(os.path.join('Sim_Wifi_Antenna_Snapshot', 'port_it_100'))
