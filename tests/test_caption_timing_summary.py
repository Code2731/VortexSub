"""Offline fixtures for the metadata timing report; no capture or models."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/summarize-caption-timing.py'


def row(event, at, **fields):
    return dict(phase='pipeline_event_received', event_name=event, worker_at_s=at,
                at_s=2000 + (at or 0), worker_pid=1, session_id=1, epoch=1, segment_id=1,
                source_revision=1, **fields)


def summarize(rows):
    with tempfile.TemporaryDirectory() as directory:
        log = Path(directory) / 'timing.jsonl'
        log.write_text('\n'.join(json.dumps(r) for r in rows), encoding='utf-8')
        result = subprocess.run([sys.executable, str(SCRIPT), str(log)], check=True,
                                capture_output=True, text=True, encoding='utf-8')
        return json.loads(result.stdout)


class TimingSummary(unittest.TestCase):
    def test_reading_wait_drop_and_buffer_overflow_are_reported_without_text(self):
        result = summarize([dict(phase='caption_reading', kind='FirstLine', reading_wait_s=2.5, overlay_visible=True),
                            dict(phase='caption_reading', kind='FirstLine', reading_wait_s=9, overlay_visible=False),
                            dict(phase='caption_reading', kind='Dropped', reason='QueueCapacity'),
                            dict(phase='caption_reading', kind='Dropped', reason='private text'),
                            dict(phase='caption_event_buffer', worker_pid=1, dropped_events=2),
                            dict(phase='caption_event_buffer', worker_pid=1, dropped_events=5)])['reading']
        self.assertEqual(result['first_line_wait_s']['median'], 2.5)
        self.assertEqual(result['drop_reasons'], {'QueueCapacity': 1})
        self.assertEqual(result['caption_event_buffer_drops_by_worker'], {'1': 5})
        self.assertNotIn('private text', str(result))

    def test_vad_process_start_completion_and_receipt_are_separate(self):
        def vad(event, received, completed, started, kind):
            return dict(phase='pre_asr_event_received', event_name=event, worker_pid=1,
                        session_id=1, epoch=1, vad_segment_id=1, worker_at_s=received,
                        observed_worker_s=completed, processing_started_worker_s=started,
                        processing_kind=kind, at_s=5000)
        result = summarize([vad('capture.voice_observed', 1.2, 1.1, 1, 'push'),
                            vad('capture.asr_eligible', 2.2, 2.1, 2.05, 'poll'),
                            row('capture.partial_requested', 2.3, vad_segment_id=1, queued=True,
                                voice_observed_worker_s=1.1, voice_vad_started_worker_s=1,
                                first_eligible_worker_s=2.1, first_admitted_worker_s=2.3)])
        durations = result['pipeline']['durations_s']
        self.assertAlmostEqual(durations['vad_push_processing_s']['median'], .1)
        self.assertAlmostEqual(durations['vad_poll_processing_s']['median'], .05)
        self.assertAlmostEqual(durations['vad_observation_to_worker_receipt_s']['median'], .1)
        self.assertAlmostEqual(durations['voice_observation_to_first_admission_s']['median'], 1.2)
        self.assertAlmostEqual(durations['voice_vad_start_to_first_admission_s']['median'], 1.3)

    def test_legacy_vad_log_does_not_invent_process_duration(self):
        result = summarize([dict(phase='pre_asr_event_received', event_name='capture.voice_observed',
                                 worker_pid=1, session_id=1, epoch=1, vad_segment_id=1,
                                 worker_at_s=1.2, observed_worker_s=1.1)])
        self.assertNotIn('vad_push_processing_s', result['pipeline']['durations_s'])
        self.assertNotIn('vad_poll_processing_s', result['pipeline']['durations_s'])

    def test_worker_stages_do_not_mix_desktop_clock(self):
        rows = [row('capture.partial_requested', 10, queued=True, audio_end_s=2,
                    voice_start_s=0, partial_deferred_wait_s=0.2),
                row('asr.started', 10.1), row('asr.completed', 10.5, applied=True, decode_s=0.35),
                row('source.partial', 10.51, stable_chars=5),
                row('translation.started', 10.6, translation_request_id=1, preview=True),
                row('translation.completed', 10.9, translation_request_id=1, applied=True),
                row('translation.updated', 10.91, translation_request_id=1)]
        result = summarize(rows)
        durations = result['pipeline']['durations_s']
        expected = {'asr_admission_to_owner_dispatch_s': 0.1,
                    'asr_owner_dispatch_to_completion_s': 0.4, 'native_decode_s': 0.35,
                    'source_revision_ready_to_translation_dispatch_s': 0.09,
                    'translation_owner_dispatch_to_completion_s': 0.3,
                    'translation_completion_to_update_publish_s': 0.01,
                    'first_observed_admission_to_first_translation_update_s': 0.91,
                    'sampled_voice_start_to_request_audio_end_s': 2}
        for name, value in expected.items():
            self.assertAlmostEqual(durations[name]['median'], value)
            self.assertEqual(durations[name]['n'], 1)
        self.assertEqual(result['pipeline']['missing_or_invalid_pairs'], {})

    def test_missing_start_and_other_worker_are_not_zero(self):
        start = row('translation.started', 10, translation_request_id=1)
        other = row('translation.completed', 11, translation_request_id=1, applied=True)
        other['worker_pid'] = 2
        result = summarize([start, other, row('asr.started', None)])['pipeline']
        self.assertNotIn('translation_owner_dispatch_to_completion_s', result['durations_s'])
        self.assertEqual(result['missing_or_invalid_pairs']['translation_owner_dispatch_to_completion_s'], 1)
        self.assertEqual(result['invalid'], 1)

    def test_decision_counts_are_observations_and_reject_unrecognized_text(self):
        result = summarize([row('source.partial', 1, stable_chars=0, preview_hold_reason='NoStablePrefix'),
                            row('source.partial', 2, stable_chars=10, preview_hold_reason='IncompleteNumber'),
                            row('translation.updated', 3, translation_request_id=1),
                            row('source.partial', 4, stable_chars=10, preview_hold_reason='AlreadyTranslated'),
                            row('source.partial', 5, stable_chars=10, preview_hold_reason='private text')])['pipeline']
        self.assertEqual(result['before_first_translation_decisions'], {'NoStablePrefix': 1, 'IncompleteNumber': 1})
        self.assertEqual(result['preview_decision_observations']['AlreadyTranslated'], 1)
        self.assertEqual(result['preview_decision_observations']['Unreported'], 1)
        self.assertNotIn('private text', str(result))

    def test_adaptive_and_outcome_categories_exclude_free_text(self):
        result = summarize([row('capture.partial_requested', 1, queued=True, adaptive_policy='ConfirmSoon', adaptive_growth_s=0.256),
                            row('asr.completed', 2, applied=True, outcome_kind='NoSpeech'),
                            row('asr.completed', 3, applied=False, outcome_kind='private error')])['pipeline']
        self.assertEqual(result['adaptive_policy_observations'], {'ConfirmSoon': 1})
        self.assertEqual(result['asr_completion_outcomes'], {'NoSpeech': 1, 'Unreported': 1})
        self.assertEqual(result['durations_s']['adaptive_required_audio_growth_s']['median'], 0.256)
        self.assertNotIn('private error', str(result))

    def test_legacy_ui_log_still_summarizes(self):
        base = dict(worker_pid=1, session_id=1, epoch=1, segment_id=1,
                    source_revision=1, translation_request_id=1)
        result = summarize([dict(base, phase='translation_event_received', at_s=2000),
                            dict(base, phase='deck_applied', at_s=2000.04,
                                 overlay_visible=True, deferred_s=0)])
        self.assertAlmostEqual(result['event_receipt_to_deck_s']['median'], 0.04)
        self.assertEqual(result['pipeline']['events'], 0)

    def test_window_flags_count_only_boolean_metadata(self):
        result = summarize([row('asr.completed', 1, window_attempted=True, window_fallback=True),
                            row('asr.completed', 2, window_attempted=True, window_fallback=False),
                            row('asr.completed', 3, window_attempted='private text', window_fallback=1)])['pipeline']
        self.assertEqual(result['decode_window_completions'], {'attempted': 2, 'fallback': 1})


if __name__ == '__main__':
    unittest.main()
