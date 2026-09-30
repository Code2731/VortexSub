"""Sequential single-server comparison using the production Rust HTTP adapter."""
import argparse
import csv
import hashlib
import json
import math
import os
from pathlib import Path
import secrets
import shutil
import socket
import statistics
import subprocess
import threading
import time
import urllib.error
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("--rounds", type=int, default=3)
parser.add_argument("--warmup", type=int, default=1)
parser.add_argument("--llama-server")
parser.add_argument("--order", choices=["llama-first", "tabby-first"], default="llama-first")
parser.add_argument("--worker", action="store_true")
parser.add_argument("--sampling-profile", choices=["current", "untruncated"], default="current")
args = parser.parse_args()
if not 1 <= args.rounds <= 10 or not 1 <= args.warmup <= 5:
    parser.error("Measured rounds 1..10, warmup rounds 1..5")
repo = Path(__file__).resolve().parent.parent
tabby = repo / "models/tabby"
python = tabby / "venv/Scripts/python.exe"
server_source = Path((tabby / "server-path.txt").read_text().strip())
tabby_manifest = json.loads((repo / "benchmarks/tabby-model.json").read_text())
catalog = json.loads((repo / "benchmarks/model-downloads.json").read_text())
llama_model = next(m for m in catalog["models"] if m["role"] == "translation")
llama_path = (repo / "benchmarks" / llama_model["path"]).resolve()
tabby_model = (repo / "benchmarks" / tabby_manifest["path"]).resolve()
llama = args.llama_server or shutil.which("llama-server.exe")
if not llama:
    candidate = Path(os.environ["LOCALAPPDATA"]) / "Microsoft/WinGet/Links/llama-server.exe"
    llama = str(candidate) if candidate.is_file() else None
if not llama or not python.is_file():
    raise RuntimeError("Installed llama-server and consented Tabby runtime are required")
llama = str(Path(llama).resolve())
probe = repo / "target/debug/translation-probe.exe"
if not probe.is_file():
    raise RuntimeError("Build translation-probe first (see wrapper script)")
fixture = repo / "benchmarks/translation-comparison-fixtures.json"
output = repo / "benchmarks/results" / (time.strftime("translation-engines-%Y%m%d-%H%M%S") + "-" + secrets.token_hex(3))
output.mkdir(parents=True)
hidden = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def sample_gpu(stop, samples):
    while not stop.is_set():
        try:
            result = subprocess.run(["nvidia-smi", "--query-gpu=index,memory.used,utilization.gpu", "--format=csv,noheader,nounits"], capture_output=True, text=True, timeout=5, **hidden)
            samples.append({"monotonic_s": time.monotonic(), "device_samples": result.stdout.strip(), "exit_code": result.returncode})
        except (OSError, subprocess.TimeoutExpired):
            break
        stop.wait(1)


print("Checking pinned model hashes", flush=True)
if sha256(llama_path) != llama_model["sha256"] or sha256(tabby_model / "model.safetensors") != tabby_manifest["weights_sha256"]:
    raise RuntimeError("Model hash mismatch")
common = {"fixture_sha256": sha256(fixture), "context_tokens": 4096,
          "concurrency": 1, "warmup_rounds": args.warmup, "measured_rounds": args.rounds,
          "server_order": args.order, "quantization_matched": False,
          "model_family": tabby_manifest["base_model"], "game_present": None,
          "quality_gate_passed": False, "end_to_end_ui_measured": False}
sampling = {"temperature": .2, "max_tokens": 256, "top_k": 40, "top_p": .9, "min_p": .1, "repetition_penalty": 1, "seed": "server random"}
if args.sampling_profile == "untruncated":
    sampling.update(top_k=0, top_p=1, min_p=0)
