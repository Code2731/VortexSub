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

    def test_legacy_ui_log_still_summarizes(self):
        base = dict(worker_pid=1, session_id=1, epoch=1, segment_id=1,
                    source_revision=1, translation_request_id=1)
        result = summarize([dict(base, phase='translation_event_received', at_s=2000),
                            dict(base, phase='deck_applied', at_s=2000.04,
                                 overlay_visible=True, deferred_s=0)])
        self.assertAlmostEqual(result['event_receipt_to_deck_s']['median'], 0.04)
        self.assertEqual(result['pipeline']['events'], 0)


if __name__ == '__main__':
    unittest.main()
