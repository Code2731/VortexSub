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


# Worker timestamps share an origin only within one worker process. Never subtract
# them from the desktop Stopwatch. Maps stay bounded; missing pairs are not zero.
revisions = OrderedDict()
requests = OrderedDict()
segments = OrderedDict()
stage_samples = {}
stage_missing_pairs = {}
stage_events = stage_invalid = ignored_asr = unapplied_translation = 0


def measure(name, end, start):
    if isinstance(start, (int, float)) and not isinstance(start, bool) and math.isfinite(start) and end >= start:
        stage_samples.setdefault(name, []).append(end - start)
    else:
        stage_missing_pairs[name] = stage_missing_pairs.get(name, 0) + 1


def pipeline(row):
    global stage_events, stage_invalid, ignored_asr, unapplied_translation
    stage_events += 1
    names = ('worker_pid', 'session_id', 'epoch', 'segment_id', 'source_revision')
    key = tuple(row.get(name) for name in names)
    at = row.get('worker_at_s')
    if (not all(isinstance(x, int) and not isinstance(x, bool) and x > 0 for x in key)
            or not isinstance(at, (int, float)) or isinstance(at, bool) or not math.isfinite(at) or at < 0):
        stage_invalid += 1
        return
    event = row.get('event_name')
    rev = revisions.get(key, {})
    seg = segments.get(key[:-1], {})
    if event in ('capture.partial_requested', 'capture.segmented') and row.get('queued') is True:
        wait = row.get('partial_deferred_wait_s')
        if isinstance(wait, (int, float)) and math.isfinite(wait) and wait >= 0:
            stage_samples.setdefault('latest_partial_deferred_wait_s', []).append(wait)
        rev.setdefault('admitted', at)
        seg.setdefault('admitted', at)
        audio_end = row.get('audio_end_s')
        voice_start = row.get('voice_start_s')
        if isinstance(audio_end, (int, float)) and math.isfinite(audio_end):
            measure('sampled_voice_start_to_request_audio_end_s', audio_end, voice_start)
    elif event == 'asr.started':
        rev.setdefault('asr_started', at)
        measure('asr_admission_to_owner_dispatch_s', at, rev.get('admitted'))
    elif event == 'asr.completed':
        if row.get('applied') is True:
            measure('asr_owner_dispatch_to_completion_s', at, rev.get('asr_started'))
            decode = row.get('decode_s')
            if isinstance(decode, (int, float)) and math.isfinite(decode) and decode >= 0:
                stage_samples.setdefault('native_decode_s', []).append(decode)
        else:
            ignored_asr += 1
    elif event in ('source.partial', 'source.final'):
        rev.setdefault('source_ready', at)
        if 'first_source' not in seg:
            seg['first_source'] = at
            measure('first_observed_admission_to_first_source_s', at, seg.get('admitted'))
        if event == 'source.partial' and (row.get('stable_chars') or 0) > 0 and 'first_stable' not in seg:
            seg['first_stable'] = at
            measure('first_source_to_first_nonempty_stable_s', at, seg.get('first_source'))
    elif event in ('translation.started', 'translation.completed', 'translation.updated'):
        request_id = row.get('translation_request_id')
        if not isinstance(request_id, int) or isinstance(request_id, bool) or request_id <= 0:
            stage_invalid += 1
            return
        request_key = key + (request_id,)
        request = requests.get(request_key, {})
        if event == 'translation.started':
            request.setdefault('started', at)
            measure('source_revision_ready_to_translation_dispatch_s', at, rev.get('source_ready'))
        elif event == 'translation.completed':
            if row.get('applied') is True:
                request['completed'] = at
                measure('translation_owner_dispatch_to_completion_s', at, request.get('started'))
            else:
                unapplied_translation += 1
        elif event == 'translation.updated':
            measure('translation_completion_to_update_publish_s', at, request.get('completed'))
            if 'first_translation' not in seg:
                seg['first_translation'] = at
                measure('first_observed_admission_to_first_translation_update_s', at, seg.get('admitted'))
                measure('first_nonempty_stable_to_first_translation_update_s', at, seg.get('first_stable'))
        bounded_put(requests, request_key, request)
    bounded_put(revisions, key, rev)
    bounded_put(segments, key[:-1], seg)


with args.log.open(encoding='utf-8-sig') as source:
    for line in source:
        try:
            row = json.loads(line)
            phase = row.get('phase')
            if phase == 'pipeline_event_received':
                pipeline(row)
                continue
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
    return None if not values else {'n': len(values), 'median': statistics.median(values), 'min': min(values), 'max': max(values)}


print(json.dumps({'matched_first_visible_deck_applications': len(samples),
                  'event_receipt_to_deck_s': stats(samples), 'deck_deferred_s': stats(deferred),
                  'pipeline': {'events': stage_events, 'invalid': stage_invalid,
                               'ignored_asr': ignored_asr, 'unapplied_translation': unapplied_translation,
                               'durations_s': {name: stats(values) for name, values in stage_samples.items()},
                               'missing_or_invalid_pairs': stage_missing_pairs,
                               'note': 'Worker-local clock differences only. Sampled voice span is an audio range, not wall-clock latency. Admission begins after VAD and may be coalesced; nonempty stable text is not necessarily eligible for translation. No device-to-screen latency or worker-to-desktop transport duration. Missing/evicted pairs are omitted, not zero.'},
                  'missing_receipts': missing, 'reported_dropped': dropped, 'invalid': invalid,
                  'note': 'UI deck application, not physical rendering or audio-to-caption latency. Hidden overlays excluded; at most 512 receipt/identity keys retained. Missing/dropped observations prevent complete coverage.'}, indent=2))
