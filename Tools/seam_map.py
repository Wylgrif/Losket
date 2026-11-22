"""Carte de joints du motif Tuiles, hors jeu : miroir numpy du shader
Losket/SeamMap (Unity/LosketShaders/Assets/Shaders/LosketSeamMap.shader).

    python Tools/seam_map.py texture.dds [autre.dds ...] [--out dossier]
    python Tools/seam_map.py texture.dds --crop 2000,2000,192

Pour chaque texture : la densite moyenne de la carte (0,045 sur les tuiles du
Starship SEP, moins de 0,005 sur son inox), la part de joints sombres (canal G
de la carte : 0 pour des joints peints en clair, 1 pour des joints peints en
sombre), les centiles de la reponse brute,
et, avec --out, la carte en PNG (ou un recadrage agrandi source | carte avec
--crop x,y,taille, en texels de la texture).

Sert a regler les seuils du detecteur sans lancer le jeu, sur les vraies
textures d'un mod. Toute modification du shader se reporte ici, et
inversement. Lit les .dds DXT1/DXT5 (le niveau de mip 0, celui que la
pre-passe lit en jeu a pleine qualite de texture) et tout format que Pillow
ouvre. Besoin de numpy, scipy et Pillow.
"""
import argparse
import os
import struct

import numpy as np
from scipy import ndimage

# Memes constantes que le shader.
LOG_EPS = 0.03
SEAM_LOW = 0.10
SEAM_HIGH = 0.25
SEAM_RADIUS = 2.0


def read_dds_mip0(path):
    """RGB (0..1) du mip 0 d'un .dds DXT1 ou DXT5, tel que le GPU le decode."""
    with open(path, 'rb') as f:
        data = f.read()
    h, w = struct.unpack_from('<II', data, 12)
    fourcc = data[84:88]
    if fourcc not in (b'DXT1', b'DXT5'):
        raise ValueError('format %r non gere' % fourcc)
    size = 8 if fourcc == b'DXT1' else 16
    bw, bh = (w + 3) // 4, (h + 3) // 4
    blocks = np.frombuffer(data, np.uint8, bw * bh * size, 128).reshape(bh, bw, size)
    col = blocks[..., size - 8:].astype(np.uint32)
    c0 = col[..., 0] | (col[..., 1] << 8)
    c1 = col[..., 2] | (col[..., 3] << 8)

    def rgb(c):
        return np.stack([((c >> 11) & 31) * (255.0 / 31), ((c >> 5) & 63) * (255.0 / 63),
                         (c & 31) * (255.0 / 31)], -1).astype(np.float32)
    p0, p1 = rgb(c0), rgb(c1)
    pal = np.stack([p0, p1, (2 * p0 + p1) / 3, (p0 + 2 * p1) / 3], -2)
    if fourcc == b'DXT1':
        # Mode trois couleurs de DXT1 quand c0 <= c1.
        three = (c0 <= c1)[..., None]
        pal[..., 2, :] = np.where(three, (p0 + p1) / 2, pal[..., 2, :])
        pal[..., 3, :] = np.where(three, 0.0, pal[..., 3, :])
    bits = col[..., 4] | (col[..., 5] << 8) | (col[..., 6] << 16) | (col[..., 7] << 24)
    out = np.zeros((bh * 4, bw * 4, 3), np.float32)
    for py in range(4):
        for px in range(4):
            sel = ((bits >> (2 * (py * 4 + px))) & 3).astype(np.int64)
            out[py::4, px::4] = np.take_along_axis(pal, sel[..., None, None].repeat(3, -1), -2)[..., 0, :]
    return out[:h, :w] / 255.0


