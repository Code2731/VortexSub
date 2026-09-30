"""Download the pinned diagnostic environment only with explicit --download consent."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import urllib.request
import venv
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("--download", action="store_true", required=True,
                    help="Model/runtime download consent is required before using this flag")
args = parser.parse_args()
repo = Path(__file__).resolve().parent.parent
manifest = json.loads((repo / "benchmarks/tabby-model.json").read_text())
root = repo / "models/tabby"
root.mkdir(parents=True, exist_ok=True)
os.environ["PYTHONPYCACHEPREFIX"] = str(root / "pycache")
os.environ["HF_HOME"] = str(root / "hf-cache")
os.environ["PIP_DISABLE_PIP_VERSION_CHECK"] = "1"


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for data in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(data)
    return digest.hexdigest()


def fetch(url, path, size=None, expected=None):
    if path.is_file() and (size is None or path.stat().st_size == size):
        if expected is None or sha256(path) == expected:
            print("Reuse:", path.name, flush=True)
            return
    temporary = path.with_suffix(path.suffix + ".part")
    path.parent.mkdir(parents=True, exist_ok=True)
    request = urllib.request.Request(url, headers={"User-Agent": "EchoSub-Tabby-Diagnostic"})
    start = notice = time.monotonic()
    done = 0
    with urllib.request.urlopen(request, timeout=60) as response, temporary.open("wb") as target:
        while data := response.read(1024 * 1024):
            target.write(data)
            done += len(data)
            if time.monotonic() - notice >= 10:
                print(f"{path.name}: {done / 1e9:.3f} GB, {time.monotonic() - start:.1f} s", flush=True)
                notice = time.monotonic()
    if size is not None and done != size:
        raise RuntimeError(f"Size mismatch: {path.name}")
    if expected is not None and sha256(temporary) != expected:
        raise RuntimeError(f"Hash mismatch: {path.name}")
    temporary.replace(path)
    print("Downloaded:", path.name, flush=True)


model_dir = (repo / "benchmarks" / manifest["path"]).resolve()
api = f'https://huggingface.co/api/models/{manifest["repository"]}/revision/{manifest["revision"]}?blobs=true'
with urllib.request.urlopen(api, timeout=60) as response:
    metadata = json.load(response)
if metadata["sha"] != manifest["revision"]:
    raise RuntimeError("Model revision mismatch")
records = []
for file in metadata["siblings"]:
    name = file["rfilename"]
    path = (model_dir / name).resolve()
    if not path.is_relative_to(model_dir):
        raise RuntimeError("Invalid model filename")
    expected = (file.get("lfs") or {}).get("sha256")
    url = f'https://huggingface.co/{manifest["repository"]}/resolve/{manifest["revision"]}/{name}'
    fetch(url, path, file["size"], expected)
    records.append({"file": name, "bytes": path.stat().st_size, "sha256": sha256(path)})
(root / "model-downloads.json").write_text(json.dumps({"manifest": manifest, "files": records}, indent=2))

archive = root / "tabby-source.zip"
fetch(f'https://api.github.com/repos/{manifest["tabby_repository"]}/zipball/{manifest["tabby_revision"]}', archive)
sources = root / "source"
sources.mkdir(exist_ok=True)
with zipfile.ZipFile(archive) as bundle:
    for entry in bundle.infolist():
        if not (sources / entry.filename).resolve().is_relative_to(sources):
            raise RuntimeError("Invalid source archive path")
    source_directory = bundle.infolist()[0].filename.split("/")[0]
    bundle.extractall(sources)
server = sources / source_directory
if not (server / "main.py").is_file():
    raise RuntimeError("Pinned Tabby archive has no main.py")
(root / "server-path.txt").write_text(str(server))
environment = root / "venv"
if not (environment / "Scripts/python.exe").is_file():
    print("Creating isolated Python environment", flush=True)
    venv.EnvBuilder(with_pip=True).create(environment)
python = environment / "Scripts/python.exe"
subprocess.run([str(python), "-m", "pip", "install", "--no-cache-dir", "--upgrade", str(server),
                manifest["torch_wheel"] + "#sha256=" + manifest["torch_wheel_sha256"], manifest["exllama_wheel"] + "#sha256=" + manifest["exllama_wheel_sha256"],
                "triton-windows", "flash-linear-attention>=0.5.0"], check=True)
with (root / "requirements-installed.txt").open("w") as output:
    subprocess.run([str(python), "-m", "pip", "freeze", "--all"], stdout=output, check=True)
print("Pinned Tabby source and installed package versions recorded under", root, flush=True)
