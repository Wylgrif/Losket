"""Prototype hors jeu du motif "tuiles" : depot blanc venu des joints.

Modele propose pour Losket, evalue sur la coque deroulee d'un cylindre
(abscisse = arc autour de la coque, ordonnee = position le long de l'axe) :

  1. une grille de tuiles hexagonales dans cette carte ;
  2. des SOURCES discretes : joints exposes au flux (fuite de plasma),
     coins de tuiles, quelques tuiles cassees, chacune avec un tirage
     aleatoire (la plupart ne fuient pas) ;
  3. un TRANSPORT le long des lignes de courant de surface (projection
     tangentielle du vent relatif, corps a forte incidence) : en chaque
     pixel on remonte le courant sur N pas et on somme ce que les sources
     amont ont lache, avec attenuation et elargissement en eventail ;
  4. une texture de FILAMENTS alignee sur le courant, qui module le depot ;
  5. une USURE DE BORD : le bord et la pointe amont de chaque tuile
     blanchissent (revetement noir erode, silice blanche dessous).

Ce n'est PAS le shader : ni eclairage, ni masque directionnel 3D. Depuis le
2026-11-05 au soir, le shader lit les joints dans la texture de la piece et
transporte en espace UV (docs/traces-starship.md, section 6) : ce script
reste le modele de reference du transport (sources, eventail, attenuation,
filaments) sur une grille hexagonale ideale, il ne reproduit plus le shader
a l'identique.

Dependances : numpy, Pillow.
Usage :  python Tools/render_tiles.py <dossier de sortie>
"""
from __future__ import annotations

import os
import sys

import numpy as np
from PIL import Image

f32 = np.float32
SQ3 = 1.7320508


# ---------------------------------------------------------------- bruit ---
def frac(x):
    return x - np.floor(x)


def hash13(p):
    p = frac(p * f32(0.1031))
    p = p + (p * (p[..., [1, 2, 0]] + f32(33.33))).sum(-1, keepdims=True)
    return frac((p[..., 0] + p[..., 1]) * p[..., 2])


def hash2(x, y, salt=0.0):
    return hash13(np.stack([x, y, np.full_like(x, 7.7 + salt)], -1).astype(f32))


def vnoise(p):
    p = p.astype(f32)
    i = np.floor(p)
    f = p - i
    f = f * f * f * (f * (f * 6 - 15) + 10)

    def c(dx, dy, dz):
        return hash13(i + np.array([dx, dy, dz], dtype=f32))

    fx, fy, fz = f[..., 0], f[..., 1], f[..., 2]

    def lerp(a, b, t):
        return a + (b - a) * t

    return lerp(
        lerp(lerp(c(0, 0, 0), c(1, 0, 0), fx), lerp(c(0, 1, 0), c(1, 1, 0), fx), fy),
        lerp(lerp(c(0, 0, 1), c(1, 0, 1), fx), lerp(c(0, 1, 1), c(1, 1, 1), fx), fy),
        fz)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


# ------------------------------------------------------- grille hexagonale ---
# Pas 1 entre centres voisins, rangees le long de x (hexagones a pointe en y).
EDGE_N = np.array([[1.0, 0.0], [0.5, SQ3 / 2], [-0.5, SQ3 / 2]], dtype=f32)
VERT_R = 0.5 / np.cos(np.pi / 6)  # rayon des sommets
HALF_EDGE = 0.5 * np.tan(np.pi / 6)  # demi-longueur d'un bord


def hex_cell(uv):
    """Retourne (gv, centre) : position relative au centre de la cellule."""
    r = np.array([1.0, SQ3], dtype=f32)
    h = r * 0.5
    a = np.mod(uv, r) - h
    b = np.mod(uv - h, r) - h
    use_a = (a * a).sum(-1) < (b * b).sum(-1)
    gv = np.where(use_a[..., None], a, b)
    return gv, uv - gv


def hex_edge(gv):
    """Distance au bord le plus proche (0 sur le joint, 0.5 au centre) et
    normale sortante de ce bord."""
    proj = np.stack([gv @ EDGE_N[k] for k in range(3)], -1)  # (...,3)
    ap = np.abs(proj)
    k = ap.argmax(-1)
    best = np.take_along_axis(ap, k[..., None], -1)[..., 0]
    sgn = np.sign(np.take_along_axis(proj, k[..., None], -1)[..., 0])
    n = EDGE_N[k] * sgn[..., None]
    return 0.5 - best, n


