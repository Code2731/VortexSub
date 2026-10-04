"""Check saved Avalonia fixture frames. Requires Pillow; no desktop-frame claim."""

import argparse
import json
import math
from pathlib import Path

from PIL import Image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding="utf-8"))
    checks = []

    def check(name, passed):
        checks.append({"name": name, "passed": bool(passed)})

    def region(frame, slot):
        box = frame[slot + "_box"]
        rect = (math.floor(box["x"]), math.floor(box["y"]),
                math.ceil(box["x"] + box["width"]),
                math.ceil(box["y"] + box["height"]))
        with Image.open(args.report.parent / (frame["name"] + ".png")) as image:
            return image.convert("RGBA").crop(rect)

    def ink_bounds(image):
        mask = Image.new("L", image.size)
        # The production translation brush is gold. Excludes background/header.
        pixels = image.get_flattened_data() if hasattr(image, "get_flattened_data") else image.getdata()
        mask.putdata([255 if r > 190 and g > 140 and b < 200 and a > 100 else 0
                      for r, g, b, a in pixels])
        return mask.getbbox()

    check("render fixture passed", report["passed"])
    frames = report["frames"]
    assembly_before = next((frame for frame in frames if frame["name"] == "13-assembly-before"), None)
    assembly_held = next((frame for frame in frames if frame["name"] == "14-assembly-held"), None)
    if assembly_before and assembly_held:
        for slot in ("upper", "lower"):
            check("assembly held " + slot + " glyphs unchanged", region(assembly_before, slot).tobytes() == region(assembly_held, slot).tobytes())
    recovery_before = next((frame for frame in frames if frame["name"] == "16-recovery-before"), None)
    recovery_held = next((frame for frame in frames if frame["name"] == "17-recovery-held"), None)
    if recovery_before and recovery_held:
        for slot in ("upper", "lower"):
            check("unit recovery held " + slot + " glyphs unchanged",
                  region(recovery_before, slot).tobytes() == region(recovery_held, slot).tobytes())
    rapid_before = next((frame for frame in frames if frame["name"] == "19-rapid-before"), None)
    rapid_held = next((frame for frame in frames if frame["name"] == "20-rapid-held"), None)
    if rapid_before and rapid_held:
        for slot in ("upper", "lower"):
            check("rapid correction held " + slot + " glyphs unchanged",
                  region(rapid_before, slot).tobytes() == region(rapid_held, slot).tobytes())
    first = next(frame for frame in frames if frame["name"] == "01-first")
    initial = region(first, "upper")
    previous = None
    for frame in frames:
        for slot in ("upper", "lower"):
            check(frame["name"] + " " + slot + " ink matches nonempty text",
                  bool(ink_bounds(region(frame, slot))) == bool(frame[slot].strip()))
        if not frame["name"].startswith("append-"):
            continue
        check(frame["name"] + " upper pixels unchanged",
              region(frame, "upper").tobytes() == initial.tobytes())
        if previous and previous["lower"].strip():
            old = region(previous, "lower")
            bounds = ink_bounds(old)
            new = region(frame, "lower")
            check(frame["name"] + " existing lower glyph pixels unchanged",
                  bounds is not None and old.crop(bounds).tobytes() == new.crop(bounds).tobytes())
        previous = frame
    result = {"passed": all(item["passed"] for item in checks),
              "scope": "Saved Avalonia render snapshots, not continuous Windows compositor frames",
              "checks": checks}
    output = args.report.with_name("pixel-report.json")
    output.write_text(json.dumps(result, indent=2), encoding="utf-8")
    failed = [item["name"] for item in checks if not item["passed"]]
    print(f"Caption render pixels: {len(checks)} checks, {len(failed)} failures; {output}")
    for name in failed:
        print("FAIL:", name)
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
