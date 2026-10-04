"""Compare production requests and prompt/context candidates using existing local assets."""
import argparse
import copy
import csv
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import statistics
import struct
import subprocess
import time
import urllib.error
import urllib.request


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def gguf_metadata(path):
    # Read metadata only; the downloaded file remains unchanged.
    with path.open('rb') as stream:
        def number(fmt):
            return struct.unpack('<' + fmt, stream.read(struct.calcsize('<' + fmt)))[0]
        def string():
            length = number('Q')
            if length > 64 * 1024 * 1024:
                raise ValueError('Oversized GGUF metadata string')
            return stream.read(length).decode('utf-8')
        def value(kind):
            if kind == 8:
                return string()
            if kind == 9:
                subtype, count = number('I'), number('Q')
                if count > 1000000:
                    raise ValueError('Oversized GGUF metadata array')
                for _ in range(count):
                    value(subtype)
                return None
            return number({0: 'B', 1: 'b', 2: 'H', 3: 'h', 4: 'I', 5: 'i',
                           6: 'f', 7: '?', 10: 'Q', 11: 'q', 12: 'd'}[kind])
        if stream.read(4) != b'GGUF' or number('I') != 3:
            raise ValueError('Expected GGUF v3')
        number('Q')  # Tensor count.
        metadata = {}
        for _ in range(number('Q')):
            key = string()
            item = value(number('I'))
            if key in ('tokenizer.chat_template', 'tokenizer.ggml.bos_token_id',
                       'tokenizer.ggml.eos_token_id', 'tokenizer.ggml.add_bos_token',
                       'tokenizer.ggml.add_eos_token', 'general.architecture'):
                metadata[key] = item
        return metadata


def gguf_template(path):
    return gguf_metadata(path)['tokenizer.chat_template']


