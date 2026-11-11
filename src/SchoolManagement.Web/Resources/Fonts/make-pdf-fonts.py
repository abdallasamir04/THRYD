"""
Static TTF instances of the site's own fonts (wwwroot/fonts) for the PDF export, which needs static
TrueType files: Manrope (Latin) 400/600/700/800 and Noto Sans Arabic 400/700. Both fonts are under the
SIL Open Font License (copies next to this script) and have no Reserved Font Name.

Usage (needs fonttools and brotli):  python make-pdf-fonts.py ../../wwwroot/fonts .
"""
import sys
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

src = sys.argv[1]   # wwwroot/fonts
out = sys.argv[2]   # destination folder

jobs = [
    ("manrope-latin.woff2", "Manrope", [(400, "Regular"), (600, "SemiBold"), (700, "Bold"), (800, "ExtraBold")]),
    ("noto-sans-arabic.woff", "Noto Sans Arabic", [(400, "Regular"), (700, "Bold")]),
]


def rename(font, family, style):
    """Static family/style names (the variable source keeps its default instance's name)."""
    name = font["name"]
    ps = f"{family.replace(' ', '')}-{style}"
    for rec in list(name.names):
        if rec.nameID in (16, 17, 21, 22, 25):
            name.removeNames(nameID=rec.nameID)
    for nid, value in ((1, family), (2, "Regular"), (3, ps), (4, f"{family} {style}"), (6, ps), (16, family), (17, style)):
        name.setName(value, nid, 3, 1, 0x409)
        name.setName(value, nid, 1, 0, 0)


for file, family, weights in jobs:
    for wght, style in weights:
        font = TTFont(f"{src}/{file}")
        font.flavor = None
        if "fvar" in font:
            axes = {a.axisTag: a for a in font["fvar"].axes}
            value = min(max(wght, axes["wght"].minValue), axes["wght"].maxValue)
            font = instancer.instantiateVariableFont(font, {"wght": value}, updateFontNames=False)
        font["OS/2"].usWeightClass = wght
        rename(font, family, style)
        path = f"{out}/{family.replace(' ', '')}-{style}.ttf"
        font.save(path)
        print(path, len(font.getGlyphOrder()), "glyphs")
