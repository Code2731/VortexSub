"""Consented existing assets only: offline Whisper prefixes and actual llama HTTP replay."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import time
import urllib.error
import urllib.request
import wave

parser = argparse.ArgumentParser()
parser.add_argument("--server")
args = parser.parse_args()
repo = Path(__file__).resolve().parent.parent
output = repo / "benchmarks/results" / (time.strftime("streaming-translation-%Y%m%d-%H%M%S") + "-" + secrets.token_hex(3))
output.mkdir(parents=True)
catalog = json.loads((repo / "benchmarks/model-downloads.json").read_text(encoding="utf-8"))
asr = next(m for m in catalog["models"] if m["id"] == "whisper-base")
model = next(m for m in catalog["models"] if m["role"] == "translation")
asr_path = (repo / "benchmarks" / asr["path"]).resolve()
model_path = (repo / "benchmarks" / model["path"]).resolve()
hidden = {"creationflags": subprocess.CREATE_NO_WINDOW}
client = repo / "tests/EchoSub.NativeAsrSmoke/bin/Debug/net10.0/EchoSub.NativeAsrSmoke.dll"
native = repo / "target/model-probe-cpu/release/echosub-worker.exe"
worker = repo / "target/debug/echosub-worker.exe"


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def run(command, log, environment=None):
    with log.open("w", encoding="utf-8") as target:
        result = subprocess.run(command, cwd=repo, env=environment, stdout=target, stderr=subprocess.STDOUT, timeout=180, **hidden)
    if result.returncode:
        raise RuntimeError(f"Diagnostic failed ({result.returncode}); inspect {log}")


print("Checking existing model and fixture hashes; no capture or download", flush=True)
if sha256(asr_path) != asr["sha256"] or sha256(model_path) != model["sha256"]:
    raise RuntimeError("Model hash mismatch")
fixtures_path = repo / "benchmarks/fixtures/local-tts/manifest.json"
fixtures = json.loads(fixtures_path.read_text(encoding="utf-8"))["fixtures"]
pcm = {}
for name in ("en-01", "en-02", "en-03"):
    fixture = next(f for f in fixtures if f["id"] == name)
    path = fixtures_path.parent / fixture["path"]
    if sha256(path) != fixture["sha256"]:
        raise RuntimeError("Fixture hash mismatch")
    with wave.open(str(path), "rb") as source:
        if (source.getframerate(), source.getnchannels(), source.getsampwidth()) != (16000, 1, 2):
            raise RuntimeError("Expected mono 16 kHz PCM16 fixture")
        pcm[name] = source.readframes(source.getnframes())
pcm["en-joined"] = b"".join(pcm[name] for name in ("en-01", "en-02", "en-03"))
prefixes = []
for name in ("en-01", "en-03", "en-joined"):
    frames = len(pcm[name]) // 2
    if frames > 128000:
        raise RuntimeError("Fixture exceeds production eight-second segment cap")
    ends = list(range(12000, frames, 12000)) + [frames]
    steps = []
    for index, end in enumerate(ends):
        path = output / f"{name}-prefix-{index}.wav"
        with wave.open(str(path), "wb") as target:
            target.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
            target.writeframes(pcm[name][:end * 2])
        steps.append({"path": str(path), "sha256": sha256(path), "audio_end_s": end / 16000, "final": end == frames})
    prefixes.append({"id": name, "steps": steps})
prefix_manifest = output / "prefix-manifest.json"
save(prefix_manifest, {"note": "Existing synthetic TTS; joined clip is an authored concatenation, not natural dialogue.", "original_manifest_sha256": sha256(fixtures_path), "cases": prefixes})
trace = output / "source-trace.json"
print("Decoding growing audio prefixes with actual CPU Whisper", flush=True)
run(["dotnet", str(client), "--streaming-source", str(native), str(asr_path), asr["sha256"], str(prefix_manifest), str(trace)], output / "source-probe.log")
sources = json.loads(trace.read_text(encoding="utf-8"))
for item in sources["cases"]:
    item["source_kind"] = "actual offline CPU Whisper, independent prefixes"
for name, texts in (
    ("authored-ja-return", ["ドアを開けないで", "ドアを開けないで。", "ドアを開けないで。戻るまで", "戻るまでドアを開けないで。"]),
    ("authored-ja-correction", ["橋を渡ってください", "橋を渡ってください。", "橋を渡らないでください。", "橋を渡らないでください。"]),
):
    sources["cases"].append({"id": name, "language": "ja", "source_kind": "authored MOCK hypotheses; no Japanese ASR",
        "steps": [{"available_s": .75 * (index + 1), "source": text, "final": index == 3} for index, text in enumerate(texts)]})
save(trace, sources)

server_path = args.server or shutil.which("llama-server.exe")
if not server_path:
    server_path = str(Path(os.environ["LOCALAPPDATA"]) / "Microsoft/WinGet/Links/llama-server.exe")
server_path = str(Path(server_path).resolve())
with socket.socket() as reservation:
    reservation.bind(("127.0.0.1", 18086))
token = secrets.token_hex(24)
credentials = output / "api-key.tmp"
credentials.write_text(token)
environment = os.environ.copy()
environment["ECHOSUB_TRANSLATION_TOKEN"] = token
server = None
try:
    print("Starting owned llama server and comparing final-only / preview replay", flush=True)
    with (output / "server-stdout.log").open("w", encoding="utf-8") as stdout, (output / "server-stderr.log").open("w", encoding="utf-8") as stderr:
        server = subprocess.Popen([server_path, "-m", str(model_path), "--alias", model["id"], "-c", "4096", "-ngl", "99", "--parallel", "1", "--top-k", "40", "--top-p", ".9", "--min-p", ".1", "--repeat-penalty", "1", "--host", "127.0.0.1", "--port", "18086", "--api-key-file", str(credentials)], cwd=output, env=environment, stdout=stdout, stderr=stderr, **hidden)
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        start = time.monotonic()
        while True:
            if server.poll() is not None or time.monotonic() - start > 120:
                raise RuntimeError("llama readiness failed; inspect server logs")
            try:
                with opener.open("http://127.0.0.1:18086/health", timeout=2) as response:
                    if json.load(response).get("status") == "ok":
                        break
            except (urllib.error.URLError, TimeoutError, json.JSONDecodeError):
                pass
            time.sleep(.25)
        endpoint = "http://127.0.0.1:18086/v1/"
        run([str(repo / "target/debug/translation-probe.exe"), endpoint, model["id"], str(repo / "benchmarks/translation-fixtures.json"), str(output / "http-warmup.json"), "1", "1"], output / "warmup.log", environment)
        run(["dotnet", str(client), "--streaming-translation", str(worker), endpoint, model["id"], str(trace), str(output / "report.json")], output / "replay.log", environment)
        save(output / "runtime.json", {"server_binary_sha256": sha256(Path(server_path)), "native_worker_sha256": sha256(native), "replay_worker_sha256": sha256(worker), "translation_weights_sha256": model["sha256"], "asr_weights_sha256": asr["sha256"], "source_trace_sha256": sha256(trace), "capture": False, "game_coexistence": None, "rendered_ui": False})
finally:
    if server and server.poll() is None:
        server.terminate()
        try:
            server.wait(timeout=10)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait(timeout=5)
    credentials.unlink(missing_ok=True)
print("Report:", output, "(offline replay only; semantic review pending)", flush=True)
