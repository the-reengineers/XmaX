#!/usr/bin/env python3
"""Generate .ico file from a source image (PNG/SVG) at multiple sizes."""

import argparse
import sys
from pathlib import Path

try:
    from PIL import Image
except ImportError:
    print("Error: Pillow is required. Install with: pip install Pillow", file=sys.stderr)
    sys.exit(1)

SIZES = [(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]


def generate_ico(source: Path, dest: Path) -> None:
    img = Image.open(source)
    if img.mode != "RGBA":
        img = img.convert("RGBA")
    dest.parent.mkdir(parents=True, exist_ok=True)
    img.save(str(dest), format="ICO", sizes=SIZES)
    print(f"Generated {dest} ({len(SIZES)} sizes)")


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate .ico from source image")
    parser.add_argument("source", type=Path, help="Source image (PNG)")
    parser.add_argument("dest", type=Path, help="Output .ico path")
    args = parser.parse_args()

    if not args.source.exists():
        print(f"Error: source not found: {args.source}", file=sys.stderr)
        sys.exit(1)

    generate_ico(args.source, args.dest)


if __name__ == "__main__":
    main()
