import sys
import os

openems_path = os.path.join(os.getcwd(), "openEMS", "openEMS")

if not os.path.isdir(openems_path):
    openems_path = r"C:\Users\Public\openEMS\openEMS"
    
if os.path.isdir(openems_path):
    os.add_dll_directory(openems_path)

try:
    import openEMS.ports
    print("Imports successful")
    print("ports Dirs:", dir(openEMS.ports))
    
except Exception as e:
    print(e)
