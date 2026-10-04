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
map_evictions = 0


def identity(row):
    names = ('worker_pid', 'session_id', 'epoch', 'segment_id', 'source_revision', 'translation_request_id')
    values = tuple(row.get(name) for name in names)
    return values if all(isinstance(value, int) and not isinstance(value, bool) and value > 0 for value in values) else None


def bounded_put(mapping, key, value):
    global map_evictions
    mapping[key] = value
    mapping.move_to_end(key)
    while len(mapping) > 512:
        mapping.popitem(last=False)
        map_evictions += 1


# Worker timestamps share an origin only within one worker process. Never subtract
# them from the desktop Stopwatch. Maps stay bounded; missing pairs are not zero.
revisions = OrderedDict()
requests = OrderedDict()
segments = OrderedDict()
stage_samples = {}
hold_observations = {}
adaptive_observations = {}
asr_outcomes = {}
window_counts = {'attempted': 0, 'fallback': 0}
first_translation_hold_observations = {}
stage_missing_pairs = {}
stage_events = stage_invalid = ignored_asr = unapplied_translation = 0
vad_segments = OrderedDict()
pre_asr_events = pre_asr_invalid = pre_asr_first_admissions = 0
pre_asr_observations = {}
pre_asr_deferrals = {'asr_busy': 0, 'preview_busy': 0, 'adaptive_wait': 0}
reading_counts = {}
reading_drops = {}
reading_blocks = {}
reading_block_durations = {}
reading_block_evictions = 0
reading_correction_reuse = {'with_reuse': 0, 'without_reuse': 0}
reading_waits = []
reading_wait_evictions = 0
caption_event_drops = OrderedDict()


def reading(row):
    global reading_wait_evictions, reading_block_evictions
    kind = row.get('kind')
    if kind not in ('Queued', 'Coalesced', 'FirstLine', 'Dropped', 'Invalidated', 'TailRetained',
                    'Blocked', 'BlockEnded', 'CorrectionApplied', 'CosmeticRetained'):
        return
    reading_counts[kind] = reading_counts.get(kind, 0) + 1
    reason = row.get('reason')
    if kind == 'Blocked' and reason in ('UnitTransition', 'Correction', 'BothLinesProtected',
                                      'LowerLineProtected', 'UpperLineProtected', 'UnreadTail'):
        reading_blocks[reason] = reading_blocks.get(reason, 0) + 1
    if kind == 'BlockEnded' and reason in ('UnitTransition', 'Correction', 'BothLinesProtected',
                                          'LowerLineProtected', 'UpperLineProtected', 'UnreadTail'):
        duration = row.get('blocked_s')
        if row.get('overlay_visible') is True and finite_seconds(duration):
            durations = reading_block_durations.setdefault(reason, [])
            durations.append(duration)
            if len(durations) > 4096:
                durations.pop(0)
                reading_block_evictions += 1
    if kind == 'CorrectionApplied':
        reused = row.get('reused_characters')
        if isinstance(reused, int) and not isinstance(reused, bool) and reused >= 0:
            reading_correction_reuse['with_reuse' if reused else 'without_reuse'] += 1
    if kind == 'Dropped' and reason in ('InputTooLong', 'QueueCapacity', 'QueueAge', 'UnreadTailAge', 'LateUnit'):
        reading_drops[reason] = reading_drops.get(reason, 0) + 1
    if kind == 'FirstLine' and row.get('overlay_visible') is True and finite_seconds(row.get('reading_wait_s')):
        reading_waits.append(row['reading_wait_s'])
        if len(reading_waits) > 4096:
            reading_waits.pop(0)
            reading_wait_evictions += 1


