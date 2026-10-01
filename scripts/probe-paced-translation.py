"""Existing consented assets only; paced native ASR and actual local Qwen HTTP."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request
import wave

parser = argparse.ArgumentParser()
parser.add_argument('--backend', choices=['cpu', 'cuda'], default='cuda')
parser.add_argument('--rounds', type=int, default=3)
parser.add_argument('--server')
parser.add_argument('--adaptive', action='store_true', help='Compare fixed 0.5 s with feedback scheduling; both first requests at 0.8 s')
parser.add_argument("--decode-window", action="store_true", help="Compare adaptive baseline with experimental DTW decode windows")
parser.add_argument('--pad-short-partials', action='store_true', help='Compare adaptive baseline with short partial zero-padding')
args = parser.parse_args()
if args.pad_short_partials and (args.adaptive or args.decode_window):
    parser.error('padding comparison cannot combine with other comparisons')
if not 1 <= args.rounds <= 10:
    parser.error('rounds must be 1..10')
repo = Path(__file__).resolve().parent.parent
out = repo / 'benchmarks/results' / (time.strftime('paced-translation-%Y%m%d-%H%M%S') + '-' + secrets.token_hex(3))
out.mkdir(parents=True)
hidden = {'creationflags': subprocess.CREATE_NO_WINDOW}


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as f:
        for data in iter(lambda: f.read(1024 * 1024), b''):
            digest.update(data)
    return digest.hexdigest()


def save(name, value):
    (out / name).write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding='utf-8')


catalog = json.loads((repo / 'benchmarks/model-downloads.json').read_text(encoding='utf-8'))
asr = next(m for m in catalog['models'] if m['id'] == 'whisper-base')
model = next(m for m in catalog['models'] if m['role'] == 'translation')
asr_path = (repo / 'benchmarks' / asr['path']).resolve()
model_path = (repo / 'benchmarks' / model['path']).resolve()
if sha(asr_path) != asr['sha256'] or sha(model_path) != model['sha256']:
    raise RuntimeError('Existing model hash mismatch')
fixtures_path = repo / 'benchmarks/fixtures/local-tts/manifest.json'
fixtures = json.loads(fixtures_path.read_text(encoding='utf-8'))['fixtures']
pcm = []
for fixture_id in ['en-01', 'en-02', 'en-03']:
    item = next(f for f in fixtures if f['id'] == fixture_id)
    path = fixtures_path.parent / item['path']
    if sha(path) != item['sha256']:
        raise RuntimeError('Existing fixture hash mismatch')
    with wave.open(str(path)) as f:
        if (f.getnchannels(), f.getsampwidth(), f.getframerate(), f.getcomptype()) != (1, 2, 16000, 'NONE'):
            raise RuntimeError('Fixture must be PCM mono 16 kHz')
        pcm.append(f.readframes(f.getnframes()))
wav = out / 'en-joined.wav'
with wave.open(str(wav), 'wb') as f:
    f.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
    f.writeframes(b''.join(pcm))
server_path = Path(args.server or shutil.which('llama-server.exe') or
                   str(Path(os.environ['LOCALAPPDATA']) / 'Microsoft/WinGet/Links/llama-server.exe')).resolve(strict=True)
with socket.socket() as check:
    check.bind(('127.0.0.1', 18087))
key_file = out / 'api-key.tmp'
key_file.write_text(secrets.token_hex(24), encoding='utf-8')
environment = os.environ.copy()
environment['ECHOSUB_WINDOW_COMPARE'] = '1' if args.decode_window else '0'
environment['ECHOSUB_PADDING_COMPARE'] = '1' if args.pad_short_partials else '0'
environment['ECHOSUB_ADAPTIVE_COMPARE'] = '1' if args.adaptive else '0'
environment['ECHOSUB_TRANSLATION_TOKEN'] = key_file.read_text(encoding='utf-8')
server = None
stop = threading.Event()


def monitor():
    with (out / 'gpu-samples.jsonl').open('w', encoding='utf-8') as log:
        while not stop.is_set():
            try:
                result = subprocess.run(['nvidia-smi', '--query-gpu=name,memory.used,utilization.gpu',
                                         '--format=csv,noheader'], capture_output=True, text=True, timeout=5, **hidden)
                log.write(json.dumps({'utc': time.time(), 'sample': result.stdout.strip(), 'exit': result.returncode}) + '\n')
                log.flush()
            except (OSError, subprocess.TimeoutExpired) as error:
                log.write(json.dumps({'error': type(error).__name__}) + '\n')
            stop.wait(1)


monitor_thread = None
try:
    with (out / 'server.log').open('w', encoding='utf-8') as log:
        server = subprocess.Popen([str(server_path), '-m', str(model_path), '--alias', model['id'],
                                   '-c', '4096', '-ngl', '99', '--parallel', '1', '--top-k', '40',
                                   '--top-p', '.9', '--min-p', '.1', '--repeat-penalty', '1',
                                   '--host', '127.0.0.1', '--port', '18087', '--api-key-file', str(key_file)],
                                  cwd=repo, env=environment, stdout=log, stderr=subprocess.STDOUT, **hidden)
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        ready_start = time.monotonic()
        while True:
            if server.poll() is not None or time.monotonic() - ready_start > 120:
                raise RuntimeError('Owned server readiness failed; inspect server.log')
            try:
                with opener.open('http://127.0.0.1:18087/health', timeout=2) as response:
                    if json.load(response).get('status') == 'ok':
                        break
            except (urllib.error.URLError, TimeoutError, json.JSONDecodeError):
                pass
            time.sleep(.25)
        print('Paced real ASR/HTTP comparison:', out, flush=True)
        # Warm only the translation server. Each ASR condition starts a fresh owner.
        warmup = repo / 'target/debug/translation-probe.exe'
        with (out / 'warmup.log').open('w', encoding='utf-8') as warm_log:
            result = subprocess.run([str(warmup), 'http://127.0.0.1:18087/v1/', model['id'],
                                     str(repo / 'benchmarks/translation-fixtures.json'), str(out / 'warmup.json'), '1', '1'],
                                    cwd=repo, env=environment, stdout=warm_log, stderr=subprocess.STDOUT, timeout=90, **hidden)
        if result.returncode:
            raise RuntimeError('HTTP warmup failed')
        monitor_thread = threading.Thread(target=monitor)
        monitor_thread.start()
        command = ['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                   str(repo / 'scripts/probe-partial-scheduling.ps1'), '-Backend', args.backend,
                   '-Rounds', str(args.rounds), '-FirstPartialSeconds', '0.8' if args.adaptive or args.decode_window or args.pad_short_partials else '1.0', '-WavPath', str(wav),
                   '-ReportPath', str(out / 'report.json'), '-TranslationEndpoint', 'http://127.0.0.1:18087/v1/',
                   '-TranslationModel', model['id']]
        save('runtime.json', {'backend': args.backend, 'adaptive_compare': args.adaptive, 'padding_compare': args.pad_short_partials, 'decode_window_compare': args.decode_window, 'asr_weights_sha256': asr['sha256'],
                             'translation_weights_sha256': model['sha256'], 'server_sha256': sha(server_path),
                             'worker_sha256': sha(repo / f'target/model-probe-{args.backend}/release/echosub-worker.exe'),
                             'wav_sha256': sha(wav), 'rounds': args.rounds,
                             'note': 'Synthetic joined English; real native/HTTP owners. No capture/VAD/rendered UI/game isolation.'})
        with (out / 'native-http.log').open('w', encoding='utf-8') as native_log:
            result = subprocess.run(command, cwd=repo, env=environment, stdout=native_log,
                                    stderr=subprocess.STDOUT, timeout=600, **hidden)
        if result.returncode:
            raise RuntimeError('Native/HTTP comparison failed; completed conditions retained')
finally:
    stop.set()
    if monitor_thread:
        monitor_thread.join(timeout=7)
    if server and server.poll() is None:
        server.terminate()
        try:
            server.wait(timeout=10)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait(timeout=5)
    key_file.unlink(missing_ok=True)
print('Report:', out / 'report.json', '(semantic review required)', flush=True)
