"""Pinned, opt-in SenseVoice file diagnostics; never changes the live backend."""

import argparse
import hashlib
import importlib.metadata
import json
import platform
from pathlib import Path
import shutil
import subprocess
import sys
import time
import unicodedata
import urllib.request
import wave

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "models/asr-candidates/sensevoice-small-int8"
RUNTIME = ROOT / "models/asr-candidates/runtime"


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verify(path, asset):
    if not path.is_file() or path.stat().st_size != asset["bytes"]:
        raise ValueError(f"Missing/wrong-size asset: {path}")
    if sha256(path) != asset["sha256"]:
        raise ValueError(f"SHA256 mismatch: {path}")


def install(manifest):
    if sys.platform != "win32" or sys.version_info[:2] != (3, 12) or platform.machine().lower() not in ("amd64", "x86_64"):
        raise ValueError("Pinned wheels require Windows x64 CPython 3.12")
    if RUNTIME.exists():
        raise ValueError(f"Runtime directory already exists; inspect it before reinstalling: {RUNTIME}")
    ASSETS.mkdir(parents=True, exist_ok=True)
    for asset in manifest["assets"]:
        target = ASSETS / asset["name"]
        if target.exists():
            verify(target, asset)
            continue
        temporary = target.with_suffix(target.suffix + ".part")
        print(f"Downloading {asset['name']} ({asset['bytes']} bytes)", flush=True)
        with urllib.request.urlopen(asset["url"], timeout=60) as source, temporary.open("wb") as output:
            shutil.copyfileobj(source, output)
        verify(temporary, asset)
        temporary.replace(target)
    wheels = [str(ASSETS / a["name"]) for a in manifest["assets"] if a["name"].endswith(".whl")]
    subprocess.run([sys.executable, "-m", "pip", "install", "--no-index", "--no-deps", "--disable-pip-version-check", "--target", str(RUNTIME), *wheels], check=True)
    print("Installed private runtime; existing Python packages were not modified.")


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


