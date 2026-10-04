"""Check saved real-time session fixture renders; no continuous compositor claim."""
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
                math.ceil(box["x"] + box["width"]), math.ceil(box["y"] + box["height"]))
        with Image.open(args.report.parent / (frame["name"] + ".png")) as picture:
            return picture.convert("RGBA").crop(rect)

    def has_ink(picture):
        pixels = picture.get_flattened_data() if hasattr(picture, "get_flattened_data") else picture.getdata()
        return any(r > 190 and g > 140 and b < 200 and a > 100 for r, g, b, a in pixels)

    expected = {
        "01-running": "문 앞에서 기다려.", "02-held": "문 앞에서 기다려.",
        "03-paused": "", "04-resumed": "재개한 자막.", "05-drained": "",
        "06-restarted": "새 세션 자막.", "07-worker-death": "",
        "08-recovered": "연결 복구 자막.", "09-disconnected": "",
        "burst-first": "첫 문장.", "burst-middle": "중간 문장.",
        "burst-last": "마지막 문장.", "burst-drained": "",
    }
    lower_expected = {"02-held": "오른쪽으로 가세요.", "burst-first": "중간 문장.",
                      "burst-middle": "중간 문장."}
    expected["burst-middle"] = ""
    check("real-time session fixture passed", report["passed"])
    frames = {frame["name"]: frame for frame in report["frames"]}
    check("all expected session snapshots present", set(frames) == set(expected))
    if set(frames) == set(expected):
        initial = frames["01-running"]
        for name, text in expected.items():
            frame = frames[name]
            check(name + " text matches lifecycle expectation", frame["upper"] == text and frame["lower"] == lower_expected.get(name, ""))
            for slot in ("upper", "lower"):
                check(name + " " + slot + " geometry fixed", frame[slot + "_box"] == initial[slot + "_box"])
                check(name + " " + slot + " rendered ink matches text", has_ink(region(frame, slot)) == bool(frame[slot].strip()))
        for slot in ("upper",):
            check("pending input preserves " + slot + " pixels",
                  region(initial, slot).tobytes() == region(frames["02-held"], slot).tobytes())
    failed = [check["name"] for check in checks if not check["passed"]]
    output = args.report.with_name("pixel-report.json")
    output.write_text(json.dumps({"passed": not failed,
        "scope": "Saved Avalonia renders from real-time mock-input session fixture; not physical continuous frames",
        "checks": checks}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Caption session render: {len(checks)} checks, {len(failed)} failures; {output}")
    for name in failed:
        print("FAIL:", name)
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