def finite_seconds(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and value >= 0


def vad_identity(row):
    values = tuple(row.get(name) for name in ('worker_pid', 'session_id', 'epoch', 'vad_segment_id'))
    return values if all(isinstance(x, int) and not isinstance(x, bool) and x > 0 for x in values) else None


def pre_asr(row):
    global pre_asr_events, pre_asr_invalid
    pre_asr_events += 1
    key = vad_identity(row)
    at = row.get('worker_at_s')
    if key is None or not finite_seconds(at):
        pre_asr_invalid += 1
        return
    event = row.get('event_name')
    pre_asr_observations[event] = pre_asr_observations.get(event, 0) + 1
    if event in ('capture.voice_observed', 'capture.asr_eligible'):
        measure('vad_observation_to_worker_receipt_s', at, row.get('observed_worker_s'))
        # Older logs did not record process start. Preserve missing values, not zeros.
        if 'processing_started_worker_s' in row and row.get('processing_started_worker_s') is not None:
            kind = row.get('processing_kind')
            if kind in ('push', 'poll'):
                measure(f'vad_{kind}_processing_s', row.get('observed_worker_s'),
                        row.get('processing_started_worker_s'))
    if event == 'capture.partial_deferred':
        for reason in pre_asr_deferrals:
            pre_asr_deferrals[reason] += int(row.get(reason) is True)
    state = vad_segments.get(key, {})
    if event == 'capture.segment_discarded':
        state['discarded'] = True
    bounded_put(vad_segments, key, state)


def first_admission(row):
    global pre_asr_first_admissions
    key = vad_identity(row)
    if key is None:
        return  # Legacy logs and file probes have no VAD timing identity.
    state = vad_segments.get(key, {})
    for field in ('deferred_asr_observations', 'deferred_preview_observations', 'deferred_adaptive_observations'):
        count = row.get(field)
        if isinstance(count, int) and not isinstance(count, bool) and count >= 0:
            state[field] = max(count, state.get(field, 0))
    if state.get('admitted'):
        bounded_put(vad_segments, key, state)
        return
    admitted = row.get('first_admitted_worker_s')
    if not finite_seconds(admitted):
        state['legacy_or_missing_timing'] = True
        bounded_put(vad_segments, key, state)
        return
    state['admitted'] = True
    pre_asr_first_admissions += 1
    observed = row.get('voice_observed_worker_s')
    eligible = row.get('first_eligible_worker_s')
    measure('voice_observation_to_first_asr_eligible_s', eligible, observed)
    measure('first_asr_eligible_to_first_admission_s', admitted, eligible)
    measure('voice_observation_to_first_admission_s', admitted, observed)
    if row.get('voice_vad_started_worker_s') is not None:
        measure('voice_vad_start_to_first_admission_s', admitted, row.get('voice_vad_started_worker_s'))
    bounded_put(vad_segments, key, state)


def measure(name, end, start):
    if finite_seconds(start) and finite_seconds(end) and end >= start:
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
        first_admission(row)
        if event == 'capture.partial_requested':
            policy = row.get('adaptive_policy')
            allowed = {'Initial', 'Fixed', 'ConfirmSoon', 'StableProgress', 'EmptyBackoff', 'UnchangedBackoff', 'DecodeCostBackoff'}
            policy = policy if isinstance(policy, str) and policy in allowed else 'Unreported'
            adaptive_observations[policy] = adaptive_observations.get(policy, 0) + 1
            growth = row.get('adaptive_growth_s')
            if isinstance(growth, (int, float)) and not isinstance(growth, bool) and math.isfinite(growth) and growth >= 0:
                stage_samples.setdefault('adaptive_required_audio_growth_s', []).append(growth)
        wait = row.get('partial_deferred_wait_s')
        if isinstance(wait, (int, float)) and math.isfinite(wait) and wait >= 0:
            stage_samples.setdefault('latest_partial_deferred_wait_s', []).append(wait)
        rev.setdefault('admitted', at)
        admitted = row.get('first_admitted_worker_s')
        seg.setdefault('admitted', admitted if finite_seconds(admitted) and admitted <= at else at)
        observed = row.get('voice_observed_worker_s')
        if finite_seconds(observed) and observed <= at:
            seg.setdefault('voice_observed', observed)
        audio_end = row.get('audio_end_s')
        voice_start = row.get('voice_start_s')
        if isinstance(audio_end, (int, float)) and math.isfinite(audio_end):
            measure('sampled_voice_start_to_request_audio_end_s', audio_end, voice_start)
    elif event == 'asr.started':
        rev.setdefault('asr_started', at)
        measure('asr_admission_to_owner_dispatch_s', at, rev.get('admitted'))
    elif event == 'asr.completed':
        window_counts['attempted'] += int(row.get('window_attempted') is True)
        window_counts['fallback'] += int(row.get('window_fallback') is True)
        kind = row.get('outcome_kind')
        kind = kind if isinstance(kind, str) and kind in {'Text', 'NoSpeech', 'OverlapOnly', 'Cancelled', 'Failed'} else 'Unreported'
        asr_outcomes[kind] = asr_outcomes.get(kind, 0) + 1
        if row.get('applied') is True:
            measure('asr_owner_dispatch_to_completion_s', at, rev.get('asr_started'))
            decode = row.get('decode_s')
            if isinstance(decode, (int, float)) and math.isfinite(decode) and decode >= 0:
                stage_samples.setdefault('native_decode_s', []).append(decode)
        else:
            ignored_asr += 1
    elif event in ('source.partial', 'source.final'):
        if event == 'source.partial':
            reason = row.get('preview_hold_reason')
            allowed = {'Eligible', 'Disabled', 'NoStablePrefix', 'FinalTranslationQueued',
                       'FinalAsrQueued', 'FinalTranslationInFlight', 'Cadence', 'EmptyTail',
                       'TooShort', 'AlreadyTranslated', 'IncompleteCondition', 'IncompleteNumber',
                       'ConditionContinuation', 'DanglingWord', 'AsrUnavailable'}
            reason = reason if isinstance(reason, str) and reason in allowed else 'Unreported'
            hold_observations[reason] = hold_observations.get(reason, 0) + 1
            if 'first_translation' not in seg:
                first_translation_hold_observations[reason] = first_translation_hold_observations.get(reason, 0) + 1
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
                if 'voice_observed' in seg:
                    measure('voice_observation_to_first_translation_update_s', at, seg['voice_observed'])
        bounded_put(requests, request_key, request)
    bounded_put(revisions, key, rev)
    bounded_put(segments, key[:-1], seg)


target_changes = {'Candidate': {'counts': {}, 'changed': 0, 'same_unit_removed_utf16': 0,
                               'source_guard_invalidated_changes': 0},
                  'Displayed': {'counts': {}, 'changed': 0, 'same_unit_removed_utf16': 0,
                                'source_guard_invalidated_changes': 0},
                  'source_guard_invalidation_observations': 0,
                  'cosmetic_deferred_observations': 0,
                  'visible_display_observations': 0, 'invalid': 0}

# Separate restart/resume epochs. Observations are not a stability/quality gate.
session_epochs = OrderedDict()
session_evictions = 0
clear_reasons = {}


def session_observation(row):
    global session_evictions
    phase = row.get('phase')
    if phase == 'deck_cleared':
        reason = row.get('reason')
        allowed = {'SessionStartRequested', 'SessionStopRequested', 'SessionPauseRequested', 'Other'}
        reason = reason if reason in allowed else 'Unreported'
        clear_reasons[reason] = clear_reasons.get(reason, 0) + 1
        return
    names = ('worker_pid', 'session_id', 'epoch')
    key = tuple(row.get(name) for name in names)
    if (not all(isinstance(v, int) and not isinstance(v, bool) for v in key)
            or key[0] <= 0 or key[1] <= 0 or key[2] < 0):
        return
    if phase not in {'worker_state_polled', 'pipeline_event_received', 'deck_applied', 'target_change_observed'}:
        return
    at = row.get('at_s')
    if not finite_seconds(at):
        return
    group = session_epochs.get(key)
    if group is None:
        group = {'worker_pid': key[0], 'session_id': key[1], 'epoch': key[2],
                 'first_observed_at_s': at, 'last_observed_at_s': at,
                 'state_samples': 0, 'state_changes': 0, 'model_profile_observations': {},
                 'max_sampled_pending_asr_inputs': None, 'translation_completions': 0,
                 'translation_error_metadata_completions': 0,
                 'translation_errors': {}, 'unapplied_completions': 0,
                 'visible_deck_applications': 0, 'context_mismatches': 0,
                 'context_unreported_applications': 0, 'visible_change_kinds': {},
                 '_owner_s': [], 'duration_sample_overflow': 0}
    group['last_observed_at_s'] = max(at, group['last_observed_at_s'])
    if phase == 'worker_state_polled':
        group['state_samples'] += 1
        group['state_changes'] += row.get('context_changed') is True
        group['last_session_state'] = row.get('session_state')
        group['last_capture_state'] = row.get('capture_state')
        group['last_translator_state'] = row.get('translator_state')
        group['last_sampled_translator_error'] = row.get('translator_error')
        # Whitelist the new fields; old logs must not be treated as default profile.
        model, profile = row.get('model_id'), row.get('input_profile')
        if (isinstance(model, str) and len(model) <= 256
                and profile in {'standard', 'qwen-greedy', 'hymt2-greedy'}):
            label = model + '/' + profile + '/isolated=' + str(row.get('isolated_context'))
            observations = group['model_profile_observations']
            if label in observations or len(observations) < 16:
                observations[label] = observations.get(label, 0) + 1
        pending = row.get('pending_asr_inputs')
        if isinstance(pending, int) and not isinstance(pending, bool) and pending >= 0:
            group['max_sampled_pending_asr_inputs'] = max(pending, group['max_sampled_pending_asr_inputs'] or 0)
    elif phase == 'pipeline_event_received' and row.get('event_name') == 'translation.completed':
        group['translation_completions'] += 1
        group['translation_error_metadata_completions'] += 'error' in row
        group['unapplied_completions'] += row.get('applied') is False
        error = row.get('error')
        if isinstance(error, str) and 0 < len(error) <= 80 and error.isascii() and error.replace('_', '').isalnum():
            errors = group['translation_errors']
            if error in errors or len(errors) < 32:
                errors[error] = errors.get(error, 0) + 1
        elapsed = row.get('elapsed_s')
        if finite_seconds(elapsed):
            if len(group['_owner_s']) < 4096:
                group['_owner_s'].append(elapsed)
            else:
                group['duration_sample_overflow'] += 1
    elif phase == 'deck_applied' and row.get('overlay_visible') is True:
        group['visible_deck_applications'] += 1
        group['context_mismatches'] += row.get('context_matches') is False
        group['context_unreported_applications'] += row.get('context_matches') is None
    elif (phase == 'target_change_observed' and row.get('observation_phase') == 'Displayed'
          and row.get('overlay_visible') is True):
        kind = (row.get('change') or {}).get('kind')
        allowed = {'Initial', 'SegmentTransition', 'UnitTransition', 'SameUnitRevision',
                   'FinalRestoration', 'FinalRevision', 'PreviewRestart', 'SourceUnitReset', 'UnidentifiedRevision'}
        if kind in allowed:
            counts = group['visible_change_kinds']
            counts[kind] = counts.get(kind, 0) + 1
    session_epochs[key] = group
    session_epochs.move_to_end(key)
    if len(session_epochs) > 512:
        session_epochs.popitem(last=False)
        session_evictions += 1


def target_change(row):
    phase = row.get('observation_phase')
    if phase == 'CosmeticDeferred':
        target_changes['cosmetic_deferred_observations'] += 1
        return
    if phase == 'SourceGuardInvalidated':
        target_changes['source_guard_invalidation_observations'] += 1
        return
    change = row.get('change')
    kinds = {'Initial', 'SegmentTransition', 'UnitTransition', 'SameUnitRevision',
             'FinalRestoration', 'FinalRevision', 'PreviewRestart', 'SourceUnitReset', 'UnidentifiedRevision'}
    if phase not in ('Candidate', 'Displayed') or not isinstance(change, dict) or change.get('kind') not in kinds:
        target_changes['invalid'] += 1
        return
    group = target_changes[phase]
    kind = change['kind']
    removed = change.get('same_unit_removed_utf16')
    if (kind in ('SameUnitRevision', 'FinalRevision')
            and (not isinstance(removed, int) or isinstance(removed, bool) or removed < 0)
            or kind not in ('SameUnitRevision', 'FinalRevision') and removed is not None):
        target_changes['invalid'] += 1
        return
    group['counts'][kind] = group['counts'].get(kind, 0) + 1
    group['changed'] += change.get('target_changed') is True
    group['same_unit_removed_utf16'] += removed or 0
    group['source_guard_invalidated_changes'] += change.get('source_guard_invalidated') is True
    if phase == 'Displayed' and row.get('overlay_visible') is True:
        target_changes['visible_display_observations'] += 1


with args.log.open(encoding='utf-8-sig') as source:
    for line in source:
        try:
            row = json.loads(line)
            phase = row.get('phase')
            session_observation(row)
            if phase == 'caption_reading':
                reading(row)
                continue
            if phase == 'caption_event_buffer':
                pid, count = row.get('worker_pid'), row.get('dropped_events')
                if isinstance(pid, int) and isinstance(count, int) and count >= 0:
                    bounded_put(caption_event_drops, pid, max(count, caption_event_drops.get(pid, 0)))
                continue
            if phase == 'target_change_observed':
                target_change(row)
                continue
            if phase == 'pre_asr_event_received':
                pre_asr(row)
                continue
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
    return None if not values else {'n': len(values), 'median': statistics.median(values),
        'p95': sorted(values)[math.ceil(.95 * len(values)) - 1], 'min': min(values), 'max': max(values)}


for group in session_epochs.values():
    group['owner_completion_elapsed_s'] = stats(group.pop('_owner_s'))
    group['observed_span_s'] = group['last_observed_at_s'] - group['first_observed_at_s']


print(json.dumps({'matched_first_visible_deck_applications': len(samples),
                  'reading': {'observations': reading_counts, 'drop_reasons': reading_drops,
                      'blocked_state_transitions': reading_blocks,
                      'closed_block_duration_s': {reason: stats(values) for reason, values in reading_block_durations.items()},
                      'block_duration_sample_evictions': reading_block_evictions,
                      'correction_applications': reading_correction_reuse,
                      'sample_window_warning': reading_wait_evictions > 0 or reading_block_evictions > 0,
                      'first_line_wait_s': stats(reading_waits), 'wait_sample_evictions': reading_wait_evictions,
                      'caption_event_buffer_drops_by_worker': dict(caption_event_drops),
                      'note': 'FirstLine is logical line admission, not physical presentation. Block durations cover closed state intervals with overlay visible at the end; clear/session reset or log end may censor intervals. Counts are not time shares. Duration samples are capped at 4096 per reason; eviction means retained-window statistics. Old logs lack new fields, not zero waiting or correction reuse. Drops may refer to an unread tail. Buffer counts are worker-lifetime cumulative.'},
                  'session_epochs': {'observations': list(session_epochs.values()),
                      'evicted': session_evictions, 'deck_clear_requests': clear_reasons,
                      'quality_gate_passed': False,
                      'note': 'At most 512 worker/session/epoch groups, 4096 elapsed samples per group. Span is logging coverage, not uninterrupted running time. Context matches sampled UI state, not rendered pixels. Cancellation/unapplied responses are not necessarily failures. Old logs lack model/profile/context fields. ASR pending inputs are sampled, not full queue or GPU memory measurements.'},
                  'target_changes': {**target_changes,
                      'semantic_contradiction': 'UNASSESSED',
                      'note': 'Candidate history observations and per-card deck assignments are separate. Same-unit counts exclude unit transitions and final restoration. Prefix invalidation is lexical, not semantic correctness. UTF-16 edits are not confidence or mistranslation rate. Old logs have no observations, not zero churn.'},
                  'event_receipt_to_deck_s': stats(samples), 'deck_deferred_s': stats(deferred),
                  'pipeline': {'events': stage_events, 'invalid': stage_invalid,
                               'ignored_asr': ignored_asr, 'unapplied_translation': unapplied_translation,
                               'adaptive_policy_observations': adaptive_observations,
                               'asr_completion_outcomes': asr_outcomes,
                               'decode_window_completions': window_counts,
                               'preview_decision_observations': hold_observations,
                               'before_first_translation_decisions': first_translation_hold_observations,
                               'durations_s': {name: stats(values) for name, values in stage_samples.items()},
                               'missing_or_invalid_pairs': stage_missing_pairs,
                               'pre_asr': {'events': pre_asr_events, 'invalid': pre_asr_invalid,
                                   'first_admissions': pre_asr_first_admissions,
                                   'event_observations': pre_asr_observations,
                                   'deferred_reason_observations': pre_asr_deferrals,
                                   'retained_segment_deferred_counts': {field: sum(s.get(field, 0) for s in vad_segments.values()) for field in
                                       ('deferred_asr_observations', 'deferred_preview_observations', 'deferred_adaptive_observations')},
                                   'retained_discarded_segments': sum(s.get('discarded', False) for s in vad_segments.values()),
                                   'retained_unadmitted_segments': sum(not s.get('admitted', False) for s in vad_segments.values()),
                                   'retained_legacy_or_missing_timing_segments': sum(s.get('legacy_or_missing_timing', False) for s in vad_segments.values())},
                               'note': 'Worker-local clock differences only. Voice observation is the VAD-positive frame processing time, not acoustic onset or qualified speech. First eligible means the first VAD partial/final candidate, before scheduler/model acceptance. Sampled voice span is an audio range, not wall-clock latency. New admission fields survive coalesced requests; old logs cannot reconstruct them. Deferred reasons may overlap and event observations may be coalesced. Retained segment counts cover only the last 512 identities; eviction can cause re-counting. Nonempty stable text is not necessarily eligible for translation. No device-to-screen latency or worker-to-desktop transport duration. Missing/evicted pairs are omitted, not zero.'},
                  'missing_receipts': missing, 'reported_dropped': dropped, 'invalid': invalid, 'bounded_map_evictions': map_evictions,
                  'note': 'UI deck application, not physical rendering or audio-to-caption latency. Hidden overlays excluded; at most 512 receipt/identity keys retained. Missing/dropped observations prevent complete coverage.'}, indent=2))