common["sampling_profile"] = args.sampling_profile
common["sampling"] = sampling
reports = {}
engines = ["llama", "tabby"] if args.order == "llama-first" else ["tabby", "llama"]
for engine in engines:
    run_dir = output / engine
    run_dir.mkdir()
    token = secrets.token_hex(24)
    environment = os.environ.copy()
    environment["ECHOSUB_TRANSLATION_TOKEN"] = token
    environment["HF_HOME"] = str(tabby / "hf-cache")
    environment["HF_HUB_OFFLINE"] = "1"
    environment["PYTHONPYCACHEPREFIX"] = str(tabby / "pycache")
    port = 18085
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", port))
    model_id = llama_model["id"] if engine == "llama" else tabby_model.name
    credentials = run_dir / ("api-key.tmp" if engine == "llama" else "api_tokens.yml")
    if engine == "llama":
        credentials.write_text(token)
        command = [llama, "-m", str(llama_path), "-c", "4096", "-ngl", "99", "--parallel", "1", "--top-k", str(sampling["top_k"]), "--top-p", str(sampling["top_p"]), "--min-p", str(sampling["min_p"]), "--repeat-penalty", "1", "--host", "127.0.0.1", "--port", str(port), "--alias", model_id, "--api-key-file", str(credentials)]
    else:
        write_json(credentials, {"api_key": token, "admin_key": secrets.token_hex(24)})
        config = {"network": {"host": "127.0.0.1", "port": port, "disable_auth": False,
                              "allowed_origins": [], "disable_fetch_requests": True},
                  "model": {"model_dir": str(tabby_model.parent), "model_name": model_id,
                            "max_seq_len": 4096, "cache_size": 4096, "cache_mode": "FP16",
                            "max_batch_size": 1, "chunk_size": 2048, "warmup": True,
                            "inline_model_loading": False, "use_dummy_models": False},
                  "draft_model": {"draft_mode": "disabled"},
                  "sampling": {name: {"override": sampling[name], "force": False} for name in ("top_k", "top_p", "min_p", "repetition_penalty")},
                  "logging": {"log_prompt": False, "log_requests": False,
                              "log_chat_completion_requests": False, "log_live_status": False}}
        write_json(run_dir / "config.yml", config)
        command = [str(python), str(repo / "scripts/tabby-server.py"), "--server", str(server_source), "--config", str(run_dir / "config.yml")]
    server = None
    stop = threading.Event()
    gpu_samples = []
    sampler = threading.Thread(target=sample_gpu, args=(stop, gpu_samples), daemon=True)
    started = time.monotonic()
    try:
        print("Starting", engine, flush=True)
        with (run_dir / "server-stdout.log").open("w", encoding="utf-8") as stdout, (run_dir / "server-stderr.log").open("w", encoding="utf-8") as stderr:
            server = subprocess.Popen(command, cwd=run_dir, env=environment, stdout=stdout, stderr=stderr, **hidden)
            sampler.start()
            notice = time.monotonic()
            while True:
                if server.poll() is not None:
                    raise RuntimeError(f"{engine} exited with {server.returncode}; inspect {run_dir}")
                if time.monotonic() - started > 300:
                    raise RuntimeError(f"{engine} readiness exceeded 300 seconds")
                try:
                    request = urllib.request.Request(f"http://127.0.0.1:{port}/v1/models", headers={"Authorization": f"Bearer {token}"})
                    with opener.open(request, timeout=2) as response:
                        models = json.load(response)
                    if engine == "llama":
                        with opener.open(f"http://127.0.0.1:{port}/health", timeout=2) as response:
                            if json.load(response).get("status") != "ok":
                                time.sleep(.25)
                                continue
                    if model_id in [m["id"] for m in models["data"]]:
                        break
                except (urllib.error.URLError, TimeoutError, KeyError, json.JSONDecodeError):
                    pass
                if time.monotonic() - notice >= 10:
                    print(f"{engine}: loading {time.monotonic() - started:.1f} s", flush=True)
                    notice = time.monotonic()
                time.sleep(.25)
            ready_s = time.monotonic() - started
            with (run_dir / "probe.log").open("w", encoding="utf-8") as probe_log:
                result = subprocess.run([str(probe), f"http://127.0.0.1:{port}/v1/", model_id, str(fixture), str(run_dir / "report.json"), str(args.warmup), str(args.rounds)], cwd=repo, env=environment, stdout=probe_log, stderr=subprocess.STDOUT, **hidden)
            if result.returncode:
                raise RuntimeError(f"{engine} contract probe failed; report retained in {run_dir}")
            report = json.loads((run_dir / "report.json").read_text(encoding="utf-8"))
            reports[engine] = report
            if args.worker:
                asr = next(m for m in catalog["models"] if m["id"] == "whisper-base")
                with (run_dir / "worker-probe.log").open("w", encoding="utf-8") as worker_log:
                    worker = subprocess.run(["dotnet", str(repo / "tests/EchoSub.NativeAsrSmoke/bin/Debug/net10.0/EchoSub.NativeAsrSmoke.dll"), "--translation", str(repo / "target/model-probe-cpu/release/echosub-worker.exe"), str((repo / "benchmarks" / asr["path"]).resolve()), asr["sha256"], str(repo / "benchmarks/fixtures/local-tts/manifest.json"), f"http://127.0.0.1:{port}/v1/", model_id, str(run_dir / "worker-report.json")], cwd=repo, env=environment, stdout=worker_log, stderr=subprocess.STDOUT, **hidden)
                if worker.returncode:
                    raise RuntimeError(f"{engine} native file worker probe failed; inspect {run_dir}")
            runtime = dict(common, engine=engine, ready_s=ready_s,
                           model_id=model_id, model_weights_sha256=llama_model["sha256"] if engine == "llama" else tabby_manifest["weights_sha256"],
                           source_revision=tabby_manifest["tabby_revision"] if engine == "tabby" else None,
                           server_binary_sha256=sha256(Path(llama)) if engine == "llama" else None,
                           quantization="GGUF Q4_K_M" if engine == "llama" else "EXL3 4.0bpw_H6",
                           sampling=sampling)
            write_json(run_dir / "runtime.json", runtime)
    finally:
        if server and server.poll() is None:
            server.terminate()
            try:
                server.wait(timeout=10)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait(timeout=5)
        stop.set()
        if sampler.is_alive():
            sampler.join(timeout=6)
        write_json(run_dir / "gpu-samples.json", {"interval_s": 1, "whole_device_not_process_peak": True, "samples": gpu_samples})
        credentials.unlink(missing_ok=True)
    time.sleep(1)