def hex_vertex(gv, n_edge):
    """Les deux sommets du bord le plus proche : le plus proche (direction
    unitaire depuis le centre, distance) et l'autre (position depuis le
    centre, distance). Meme choix que HexEdgeVertex dans le shader."""
    t = np.stack([-n_edge[..., 1], n_edge[..., 0]], -1)
    side = np.where((gv * t).sum(-1) < 0, -HALF_EDGE, HALF_EDGE).astype(f32)
    v = n_edge * 0.5 + t * side[..., None]
    d = np.linalg.norm(gv - v, axis=-1)
    v_other = n_edge * 0.5 - t * side[..., None]
    d_other = np.linalg.norm(gv - v_other, axis=-1)
    return v / VERT_R, d, v_other, d_other


# ------------------------------------------------------------- ecoulement ---
class Hull:
    """Coque cylindrique de rayon R, vent relatif a incidence alpha.

    Carte (w, h) : w = arc autour de la coque depuis la ligne au vent (m),
    h = position le long de l'axe (m), croissant vers l'arriere. Lignes de
    courant de surface = projection tangentielle du vent (impact newtonien) :
    composante axiale cos(alpha), composante circonferentielle
    sin(alpha) * sin(theta), qui s'ecarte de la ligne au vent.
    """

    def __init__(self, radius=4.5, alpha_deg=65.0, tile=0.30, rot=False,
                 sector_deg=100.0):
        self.R = radius
        self.a = np.radians(alpha_deg)
        self.tile = tile
        self.rot = rot
        self.sector_cos = np.cos(np.radians(sector_deg))

    def flow_dir(self, p):
        theta = p[..., 0] / self.R
        v = np.stack([np.sin(self.a) * np.sin(theta),
                      np.full_like(theta, np.cos(self.a))], -1)
        return v / np.maximum(np.linalg.norm(v, axis=-1, keepdims=True), 1e-6)

    def heat(self, p):
        """Chauffe relative : 1 sur la ligne au vent, 0 sous le vent. Flux
        quasi axial (nez en avant) : pas de ligne au vent, fondu vers une
        chauffe uniforme, comme dans le shader."""
        theta = p[..., 0] / self.R
        blend = smoothstep(0.05, 0.35, np.sin(self.a))
        return 1.0 + (np.clip(np.cos(theta), 0, None) ** 1.5 - 1.0) * blend

    def sector(self, p):
        """Appartenance au secteur tuile : cone autour de la ligne au vent,
        a bord doux. Rien ne fuit ni ne s'use hors des tuiles. Flux axial :
        toute la circonference."""
        f = np.cos(p[..., 0] / self.R)
        blend = smoothstep(0.05, 0.35, np.sin(self.a))
        s = smoothstep(self.sector_cos - 0.08, self.sector_cos + 0.08, f)
        return 1.0 + (s - 1.0) * blend

    def uv(self, p):
        q = p / self.tile
        if self.rot:
            q = q[..., ::-1]
        return q


