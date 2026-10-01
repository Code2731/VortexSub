"""Download pinned diagnostic assets after obtaining repository download consent."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / 'benchmarks/laya-model.json'


def verify(path, spec):
    if not path.exists() or path.stat().st_size != spec['bytes']:
        return False
    if 'sha256' in spec:
        with path.open('rb') as stream:
            return hashlib.file_digest(stream, 'sha256').hexdigest() == spec['sha256']
    data = path.read_bytes()
    return hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest() == spec['git_blob']


def download(path, url, spec):
    path.parent.mkdir(parents=True, exist_ok=True)
    if verify(path, spec):
        print('Already verified:', path.name, flush=True)
        return
    partial = path.with_name(path.name + '.partial')
    subprocess.run(['curl.exe', '--fail', '--location', '--retry', '3',
                    '--continue-at', '-', '--output', str(partial), url], check=True)
    if not verify(partial, spec):
        raise ValueError('Downloaded size/hash mismatch: ' + str(path))
    partial.replace(path)
    print('Verified:', path.name, flush=True)


def main():
    if sys.argv[1:] != ['--download-approved']:
        raise SystemExit('Obtain download consent, then use --download-approved')
    catalog = json.loads(CATALOG.read_text(encoding='utf-8'))
    model = (CATALOG.parent / catalog['directory']).resolve()
    for spec in catalog['files']:
        download(model / spec['path'],
                 f"https://huggingface.co/{catalog['repo']}/resolve/{catalog['revision']}/{spec['path']}", spec)
    package = catalog['package']
    wheel = ROOT / 'models/laya/laya-0.3.23-py3-none-any.whl'
    download(wheel, package['url'], package)
    sdk = (CATALOG.parent / package['directory']).resolve()
    subprocess.run([sys.executable, '-m', 'pip', 'install', '--no-index', '--no-deps',
                    '--target', str(sdk), '--upgrade', str(wheel)], check=True)


if __name__ == '__main__':
    main()
