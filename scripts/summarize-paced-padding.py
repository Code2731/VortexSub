"""Summarize paired native/HTTP padding runs; quality review remains manual."""
import argparse
import json
from pathlib import Path
import statistics


def lexical(text):
    return "".join(c.lower() for c in text if c.isalnum())


def metrics(run):
    previous, revisions, partials, native_s = "", 0, 0, 0.0
    for event in run["events"]:
        message = event["message"]
        payload = message["payload"]
        if message["event"] == "asr.completed":
            native_s += payload["decode_s"]
        if message["event"] == "source.partial":
            text = lexical(payload["record"]["source"])
            revisions += bool(previous and not text.startswith(previous))
            previous = text
            partials += 1
    return {**{name: run[name] for name in ("first_text_s", "first_stable_s", "first_translation_s", "final_translation_s")},
            "native_decode_total_s": native_s, "partial_events": partials,
            "lexical_revision_events": revisions, "translation_requests": len(run["requests"])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding="utf-8"))
    runs = report["runs"]
    pairs = {}
    for run in runs:
        if not run["adaptive"] or run["decode_window"]:
            raise ValueError("Requires adaptive padding-only comparison")
        key = (run["round"], run["pad_short_partials"])
        if key in pairs:
            raise ValueError("Duplicate condition")
        pairs[key] = metrics(run)
    if len(runs) != report["rounds_requested"] * 2:
        raise ValueError("Incomplete comparison")
    groups = {}
    for enabled in (False, True):
        values = [pairs[(r, enabled)] for r in range(1, report["rounds_requested"] + 1)]
        groups["padding_on" if enabled else "padding_off"] = {
            name: {"median": statistics.median(v[name] for v in values),
                   "min": min(v[name] for v in values), "max": max(v[name] for v in values)}
            for name in values[0]
        }
    changes = {name: statistics.median(pairs[(r, True)][name] - pairs[(r, False)][name]
                                     for r in range(1, report["rounds_requested"] + 1)) for name in next(iter(pairs.values()))}
    summary = {"groups": groups, "paired_median_on_minus_off": changes,
               "final_sources": sorted({r["final_record"]["source"] for r in runs}),
               "final_translations": sorted({r["final_record"]["translation"] for r in runs}),
               "quality_gate_passed": False,
               "note": "Known-end file replay, actual owners/scheduler/HTTP; no capture/VAD/display/game. Review request/translation texts manually."}
    args.output.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