def probe(args, manifest):
    if not 1 <= args.rounds <= 20 or not 1 <= args.threads <= 64:
        raise ValueError("rounds must be 1..20; threads must be 1..64")
    for asset in manifest["assets"]:
        verify(ASSETS / asset["name"], asset)
    if not RUNTIME.is_dir():
        raise ValueError("Private runtime missing; download approval is required before setup")
    sys.path.insert(0, str(RUNTIME))
    import numpy as np
    import sherpa_onnx

    if not Path(sherpa_onnx.__file__).resolve().is_relative_to(RUNTIME.resolve()):
        raise ValueError("Unexpected sherpa_onnx import location")
    for package in ("sherpa-onnx", "sherpa-onnx-core"):
        if importlib.metadata.version(package) != manifest["runtime_version"]:
            raise ValueError(f"Unexpected runtime version: {package}")
    fixtures_path = args.fixtures.resolve()
    fixtures = json.loads(fixtures_path.read_text(encoding="utf-8"))["fixtures"]
    loaded, recognizers, results = [], {}, []
    for fixture in fixtures:
        path = (fixtures_path.parent / fixture["path"]).resolve()
        if sha256(path) != fixture["sha256"]:
            raise ValueError(f"Fixture hash mismatch: {path}")
        with wave.open(str(path), "rb") as source:
            if (source.getnchannels(), source.getsampwidth(), source.getframerate(), source.getcomptype()) != (1, 2, 16000, "NONE"):
                raise ValueError(f"Requires 16kHz mono PCM16: {path}")
            audio = np.frombuffer(source.readframes(source.getnframes()), dtype="<i2").astype(np.float32) / 32768.0
        if len(audio) > 16000 * 60:
            raise ValueError("Diagnostic inputs are limited to 60 seconds")
        loaded.append((fixture, audio))
    load_times = {}
    for language in sorted({fixture["language"] for fixture, _ in loaded}):
        if language not in ("en", "ko", "ja", "zh", "yue"):
            raise ValueError(f"Unsupported language: {language}")
        started = time.perf_counter()
        recognizers[language] = sherpa_onnx.OfflineRecognizer.from_sense_voice(
            model=str(ASSETS / "model.int8.onnx"), tokens=str(ASSETS / "tokens.txt"),
            num_threads=args.threads, provider="cpu", language=language, use_itn=True)
        load_times[language] = time.perf_counter() - started

    def decode(recognizer, audio):
        started = time.perf_counter()
        stream = recognizer.create_stream()
        stream.accept_waveform(16000, audio)
        recognizer.decode_stream(stream)
        return stream.result.text, time.perf_counter() - started

    for round_index in range(args.rounds):
        # Alternate mode order to expose warm-cache/order effects in raw results.
        modes = ("full", "prefix") if round_index % 2 == 0 else ("prefix", "full")
        for fixture, audio in loaded:
            recognizer = recognizers[fixture["language"]]
            for mode in modes:
                ends = [len(audio)]
                if mode == "prefix":
                    ends = list(range(12800, len(audio), 4096)) + [len(audio)]
                observations, previous, available_s, revisions = [], "", 0.0, 0
                for end in ends:
                    text, elapsed = decode(recognizer, audio[:end])
                    # Sequential replay simulation; no wall-clock audio/capture is involved.
                    available_s = max(end / 16000, available_s) + elapsed
                    common = 0
                    for a, b in zip(previous, text):
                        if a != b:
                            break
                        common += 1
                    if previous and common < len(previous):
                        revisions += 1
                    observations.append({"audio_end_s": end / 16000, "decode_s": elapsed,
                                         "simulated_available_s": available_s, "text": text,
                                         "shared_prefix_chars": common,
                                         "removed_chars": len(previous) - common})
                    previous = text
                reference, hypothesis = normalize(fixture["reference"]), normalize(previous)
                results.append({"fixture_id": fixture["id"], "language": fixture["language"],
                                "kind": fixture["kind"], "round": round_index + 1, "mode": mode,
                                "audio_s": len(audio) / 16000, "reference": fixture["reference"],
                                "normalized_cer": distance(reference, hypothesis) / len(reference) if reference else None,
                                "unexpected_text": bool(hypothesis) if not reference else None,
                                "revision_events": revisions, "observations": observations})
                print(f"round={round_index + 1} {fixture['id']} {mode}: {previous}", flush=True)
    report = {"schema_version": 1, "candidate": manifest["candidate"], "provider": "cpu",
              "python": sys.version, "platform": platform.platform(), "threads": args.threads,
              "runtime_version": manifest["runtime_version"], "model_revision": manifest["model_revision"], "use_itn": True,
              "numpy_version": np.__version__, "python_executable": sys.executable,
              "assets_manifest_sha256": sha256(ROOT / "benchmarks/asr-candidates.json"),
              "fixture_manifest_sha256": sha256(fixtures_path),
              "load_s_by_language": load_times, "timing_scope": "file decode and sequential prefix simulation; not live latency",
              "results": results}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Report: {args.report.resolve()}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--download-approved", action="store_true", help="Install ONLY after explicit model/runtime download consent")
    parser.add_argument("--fixtures", type=Path, default=ROOT / "benchmarks/fixtures/local-tts/manifest.json")
    parser.add_argument("--report", type=Path, default=ROOT / "benchmarks/results/sensevoice" / f"probe-{time.time_ns()}.json")
    parser.add_argument("--threads", type=int, default=8)
    parser.add_argument("--rounds", type=int, default=3)
    args = parser.parse_args()
    manifest = json.loads((ROOT / "benchmarks/asr-candidates.json").read_text(encoding="utf-8"))
    if args.download_approved:
        install(manifest)
    else:
        probe(args, manifest)


if __name__ == "__main__":
    main()
