"""Align the bundled Regular font's cmap with the Filled icon IDs used by WinUI.

Run after replacing either upstream TTF. Requires fonttools for asset preparation
only. Glyph outlines and names stay upstream Fluent System Icons (MIT).
"""
import argparse
import json
import re
from pathlib import Path

from fontTools.ttLib import TTFont

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src/InkCanvas.App/Assets"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    with TTFont(ASSETS / "FluentSystemIcons.ttf") as filled, TTFont(ASSETS / "FluentSystemIcons-Regular.ttf") as regular:
        names = set(regular.getGlyphOrder())
        mapping = {
            code: name.removesuffix("_filled") + "_regular"
            for code, name in filled.getBestCmap().items()
            if name.endswith("_filled") and name.removesuffix("_filled") + "_regular" in names
        }
        catalog = json.loads((ROOT / "parity/icon-map.json").read_text(encoding="utf-8"))
        required = {int(icon["fluentCodepoint"][2:], 16) for icon in catalog["segoe"] if "fluentCodepoint" in icon}
        required.add(0xF3E0)
        glyphs = (ROOT / "src/InkCanvas.Controls/Controls/Glyphs.cs").read_text(encoding="utf-8-sig")
        required.update(int(code, 16) for code in re.findall(r'public const string \w+ = "\\u([0-9A-Fa-f]{4})";', glyphs))
        missing = required - mapping.keys()
        if missing:
            raise ValueError(f"Regular variants missing for application icons: {sorted(missing)}")
        if args.check:
            actual = regular.getBestCmap()
            if any(actual.get(code) != name for code, name in mapping.items()):
                raise ValueError("Regular cmap differs from the WinUI icon IDs. Run this script without --check.")
        else:
            for table in regular["cmap"].tables:
                if table.isUnicode():
                    table.cmap = {code: name for code, name in mapping.items() if table.format not in (4, 6) or code <= 0xFFFF}
            regular.save(ASSETS / "FluentSystemIcons-Regular.ttf")
        print(f"Verified {len(mapping)} matching Filled/Regular icon IDs, including {len(required)} application icons.")


if __name__ == "__main__":
    main()
