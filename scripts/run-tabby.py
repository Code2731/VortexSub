"""Start existing Tabby assets and live UI. Never downloads or starts capture."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import time
import urllib.error
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("--no-build", action="store_true")
args = parser.parse_args()
repo = Path(__file__).resolve().parent.parent
root = repo / "models/tabby"
manifest = json.loads((repo / "benchmarks/tabby-model.json").read_text())
model = (repo / "benchmarks" / manifest["path"]).resolve()
server_source = Path((root / "server-path.txt").read_text().strip())
python = root / "venv/Scripts/python.exe"
run = repo / "logs" / (time.strftime("tabby-%Y%m%d-%H%M%S") + "-" + secrets.token_hex(3))
run.mkdir(parents=True)
server = None
auth = run / "api_tokens.yml"
hidden = {"creationflags": subprocess.CREATE_NO_WINDOW}
exit_code = 1
try:
    print("[1/3] Checking existing EXL3 model...", flush=True)
    digest = hashlib.sha256()
    with (model / "model.safetensors").open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest() != manifest["weights_sha256"]:
        raise RuntimeError("EXL3 model hash mismatch")
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 1234))
    token = secrets.token_hex(24)
    auth.write_text(json.dumps({"api_key": token, "admin_key": secrets.token_hex(24)}))
    config = {"network": {"host": "127.0.0.1", "port": 1234, "disable_auth": False,
                          "allowed_origins": [], "disable_fetch_requests": True},
              "model": {"model_dir": str(model.parent), "model_name": model.name,
                        "max_seq_len": 4096, "cache_size": 4096, "cache_mode": "FP16",
                        "max_batch_size": 1, "warmup": True, "inline_model_loading": False, "use_dummy_models": False},
              "draft_model": {"draft_mode": "disabled"},
              "sampling": {"top_k": {"override": 40, "force": False}, "top_p": {"override": .9, "force": False},
                           "min_p": {"override": .1, "force": False}, "repetition_penalty": {"override": 1, "force": False}},
              "logging": {"log_prompt": False, "log_requests": False,
                          "log_chat_completion_requests": False, "log_live_status": False}}
    (run / "config.yml").write_text(json.dumps(config))
    environment = os.environ.copy()
    environment["ECHOSUB_TRANSLATION_TOKEN"] = token
    environment["HF_HOME"] = str(root / "hf-cache")
    environment["HF_HUB_OFFLINE"] = "1"
    environment["PYTHONPYCACHEPREFIX"] = str(root / "pycache")
    command = [str(python), str(repo / "scripts/tabby-server.py"), "--server", str(server_source), "--config", str(run / "config.yml")]
    print("[2/3] Loading Tabby/ExLlama; logs:", run, flush=True)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with (run / "stdout.log").open("w", encoding="utf-8") as stdout, (run / "stderr.log").open("w", encoding="utf-8") as stderr:
        server = subprocess.Popen(command, cwd=run, env=environment, stdout=stdout, stderr=stderr, **hidden)
        started = notice = time.monotonic()
        while True:
            if server.poll() is not None:
                raise RuntimeError(f"Tabby exited with {server.returncode}; inspect {run}")
            if time.monotonic() - started > 300:
                raise RuntimeError("Tabby readiness exceeded 300 seconds")
            try:
                request = urllib.request.Request("http://127.0.0.1:1234/v1/models", headers={"Authorization": f"Bearer {token}"})
                with opener.open(request, timeout=2) as response:
                    catalog = json.load(response)
                if model.name in [m["id"] for m in catalog["data"]]:
                    break
            except (urllib.error.URLError, TimeoutError, KeyError, json.JSONDecodeError):
                pass
            if time.monotonic() - notice >= 10:
                print(f"Waiting for Tabby: {time.monotonic() - started:.1f} seconds", flush=True)
                notice = time.monotonic()
            time.sleep(.25)
        print("[3/3] Opening UI. Click server/model lookup, then Start session after Ready.", flush=True)
        powershell = Path(os.environ["SystemRoot"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
        ui = [str(powershell), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(repo / "scripts/run.ps1"), "-Live", "-Offline", "-NoPause"]
        if args.no_build:
            ui.append("-NoBuild")
        exit_code = subprocess.run(ui, cwd=repo, env=environment).returncode
except Exception as error:
    print("EchoSub Tabby launcher failed:", error, flush=True)
finally:
    if server and server.poll() is None:
        server.terminate()
        try:
            server.wait(timeout=10)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait(timeout=5)
    auth.unlink(missing_ok=True)
raise SystemExit(exit_code)
