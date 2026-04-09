import json
d = json.load(open(r'c:\Users\Bin Xiao\OneDrive\Desktop\PCBAntSim\MIFA_onCarrier\MIFA.antproj','r'))
shapes = d.get('ManualShapes', [])
for s in shapes:
    if s.get('IsCarrier'):
        name = s['Name']
        verts = s.get('Vertices', [])
        merged = s.get('MergedPolygons', [])
        is_ant = name.startswith('Antenna (')
        print(f"--- {name} (Layer={s['LayerName']}, is_ant={is_ant}) ---")
        xy = [(v['X'], v['Y']) for v in verts]
        print(f"  verts: {xy}")
        for i, mp in enumerate(merged):
            mxy = [(v['X'], v['Y']) for v in mp]
            print(f"  merged[{i}]: {mxy}")