# ---------------------------------------------------------------- sources ---
def sources(hull, p, leak_frac=0.07, broken_frac=0.010, blur=0.24):
    """Ce que la surface lache au point p, par unite de longueur de
    trajet : joints exposes, coins, tuiles cassees.

    blur (en pas de tuile) elargit les bandes de source au moins au pas
    de marche du transport (bande : 1 pas, somme constante ; disque :
    1,5 pas ; disque a plateau : 2 pas), sinon une marche tombe ou non
    sur le joint selon le pixel et des barreaux apparaissent le long des
    panaches ; la hauteur est reduite d'autant pour que la dose integree
    ne change pas."""
    d = hull.flow_dir(p)
    gv, center = hex_cell(hull.uv(p))
    d_edge, n_edge = hex_edge(gv)
    vdir, d_vert, v_other, d_other = hex_vertex(gv, n_edge)
    corner_frac = 0.7 * leak_frac

    # Joint : bande lissee, fuite tiree au sort par joint (id = milieu du
    # bord, commun aux deux tuiles), ponderee par l'exposition du joint au
    # flux (un joint transversal au courant recoit le gaz, un joint parallele
    # non). Peu de joints fuient, mais ceux qui fuient fuient fort.
    mid = center + n_edge * 0.5
    seam_id = hash2(np.round(mid[..., 0] * 100), np.round(mid[..., 1] * 100), 1.0)
    leak = smoothstep(1.0 - leak_frac, 1.0, seam_id)
    exposure = np.abs((n_edge * d).sum(-1))
    seam_w = max(0.24, blur)
    seam = smoothstep(seam_w, 0.0, d_edge) * (0.24 / seam_w) * exposure * leak * 1.5

    # Coins : les DEUX sommets du bord le plus proche, chacun sa fuite
    # (n'evaluer que le plus proche tronquait son disque la ou l'autre
    # devient plus proche : arete dure, barreaux le long des panaches).
    corner_w = min(max(0.26, 1.5 * blur), 0.5)
    corner = 0.0
    for vp, dv in ((center + vdir * VERT_R, d_vert), (center + v_other, d_other)):
        vert_id = hash2(np.round(vp[..., 0] * 100), np.round(vp[..., 1] * 100), 2.0)
        corner = corner + smoothstep(corner_w, 0.0, dv) * (0.26 / corner_w) \
            * smoothstep(1.0 - corner_frac, 1.0, vert_id) * 3.0

    # Tuile cassee : la tuile entiere, fondue a zero sur son bord (distance
    # hexagonale) pour n'etre jamais tronquee, plus forte vers l'amont.
    cell_id = hash2(center[..., 0], center[..., 1], 3.0)
    broken = (cell_id > 1.0 - broken_frac).astype(f32)
    hex_dist = 0.5 - d_edge
    gdir = gv / np.maximum(np.linalg.norm(gv, axis=-1, keepdims=True), 1e-4)
    upstream = 0.5 + 0.5 * (gdir * -d).sum(-1)
    broken_src = broken * upstream * smoothstep(0.5, 0.15, hex_dist) * 2.0

    return hull.heat(p) * hull.sector(p) * (seam + corner + broken_src)


def advect(hull, p, n_steps=24, length=1.8, fan_deg=6.0):
    """Remonte le courant depuis p et somme les sources amont, avec
    attenuation exp(-s/decay), decay = 0.75 x portee comme dans le
    shader, et elargissement en eventail.

    Le premier pas est decale d'un bruit LISSE (periode ~10 cm) : un hash
    par pixel se retirait a chaque image en jeu des que la camera bougeait
    d'un millimetre, et tout le depot scintillait."""
    ds = length / n_steps
    decay = 0.75 * length
    blur = max(0.24, ds / hull.tile)
    jitter = vnoise(np.stack([p[..., 0] * 9.0, p[..., 1] * 9.0,
                              np.full_like(p[..., 0], 3.3)], -1))
    d0 = hull.flow_dir(p)
    q = p - d0 * ds * jitter[..., None]
    acc = np.zeros(p.shape[:-1], f32)
    tan_fan = np.tan(np.radians(fan_deg))
    for k in range(1, n_steps + 1):
        d = hull.flow_dir(q)
        q = q - d * ds
        s = (k + jitter) * ds
        perp = np.stack([-d[..., 1], d[..., 0]], -1)
        sigma = (s * tan_fan)[..., None]
        taps = sources(hull, q, blur=blur) + 0.5 * (sources(hull, q + perp * sigma, blur=blur)
                                                    + sources(hull, q - perp * sigma, blur=blur))
        acc += taps / 2.0 * np.exp(-s / decay) * ds
    return acc


def filaments(hull, p):
    """Texture de filaments alignee sur le courant local."""
    d = hull.flow_dir(p)
    along = (p * d).sum(-1)
    across = (p * np.stack([-d[..., 1], d[..., 0]], -1)).sum(-1)
    # 28/m = echelle de motif 4 x 7, comme dans le shader (qui fond en plus
    # les filaments vers leur moyenne quand ils passent sous trois pixels).
    n1 = vnoise(np.stack([across * 28.0, along * 1.78, np.full_like(along, 2.1)], -1))
    n2 = vnoise(np.stack([across * 56.0 + 3.3, along * 6.1 + 1.1, np.full_like(along, 5.7)], -1))
    rid = 1.0 - np.abs(2.0 * n1 - 1.0) - 0.3 * n2
    return np.clip(rid, 0, 1) ** 1.3