left, right = reports["llama"], reports["tabby"]
if left["fixture_sha256"] != right["fixture_sha256"] or left["offered"] != right["offered"] or len(left["results"]) != len(right["results"]):
    raise RuntimeError("Incomparable corpora or counts")
pairs = []
for a, b in zip(left["results"], right["results"]):
    if (a["id"], a["round"], a["request_sha256"]) != (b["id"], b["round"], b["request_sha256"]):
        raise RuntimeError("Request fingerprints differ")
    pairs.append({"id": a["id"], "round": a["round"], "source": a["source"],
                  "context": json.dumps(a["context"], ensure_ascii=False), "reference": a["reference"],
                  "llama": a["translation"], "tabby": b["translation"],
                  "llama_s": a["elapsed_s"], "tabby_s": b["elapsed_s"], "manual_review": "PENDING"})
with (output / "semantic-review.csv").open("w", newline="", encoding="utf-8-sig") as target:
    writer = csv.DictWriter(target, fieldnames=list(pairs[0]))
    writer.writeheader()
    writer.writerows(pairs)
summary = dict(common, paired_requests=len(pairs), engines={})
for engine, report in reports.items():
    groups = {}
    for mode in ("all", "plain", "context"):
        values = sorted(r["elapsed_s"] for r in report["results"] if mode == "all" or r["id"].startswith(mode + "-"))
        groups[mode] = {"count": len(values), "mean_s": statistics.mean(values), "p95_s": values[math.ceil(.95 * len(values)) - 1], "max_s": max(values)}
    summary["engines"][engine] = groups
summary["mean_saved_s"] = summary["engines"]["llama"]["all"]["mean_s"] - summary["engines"]["tabby"]["all"]["mean_s"]
write_json(output / "summary.json", summary)
print(json.dumps(summary, indent=2), flush=True)
print("Report:", output, "(manual semantic/game review still required)", flush=True)
