"""Rend hors du jeu le motif "taches" du shader de brulure, sur un plan.

Portage en Python des fonctions de bruit et du calcul de couleur de
Unity/LosketShaders/Assets/Shaders/LosketBurnOverlay.shader (motif taches,
tache localisee des marques de panache). Sert a juger un reglage de motif en
quelques secondes, sans recompiler le bundle ni lancer KSP. Ce n'est PAS le
shader : ni eclairage, ni masque directionnel, ni motif stries. Toute
modification du bruit cote shader doit etre reportee ici.

Dependances : numpy, Pillow.

Usage :  python Tools/render_pattern.py <dossier de sortie>

Sorties :
  reservoir.png  marque de panache sur une piece en preregle Metal,
                 sans (gauche) et avec (droite) deformation du bruit
  tuyere.png     tuyere marquee par elle-meme, reglages de la premiere
                 version (gauche) et reglages actuels (droite)
"""
from __future__ import annotations

import os
import sys

import numpy as np
from PIL import Image

from make_temper_lut import STOPS

f32 = np.float32


def frac(x):
    return x - np.floor(x)


def hash13(p):
    p = frac(p * f32(0.1031))
    p = p + (p * (p[..., [1, 2, 0]] + f32(33.33))).sum(-1, keepdims=True)
    return frac((p[..., 0] + p[..., 1]) * p[..., 2])


def hash33(p):
    p = frac(p * np.array([0.1031, 0.1030, 0.0973], dtype=f32))
    p = p + (p * (p[..., [1, 0, 2]] + f32(33.33))).sum(-1, keepdims=True)
    return frac((p[..., [0, 0, 1]] + p[..., [1, 0, 0]]) * p[..., [2, 1, 0]])


def _lattice_noise(p, corner_hash, vector):
    i = np.floor(p)
    f = p - i
    f = f * f * f * (f * (f * 6 - 15) + 10)

    def corner(dx, dy, dz):
        return corner_hash(i + np.array([dx, dy, dz], dtype=f32))

    fx, fy, fz = (f[..., k:k + 1] if vector else f[..., k] for k in range(3))

    def lerp(a, b, t):
        return a + (b - a) * t

    return lerp(
        lerp(lerp(corner(0, 0, 0), corner(1, 0, 0), fx),
             lerp(corner(0, 1, 0), corner(1, 1, 0), fx), fy),
        lerp(lerp(corner(0, 0, 1), corner(1, 0, 1), fx),
             lerp(corner(0, 1, 1), corner(1, 1, 1), fx), fy),
        fz)


def vnoise(p):
    return _lattice_noise(p.astype(f32), hash13, False)


def vnoise3(p):
    return _lattice_noise(p.astype(f32), hash33, True)


TURN = np.array([[0.36, 0.48, -0.80],
                 [-0.80, 0.60, 0.00],
                 [0.48, 0.64, 0.60]], dtype=f32)


def wnoise(p, amount):
    q = p @ TURN.T
    q = q + (vnoise3(q * f32(1.7) + f32(7.3)) - 0.5) * (0.8 * amount)
    return vnoise(q)


_LUT_X = np.array([s[0] for s in STOPS])
_LUT_C = np.array([[int(s[1][k:k + 2], 16) / 255 for k in (0, 2, 4)] + [s[2]]
                   for s in STOPS])


def temper_lut(u):
    return np.stack([np.interp(u, _LUT_X, _LUT_C[:, k]) for k in range(4)], -1)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def render(warp, width_px=640, height_px=400, width_m=5.0,
           burn_mag=0.99, peak_temp=0.6, spot_radius=1.2, spot_cut=0.0,
           noise_scale=4.0, sharpness=2.5, soot_rgb=(0.35, 0.35, 0.36), bleach=0.15,
           temper_gain=1.0, soot_gain=1.0, base_rgb=(0.62, 0.63, 0.64)):
    """Une tache centree, vue de face. Les noms suivent BurnParams."""
    height_m = width_m * height_px / width_px
    x = np.linspace(-width_m / 2, width_m / 2, width_px, dtype=f32)
    y = np.linspace(-height_m / 2, height_m / 2, height_px, dtype=f32)
    X, Y = np.meshgrid(x, y)
    # Plan legerement incline dans le repere motif, comme une vraie piece :
    # un plan aligne sur les axes flatterait le bruit d'origine.
    s_pos = np.stack([X + 3.1, Y * 0.94 + 1.7, Y * 0.34 + X * 0.1 + 0.4], -1).astype(f32)

    ep = s_pos * f32(1.3) + f32(11.7)
    if warp > 0:
        edge_noise = wnoise(ep, warp) * 0.8 + wnoise(ep * f32(2.3) + f32(5.1), warp) * 0.2
    else:
        edge_noise = vnoise(ep)
    rad = spot_radius * (0.75 + 0.5 * edge_noise)
    spot = np.exp(-(X * X + Y * Y) / (rad * rad))
    if spot_cut > 0:
        spot = spot * smoothstep(0.5 * spot_cut, spot_cut, spot)
    mask = spot

    pb = s_pos * f32(noise_scale)
    n = (lambda q: wnoise(q, warp)) if warp > 0 else vnoise
    blob = n(pb) * 0.5 + n(pb * f32(2.63) + f32(17.3)) * 0.32 \
        + n(pb * f32(5.71) + f32(31.9)) * 0.18
    grime = np.clip(mask * burn_mag * (0.45 + 1.1 * blob) * 1.6, 0, 1) ** sharpness
    soot = (1 - np.exp(-3 * grime)) * soot_gain

    lut_u = np.clip(peak_temp * (0.55 + 0.45 * mask) + 0.12 * (blob - 0.5), 0, 1)
    temper = temper_lut(lut_u)
    temper_a = temper[..., 3] * temper_gain * smoothstep(0.02, 0.25, mask * burn_mag) * (1 - soot)

    deposit = np.array(soot_rgb) * (1 - bleach) + np.array([0.93, 0.91, 0.88]) * bleach
    col = temper[..., :3] * (1 - soot[..., None]) + deposit * soot[..., None]
    alpha = np.clip(np.maximum(soot * 0.95, temper_a), 0, 1)[..., None]
    out = np.array(base_rgb) * (1 - alpha) + col * alpha
    return (np.clip(out, 0, 1) * 255).astype(np.uint8)


def side_by_side(left, right):
    gap = np.full((left.shape[0], 12, 3), 255, np.uint8)
    return Image.fromarray(np.concatenate([left, gap, right], 1))


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else '.'
    os.makedirs(out, exist_ok=True)

    side_by_side(render(0.0), render(1.0)).save(os.path.join(out, 'reservoir.png'))

    # Tuyere apres ~3 min de pleine poussee, preregle Suie, sur metal sombre.
    nozzle = dict(burn_mag=0.59, peak_temp=1.0, noise_scale=3.0, sharpness=1.3,
                  soot_rgb=(0.05, 0.048, 0.045), bleach=0.0, soot_gain=0.3,
                  base_rgb=(0.25, 0.25, 0.26))
    side_by_side(
        render(0.0, spot_radius=1.25, temper_gain=0.8, spot_cut=0.0, **nozzle),
        render(1.0, spot_radius=0.78, temper_gain=0.4, spot_cut=0.35, **nozzle),
    ).save(os.path.join(out, 'tuyere.png'))
    print('ecrit : reservoir.png, tuyere.png dans', os.path.abspath(out))


if __name__ == '__main__':
    main()