def edge_wear(hull, p, wear_frac=0.5):
    """Blanchiment du bord et de la pointe amont de chaque tuile."""
    d = hull.flow_dir(p)
    gv, center = hex_cell(hull.uv(p))
    d_edge, n_edge = hex_edge(gv)
    vdir, d_vert, _, _ = hex_vertex(gv, n_edge)
    heat2 = hull.heat(p) ** 2 * hull.sector(p)
    cell_id = hash2(center[..., 0], center[..., 1], 4.0)
    var = 0.25 + 0.75 * smoothstep(1.0 - wear_frac, 1.0, cell_id)

    faces_up = np.clip((n_edge * -d).sum(-1) - 0.3, 0, None) / 0.7
    edge = smoothstep(0.09, 0.0, d_edge) * faces_up * 0.35

    point_up = np.clip((vdir * -d).sum(-1) - 0.55, 0, None) / 0.45
    corner = smoothstep(0.17, 0.02, d_vert) * point_up * 0.6

    # Tuile cassee : le coin manquant montre le feutre blanc.
    broken_id = hash2(center[..., 0], center[..., 1], 3.0)
    broken = (broken_id > 1.0 - 0.012).astype(f32)
    chip = broken * (vdir * -d).sum(-1).clip(0, None) * smoothstep(0.34, 0.22, d_vert) * 2.0

    return heat2 * (var * (edge + corner) + chip)


# ------------------------------------------------------------------ rendu ---
def render(hull, w0, h0, size_m=3.0, px=560, gain_deposit=1.0, gain_wear=1.0,
           base_rgb=(0.075, 0.075, 0.085), show_seams=True):
    x = np.linspace(-size_m / 2, size_m / 2, px, dtype=f32) + w0
    y = np.linspace(-size_m / 2, size_m / 2, px, dtype=f32) + h0
    W, H = np.meshgrid(x, y)
    p = np.stack([W, H], -1)

    dep = advect(hull, p) * gain_deposit
    fil = filaments(hull, p)
    dep_vis = dep * (0.15 + 0.85 * fil) * 4.0
    wear = edge_wear(hull, p) * gain_wear
    print('  depot : p50 %.3f  p90 %.3f  p99 %.3f  max %.3f' % tuple(
        np.percentile(dep, [50, 90, 99, 100])))

    cov_dep = 1.0 - np.exp(-2.5 * dep_vis)
    cov_wear = 1.0 - np.exp(-2.5 * wear)

    gv, center = hex_cell(hull.uv(p))
    d_edge, _ = hex_edge(gv)
    tile_var = (hash2(center[..., 0], center[..., 1], 9.0) - 0.5) * 0.03
    base = np.array(base_rgb, f32)[None, None] + tile_var[..., None]
    if show_seams:
        seam = smoothstep(0.045, 0.0, d_edge)[..., None]
        base = base * (1 - seam) + np.array([0.30, 0.30, 0.29], f32) * seam

    white = np.array([0.92, 0.90, 0.87], f32)
    silica = np.array([0.86, 0.85, 0.83], f32)
    out = base * (1 - cov_wear[..., None]) + silica * cov_wear[..., None]
    out = out * (1 - cov_dep[..., None]) + white * cov_dep[..., None]
    return (np.clip(out, 0, 1) * 255).astype(np.uint8)


def side_by_side(*imgs):
    gap = np.full((imgs[0].shape[0], 12, 3), 255, np.uint8)
    parts = []
    for k, im in enumerate(imgs):
        if k:
            parts.append(gap)
        parts.append(im)
    return Image.fromarray(np.concatenate(parts, 1))


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else '.'
    os.makedirs(out, exist_ok=True)
    hull = Hull(radius=4.5, alpha_deg=65.0, tile=0.30)

    # Ligne au vent (courant axial), flanc a 45 deg (courant oblique),
    # flanc a 75 deg (courant presque circonferentiel, bord de la zone
    # tuilee) : meme reglage, seule la position sur la coque change.
    windward = render(hull, 0.0, 2.0)
    side45 = render(hull, np.radians(45) * hull.R, 2.0)
    side75 = render(hull, np.radians(75) * hull.R, 2.0)
    side_by_side(windward, side45, side75).save(os.path.join(out, 'tuiles.png'))

    # Variante sans usure de bord et sans depot, pour isoler les couches.
    only_dep = render(hull, np.radians(45) * hull.R, 2.0, gain_wear=0.0)
    only_wear = render(hull, np.radians(45) * hull.R, 2.0, gain_deposit=0.0)
    side_by_side(only_dep, only_wear).save(os.path.join(out, 'tuiles-couches.png'))
    print('ecrit : tuiles.png, tuiles-couches.png dans', os.path.abspath(out))


if __name__ == '__main__':
    main()
