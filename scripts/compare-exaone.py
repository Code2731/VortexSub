"""Sequential Qwen/candidate HTTP comparison; leaves semantic review pending."""
import argparse
import csv
import json
import math
from pathlib import Path
import secrets
import statistics
import subprocess
import sys
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rounds', type=int, choices=range(1, 4), default=3)
    parser.add_argument('--sampling', choices=['existing', 'greedy'], default='existing')
    parser.add_argument('--verify-input-contract', action='store_true')
    parser.add_argument('--context-ablation', action='store_true', help='Compare existing, separated and source-only EXAONE inputs')
    parser.add_argument('--fixtures', type=Path, default=Path('benchmarks/translation-research-fixtures.json'))
    parser.add_argument('--candidate', choices=['exaone', 'hymt2'], default='exaone')
    parser.add_argument('--server', help='Pinned runtime path used for both Qwen and candidate')
    args = parser.parse_args()
    if args.candidate != 'exaone' and args.context_ablation:
        parser.error('Context ablation only implements EXAONE inputs')
    repo = Path(__file__).resolve().parent.parent
    out = repo / 'benchmarks/results' / (time.strftime(args.candidate + '-comparison-%Y%m%d-%H%M%S') + '-' + secrets.token_hex(3))
    out.mkdir(parents=True)
    models = {
        'qwen': ('benchmarks/model-downloads.json', 'qwen3-4b-instruct-2507-q4_k_m', 'baseline'),
        'exaone': ('benchmarks/translation-research-models.json', 'exaone-3.5-2.4b-q4_k_m', 'exaone'),
    }
    if args.candidate == 'hymt2':
        models.pop('exaone')
        models['hymt2'] = ('benchmarks/translation-research-models.json', 'hy-mt2-1.8b-q4_k_m', 'hymt2')
    if args.context_ablation:
        models['exaone-separated'] = (*models['exaone'][:2], 'exaone/separated')
        models['exaone-source-only'] = (*models['exaone'][:2], 'exaone/source-only')
    runs, pooled = [], []
    def save(name, value):
        (out / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    save('scope.json', {
        'quality_gate_passed': False, 'rounds': args.rounds, 'sampling_mode': args.sampling,
        'context_ablation': args.context_ablation, 'fixtures': str(args.fixtures), 'candidate': args.candidate,
        'requested_server': args.server,
        'scope': 'Authored full-source HTTP comparison, no ASR/capture/UI. Warmup excluded. '
                 'One model loaded at a time; reverse model and fixture order on alternate rounds. '
                 'Sampling mode recorded separately; no cross-hardware determinism guarantee. '
                 'Semantic annotations pending; Japanese EXAONE results exploratory when selected.',
        'gpu': 'Windows RTX 3080 10 GB; GPU memory not sampled; game state not established',
    })
    print('Comparison output:', out, flush=True)
    for round_index in range(args.rounds):
        keys = list(models)
        shift = round_index % len(keys)
        order = keys[shift:] + keys[:shift]
        if round_index % 2:
            order.reverse()
        if len(keys) == 2:
            order = keys if round_index % 2 == 0 else list(reversed(keys))
        fixtures = json.loads((repo / args.fixtures).read_text(encoding='utf-8'))
        if round_index % 2:
            fixtures['cases'].reverse()
        fixture_path = out / f'fixtures-round-{round_index + 1}.json'
        fixture_path.write_text(json.dumps(fixtures, ensure_ascii=False, indent=2), encoding='utf-8')
        for name in order:
            catalog, model_id, profile = models[name]
            run = out / f'round-{round_index + 1}-{name}'
            print(f'Round {round_index + 1}: {name}', flush=True)
            command = [sys.executable, '-X', 'utf8', str(repo / 'scripts/probe-translation-context.py'),
                '--rounds', '1', '--catalog', catalog, '--model-id', model_id, '--profiles', profile.split('/')[0],
                '--fixtures', str(fixture_path), '--output', str(run), '--sampling', args.sampling]
            if '/' in profile:
                command.extend(['--exaone-input', profile.split('/')[1]])
            if args.server:
                command.extend(['--server', args.server])
            if args.verify_input_contract and name != 'qwen':
                command.append('--verify-input-contract')
            result = subprocess.run(command, cwd=repo)
            runs.append({'round': round_index + 1, 'model_id': model_id, 'profile': profile,
                         'path': str(run.relative_to(out)), 'exit_code': result.returncode})
            save('runs.json', runs)
            if (run / 'report.json').exists():
                report = json.loads((run / 'report.json').read_text(encoding='utf-8'))
                for row in report['results']:
                    pooled.append({**row, 'round': round_index + 1, 'model_id': model_id, 'profile': profile,
                                   'run': str(run.relative_to(out))})
            if result.returncode:
                raise RuntimeError(f'{name} comparison failed; retained files in {run}')
    summary = []
    for name, (_, model_id, profile) in models.items():
        for language in ('en', 'ja'):
            rows = [r for r in pooled if r['model_id'] == model_id and r['profile'] == profile and r['language'] == language]
            durations = sorted(r['elapsed_s'] for r in rows)
            by_case = {}
            for row in rows:
                by_case.setdefault(row['id'], []).append(row['translation'])
            output_tokens = [r['usage']['completion_tokens'] for r in rows
                             if r.get('usage') and 'completion_tokens' in r['usage']]
            summary.append({'model_id': model_id, 'profile': profile, 'language': language,
                'rows': len(rows), 'unique_case_ids': len({r['id'] for r in rows}),
                'response_errors': sum(bool(r['error']) for r in rows),
                'median_s': statistics.median(durations),
                'p95_s': durations[math.ceil(.95 * len(durations)) - 1], 'max_s': max(durations),
                'mean_output_tokens': statistics.mean(output_tokens) if output_tokens else None,
                'identical_repeat_case_ids': sum(len(items) == args.rounds and len(set(items)) == 1
                                                  for items in by_case.values()),
                'scope_note': 'Repeated equality is reproducibility, not translation correctness.',
                'quality_review': 'PENDING'})
    save('summary.json', {'quality_gate_passed': False, 'groups': summary})
    secrets.SystemRandom().shuffle(pooled)
    fields = ['review_id', 'case_id', 'language', 'partition', 'category', 'context_condition',
              'source', 'context', 'reference', 'translation', 'response_error',
              'critical_error', 'omission', 'extra_explanation', 'naturalness_1_to_5', 'notes']
    mapping = {}
    with (out / 'semantic-review.csv').open('w', encoding='utf-8-sig', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        for index, row in enumerate(pooled, 1):
            review_id = f'review-{index:04d}'
            mapping[review_id] = {key: row[key] for key in ('model_id', 'profile', 'round', 'run')}
            mapping[review_id]['case_id'] = row['id']
            writer.writerow({'review_id': review_id, 'case_id': row['id'],
                **{key: row.get(key, '') for key in ('language', 'partition', 'category', 'context_condition',
                                                     'source', 'reference', 'translation')},
                'context': json.dumps(row.get('context', []), ensure_ascii=False),
                'response_error': row['error'] or '', 'critical_error': 'PENDING', 'omission': 'PENDING',
                'extra_explanation': 'PENDING', 'naturalness_1_to_5': 'PENDING', 'notes': ''})
    save('review-mapping.json', mapping)
    print(json.dumps(summary, ensure_ascii=False, indent=2), flush=True)


if __name__ == '__main__':
    main()
