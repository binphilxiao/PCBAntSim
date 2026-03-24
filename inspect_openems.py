import sys
import os

if os.path.isdir(os.path.join(os.getcwd(), "openEMS", "openEMS")):
    openems_path = os.path.join(os.getcwd(), "openEMS", "openEMS")
    print(f"Using local openEMS: {openems_path}")
else:
    openems_path = r"C:\Users\Public\openEMS\openEMS"

if os.path.isdir(openems_path):
    os.add_dll_directory(openems_path)

try:
    from openEMS import openEMS
    from CSXCAD import ContinuousStructure
    print("Imports successful")
    
    sim = openEMS()
    print("Sim object created:", sim)
    print("Dirs:", dir(sim))
    
    csx = ContinuousStructure()
    print("CSX object created:", csx)
    print("CSX Dirs:", dir(csx))
    
except Exception as e:
    print(e)
