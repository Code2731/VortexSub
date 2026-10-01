"""Compare production requests and prompt/context candidates using existing local assets."""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import statistics
import subprocess
import time
import urllib.error
import urllib.request


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def candidate(body, profile, config):
    body = copy.deepcopy(body)
    if profile == 'production':
        return body
    payload = {}
    for message in body['messages'][1:]:
        payload.update(json.loads(message['content']))
    body['messages'] = [body['messages'][0],
                        {'role': 'user', 'content': json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(',', ':'))}]
    if profile == 'baseline':
        body['messages'][0]['content'] = config['baseline_system']
    elif profile == 'gemma':
        if payload['context']:
            raise ValueError('TranslateGemma official template comparison requires context_condition=none')
        body['messages'] = [{'role': 'user', 'content': [{'type': 'text',
            'source_lang_code': payload['source_language'], 'target_lang_code': payload['target_language'],
            'text': payload['source_text']}]}]
        return body
    elif profile == 'plain':
        names = {'en': 'English', 'ja': 'Japanese', 'ko': 'Korean'}
        body['messages'] = [{'role': 'system', 'content':
            f"Translate the current {names[payload['source_language']]} subtitle into {names[payload['target_language']]}. "
            'Earlier dialogue is reference only. Output only the current subtitle translation, with no explanation.'}]
        if payload['context']:
            body['messages'].append({'role': 'user', 'content': 'Earlier dialogue (reference only):\n' + '\n'.join(payload['context'])})
        body['messages'].append({'role': 'user', 'content': 'Current subtitle:\n' + payload['source_text']})
        return body
    elif profile != 'production':
        body['messages'][0]['content'] = config['strict_system']
    if profile == 'korean':
        body['messages'][0]['content'] = config['korean_system']
    if profile in ('isolated', 'readable', 'examples'):
        context = payload.pop('context')
        if profile in ('readable', 'examples'):
            names = {'en': 'English', 'ja': 'Japanese', 'ko': 'Korean'}
            payload['source_language'] = names[payload['source_language']]
            payload['target_language'] = names[payload['target_language']]
        body['messages'] = [body['messages'][0],
                            {'role': 'user', 'content': json.dumps({'context': context}, ensure_ascii=False)},
                            {'role': 'user', 'content': json.dumps(payload, ensure_ascii=False)}]
        if profile == 'examples':
            demonstrations = []
            for item in config['examples']:
                demonstrations.extend([
                    {'role': 'user', 'content': json.dumps({'source_language': 'English', 'target_language': 'Korean', 'source_text': item['source']}, ensure_ascii=False)},
                    {'role': 'assistant', 'content': item['translation']}])
            body['messages'][1:1] = demonstrations
    return body


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rounds', type=int, default=3)
    parser.add_argument('--server')
    parser.add_argument('--fixtures', type=Path, help='Authored full-source fixture JSON; defaults to the prompt/context comparison set')
    parser.add_argument('--catalog', type=Path, help='Model manifest; defaults to model-downloads.json')
    parser.add_argument('--model-id', help='Exact ID from the selected manifest')
    parser.add_argument('--context-conditions', nargs='+', help='Select authored context conditions, for example none')
    parser.add_argument('--owner-check', action='store_true', help='Also call the real Rust HTTP owner once per fixture/policy')
    parser.add_argument('--profiles', nargs='+', choices=['baseline', 'strict', 'isolated', 'readable', 'examples', 'korean', 'production', 'plain', 'gemma'])
    args = parser.parse_args()
    if not 1 <= args.rounds <= 10:
        parser.error('rounds must be 1..10')
    repo = Path(__file__).resolve().parent.parent
    out = repo / 'benchmarks/results' / (time.strftime('translation-context-%Y%m%d-%H%M%S') + '-' + secrets.token_hex(3))
    out.mkdir(parents=True)
    print('Checking existing assets and exporting bounded requests:', out, flush=True)
    catalog_path = (args.catalog or repo / 'benchmarks/model-downloads.json').resolve(strict=True)
    catalog = json.loads(catalog_path.read_text(encoding='utf-8'))
    matches = [m for m in catalog['models'] if m['role'] == 'translation' and (not args.model_id or m['id'] == args.model_id)]
    if not matches or args.model_id and len(matches) != 1:
        parser.error('Select an existing unique translation model ID from the manifest')
    model = matches[0]
    if args.owner_check and model.get('disable_thinking'):
        parser.error('OwnerCheck does not implement this candidate model thinking configuration')
    model_path = (catalog_path.parent / model['path']).resolve()
    if sha(model_path) != model['sha256']:
        raise RuntimeError('Existing translation model hash mismatch')
    server_path = Path(args.server or shutil.which('llama-server.exe') or
                       str(Path(os.environ['LOCALAPPDATA']) / 'Microsoft/WinGet/Links/llama-server.exe')).resolve(strict=True)
    fixtures = (args.fixtures or repo / 'benchmarks/translation-context-fixtures.json').resolve(strict=True)
    profiles_path = repo / 'benchmarks/translation-prompt-profiles.json'
    profiles = json.loads(profiles_path.read_text(encoding='utf-8'))
    selected_profiles = args.profiles or profiles['profiles']
    if len(set(selected_profiles)) != len(selected_profiles):
        parser.error('duplicate profiles')
    if args.owner_check and (args.catalog or 'gemma' in selected_profiles):
        parser.error('OwnerCheck is limited to the existing production manifest and policies')
    cases = json.loads(fixtures.read_text(encoding='utf-8'))['cases']
    if args.context_conditions:
        cases = [case for case in cases if case.get('context_condition') in args.context_conditions]
        if not cases:
            parser.error('No cases matched context conditions')
    probe = repo / 'target/debug/translation-probe.exe'
    export_args = [str(probe), '--prepare', model['id'], str(fixtures), str(out / 'requests.json')]
    if 'production' in selected_profiles:
        export_args.append('--isolated-context')
    subprocess.run(export_args, check=True, cwd=repo)
    exported = json.loads((out / 'requests.json').read_text(encoding='utf-8'))
    requests = {r['id']: r['body'] for r in exported['requests']}
    selected_ids = {case['id'] for case in cases}
    bodies = {name: {id: candidate(body, name, profiles) for id, body in requests.items() if id in selected_ids}
              for name in selected_profiles}
    if model.get('disable_thinking'):
        for requests_by_id in bodies.values():
            for body in requests_by_id.values():
                body['chat_template_kwargs'] = {'enable_thinking': False}
    if exported['fixture_sha256'] != sha(fixtures):
        raise RuntimeError('Exported fixture hash mismatch')
    def save(name, data):
        (out / name).write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
    save('candidate-requests.json', bodies)
    save('fixtures.json', json.loads(fixtures.read_text(encoding='utf-8')))
    save('profiles.json', profiles)
    save('runtime.json', {'model_id': model['id'], 'model_sha256': sha(model_path),
                         'catalog_sha256': sha(catalog_path), 'disable_thinking': model.get('disable_thinking', False),
                         'context_conditions': args.context_conditions,
                         'server_sha256': sha(server_path), 'probe_sha256': sha(probe),
                         'fixtures_sha256': sha(fixtures), 'profiles_sha256': sha(profiles_path),
                         'sampling': {'temperature': .2, 'max_tokens': 256, 'top_k': 40, 'top_p': .9, 'min_p': .1},
                         'export_policy': 'isolated-context' if 'production' in selected_profiles else 'original',
                         'scope': 'Full authored source over real local HTTP; production is opt-in isolated prepare; baseline restores original prompt/layout. No ASR/capture/UI; manual review.'})
    port = 18089
    with socket.socket() as check:
        check.bind(('127.0.0.1', port))
    key_file = out / 'api-key.tmp'
    token = secrets.token_hex(24)
    key_file.write_text(token, encoding='utf-8')
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    server = None
    results, warmup = [], []
    def send(body):
        request = urllib.request.Request(f'http://127.0.0.1:{port}/v1/chat/completions',
                    data=json.dumps(body, ensure_ascii=False).encode('utf-8'),
                    headers={'Authorization': f'Bearer {token}', 'Content-Type': 'application/json'})
        start = time.monotonic()
        with opener.open(request, timeout=10) as response:
            data = response.read(256 * 1024 + 1)
        if len(data) > 256 * 1024:
            raise RuntimeError('Oversized response')
        value = json.loads(data)
        choice = value['choices'][0]
        text = choice['message'].get('content', '').strip()
        error = None if choice['finish_reason'] == 'stop' and text else 'IncompleteResponse'
        if model.get('disable_thinking') and (choice['message'].get('reasoning_content') or '<think>' in text):
            error = 'UnexpectedReasoning'
        return text if error is None else None, time.monotonic() - start, value.get('usage'), error
    try:
        with (out / 'server.log').open('w', encoding='utf-8') as log:
            server_args = [str(server_path), '-m', str(model_path), '--alias', model['id'],
                         '-c', '4096', '-ngl', '99', '--parallel', '1', '--top-k', '40', '--top-p', '.9',
                         '--min-p', '.1', '--repeat-penalty', '1', '--host', '127.0.0.1', '--port', str(port),
                         '--api-key-file', str(key_file)]
            if model.get('disable_thinking') or 'gemma' in selected_profiles:
                server_args.append('--jinja')
            server = subprocess.Popen(server_args, cwd=repo, stdout=log, stderr=subprocess.STDOUT,
                         creationflags=subprocess.CREATE_NO_WINDOW)
            start = time.monotonic()
            while True:
                if server.poll() is not None or time.monotonic() - start > 120:
                    raise RuntimeError('Owned server readiness failed')
                try:
                    with opener.open(f'http://127.0.0.1:{port}/health', timeout=2) as response:
                        if json.load(response).get('status') == 'ok':
                            break
                except (urllib.error.URLError, TimeoutError):
                    pass
                time.sleep(.25)
            print('Prompt/context comparison:', out, flush=True)
            for profile in selected_profiles:
                text, elapsed, usage, error = send(bodies[profile][cases[0]['id']])
                warmup.append({'profile': profile, 'translation': text, 'elapsed_s': elapsed, 'usage': usage, 'error': error})
            for round in range(args.rounds):
                shift = round % len(selected_profiles)
                order = selected_profiles[shift:] + selected_profiles[:shift]
                for case in cases if round % 2 == 0 else list(reversed(cases)):
                    for profile in order:
                        body = bodies[profile][case['id']]
                        text, elapsed, usage, error = send(body)
                        results.append({**case, 'round': round + 1, 'profile': profile, 'translation': text,
                                        'elapsed_s': elapsed, 'usage': usage, 'error': error, 'manual_review': 'PENDING',
                                        'request_sha256': hashlib.sha256(json.dumps(body, sort_keys=True, ensure_ascii=False).encode('utf-8')).hexdigest()})
                        save('report.json', {'quality_gate_passed': False, 'warmup': warmup, 'results': results,
                                           'offered': len(cases) * len(selected_profiles) * args.rounds})
                print(f'Round {round + 1}: {len(results)} completed', flush=True)
            for profile in selected_profiles:
                print(profile, 'HTTP median_s:', statistics.median(r['elapsed_s'] for r in results if r['profile'] == profile), flush=True)
            if args.owner_check:
                child_env = os.environ.copy()
                child_env['ECHOSUB_TRANSLATION_TOKEN'] = token
                for name, flag in [('original', []), ('isolated', ['--isolated-context'])]:
                    with (out / f'owner-{name}.log').open('w', encoding='utf-8') as owner_log:
                        subprocess.run([str(probe), f'http://127.0.0.1:{port}/v1/', model['id'], str(fixtures),
                                        str(out / f'owner-{name}.json'), '0', '1', *flag],
                                       cwd=repo, env=child_env, stdout=owner_log, stderr=subprocess.STDOUT,
                                       check=True, timeout=180, creationflags=subprocess.CREATE_NO_WINDOW)
                print('Rust HTTP owner: both policies completed', flush=True)
            if any(r['error'] for r in results + warmup):
                raise RuntimeError('Completed comparison with invalid responses; inspect report')
    finally:
        if server and server.poll() is None:
            server.terminate()
            try:
                server.wait(timeout=10)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait(timeout=5)
        key_file.unlink(missing_ok=True)


if __name__ == '__main__':
    main()
