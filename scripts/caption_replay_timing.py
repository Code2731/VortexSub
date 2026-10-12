"""Exact fragment evidence in saved replay frames, not semantic/version ownership proof."""
import math


VERSION = ('session_id', 'epoch', 'segment_id', 'source_revision', 'translation_request_id')


def fragment_timings(run, frames):
    targets = run.get('timing_targets', [])
    if len(targets) > 128:
        raise ValueError('Too many timing targets')
    events = []
    for event in run['events']:
        if not math.isfinite(event['at_s']) or not 0 <= event['at_s'] <= 120:
            raise ValueError('Invalid input time')
        record = event['message']['payload'].get('record', {})
        if record.get('translation_state') == 'Done' and record.get('translation'):
            events.append((event['at_s'], record))
    previous_time = -1
    for frame in frames:
        at = frame['virtual_s']
        if not math.isfinite(at) or at < 0 or at < previous_time:
            raise ValueError('Invalid frame time')
        previous_time = at
    results, seen = [], set()
    for target in targets:
        key = tuple(target[name] for name in VERSION)
        text = target['text']
        unit_start = target.get('unit_start', 0)
        if type(unit_start) is not int or unit_start < 0 or unit_start > 16384:
            raise ValueError('Invalid timing unit offset')
        if any(type(value) is not int or value <= 0 for value in key):
            raise ValueError('Invalid timing identity')
        if not isinstance(text, str) or not text.strip() or len(text) > 16384 or '\n' in text or '\r' in text:
            raise ValueError('Timing fragment must fit one saved row without normalization')
        if (key, text) in seen:
            raise ValueError('Duplicate timing target')
        seen.add((key, text))
        matching = [(at, record) for at, record in events if tuple(record.get(name) for name in VERSION) == key]
        if not matching or any(record['translation'].count(text) != 1 for _, record in matching):
            raise ValueError('Timing target is absent or ambiguous in its input version')
        if len({record['translation'] for _, record in matching}) != 1:
            raise ValueError('Conflicting text for one input version')
        receipt = min(at for at, _ in matching)
        next_version = min((at for at, record in events if at > receipt
                            and tuple(record.get(name) for name in VERSION[:3]) == key[:3]
                            and tuple(record.get(name) for name in VERSION) != key), default=None)

        def visible(frame):
            for slot in ('upper', 'lower'):
                identity = frame.get(slot + '_identity') or {}
                if (identity.get('Session'), identity.get('Epoch'), identity.get('Segment')) == key[:3] \
                        and identity.get('UnitStart') == unit_start \
                        and text in frame[slot]:
                    return True
            return False

        earlier = [frame for frame in frames if frame['virtual_s'] < receipt]
        prior = [frame for frame in earlier if visible(frame)]
        current = earlier[-1] if earlier else None
        first = next((frame['virtual_s'] for frame in frames
                      if frame['virtual_s'] >= receipt and (next_version is None or frame['virtual_s'] < next_version)
                      and visible(frame)), None)
        status = ('already_visible' if current and visible(current) else 'previously_delivered') if prior else \
                 'newly_visible' if first is not None else 'superseded' if next_version is not None else 'not_visible'
        results.append(dict(**{name: target[name] for name in VERSION}, text=text, received_at_s=receipt,
                            unit_start=unit_start,
                            status=status, first_observed_at_s=prior[0]['virtual_s'] if prior else first,
                            wait_s=round(first - receipt, 6) if status == 'newly_visible' else None,
                            next_version_at_s=next_version))
    return results
