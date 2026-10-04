"""Summarize manually annotated translation CSV; never auto-select a model."""
import argparse
import csv
import json
from collections import defaultdict
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('review', type=Path)
    parser.add_argument('--mapping', type=Path, help='Optional private mapping, for per-model/profile counts after annotation')
    args = parser.parse_args()
    mapping = json.loads(args.mapping.read_text(encoding='utf-8')) if args.mapping else None
    groups = defaultdict(list)
    seen = set()
    with args.review.open(encoding='utf-8-sig', newline='') as stream:
        for row in csv.DictReader(stream):
            if not row.get('review_id') or row['review_id'] in seen:
                raise ValueError('Missing or duplicate review_id')
            seen.add(row['review_id'])
            for field in ('critical_error', 'omission', 'extra_explanation'):
                if row[field] not in ('0', '1', 'PENDING'):
                    raise ValueError(f'{row["review_id"]}: {field} must be 0, 1 or PENDING')
            if row['naturalness_1_to_5'] not in ('1', '2', '3', '4', '5', 'PENDING'):
                raise ValueError('Naturalness must be 1..5 or PENDING')
            identity = mapping[row['review_id']] if mapping is not None else {}
            groups[(identity.get('model_id', 'BLINDED'), identity.get('profile', 'BLINDED'),
                    row['language'], row['partition'], row['category'], row['context_condition'])].append(row)
    summary = []
    for key, rows in sorted(groups.items()):
        valid = [r for r in rows if not r['response_error']]
        reviewed = [r for r in valid if all(r[f] != 'PENDING' for f in
                    ('critical_error', 'omission', 'extra_explanation', 'naturalness_1_to_5'))]
        summary.append({'model_id': key[0], 'profile': key[1],
            'language': key[2], 'partition': key[3], 'category': key[4],
            'context_condition': key[5], 'offered_rows': len(rows),
            'unique_case_ids': len({r['case_id'] for r in rows}),
            'response_errors': len(rows) - len(valid), 'reviewed_rows': len(reviewed),
            'pending_rows': len(valid) - len(reviewed),
            'critical_error_rows': sum(r['critical_error'] == '1' for r in reviewed),
            'omission_rows': sum(r['omission'] == '1' for r in reviewed),
            'extra_explanation_rows': sum(r['extra_explanation'] == '1' for r in reviewed),
            'naturalness_mean': sum(int(r['naturalness_1_to_5']) for r in reviewed) / len(reviewed) if reviewed else None})
    print(json.dumps({'quality_gate_passed': False,
        'scope': 'Manual review counts only. Repeated rows are not independent sentences. '
                 'No automatic model selection. Without mapping these are blinded review-progress counts only.',
        'groups': summary}, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
