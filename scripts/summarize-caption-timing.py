"""Summarize metadata-only timing logs; no physical render latency claim."""
import argparse
from collections import OrderedDict
import json
import math
from pathlib import Path
import statistics

parser = argparse.ArgumentParser()
parser.add_argument('log', type=Path)
args = parser.parse_args()
receipts = OrderedDict()
applied = OrderedDict()
samples = []
deferred = []
missing = dropped = invalid = 0


def identity(row):
    names = ('worker_pid', 'session_id', 'epoch', 'segment_id', 'source_revision', 'translation_request_id')
    values = tuple(row.get(name) for name in names)
    return values if all(isinstance(value, int) and not isinstance(value, bool) and value > 0 for value in values) else None


def bounded_put(mapping, key, value):
    mapping[key] = value
    mapping.move_to_end(key)
    while len(mapping) > 512:
        mapping.popitem(last=False)


with args.log.open(encoding='utf-8-sig') as source:
    for line in source:
        try:
            row = json.loads(line)
            phase = row.get('phase')
            if phase == 'dropped':
                dropped += int(row['count'])
                continue
            key = identity(row)
            at = float(row.get('at_s', 0))
            if not key or not math.isfinite(at):
                continue
            if phase == 'translation_event_received':
                if key not in receipts:
                    bounded_put(receipts, key, at)
            elif phase == 'deck_applied' and row.get('overlay_visible') is True and key not in applied:
                bounded_put(applied, key, True)
                received = receipts.get(key)
                wait = float(row.get('deferred_s', 0))
                if received is None:
                    missing += 1
                elif at >= received and math.isfinite(wait) and wait >= 0:
                    samples.append(at - received)
                    deferred.append(wait)
                else:
                    invalid += 1
        except (json.JSONDecodeError, ValueError, TypeError, AttributeError, KeyError, OverflowError):
            invalid += 1


def stats(values):
    return None if not values else {'median': statistics.median(values), 'min': min(values), 'max': max(values)}


print(json.dumps({'matched_first_visible_deck_applications': len(samples),
                  'event_receipt_to_deck_s': stats(samples), 'deck_deferred_s': stats(deferred),
                  'missing_receipts': missing, 'reported_dropped': dropped, 'invalid': invalid,
                  'note': 'UI deck application, not physical rendering or audio-to-caption latency. Hidden overlays excluded; at most 512 receipt/identity keys retained. Missing/dropped observations prevent complete coverage.'}, indent=2))
