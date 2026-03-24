import sys
import os

openems_path = os.path.join(os.getcwd(), "openEMS", "openEMS")

if not os.path.isdir(openems_path):
    openems_path = r"C:\Users\Public\openEMS\openEMS"
    
if os.path.isdir(openems_path):
    os.add_dll_directory(openems_path)

try:
    from openEMS import openEMS
    from CSXCAD import ContinuousStructure
    
    csx = ContinuousStructure()
    grid = csx.GetGrid()
    print("Grid object:", grid)
    print("Grid Dirs:", dir(grid))
    
except Exception as e:
    print(e)
