
print("Hello World")
try:
    import numpy as np
    print("Numpy imported")
    import matplotlib.pyplot as plt
    print("Matplotlib imported")
except Exception as e:
    print(f"Error: {e}")
