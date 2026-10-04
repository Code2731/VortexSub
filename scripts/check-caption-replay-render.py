"""Check recorded-event Avalonia snapshots; requires existing Pillow, no desktop-frame claim."""
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
    checks = [{"name": "render replay passed", "passed": bool(report["passed"])}]

    def check(name, passed):
        checks.append({"name": name, "passed": bool(passed)})

    def region(frame, slot):
        box = frame[slot + "_box"]
        rect = (math.floor(box["x"]), math.floor(box["y"]),
                math.ceil(box["x"] + box["width"]), math.ceil(box["y"] + box["height"]))
        with Image.open(args.report.parent / (frame["name"] + ".png")) as image:
            return image.convert("RGBA").crop(rect)

    def ink(image):
        mask = Image.new("L", image.size)
        pixels = image.get_flattened_data() if hasattr(image, "get_flattened_data") else image.getdata()
        mask.putdata([255 if r > 190 and g > 140 and b < 200 and a > 100 else 0
                      for r, g, b, a in pixels])
        return mask.getbbox()

    previous = None
    for frame in report["frames"]:
        for slot in ("upper", "lower"):
            current = region(frame, slot)
            check(frame["name"] + " " + slot + " visible glyphs match text",
                  bool(ink(current)) == bool(frame[slot].strip()))
            if previous is None or previous["run"] != frame["run"]:
                continue
            check(frame["name"] + " " + slot + " fixed box", frame[slot + "_box"] == previous[slot + "_box"])
            old = region(previous, slot)
            if frame[slot] == previous[slot]:
                check(frame["name"] + " " + slot + " unchanged text pixels", current.tobytes() == old.tobytes())
            elif previous[slot] and frame[slot].startswith(previous[slot]):
                bounds = ink(old)
                check(frame["name"] + " " + slot + " append retains old glyph pixels",
                      bounds is not None and current.crop(bounds).tobytes() == old.crop(bounds).tobytes())
        previous = frame
    result = {"passed": all(item["passed"] for item in checks),
              "scope": "Saved render snapshots, not continuous Windows compositor frames", "checks": checks}
    output = args.report.with_name("pixel-report.json")
    output.write_text(json.dumps(result, indent=2), encoding="utf-8")
    failed = [item["name"] for item in checks if not item["passed"]]
    print(f"Caption replay pixels: {len(checks)} checks, {len(failed)} failures; {output}")
    for name in failed:
        print("FAIL:", name)
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