def candidate(body, profile, config, exaone_input='existing'):
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
    elif profile in ('exaone', 'hymt2'):
        names = {'en': 'English', 'ja': 'Japanese', 'ko': 'Korean'}
        target = names[payload['target_language']]
        context = '\n'.join(payload['context'])
        if profile == 'exaone':
            user_input = ((f'Earlier dialogue (reference only):\n{context}\n\n' if context else '') +
                f'Translate only the following {names[payload["source_language"]]} subtitle into {target}. '
                'Preserve its meaning and output only the translation, without explanation.\n\n' + payload['source_text'])
            if exaone_input != 'existing':
                if payload['target_language'] != 'ko':
                    raise ValueError('EXAONE separated comparison currently targets Korean only')
                reference = context if exaone_input == 'separated' else ''
                user_input = ('다음 [현재 자막]만 한국어로 번역하세요. [참고 문맥]은 대명사와 용어 해석에만 '
                    '사용하고 번역하거나 정보를 덧붙이지 마세요. 부정, 조건, 행동 주체, 대상, 숫자, '
                    '정정을 그대로 보존하세요. 설명 없이 번역문만 출력하세요.\n\n'
                    '[참고 문맥]\n' + (reference or '(없음)') + '\n\n[현재 자막]\n' + payload['source_text'])
            body['messages'] = [
                {'role': 'system', 'content': config['exaone_system']},
                {'role': 'user', 'content': user_input}]
            body['stop'] = ['[|endofturn|]']
        else:
            if context:
                content = (f'[Background Information]\n{context}\n\n'
                    f'Please translate the following text into {target}, taking the provided background '
                    f'information into consideration.\n[Source Text]\n{payload["source_text"]}')
            else:
                content = (f'Translate the following text into {target}. Note that you should only output '
                    f'the translated result without any additional explanation:\n{payload["source_text"]}')
            body['messages'] = [{'role': 'user', 'content': content}]
        body['repeat_penalty'] = 1.0
        return body
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
    parser.add_argument('--output', type=Path, help='New result directory; must not already exist')
    parser.add_argument('--sampling', choices=['existing', 'greedy', 'hymt2-recommended'], default='existing')
    parser.add_argument('--compare-sampling', action='store_true', help='Paired Hy-MT2 greedy/recommended comparison, alternates first request')
    parser.add_argument('--exaone-input', choices=['existing', 'separated', 'source-only'], default='existing')
    parser.add_argument('--verify-input-contract', action='store_true', help='Candidate template/tokenization and chat prompt-count check')
    parser.add_argument('--termination-check', action='store_true', help='Hy-MT2 native completion stop_type check outside benchmark timing')
    parser.add_argument('--fixtures', type=Path, help='Authored full-source fixture JSON; defaults to the prompt/context comparison set')
    parser.add_argument('--catalog', type=Path, help='Model manifest; defaults to model-downloads.json')
    parser.add_argument('--model-id', help='Exact ID from the selected manifest')
    parser.add_argument('--context-conditions', nargs='+', help='Select authored context conditions, for example none')
    parser.add_argument('--owner-check', action='store_true', help='Also call the real Rust HTTP owner once per fixture/policy')
    parser.add_argument('--replay-trace', type=Path, help='Replay a fixed source trace through the production worker after warmup; skips full-source comparison')
    parser.add_argument('--prepare-only', action='store_true', help='Export bounded candidate requests without loading weights or starting a server')
    parser.add_argument('--profiles', nargs='+', choices=['baseline', 'strict', 'isolated', 'readable', 'examples', 'korean', 'production', 'plain', 'gemma', 'exaone', 'hymt2'])
    args = parser.parse_args()
    if args.verify_input_contract and args.prepare_only:
        parser.error('Input contract verification requires an installed model/server')
    if args.owner_check and args.sampling != 'existing':
        parser.error('OwnerCheck only implements existing sampling')
    if not 1 <= args.rounds <= 10:
        parser.error('rounds must be 1..10')
    repo = Path(__file__).resolve().parent.parent
    out = args.output or repo / 'benchmarks/results' / (time.strftime('translation-context-%Y%m%d-%H%M%S') + '-' + secrets.token_hex(3))
    out.mkdir(parents=True)
    if args.prepare_only and args.owner_check:
        parser.error('PrepareOnly cannot run OwnerCheck')
    print('Exporting bounded requests:', out, flush=True)
    catalog_path = (args.catalog or repo / 'benchmarks/model-downloads.json').resolve(strict=True)
    catalog = json.loads(catalog_path.read_text(encoding='utf-8'))
    matches = [m for m in catalog['models'] if m['role'] == 'translation' and (not args.model_id or m['id'] == args.model_id)]
    if not matches or args.model_id and len(matches) != 1:
        parser.error('Select an existing unique translation model ID from the manifest')
    model = matches[0]
    if args.owner_check and (model.get('disable_thinking') or model.get('comparison_profile')):
        parser.error('OwnerCheck does not implement this candidate model thinking configuration')
    fixtures = (args.fixtures or repo / 'benchmarks/translation-context-fixtures.json').resolve(strict=True)
    profiles_path = repo / 'benchmarks/translation-prompt-profiles.json'
    profiles = json.loads(profiles_path.read_text(encoding='utf-8'))
    selected_profiles = args.profiles or profiles['profiles']
    if len(set(selected_profiles)) != len(selected_profiles):
        parser.error('duplicate profiles')
    required_profile = model.get('comparison_profile')
    if args.replay_trace and (args.sampling != 'greedy' or args.rounds != 1
            or args.compare_sampling or args.owner_check or args.prepare_only
            or selected_profiles != (['hymt2'] if required_profile == 'hymt2' else ['baseline'])
            or required_profile not in (None, 'hymt2')):
        parser.error('Replay requires one greedy round and exactly baseline Qwen or hymt2 profile')
    if (args.sampling == 'hymt2-recommended' or args.compare_sampling) and required_profile != 'hymt2':
        parser.error('Recommended sampling comparison requires Hy-MT2')
    if args.compare_sampling and args.termination_check:
        parser.error('Run termination check separately from the sampling comparison')
    if args.termination_check and (required_profile != 'hymt2' or not args.verify_input_contract or args.prepare_only):
        parser.error('Termination check requires installed Hy-MT2 and input contract verification')
    if args.exaone_input != 'existing' and required_profile != 'exaone':
        parser.error('EXAONE input variants require the EXAONE catalog/profile')
    if args.verify_input_contract and required_profile not in ('exaone', 'hymt2'):
        parser.error('Input contract verification requires EXAONE or Hy-MT2')
    if required_profile in ('exaone', 'hymt2') and selected_profiles != [required_profile]:
        parser.error(f'This model requires exactly profile {required_profile}')
    if any(p in ('exaone', 'hymt2') and p != required_profile for p in selected_profiles):
        parser.error('Candidate profile must match comparison_profile in the catalog')
    if args.prepare_only and 'gemma' in selected_profiles:
        parser.error('Gemma template rendering requires verified local GGUF assets')
    if args.owner_check and (args.catalog or 'gemma' in selected_profiles):
        parser.error('OwnerCheck is limited to the existing production manifest and policies')
    cases = json.loads(fixtures.read_text(encoding='utf-8'))['cases']
    if args.context_conditions:
        cases = [case for case in cases if case.get('context_condition') in args.context_conditions]
        if not cases:
            parser.error('No cases matched context conditions')
    if not args.prepare_only:
        if model.get('asset_status') not in (None, 'installed_verified'):
            parser.error('Candidate weights are not approved/installed; use PrepareOnly until asset preparation')
        model_path = (catalog_path.parent / model['path']).resolve(strict=True)
        if sha(model_path) != model['sha256']:
            raise RuntimeError('Existing translation model hash mismatch')
        server_path = Path(args.server or shutil.which('llama-server.exe') or
                           str(Path(os.environ['LOCALAPPDATA']) / 'Microsoft/WinGet/Links/llama-server.exe')).resolve(strict=True)
    probe = repo / 'target/debug/translation-probe.exe'
    export_args = [str(probe), '--prepare', model['id'], str(fixtures), str(out / 'requests.json')]
    if 'production' in selected_profiles:
        export_args.append('--isolated-context')
    subprocess.run(export_args, check=True, cwd=repo)
    exported = json.loads((out / 'requests.json').read_text(encoding='utf-8'))
    requests = {r['id']: r['body'] for r in exported['requests']}
    selected_ids = {case['id'] for case in cases}
    bodies = {name: {id: candidate(body, name, profiles, args.exaone_input) for id, body in requests.items() if id in selected_ids}
              for name in selected_profiles}
    sampling = {'temperature': .2, 'max_tokens': 256, 'top_k': 40, 'top_p': .9,
                'min_p': .1, 'repeat_penalty': 1}
    if args.sampling == 'greedy' or args.compare_sampling:
        sampling.update(temperature=0, top_k=1, top_p=1, min_p=0, seed=42)
    recommended = {'temperature': .7, 'max_tokens': 256, 'top_k': 20, 'top_p': .6,
                   'min_p': 0, 'repeat_penalty': 1.05, 'seed': 42}
    if args.sampling == 'hymt2-recommended':
        sampling = recommended
    sampling_configurations = {name: sampling for name in selected_profiles}
    if args.compare_sampling:
        original_bodies = bodies['hymt2']
        selected_profiles = ['hymt2-greedy', 'hymt2-recommended']
        bodies = {name: copy.deepcopy(original_bodies) for name in selected_profiles}
        sampling_configurations = {'hymt2-greedy': sampling, 'hymt2-recommended': recommended}
    if args.sampling != 'existing' or args.compare_sampling:
        for name, requests_by_id in bodies.items():
            for body in requests_by_id.values():
                body.update(sampling_configurations[name])
    if args.replay_trace:
        policy = 'hymt2-greedy' if required_profile == 'hymt2' else 'qwen-greedy'
        subprocess.run([str(probe), '--prepare', model['id'], str(fixtures),
            str(out / 'replay-requests.json'), '--' + policy], cwd=repo, check=True)
        replay_requests = json.loads((out / 'replay-requests.json').read_text(encoding='utf-8'))
        actual = {r['id']: r['body'] for r in replay_requests['requests']}
        if any(actual[id] != body for id, body in bodies[selected_profiles[0]].items()):
            raise ValueError('Rust replay request differs from verified comparison profile')
    contract_template = None
    if args.verify_input_contract:
        from jinja2 import StrictUndefined
        from jinja2.sandbox import SandboxedEnvironment
        metadata = gguf_metadata(model_path)
        expected_arch, expected_eos = ('exaone', 361) if required_profile == 'exaone' else ('hunyuan-dense', 120020)
        if metadata.get('general.architecture') != expected_arch or metadata.get('tokenizer.ggml.eos_token_id') != expected_eos:
            raise ValueError('Unexpected candidate architecture/EOS metadata')
        contract_template = SandboxedEnvironment(undefined=StrictUndefined).from_string(metadata['tokenizer.chat_template'])
    gemma_prompts = {}
    if 'gemma' in selected_profiles:
        from jinja2 import StrictUndefined
        from jinja2.sandbox import SandboxedEnvironment
        template_text = gguf_template(model_path)
        (out / 'embedded-template.jinja').write_text(template_text, encoding='utf-8')
        template = SandboxedEnvironment(undefined=StrictUndefined).from_string(template_text)
        def reject(message):
            raise ValueError(message)
        for id, body in bodies['gemma'].items():
            gemma_prompts[id] = template.render(messages=body['messages'], bos_token='<bos>',
                add_generation_prompt=True, raise_exception=reject)
    if model.get('disable_thinking'):
        for requests_by_id in bodies.values():
            for body in requests_by_id.values():
                body['chat_template_kwargs'] = {'enable_thinking': False}
    if exported['fixture_sha256'] != sha(fixtures):
        raise RuntimeError('Exported fixture hash mismatch')
    def save(name, data):
        (out / name).write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
    if gemma_prompts:
        save('rendered-gemma-prompts.json', gemma_prompts)
    save('candidate-requests.json', bodies)
    save('fixtures.json', json.loads(fixtures.read_text(encoding='utf-8')))
    save('profiles.json', profiles)
    save('request-fingerprints.json', {name: {id: hashlib.sha256(
        json.dumps(body, sort_keys=True, ensure_ascii=False).encode('utf-8')).hexdigest()
        for id, body in items.items()} for name, items in bodies.items()})
    if args.prepare_only:
        save('runtime.json', {'model_id': model['id'], 'profiles': selected_profiles,
            'exaone_input': args.exaone_input, 'sampling_mode': args.sampling,
            'catalog_sha256': sha(catalog_path), 'fixtures_sha256': sha(fixtures),
            'profiles_sha256': sha(profiles_path), 'probe_sha256': sha(probe),
            'prepared_requests': sum(len(items) for items in bodies.values()),
            'quality_gate_passed': False, 'weights_loaded': False,
            'template_validation': 'PENDING: verify GGUF and server before inference',
            'export_policy': 'isolated-context' if 'production' in selected_profiles else 'original',
            'scope': 'Offline bounded request export only; no model, HTTP, ASR, capture or UI.'})
        print('Prepared requests only; template/inference validation pending:', out, flush=True)
        return
    if required_profile in ('exaone', 'hymt2'):
        candidate_template = gguf_template(model_path)
        (out / 'embedded-template.jinja').write_text(candidate_template, encoding='utf-8')
    save('runtime.json', {'model_id': model['id'], 'model_sha256': sha(model_path),
                         'catalog_sha256': sha(catalog_path), 'disable_thinking': model.get('disable_thinking', False),
                         'context_conditions': args.context_conditions,
                         'gemma_transport': 'embedded Jinja2 render -> tokenize without added BOS -> completion' if gemma_prompts else None,
                         'gemma_template_sha256': hashlib.sha256(template_text.encode('utf-8')).hexdigest() if gemma_prompts else None,
                         'server_sha256': sha(server_path), 'probe_sha256': sha(probe),
                         'fixtures_sha256': sha(fixtures), 'profiles_sha256': sha(profiles_path),
                         'sampling': sampling, 'sampling_mode': args.sampling, 'exaone_input': args.exaone_input,
                         'sampling_configurations': sampling_configurations,
                         'comparison_sampling': args.compare_sampling,
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
    results, warmup, wire_requests, rendered_chat, contracts = [], [], {}, {}, {}
    def tokenize_prompt(prompt):
        request = urllib.request.Request(f'http://127.0.0.1:{port}/tokenize',
            data=json.dumps({'content': prompt, 'add_special': True, 'parse_special': True}).encode('utf-8'),
            headers={'Authorization': f'Bearer {token}', 'Content-Type': 'application/json'})
        with opener.open(request, timeout=10) as response:
            data = response.read(256 * 1024 + 1)
        if len(data) > 256 * 1024:
            raise ValueError('Oversized tokenization response')
        return json.loads(data)['tokens']
    def send(body):
        if required_profile in ('exaone', 'hymt2'):
            fingerprint = hashlib.sha256(json.dumps(body, sort_keys=True, ensure_ascii=False).encode('utf-8')).hexdigest()
            if fingerprint not in rendered_chat:
                template_request = urllib.request.Request(f'http://127.0.0.1:{port}/apply-template',
                    data=json.dumps({'messages': body['messages']}, ensure_ascii=False).encode('utf-8'),
                    headers={'Authorization': f'Bearer {token}', 'Content-Type': 'application/json'})
                with opener.open(template_request, timeout=10) as response:
                    rendered = response.read(256 * 1024 + 1)
                if len(rendered) > 256 * 1024:
                    raise RuntimeError('Oversized rendered prompt')
                prompt = json.loads(rendered).get('prompt')
                if not isinstance(prompt, str) or not prompt:
                    raise RuntimeError('Server did not provide its rendered chat prompt; exclude this run')
                rendered_chat[fingerprint] = prompt
                save('rendered-chat-prompts.json', rendered_chat)
                if contract_template is not None:
                    local_prompt = contract_template.render(messages=body['messages'], add_generation_prompt=True)
                    local_tokens, server_tokens = tokenize_prompt(local_prompt), tokenize_prompt(prompt)
                    bos = metadata.get('tokenizer.ggml.bos_token_id')
                    bos_count = server_tokens.count(bos)
                    expected_bos = int(metadata.get('tokenizer.ggml.add_bos_token', False)) if required_profile == 'exaone' else 1
                    assistant_suffix = '[|assistant|]' if required_profile == 'exaone' else '<｜hy_Assistant｜>'
                    valid = (local_tokens == server_tokens and bos_count == expected_bos
                             and len(server_tokens) < 4096 and prompt.endswith(assistant_suffix))
                    contracts[fingerprint] = {'metadata': metadata, 'local_tokens': local_tokens,
                        'server_tokens': server_tokens, 'bos_count': bos_count,
                        'rendered_tokenization_matches': valid, 'chat_prompt_count_matches': None,
                        'scope': 'Local/server render token equality plus chat usage count; internal chat token sequence is not exposed.'}
                    save('input-contracts.json', contracts)
                    if not valid:
                        raise ValueError('Candidate rendered input contract mismatch; exclude this run')
        start = time.monotonic()
        gemma = body['messages'][0]['role'] == 'user' and isinstance(body['messages'][0]['content'], list)
        endpoint = '/v1/chat/completions'
        if gemma:
            id = next(id for id, item in bodies['gemma'].items() if item == body)
            tokenize = urllib.request.Request(f'http://127.0.0.1:{port}/tokenize',
                data=json.dumps({'content': gemma_prompts[id], 'add_special': False, 'parse_special': True}).encode('utf-8'),
                headers={'Authorization': f'Bearer {token}', 'Content-Type': 'application/json'})
            with opener.open(tokenize, timeout=10) as response:
                tokens = json.loads(response.read(256 * 1024 + 1))['tokens']
            if not tokens or len(tokens) > 2048 or tokens[0] != 2 or tokens.count(2) != 1:
                raise ValueError('Gemma prompt must have exactly one BOS and fit the official 2K input budget')
            endpoint = '/completion'
            body = {'prompt': tokens, 'n_predict': sampling['max_tokens'], 'stream': False,
                    **{key: item for key, item in sampling.items() if key != 'max_tokens'},
                    'stop': ['<end_of_turn>'], 'cache_prompt': True}
            wire_requests[id] = body
            save('gemma-completion-requests.json', wire_requests)
        request = urllib.request.Request(f'http://127.0.0.1:{port}{endpoint}',
                    data=json.dumps(body, ensure_ascii=False).encode('utf-8'),
                    headers={'Authorization': f'Bearer {token}', 'Content-Type': 'application/json'})
        with opener.open(request, timeout=10) as response:
            data = response.read(256 * 1024 + 1)
        elapsed = time.monotonic() - start
        if len(data) > 256 * 1024:
            raise RuntimeError('Oversized response')
        value = json.loads(data)
        if contract_template is not None:
            contract = contracts[fingerprint]
            contract['chat_prompt_count_matches'] = value.get('usage', {}).get('prompt_tokens') == len(contract['server_tokens'])
            save('input-contracts.json', contracts)
            if not contract['chat_prompt_count_matches']:
                raise ValueError('Chat prompt count differs from rendered tokenization; exclude this run')
        if gemma:
            text = value.get('content', '').strip()
            stopped = value.get('stop_type') in ('eos', 'word') or value.get('stopped_eos') or value.get('stopped_word')
            error = None if stopped and text and not value.get('truncated') else 'IncompleteResponse'
            return text if error is None else None, elapsed, value.get('timings'), error
        choice = value['choices'][0]
        text = choice['message'].get('content', '').strip()
        error = None if choice['finish_reason'] == 'stop' and text else 'IncompleteResponse'
        if model.get('disable_thinking') and (choice['message'].get('reasoning_content') or '<think>' in text):
            error = 'UnexpectedReasoning'
        return text if error is None else None, elapsed, value.get('usage'), error
    try:
        with (out / 'server.log').open('w', encoding='utf-8') as log:
            server_args = [str(server_path), '-m', str(model_path), '--alias', model['id'],
                         '-c', '4096', '-ngl', '99', '--parallel', '1', '--top-k', '40', '--top-p', '.9',
                         '--min-p', '.1', '--repeat-penalty', '1', '--host', '127.0.0.1', '--port', str(port),
                         '--api-key-file', str(key_file)]
            if model.get('disable_thinking') or required_profile in ('exaone', 'hymt2') or 'gemma' in selected_profiles:
                server_args.append('--jinja')
            if args.termination_check:
                server_args.extend(['-lv', '4'])
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
            server_log = (out / 'server.log').read_text(encoding='utf-8', errors='replace')
            fallback = 'defaulting to chatml' in server_log.lower()
            runtime = json.loads((out / 'runtime.json').read_text(encoding='utf-8'))
            runtime['server_template_fallback'] = fallback
            runtime['fallback_bypassed_with_embedded_prompt'] = bool(gemma_prompts)
            runtime['candidate_template_sha256'] = hashlib.sha256(candidate_template.encode('utf-8')).hexdigest() if required_profile in ('exaone', 'hymt2') else None
            runtime['profiles'] = selected_profiles
            save('runtime.json', runtime)
            if fallback and not gemma_prompts:
                raise RuntimeError('Server silently substituted ChatML; exclude this run from model quality comparison')
            for profile in selected_profiles:
                text, elapsed, usage, error = send(bodies[profile][cases[0]['id']])
                warmup.append({'profile': profile, 'translation': text, 'elapsed_s': elapsed, 'usage': usage, 'error': error})
            if args.replay_trace:
                if any(row['error'] for row in warmup):
                    raise RuntimeError('Replay warmup failed')
                trace_path = args.replay_trace.resolve(strict=True)
                child_env = os.environ.copy()
                child_env['ECHOSUB_TRANSLATION_TOKEN'] = token
                policy = 'hymt2-greedy' if required_profile == 'hymt2' else 'qwen-greedy'
                with (out / 'replay.log').open('w', encoding='utf-8') as replay_log:
                    subprocess.run(['dotnet', str(repo / 'tests/EchoSub.NativeAsrSmoke/bin/Debug/net10.0/EchoSub.NativeAsrSmoke.dll'),
                        '--streaming-translation', str(repo / 'target/debug/echosub-worker.exe'),
                        f'http://127.0.0.1:{port}/v1/', model['id'], str(trace_path), str(out / 'replay.json'), policy],
                        cwd=repo, env=child_env, stdout=replay_log, stderr=subprocess.STDOUT,
                        check=True, timeout=900, creationflags=subprocess.CREATE_NO_WINDOW)
                save('replay-scope.json', {'source_trace_sha256': sha(trace_path), 'policy': policy,
                    'worker_sha256': sha(repo / 'target/debug/echosub-worker.exe'),
                    'client_sha256': sha(repo / 'tests/EchoSub.NativeAsrSmoke/bin/Debug/net10.0/EchoSub.NativeAsrSmoke.dll'),
                    'warmup': warmup, 'production_worker_scheduler': True, 'asr_admission': 'MOCK',
                    'capture': False, 'rendered_ui': False, 'quality_gate_passed': False})
                print('Fixed source replay completed:', out, flush=True)
                return
            if args.termination_check:
                native = {'prompt': next(iter(rendered_chat.values())), 'n_predict': sampling['max_tokens'],
                    **{key: item for key, item in sampling.items() if key != 'max_tokens'},
                    'stream': False, 'return_tokens': True, 'cache_prompt': False}
                request = urllib.request.Request(f'http://127.0.0.1:{port}/completion',
                    data=json.dumps(native, ensure_ascii=False).encode('utf-8'),
                    headers={'Authorization': f'Bearer {token}', 'Content-Type': 'application/json'})
                with opener.open(request, timeout=10) as response:
                    raw = response.read(256 * 1024 + 1)
                if len(raw) > 256 * 1024:
                    raise ValueError('Oversized native completion')
                result = json.loads(raw)
                save('termination-check.json', result)
                generated_tokens = result.get('tokens') or []
                if (result.get('stop_type') != 'eos' or not result.get('content', '').strip()
                        or not generated_tokens or generated_tokens[-1] != metadata['tokenizer.ggml.eos_token_id']):
                    raise ValueError('Native completion did not terminate with EOS; inspect retained result')
            for round in range(args.rounds):
                shift = round % len(selected_profiles)
                order = selected_profiles[shift:] + selected_profiles[:shift]
                for case_index, case in enumerate(cases if round % 2 == 0 else list(reversed(cases))):
                    if args.compare_sampling:
                        shift = (round + case_index) % len(selected_profiles)
                        order = selected_profiles[shift:] + selected_profiles[:shift]
                    for request_position, profile in enumerate(order, 1):
                        body = bodies[profile][case['id']]
                        text, elapsed, usage, error = send(body)
                        results.append({**case, 'round': round + 1, 'profile': profile, 'input_variant': args.exaone_input,
                                        'translation': text,
                                        'request_position': request_position,
                                        'elapsed_s': elapsed, 'usage': usage, 'error': error, 'manual_review': 'PENDING',
                                        'request_sha256': hashlib.sha256(json.dumps(body, sort_keys=True, ensure_ascii=False).encode('utf-8')).hexdigest()})
                        save('report.json', {'quality_gate_passed': False, 'warmup': warmup, 'results': results,
                                           'offered': len(cases) * len(selected_profiles) * args.rounds})
                print(f'Round {round + 1}: {len(results)} completed', flush=True)
            review_rows = list(enumerate(results))
            secrets.SystemRandom().shuffle(review_rows)
            mapping = {}
            fields = ['review_id', 'case_id', 'language', 'partition', 'category', 'context_condition',
                      'source', 'context', 'reference', 'translation', 'response_error',
                      'critical_error', 'omission', 'extra_explanation', 'naturalness_1_to_5', 'notes']
            with (out / 'semantic-review.csv').open('w', encoding='utf-8-sig', newline='') as stream:
                writer = csv.DictWriter(stream, fieldnames=fields)
                writer.writeheader()
                for n, (index, row) in enumerate(review_rows, 1):
                    review_id = f'review-{n:04d}'
                    mapping[review_id] = {'result_index': index, 'profile': row['profile'],
                                          'model_id': model['id'], 'round': row['round']}
                    writer.writerow({'review_id': review_id, 'case_id': row['id'],
                        'language': row['language'], 'partition': row.get('partition', 'legacy'),
                        'category': row.get('category', 'unclassified'),
                        'context_condition': row.get('context_condition', ''), 'source': row['source'],
                        'context': json.dumps(row.get('context', []), ensure_ascii=False),
                        'reference': row.get('reference', ''), 'translation': row['translation'],
                        'response_error': row['error'] or '', 'critical_error': 'PENDING',
                        'omission': 'PENDING', 'extra_explanation': 'PENDING',
                        'naturalness_1_to_5': 'PENDING', 'notes': ''})
            save('review-mapping.json', mapping)
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
