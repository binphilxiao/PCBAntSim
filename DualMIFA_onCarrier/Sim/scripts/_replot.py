"""Quick field-dump-only re-plot using same data as run_simulation.py"""
import os, sys, numpy as np
base = r'c:\Users\Bin Xiao\OneDrive\Desktop\PCBAntSim\MIFA_onCarrier\Sim'
sim_path = os.path.join(base, 'sim_data')
results_dir = os.path.join(base, 'results')

import h5py, matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.colors import LogNorm
from matplotlib.patches import Polygon as MplPoly

_shape_outlines = []
_shape_outlines.append({'name': 'CARRIER_TOP_GND', 'is_antenna': False, 'xy': [(-40, 0), (-40, -100), (40, -100), (40, 0), (15, 0), (15, -4), (-2.5, -4), (-2.5, -4.5), (-3, -4.5), (-3, -4), (-15, -4), (-15, 0), (-40, 0)]})
_shape_outlines.append({'name': 'Ant', 'is_antenna': False, 'xy': [(4.1, -3.2), (4.4, -3.2), (4.4, -3.85), (4.1, -3.85), (4.1, -3.2)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(-4.6, -4.3), (-3.3, -4.3), (-3.3, -0.1), (-4.6, -0.1), (-4.6, -4.3)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(-4.6, -0.4), (-2.6, -0.4), (-2.6, -0.1), (-4.6, -0.1), (-4.6, -0.4)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(-2.9, -4.3), (-2.6, -4.3), (-2.6, -0.1), (-2.9, -0.1), (-2.9, -4.3)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(-2.9, -0.4), (-1.6, -0.4), (-1.6, -0.1), (-2.9, -0.1), (-2.9, -0.4)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(-1.9, -3.55), (-1.6, -3.55), (-1.6, -0.1), (-1.9, -0.1), (-1.9, -3.55)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(-1.9, -3.55), (-0.6, -3.55), (-0.6, -3.25), (-1.9, -3.25), (-1.9, -3.55)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(-0.9, -3.55), (-0.6, -3.55), (-0.6, -0.1), (-0.9, -0.1), (-0.9, -3.55)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(-0.9, -0.4), (0.4, -0.4), (0.4, -0.1), (-0.9, -0.1), (-0.9, -0.4)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(0.1, -3.55), (0.4, -3.55), (0.4, -0.1), (0.1, -0.1), (0.1, -3.55)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(0.1, -3.55), (1.4, -3.55), (1.4, -3.25), (0.1, -3.25), (0.1, -3.55)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(1.1, -3.55), (1.4, -3.55), (1.4, -0.1), (1.1, -0.1), (1.1, -3.55)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(1.1, -0.4), (2.4, -0.4), (2.4, -0.1), (1.1, -0.1), (1.1, -0.4)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(2.1, -3.55), (2.4, -3.55), (2.4, -0.1), (2.1, -0.1), (2.1, -3.55)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(2.1, -3.55), (3.4, -3.55), (3.4, -3.25), (2.1, -3.25), (2.1, -3.55)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(3.1, -3.55), (3.4, -3.55), (3.4, -0.1), (3.1, -0.1), (3.1, -3.55)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(3.1, -0.4), (4.4, -0.4), (4.4, -0.1), (3.1, -0.1), (3.1, -0.4)]})
_shape_outlines.append({'name': 'Antenna (MIFA)', 'is_antenna': True, 'xy': [(4.1, -3.3), (4.4, -3.3), (4.4, -0.1), (4.1, -0.1), (4.1, -3.3)]})

def _plot_field_dump(name, title, cmap='hot'):
    h5_path = os.path.join(sim_path, name + '.h5')
    if not os.path.isfile(h5_path):
        print(f'[WARN] not found: {h5_path}'); return
    with h5py.File(h5_path, 'r') as hf:
        x = np.array(hf['/Mesh/x']) * 1e3
        y = np.array(hf['/Mesh/y']) * 1e3
        re = np.array(hf['/FieldData/FD/f0_real'])
        im = np.array(hf['/FieldData/FD/f0_imag'])
        mag = np.sqrt(np.sum(re**2 + im**2, axis=0))
        mag = np.squeeze(mag)
        vmax = mag.max()
        if vmax == 0: return
        vmin = max(vmax*1e-3, mag[mag>0].min()) if np.any(mag>0) else 1e-10

        _ant_shapes = [s for s in _shape_outlines if s['is_antenna']]
        _crop_src = _ant_shapes if _ant_shapes else _shape_outlines
        if len(_crop_src) > 0:
            all_sx = [px for s in _crop_src for px, _ in s['xy']]
            all_sy = [py for s in _crop_src for _, py in s['xy']]
            margin_mm = 3.0
            x_lo = min(all_sx) - margin_mm
            x_hi = max(all_sx) + margin_mm
            y_lo = min(all_sy) - margin_mm
            y_hi = max(all_sy) + margin_mm
        else:
            x_lo, x_hi = x[0], x[-1]
            y_lo, y_hi = y[0], y[-1]

        fig, ax = plt.subplots(figsize=(10, 8))
        pcm = ax.pcolormesh(x, y, mag, shading='auto', cmap=cmap,
                            norm=LogNorm(vmin=vmin, vmax=vmax))
        ax.set_xlim(x_lo, x_hi)
        ax.set_ylim(y_lo, y_hi)
        for shp in _shape_outlines:
            ec = 'lime' if shp['is_antenna'] else 'cyan'
            lw = 1.5 if shp['is_antenna'] else 0.8
            poly = MplPoly(shp['xy'], closed=True, fill=False,
                           edgecolor=ec, linewidth=lw, linestyle='-')
            ax.add_patch(poly)
        ax.set_xlabel('X (mm)')
        ax.set_ylabel('Y (mm)')
        ax.set_title(title)
        ax.set_aspect('equal')
        plt.colorbar(pcm, ax=ax, label='Magnitude')
        plt.tight_layout()
        png_path = os.path.join(results_dir, name + '.png')
        fig.savefig(png_path, dpi=150)
        plt.close(fig)
        print(f'Saved: {png_path}')

_plot_field_dump('Jf_surface', 'Surface Current Density |J| (A/m)', 'hot')
_plot_field_dump('Ef_surface', 'Electric Field |E| (V/m)', 'viridis')
_plot_field_dump('Hf_surface', 'Magnetic Field |H| (A/m)', 'inferno')
