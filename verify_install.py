import os
import sys

# Path to openEMS DLLs (adjust based on where you unzipped)
# Prioritize local openEMS folder if it exists
local_openems_path = os.path.join(os.getcwd(), "openEMS", "openEMS")
public_openems_path = r"C:\Users\Public\openEMS\openEMS"

if os.path.isdir(local_openems_path):
    openems_path = local_openems_path
    print(f"Using local openEMS at: {openems_path}")
else:
    openems_path = public_openems_path
    print(f"Using system openEMS at: {openems_path}")

if os.path.isdir(openems_path):
    print(f"Adding DLL directory: {openems_path}")
    try:
        os.add_dll_directory(openems_path)
    except AttributeError:
        # Python < 3.8
        os.environ["PATH"] = openems_path + ";" + os.environ["PATH"]

try:
    import CSXCAD
    print("CSXCAD imported successfully!")
except ImportError as e:
    print(f"Failed to import CSXCAD: {e}")

try:
    import openEMS
    print("openEMS imported successfully!")
except ImportError as e:
    print(f"Failed to import openEMS: {e}")
