"""Compare file probes using identical normalization; never infer live latency."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import unicodedata


def normalize(text):
    return "".join(c.lower() for c in unicodedata.normalize("NFKC", text) if c.isalnum())


def distance(left, right):
    row = list(range(len(right) + 1))
    for i, a in enumerate(left, 1):
        next_row = [i]
        for j, b in enumerate(right, 1):
            next_row.append(min(next_row[-1] + 1, row[j] + 1, row[j - 1] + (a != b)))
        row = next_row
    return row[-1]


def median(values):
    return statistics.median(values) if values else None


def summarize(report):
    groups = {}
    for language in sorted({r["language"] for r in report["results"]}):
        rows = [r for r in report["results"] if r["language"] == language and r["kind"] in ("speech", "synthetic_tts")]
        full = [r for r in rows if r["mode"] == "full"]
        partial = [r for r in rows if r["mode"] == "prefix"]
        edits, units, exact = 0, 0, 0
        for row in full:
            reference = normalize(row["reference"])
            text = normalize(row["observations"][-1]["text"])
            edits += distance(reference, text)
            units += len(reference)
            exact += reference == text
        first_text, repeated_prefix, revisions, totals, finish_lag = [], [], [], [], []
        for row in partial:
            previous, revision_count, first, repeated = "", 0, None, None
            for observation in row["observations"]:
                text = observation["text"].strip()
                if first is None and normalize(text):
                    first = observation["simulated_available_s"]
                common = 0
                for a, b in zip(previous, text):
                    if a != b:
                        break
                    common += 1
                if previous and common < len(previous):
                    revision_count += 1
                if repeated is None and len(normalize(text[:common])) >= 4:
                    repeated = observation["simulated_available_s"]
                previous = text
            if first is not None:
                first_text.append(first)
            if repeated is not None:
                repeated_prefix.append(repeated)
            revisions.append(revision_count)
            totals.append(sum(o["decode_s"] for o in row["observations"]))
            finish_lag.append(row["observations"][-1]["simulated_available_s"] - row["audio_s"])
        groups[language] = {
            "full_runs": len(full), "prefix_runs": len(partial),
            "full_decode_median_s": median([r["observations"][0]["decode_s"] for r in full]),
            "full_normalized_cer_micro": edits / units if units else None,
            "full_exact_normalized_runs": exact,
            "prefix_decode_median_s": median([o["decode_s"] for r in partial for o in r["observations"]]),
            "prefix_decode_total_per_clip_median_s": median(totals),
            "prefix_first_nonempty_simulated_median_s": median(first_text),
            "prefix_first_repeated_4chars_simulated_median_s": median(repeated_prefix),
            "prefix_repeated_4chars_runs": len(repeated_prefix),
            "prefix_revision_events_median": median(revisions),
            "prefix_final_lag_simulated_median_s": median(finish_lag),
        }
    negative = [r for r in report["results"] if not normalize(r["reference"]) and r["mode"] == "full"]
    return {"candidate": report["candidate"], "provider": report["provider"],
            "threads": report["threads"], "languages": groups,
            "negative_full_runs": len(negative),
            "negative_unexpected_text_runs": sum(bool(normalize(r["observations"][-1]["text"])) for r in negative),
            "negative_control_marker_only_runs": sum(r["observations"][-1]["text"].strip().lower() in ("[blank_audio]", "[silence]") for r in negative)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reports", type=Path, nargs="+")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    reports = [json.loads(p.read_text(encoding="utf-8")) for p in args.reports]
    if len({r["fixture_manifest_sha256"] for r in reports}) != 1:
        raise ValueError("Fixture manifest hashes differ")
    signatures = [{(r["fixture_id"], r["language"], r["kind"], r["reference"], r["round"], r["mode"], r["audio_s"],
                    tuple(o["audio_end_s"] for o in r["observations"])) for r in report["results"]} for report in reports]
    if any(signature != signatures[0] for signature in signatures[1:]):
        raise ValueError("Fixture rows, rounds, modes, or prefix boundaries differ")
    summary = {"normalization": "NFKC lowercase alphanumeric, no whitespace/punctuation; literal digits versus spelled numbers differ",
               "timing_scope": "file decode; sequential prefix simulation is not worker or display latency",
               "prefix_metric": "4 alphanumeric characters shared with previous observation; not a product stability decision",
               "sources": [{"path": str(p.resolve()), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in args.reports],
               "candidates": [summarize(r) for r in reports]}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary["candidates"], ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
