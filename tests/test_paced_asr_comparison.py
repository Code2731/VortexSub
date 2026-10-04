"""Offline summary checks; no models or audio required."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("paced_asr_comparison",
    Path(__file__).resolve().parents[1] / "scripts/summarize-paced-asr-comparison.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ComparisonTests(unittest.TestCase):
    def test_missing_times_are_not_zero_and_surface_is_not_exact(self):
        runtime = {"fixture_id": "example", "asr_model": "whisper-base",
                   "fixture_reference": "Go LEFT."}
        run = {"supported_preview": False, "round": 1, "events": [],
               "first_translation_s": None, "final_translation_s": 2.0,
               "final_record": {"source": "go left!", "translation_state": "Done"}}
        rows, _ = module.summarize({"runs": [run]}, runtime, "report.json")
        self.assertEqual(rows[0]["first_translation_s"]["n"], 0)
        self.assertIsNone(rows[0]["first_translation_s"]["median"])
        self.assertEqual(rows[0]["final_translation_s"]["median"], 2.0)
        self.assertEqual(rows[0]["final_source_surface_matches"], 1)
        self.assertNotEqual(module.surface_words("thirteen"), module.surface_words("13"))

    def test_review_preserves_preview_input_and_final_source(self):
        runtime = {"fixture_id": "example", "asr_model": "whisper-small",
                   "fixture_reference": "Go left until I return."}
        record = {"translation": "왼쪽으로 가세요.", "translation_is_preview": True,
                  "translation_source": "Go left.", "source": "Go left until I return.",
                  "translation_request_id": 1}
        final = {**record, "translation_is_preview": False, "translation_source": "",
                 "translation_request_id": 2}
        events = [{"at_s": index + 1.0, "message": {"event": "translation.updated",
                   "payload": {"record": value}}} for index, value in enumerate((record, final))]
        run = {"supported_preview": True, "round": 1, "events": events,
               "final_record": {**final, "translation_state": "Done"}}
        rows, review = module.summarize({"runs": [run]}, runtime, "report.json")
        self.assertEqual(rows[0]["first_reference_source_translation_s"]["median"], 2.0)
        self.assertEqual(len(review), 2)
        self.assertEqual(review[0]["translation_source"], "Go left.")
        self.assertEqual(review[1]["translation_source"], runtime["fixture_reference"])
        self.assertTrue(review[0]["preview"])
        self.assertFalse(review[1]["preview"])


if __name__ == "__main__":
    unittest.main()
