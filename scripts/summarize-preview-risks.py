"""Summarize controlled native/HTTP previews; preserve texts for manual review."""
import argparse
import json
from pathlib import Path
import statistics


def median(values):
    values = [v for v in values if v is not None]
    return statistics.median(values) if values else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--catalog", type=Path, required=True)
    args = parser.parse_args()
    catalog = json.loads(args.catalog.read_text(encoding="utf-8"))
    cases = []
    for item in catalog["texts"]:
        report = json.loads((args.directory / item["id"] / "report.json").read_text(encoding="utf-8"))
        runtime = json.loads((args.directory / item["id"] / "runtime.json").read_text(encoding="utf-8"))
        if runtime["fixture_reference"] != item["text"]:
            raise ValueError(f"Fixture text differs: {item['id']}")
        runs = report["runs"]
        if len(runs) != 2 * report["rounds_requested"]:
            raise ValueError(f"Incomplete case: {item['id']}")
        groups = {}
        for enabled in (False, True):
            selected = [r for r in runs if r["supported_preview"] == enabled]
            if len(selected) != report["rounds_requested"] or len({r["round"] for r in selected}) != len(selected):
                raise ValueError("Missing/duplicate conditions")
            previews, finals, revision_counts = {}, [], []
            complete_times = []
            for run in selected:
                if not run["pad_short_partials"] or not run["adaptive"] or run["decode_window"]:
                    raise ValueError("Comparison configuration differs")
                previous, revisions, full_time = "", 0, None
                for event in run["events"]:
                    msg = event["message"]
                    if msg["event"] == "source.partial":
                        text = "".join(c.lower() for c in msg["payload"]["record"]["source"] if c.isalnum())
                        revisions += bool(previous and not text.startswith(previous))
                        previous = text
                    if msg["event"] == "translation.updated":
                        record = msg["payload"]["record"]
                        if record["translation_is_preview"]:
                            key = (record["translation_source"], record["translation"])
                            previews.setdefault(key, []).append({"round":run["round"],"at_s":event["at_s"]})
                        if record["translation_source"] == item["text"] or record["source_state"] == "Final" and record["source"] == item["text"]:
                            full_time = full_time if full_time is not None else event["at_s"]
                revision_counts.append(revisions)
                complete_times.append(full_time)
                finals.append({"round":run["round"],"source":run["final_record"]["source"],"translation":run["final_record"]["translation"]})
            groups["on" if enabled else "off"] = {
                "first_translation_median_s": median([r["first_translation_s"] for r in selected]),
                "first_stable_median_s": median([r["first_stable_s"] for r in selected]),
                "exact_full_source_translation_median_s": median(complete_times),
                "exact_full_source_translation_runs": sum(t is not None for t in complete_times),
                "final_translation_median_s": median([r["final_translation_s"] for r in selected]),
                "lexical_revision_events_median": median(revision_counts),
                "translation_requests_median": median([len(r["requests"]) for r in selected]),
                "runs_with_applied_preview": sum(any(e["message"]["event"] == "translation.updated" and e["message"]["payload"]["record"]["translation_is_preview"] for e in r["events"]) for r in selected),
                "previews": [{"source":s,"translation":t,"occurrences":v} for (s,t),v in previews.items()],
                "finals": finals,
            }
        cases.append({**item,"audio_s":runs[0]["audio_s"],"groups":groups,"manual_review":"PENDING"})
    summary = {"quality_gate_passed":False,"note":"Synthetic controls; exact-source matching is not semantic grading; applied previews and finals require review.","cases":cases}
    (args.directory / "summary.json").write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding="utf-8")
    for case in cases:
        print(case["id"], "first translation off/on:",case["groups"]["off"]["first_translation_median_s"],case["groups"]["on"]["first_translation_median_s"])


if __name__ == "__main__":
    main()
