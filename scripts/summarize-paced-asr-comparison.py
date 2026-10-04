"""Compare existing paced ASR reports; translation semantics require manual review."""
import argparse
import csv
import json
from pathlib import Path
import statistics
import re


TIMINGS = ("first_text_s", "first_stable_s", "first_translation_s",
           "final_asr_s", "final_translation_s")


def surface_words(text):
    return re.findall(r"\w+(?:['’]\w+)?", text.casefold())


def translated_records(run):
    for event in run["events"]:
        if event["message"]["event"] != "translation.updated":
            continue
        record = event["message"]["payload"]["record"]
        if record.get("translation"):
            yield event["at_s"], record


def timing_summary(values):
    return {"n": len(values), "median": statistics.median(values) if values else None,
            "min": min(values) if values else None, "max": max(values) if values else None}


def summarize(report, runtime, report_path):
    rows, translations = [], []
    for supported in (False, True):
        runs = [run for run in report["runs"] if run["supported_preview"] == supported]
        if not runs:
            continue
        row = {"fixture": runtime["fixture_id"], "asr_model": runtime["asr_model"],
               "supported_preview": supported, "runs": len(runs), "report": str(report_path)}
        for field in TIMINGS:
            values = [run[field] for run in runs if run.get(field) is not None]
            row[field] = timing_summary(values)
        reference = runtime.get("fixture_reference")
        full_source_times = []
        if reference is not None:
            for run in runs:
                for at_s, record in translated_records(run):
                    source = record["translation_source"] if record["translation_is_preview"] else record["source"]
                    if surface_words(source) == surface_words(reference):
                        full_source_times.append(at_s)
                        break
        # This matches the input words, not the translation's meaning.
        row["first_reference_source_translation_s"] = timing_summary(full_source_times)
        row["final_source_surface_matches"] = (sum(
            surface_words(run["final_record"]["source"]) == surface_words(reference)
            for run in runs) if reference is not None else None)
        row["final_translation_done"] = sum(
            run["final_record"]["translation_state"] == "Done" for run in runs)
        rows.append(row)
        for run in runs:
            for at_s, record in translated_records(run):
                translations.append({"fixture": runtime["fixture_id"],
                    "asr_model": runtime["asr_model"], "supported_preview": supported,
                    "round": run["round"], "at_s": at_s,
                    "preview": record["translation_is_preview"],
                    "request_id": record["translation_request_id"],
                    "reference": reference,
                    "translation_source": record["translation_source"] if record["translation_is_preview"] else record["source"],
                    "translation": record["translation"], "report": str(report_path)})
    return rows, translations


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reports", type=Path, nargs="+")
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    if len(args.reports) > 32:
        parser.error("At most 32 reports")
    rows, translations = [], []
    configurations = set()
    fixture_inputs, asr_weights = {}, {}
    for path in args.reports:
        runtime_path = path.parent / "runtime.json"
        if path.stat().st_size > 32 * 1024 * 1024 or runtime_path.stat().st_size > 1024 * 1024:
            raise ValueError("Report/runtime too large")
        report = json.loads(path.read_text(encoding="utf-8"))
        runtime = json.loads(runtime_path.read_text(encoding="utf-8"))
        fixture = runtime["fixture_id"]
        fixture_input = (runtime["wav_sha256"], runtime.get("fixture_reference"))
        if fixture in fixture_inputs and fixture_inputs[fixture] != fixture_input:
            raise ValueError("Same fixture must have matching audio/reference")
        fixture_inputs[fixture] = fixture_input
        asr_model = runtime["asr_model"]
        if asr_model in asr_weights and asr_weights[asr_model] != runtime["asr_weights_sha256"]:
            raise ValueError("Same ASR model must have matching weights")
        asr_weights[asr_model] = runtime["asr_weights_sha256"]
        configurations.add(tuple(runtime[key] for key in (
            "backend", "translation_model", "input_profile", "translation_weights_sha256",
            "server_sha256", "worker_sha256", "fixture_manifest_sha256",
            "supported_compare", "adaptive_compare", "padding_compare", "decode_window_compare",
            "isolated_translation_context")))
        summary, review = summarize(report, runtime, path)
        rows.extend(summary)
        translations.extend(review)
    if len(configurations) != 1:
        raise ValueError("Comparison requires matching runtime/profile/scheduler/manifest hashes")
    args.output_dir.mkdir(parents=True, exist_ok=True)
    (args.output_dir / "summary.json").write_text(json.dumps({
        "scope": "PCM start clock; missing timings counted separately; surface matching is not semantic quality",
        "quality_gate_passed": False, "rows": rows}, indent=2, ensure_ascii=False), encoding="utf-8")
    if translations:
        with (args.output_dir / "translation-review.csv").open("w", encoding="utf-8-sig", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=list(translations[0]))
            writer.writeheader()
            writer.writerows(translations)
    print(f"Paced ASR comparison: {len(rows)} conditions, {len(translations)} translations; manual review required")


if __name__ == "__main__":
    main()
