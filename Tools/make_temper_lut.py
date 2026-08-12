"""Genere la LUT de revenu (temperature -> teinte) du shader de brulure.

Les treize couleurs proviennent du nuancier de tempering de l'acier fourni en
reference (Exemples/shema.png) : 210 C jaune paille -> 330 C gris-vert. L'axe
horizontal de la LUT est une temperature normalisee [0, 1] ou 210 C correspond
a 0.30 et 330 C a 0.90 ; en dessous, le metal est a peine teinte (alpha faible),
au-dessus on sature sur un gris terne.

Le canal alpha encode la force de la teinte : le shader s'en sert pour ne rien
afficher sur une piece restee froide.

Sortie : GameData/Losket/PluginData/temper_lut.png (256x8, RGBA). Le dossier
PluginData est ignore par le GameDatabase de KSP : la texture n'est pas
compressee en DXT par le jeu (ce qui poserait des bandes sur un degrade) et
c'est le plugin qui la charge lui-meme.

Usage :  python Tools/make_temper_lut.py
"""
from __future__ import annotations

import os
import struct
import zlib

WIDTH, HEIGHT = 256, 8

# (position 0..1, "RRGGBB", alpha 0..1) — points d'ancrage du degrade.
STOPS = [
    (0.00, "FFFFFF", 0.00),   # froid : aucune teinte
    (0.22, "FDF6D8", 0.06),   # premiere trace a peine visible
    (0.30, "F5E9B0", 0.45),   # 210 C  jaune clair
    (0.35, "EDD98A", 0.60),   # 220 C  paille
    (0.40, "EFCB62", 0.70),   # 230 C  jaune
    (0.45, "E7B04A", 0.78),   # 240 C  jaune fonce
    (0.50, "D19136", 0.84),   # 250 C  brun-jaune
    (0.55, "A55B24", 0.88),   # 260 C  brun-rouge
    (0.60, "8A4468", 0.90),   # 270 C  pourpre
    (0.65, "6E4E8E", 0.90),   # 280 C  violet
    (0.70, "4E5A94", 0.90),   # 290 C  bleu fonce
    (0.75, "6079A8", 0.90),   # 300 C  bleu Wedgwood
    (0.80, "8099BC", 0.88),   # 310 C  bleu clair
    (0.85, "96ABBE", 0.86),   # 320 C  bleu-gris
    (0.90, "9FB6AD", 0.84),   # 330 C  gris-vert
    (1.00, "939E98", 0.82),   # au-dela : gris terne
]


def sample(t: float) -> tuple[int, int, int, int]:
    for (p0, c0, a0), (p1, c1, a1) in zip(STOPS, STOPS[1:]):
        if p0 <= t <= p1:
            f = (t - p0) / (p1 - p0) if p1 > p0 else 0.0
            rgb0 = bytes.fromhex(c0)
            rgb1 = bytes.fromhex(c1)
            r, g, b = (round(x0 + (x1 - x0) * f) for x0, x1 in zip(rgb0, rgb1))
            return r, g, b, round((a0 + (a1 - a0) * f) * 255)
    r, g, b = bytes.fromhex(STOPS[-1][1])
    return r, g, b, round(STOPS[-1][2] * 255)


def png_chunk(tag: bytes, data: bytes) -> bytes:
    return (struct.pack(">I", len(data)) + tag + data
            + struct.pack(">I", zlib.crc32(tag + data)))


def main() -> None:
    row = b"".join(bytes(sample((x + 0.5) / WIDTH)) for x in range(WIDTH))
    raw = (b"\x00" + row) * HEIGHT

    png = (b"\x89PNG\r\n\x1a\n"
           + png_chunk(b"IHDR", struct.pack(">IIBBBBB", WIDTH, HEIGHT, 8, 6, 0, 0, 0))
           + png_chunk(b"IDAT", zlib.compress(raw, 9))
           + png_chunk(b"IEND", b""))

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = os.path.join(repo, "GameData", "Losket", "PluginData", "temper_lut.png")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "wb") as fh:
        fh.write(png)
    print(f"ecrit : {out} ({len(png)} octets)")


if __name__ == "__main__":
    main()
