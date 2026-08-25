"""Genere l'icone Losket de la barre d'applications de KSP.

38x38 RGBA : une capsule stylisee vue de profil, flanc gauche noirci de suie
et bouclier orange de rentree — les deux effets du mod en une vignette.

Sortie : GameData/Losket/Textures/toolbar.png (chargee par le GameDatabase,
donc HORS de PluginData).

Usage :  python Tools/make_toolbar_icon.py
"""
from __future__ import annotations

import math
import os
import struct
import zlib

SIZE = 38


def pixel(x: float, y: float) -> tuple[int, int, int, int]:
    # Repere centre, y vers le haut.
    cx = x - SIZE / 2.0
    cy = SIZE / 2.0 - y

    # Silhouette : cone tronque (capsule) pointe en haut.
    top, bottom = 12.0, -9.0
    if bottom - 3.5 <= cy <= top:
        half = 4.0 + (top - cy) * 0.45 if cy > bottom else 13.45
        if cy >= bottom:
            half = 4.0 + (top - cy) * 0.45
            if abs(cx) <= half:
                # Flanc gauche noirci par la suie, degrade vers le blanc.
                t = max(0.0, min(1.0, (cx + half) / (2.0 * half)))
                soot = (1.0 - t) ** 1.5
                base = 235 - int(190 * soot)
                return base, base, max(base - 4, 0), 255
        else:
            # Bouclier : arc orange sous la capsule.
            if abs(cx) <= 13.45 * (1.0 - (bottom - cy) / 4.0):
                heat = (bottom - cy) / 3.5
                r = 255
                g = int(150 - 90 * heat)
                b = int(40 - 30 * heat)
                return r, max(g, 30), max(b, 5), 255

    # Trois grains de poussiere en orbite basse autour de la capsule.
    for gx, gy, gr in ((-14.0, -6.0, 1.6), (14.5, 2.0, 1.2), (-13.0, 7.5, 1.1)):
        if math.hypot(cx - gx, cy - gy) <= gr:
            return 200, 185, 160, 255

    return 0, 0, 0, 0


def png_chunk(tag: bytes, data: bytes) -> bytes:
    return (struct.pack(">I", len(data)) + tag + data
            + struct.pack(">I", zlib.crc32(tag + data)))


def main() -> None:
    raw = b"".join(
        b"\x00" + b"".join(bytes(pixel(x + 0.5, y + 0.5)) for x in range(SIZE))
        for y in range(SIZE))

    png = (b"\x89PNG\r\n\x1a\n"
           + png_chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
           + png_chunk(b"IDAT", zlib.compress(raw, 9))
           + png_chunk(b"IEND", b""))

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = os.path.join(repo, "GameData", "Losket", "Textures", "toolbar.png")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "wb") as fh:
        fh.write(png)
    print(f"ecrit : {out} ({len(png)} octets)")


if __name__ == "__main__":
    main()
