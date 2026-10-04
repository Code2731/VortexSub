"""Fixed partial-source replay: real worker/core/HTTP, mocked ASR admission, no UI."""
import argparse
import csv
import hashlib
import json
from pathlib import Path
import secrets
import statistics
import subprocess
import sys
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--trace', type=Path, required=True)
    parser.add_argument('--rounds', type=int, choices=range(1, 4), default=3)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    if args.trace.stat().st_size > 1024 * 1024:
        parser.error('Trace exceeds 1 MiB')
    raw = args.trace.read_bytes()
    if len(raw) > 1024 * 1024:
        parser.error('Trace grew beyond 1 MiB')
    trace = json.loads(raw)
    cases = trace['cases']
    if not 1 <= len(cases) <= 100 or len({c['id'] for c in cases}) != len(cases):
        parser.error('Expected 1..100 unique trace cases')
    out = repo / 'benchmarks/results' / (time.strftime('fixed-translation-%Y%m%d-%H%M%S') + '-' + secrets.token_hex(3))
    out.mkdir(parents=True)
    frozen = out / 'source-trace.json'
    frozen.write_bytes(raw)

    def save(name, value):
        (out / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')

    save('scope.json', {'trace_sha256': hashlib.sha256(raw).hexdigest(), 'original_trace': str(args.trace.resolve()),
        'rounds': args.rounds, 'production_worker_scheduler': True, 'asr_admission': 'MOCK',
        'scope': 'Precomputed Whisper or authored revisions, actual HTTP and history IPC. '
                 'First translation is an observed history update, not rendered caption or verified meaning unit. '
                 'Availability includes original precomputed ASR estimates when supplied; no fresh native decode. '
                 'Final-only/preview mode order alternates by case; model order reverses by round. '
                 'UTF-16 prefix replacement counts are textual edits, not semantic errors.',
        'capture': False, 'rendered_ui': False, 'quality_gate_passed': False,
        'gpu': 'Windows RTX 3080 10 GB; no game contention or sampled VRAM measurement'})
    models = {
        'qwen': ('benchmarks/model-downloads.json', 'qwen3-4b-instruct-2507-q4_k_m', 'baseline'),
        'hymt2': ('benchmarks/translation-research-models.json', 'hy-mt2-1.8b-q4_k_m', 'hymt2'),
    }
    runs, pooled = [], []
    print('Fixed replay output:', out, flush=True)
    for round_index in range(args.rounds):
        for name in (list(models) if round_index % 2 == 0 else list(reversed(models))):
            catalog, model_id, profile = models[name]
            path = out / f'round-{round_index + 1}-{name}'
            command = [sys.executable, '-X', 'utf8', str(repo / 'scripts/probe-translation-context.py'),
                '--rounds', '1', '--sampling', 'greedy', '--profiles', profile,
                '--catalog', catalog, '--model-id', model_id,
                '--server', str(repo / 'models/runtime-b11146/llama-server.exe'),
                '--fixtures', str(repo / 'benchmarks/translation-exaone-holdout.json'),
                '--output', str(path), '--replay-trace', str(frozen)]
            if name == 'hymt2':
                command.append('--verify-input-contract')
            result = subprocess.run(command, cwd=repo)
            runs.append({'round': round_index + 1, 'model': name, 'path': str(path.relative_to(out)), 'exit_code': result.returncode})
            save('runs.json', runs)
            if result.returncode:
                raise RuntimeError(f'Replay failed; retained output: {path}')
            report = json.loads((path / 'replay.json').read_text(encoding='utf-8'))
            if report['source_trace_sha256'] != hashlib.sha256(raw).hexdigest():
                raise RuntimeError('Replay did not use frozen trace')
            for row in report['runs']:
                pooled.append({**row, 'round': round_index + 1, 'model': name, 'run': str(path.relative_to(out))})
            save('pooled.json', pooled)
    groups = []
    for case in cases:
        for name in models:
            for preview in (False, True):
                rows = [r for r in pooled if r['id'] == case['id'] and r['model'] == name and r['preview_enabled'] == preview]
                group = {'case_id': case['id'], 'language': case['language'], 'model': name,
                    'source_kind': case.get('source_kind', 'unspecified'), 'preview_enabled': preview, 'runs': len(rows)}
                for metric in ('first_translation_s', 'final_http_and_ipc_s', 'removed_characters',
                               'final_correction_removed_characters', 'changed_translation_updates'):
                    group['median_' + metric] = statistics.median(r[metric] for r in rows)
                group['preview_observed_runs'] = sum(r['first_preview_s'] is not None for r in rows)
                observations = [o for r in rows for o in r.get('target_observations', [])]
                group['target_changes'] = {}
                for phase in ('Candidate', 'Displayed'):
                    changes = [o['change'] for o in observations if o['phase'] == phase and o.get('change')]
                    counts = {}
                    for change in changes:
                        counts[change['kind']] = counts.get(change['kind'], 0) + 1
                    group['target_changes'][phase] = {'counts': counts,
                        'same_unit_removed_utf16': sum(c['same_unit_removed_utf16'] or 0 for c in changes),
                        'guard_invalidated_changes': sum(c['source_guard_invalidated'] for c in changes),
                        'semantic_contradiction': 'UNASSESSED'}
                group['guard_invalidation_observations'] = sum(o['phase'] == 'SourceGuardInvalidated' for o in observations)
                paired = [r for r in rows if 'cosmetic_policy' in r]
                if paired:
                    group['cosmetic_policy'] = {
                        'first_deck_delta_s': [r['cosmetic_policy']['first_deck_delta_s'] for r in paired],
                        'deferred_observations': sum(o['phase'] == 'CosmeticDeferred' for r in paired
                            for o in r['cosmetic_policy']['target_observations']),
                        'baseline_visible_translation_changes': sum(len(r['deck_snapshots']) for r in paired),
                        'candidate_visible_translation_changes': sum(len(r['cosmetic_policy']['snapshots']) for r in paired),
                        'scope': 'Both decks receive identical history and clock; no second inference. No physical rendering.'}
                groups.append(group)
    save('summary.json', {'quality_gate_passed': False, 'groups': groups})
    review = [(r, s) for r in pooled for s in r['snapshots']]
    secrets.SystemRandom().shuffle(review)
    mapping = {}
    with (out / 'semantic-review.csv').open('w', encoding='utf-8-sig', newline='') as stream:
        fields = ['review_id', 'case_id', 'source_kind', 'source', 'translation_source', 'translation',
                  'preview', 'received_s', 'change_kind', 'unit_start_utf16', 'source_guard_invalidated',
                  'semantic_contradiction', 'critical_error', 'omission', 'unsupported_completion', 'notes']
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        for index, (run, snapshot) in enumerate(review, 1):
            review_id = f'review-{index:04d}'
            record = snapshot['record']
            change = next((o['change'] for o in run.get('target_observations', [])
                if o['phase'] == 'Candidate' and o.get('change')
                and o['change']['request_id'] == record['translation_request_id']), {})
            mapping[review_id] = {k: run[k] for k in ('model', 'round', 'run', 'preview_enabled')}
            writer.writerow({'review_id': review_id, 'case_id': run['id'], 'source_kind': run['source_kind'],
                'source': record['source'], 'translation_source': record['translation_source'],
                'translation': record['translation'], 'preview': record['translation_is_preview'],
                'received_s': snapshot['received_s'], 'critical_error': 'PENDING', 'omission': 'PENDING',
                'change_kind': change.get('kind', ''), 'unit_start_utf16': change.get('unit_start_utf16', ''),
                'source_guard_invalidated': change.get('source_guard_invalidated', ''),
                'semantic_contradiction': 'PENDING',
                'unsupported_completion': 'PENDING'})
    save('review-mapping.json', mapping)
    print(json.dumps(groups, ensure_ascii=False, indent=2), flush=True)


if __name__ == '__main__':
    main()
