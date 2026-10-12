import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'scripts'))
from caption_replay_timing import fragment_timings


class FragmentTimingTests(unittest.TestCase):
    def run_case(self, frames, text='changed', extra=()):
        target = dict(session_id=1, epoch=2, segment_id=3, source_revision=4, translation_request_id=5, text=text)
        record = dict(target, translation_state='Done', translation=text)
        run = dict(events=[dict(at_s=1, message=dict(payload=dict(record=record))), *extra], timing_targets=[target])
        return fragment_timings(run, frames)[0]

    def frame(self, at, text='', segment=3, session=1, epoch=2, unit_start=0):
        return dict(virtual_s=at, upper=text, lower='',
                    upper_identity=dict(Session=session, Epoch=epoch, Segment=segment, UnitStart=unit_start))

    def test_changed_fragment_wait(self):
        result = self.run_case([self.frame(0, 'old'), self.frame(1, 'old'), self.frame(2.8, 'changed')])
        self.assertEqual((result['status'], result['wait_s']), ('newly_visible', 1.8))

    def test_immediate(self):
        self.assertEqual(self.run_case([self.frame(1, 'changed')])['wait_s'], 0)

    def test_already_visible_is_not_new_version_render(self):
        result = self.run_case([self.frame(0, 'changed'), self.frame(1, 'changed')])
        self.assertEqual(result['status'], 'already_visible')
        self.assertIsNone(result['wait_s'])

    def test_previously_delivered(self):
        result = self.run_case([self.frame(0, 'changed'), self.frame(.5), self.frame(2, 'changed')])
        self.assertEqual(result['status'], 'previously_delivered')
        self.assertIsNone(result['wait_s'])

    def test_other_identity_cannot_supply_evidence(self):
        for fields in (dict(segment=4), dict(session=2), dict(epoch=3), dict(unit_start=5)):
            self.assertEqual(self.run_case([self.frame(2, 'changed', **fields)])['status'], 'not_visible')

    def test_missing_identity(self):
        self.assertEqual(self.run_case([dict(virtual_s=2, upper='changed', lower='')])['status'], 'not_visible')

    def test_internal_spaces_not_normalized(self):
        self.assertEqual(self.run_case([self.frame(2, 'a b')], 'a  b')['status'], 'not_visible')

    def test_superseded_version_not_attributed(self):
        record = dict(session_id=1, epoch=2, segment_id=3, source_revision=4,
                      translation_request_id=6, translation_state='Done', translation='changed')
        extra = [dict(at_s=2, message=dict(payload=dict(record=record)))]
        self.assertEqual(self.run_case([self.frame(2, 'changed')], extra=extra)['status'], 'superseded')

    def test_non_monotonic_frames_rejected(self):
        with self.assertRaises(ValueError):
            self.run_case([self.frame(2), self.frame(1)])

    def test_bad_fragment_rejected(self):
        for text in ('', 'a\nb', 'x' * 16385):
            with self.assertRaises(ValueError):
                self.run_case([], text)

    def test_optional_targets_on_old_trace(self):
        self.assertEqual(fragment_timings(dict(events=[]), []), [])

    def test_ambiguous_input_rejected(self):
        target = dict(session_id=1, epoch=1, segment_id=1, source_revision=1, translation_request_id=1, text='a')
        run = dict(timing_targets=[target], events=[dict(at_s=0, message=dict(payload=dict(
            record=dict(target, translation_state='Done', translation='a a'))))])
        with self.assertRaises(ValueError):
            fragment_timings(run, [])

    def test_nonfinite_input_time_rejected(self):
        with self.assertRaises(ValueError):
            fragment_timings(dict(events=[dict(at_s=float('nan'))]), [])

    def test_duplicate_targets_rejected(self):
        target = dict(session_id=1, epoch=1, segment_id=1, source_revision=1, translation_request_id=1, text='a')
        run = dict(timing_targets=[target, target], events=[dict(at_s=0, message=dict(payload=dict(
            record=dict(target, translation_state='Done', translation='a'))))])
        with self.assertRaises(ValueError):
            fragment_timings(run, [])


if __name__ == '__main__':
    unittest.main()
