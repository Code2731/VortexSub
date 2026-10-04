"""Classify English paced ASR revisions; lexical flags are not semantic judgements."""
import argparse
from collections import Counter
import csv
import json
from pathlib import Path
import re


def tokens(text):
    return re.findall(r"\w+(?:['’]\w+)?", text.casefold())


def classify(previous, current):
    old, new = tokens(previous), tokens(current)
    if previous == current:
        kind = "identical"
    elif " ".join(previous.casefold().split()) == " ".join(current.casefold().split()):
        kind = "case_or_whitespace_only"
    elif old == new:
        # Punctuation can change meaning. This is never an admission permission.
        kind = "punctuation_only"
    elif old and len(new) > len(old) and new[:len(old)] == old:
        kind = "lexical_extension"
    else:
        kind = "lexical_rewrite"
    def negatives(words):
        return Counter(w for w in words if w in {"not", "no", "never", "without", "cannot"}
                       or w.replace("’", "'").endswith("n't"))
    def conditions(words):
        return Counter(w for w in words if w in {"if", "only", "until", "unless", "before", "after"})
    return {"kind": kind, "negation_tokens_changed": negatives(old) != negatives(new),
            "condition_tokens_changed": conditions(old) != conditions(new)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reports", type=Path, nargs="+")
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    if len(args.reports) > 32:
        parser.error("At most 32 reports")
    args.output_dir.mkdir(parents=True, exist_ok=True)
    summaries, transitions = [], []
    for path in args.reports:
        if path.stat().st_size > 32 * 1024 * 1024:
            raise ValueError("Report exceeds 32 MiB")
        report = json.loads(path.read_text(encoding="utf-8"))
        counts, holds = Counter(), Counter()
        flags = Counter()
        partials = 0
        for run_index, run in enumerate(report["runs"]):
            previous = None
            for event in run["events"]:
                if event["message"]["event"] != "source.partial":
                    continue
                payload = event["message"]["payload"]
                current = payload["record"]["source"]
                holds[payload.get("preview_hold_reason") or "UNREPORTED"] += 1
                partials += 1
                if previous is not None:
                    result = classify(previous, current)
                    counts[result["kind"]] += 1
                    for flag in ("negation_tokens_changed", "condition_tokens_changed"):
                        flags[flag] += result[flag]
                    transitions.append({"report": str(path), "run_index": run_index,
                        "round": run["round"], "supported_preview": run["supported_preview"],
                        "at_s": event["at_s"], "previous": previous, "current": current,
                        "stable_source": payload["record"]["stable_source"],
                        "hold_reason": payload.get("preview_hold_reason") or "UNREPORTED", **result})
                previous = current
        summaries.append({"report": str(path), "runs": len(report["runs"]),
            "partials": partials, "transition_counts": dict(counts), "hold_observations": dict(holds),
            "lexical_flags": dict(flags)})
    result = {"language_scope": "English lexical analysis",
        "scope": "Observed transitions only; hold counts are not durations; no counterfactual speed or semantic safety claim",
        "reports": summaries}
    (args.output_dir / "summary.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    if transitions:
        with (args.output_dir / "transitions.csv").open("w", encoding="utf-8-sig", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=list(transitions[0]))
            writer.writeheader()
            writer.writerows(transitions)
    print(f"ASR stability: {len(summaries)} reports, {len(transitions)} transitions; {args.output_dir}")


if __name__ == "__main__":
    main()