def load_luminance(path):
    if path.lower().endswith('.dds'):
        rgb = read_dds_mip0(path)
    else:
        from PIL import Image
        rgb = np.asarray(Image.open(path).convert('RGB'), dtype=np.float32) / 255.0
    lum = rgb @ np.array([0.299, 0.587, 0.114], dtype=np.float32)
    # Dimensions paires : la pre-passe resume des blocs de 2x2 texels.
    return lum[:lum.shape[0] // 2 * 2, :lum.shape[1] // 2 * 2]


def prepass(lum):
    """Passe 0 : par bloc 2x2, moyenne et minimum des log-luminances."""
    lg = np.log(lum + LOG_EPS)
    quad = np.stack([lg[0::2, 0::2], lg[1::2, 0::2], lg[0::2, 1::2], lg[1::2, 1::2]], 0)
    return quad.mean(0), quad.min(0)


def detect(mean_field, min_field, radius=SEAM_RADIUS):
    """Passe 1 : reponse brute (en log) du detecteur de lignes longues, et
    masque des texels ou la vallee (joint sombre) l'emporte sur la crete.
    Les decalages sont en texels de la texture d'origine, soit des
    demi-texels des champs, lus en bilineaire avec repetition."""
    cache_r, cache_g = {}, {}

    def tap(field, cache, dx, dy):
        key = (round(dx, 4), round(dy, 4))
        if key not in cache:
            cache[key] = ndimage.shift(field, (-dy / 2, -dx / 2), order=1, mode='grid-wrap')
        return cache[key]

    def across(field, cache, px, py, ax, ay):
        return tap(field, cache, px, py) - 0.5 * (
            tap(field, cache, px + ax, py + ay) + tap(field, cache, px - ax, py - ay))

    best_ridge = np.zeros_like(mean_field)
    best_valley = np.zeros_like(mean_field)
    for k in range(12):
        a = k * 0.2617994
        ux, uy = np.cos(a), np.sin(a)
        ax, ay = ux * radius, uy * radius
        lx, ly = -uy, ux
        # Reponse centrale moins la part de signe oppose des voisins en travers.
        r0 = across(mean_field, cache_r, 0, 0, ax, ay)
        r1 = across(mean_field, cache_r, ax, ay, ax, ay)
        r2 = across(mean_field, cache_r, -ax, -ay, ax, ay)
        g0 = across(min_field, cache_g, 0, 0, ax, ay)
        g1 = across(min_field, cache_g, ax, ay, ax, ay)
        g2 = across(min_field, cache_g, -ax, -ay, ax, ay)
        ridge = np.maximum(np.maximum(r0, 0) - np.maximum(np.maximum(-r1, -r2), 0), 0)
        valley = np.maximum(np.maximum(-g0, 0) - np.maximum(np.maximum(g1, g2), 0), 0)
        # Maintien le long de la ligne, dans la meme polarite.
        for s in range(4):
            d = (3.0 if s < 2 else 6.0) * (1.0 if (s & 1) == 0 else -1.0)
            px, py = lx * d, ly * d
            ra = np.maximum(across(mean_field, cache_r, px, py, ax, ay), np.maximum(
                across(mean_field, cache_r, px + ux, py + uy, ax, ay),
                across(mean_field, cache_r, px - ux, py - uy, ax, ay)))
            ga = np.minimum(across(min_field, cache_g, px, py, ax, ay), np.minimum(
                across(min_field, cache_g, px + ux, py + uy, ax, ay),
                across(min_field, cache_g, px - ux, py - uy, ax, ay)))
            ridge = np.minimum(ridge, np.maximum(ra, 0))
            valley = np.minimum(valley, np.maximum(-ga, 0))
        best_ridge = np.maximum(best_ridge, ridge)
        best_valley = np.maximum(best_valley, valley)
        cache_r.clear()
        cache_g.clear()
    return np.maximum(best_ridge, best_valley), best_valley >= best_ridge


def smoothstep(lo, hi, x):
    t = np.clip((x - lo) / (hi - lo), 0, 1)
    return t * t * (3 - 2 * t)


def soften(seam):
    """Passe 2 : tente 3x3, avec repetition. Conserve la moyenne."""
    k = np.array([0.25, 0.5, 0.25], dtype=np.float32)
    return ndimage.convolve1d(ndimage.convolve1d(seam, k, 0, mode='wrap'), k, 1, mode='wrap')


def seam_map(lum, low=SEAM_LOW, high=SEAM_HIGH):
    """Carte de joints (canal R, 0..1, moitie de la taille de la texture),
    reponse brute, et canal G : le joint la ou il est sombre."""
    raw, dark = detect(*prepass(lum))
    seam = smoothstep(low, high, raw)
    return soften(seam), raw, soften(seam * dark)


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('textures', nargs='+')
    ap.add_argument('--out', help='dossier ou ecrire les PNG')
    ap.add_argument('--crop', help='x,y,taille en texels de la texture : recadrage agrandi')
    ap.add_argument('--low', type=float, default=SEAM_LOW)
    ap.add_argument('--high', type=float, default=SEAM_HIGH)
    args = ap.parse_args()
    if args.out:
        os.makedirs(args.out, exist_ok=True)
    for path in args.textures:
        lum = load_luminance(path)
        seam, raw, dark = seam_map(lum, args.low, args.high)
        pct = (90, 95, 98, 99)
        print('%-44s %dx%d  densite %.4f  sombres %.2f  reponse brute %s' % (
            os.path.basename(path), lum.shape[1], lum.shape[0], seam.mean(),
            dark.sum() / max(seam.sum(), 1e-9),
            ' '.join('p%d %.3f' % (p, v) for p, v in zip(pct, np.percentile(raw, pct)))))
        if not args.out:
            continue
        from PIL import Image
        base = os.path.splitext(os.path.basename(path))[0]
        if args.crop:
            x, y, n = (int(v) for v in args.crop.split(','))
            src = np.clip(lum[y:y + n, x:x + n] * 2.0, 0, 1)
            cut = seam[y // 2:(y + n) // 2, x // 2:(x + n) // 2]
            zoom = max(1, 768 // n)
            left = Image.fromarray((src * 255).astype(np.uint8)).resize((n * zoom, n * zoom), Image.NEAREST)
            right = Image.fromarray((cut * 255).astype(np.uint8)).resize((n * zoom, n * zoom), Image.NEAREST)
            sheet = Image.new('L', (2 * n * zoom + 8, n * zoom), 128)
            sheet.paste(left, (0, 0))
            sheet.paste(right, (n * zoom + 8, 0))
            sheet.save(os.path.join(args.out, base + '_joints_recadre.png'))
        else:
            Image.fromarray((seam * 255).astype(np.uint8)).save(os.path.join(args.out, base + '_joints.png'))


if __name__ == '__main__':
    main()
