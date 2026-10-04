"""Offline regression checks for the diagnostic classifier, no inference."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("asr_stability",
    Path(__file__).resolve().parents[1] / "scripts/summarize-asr-stability.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class StabilityTests(unittest.TestCase):
    def test_exact_and_surface_changes(self):
        self.assertEqual(module.classify("Go left.", "Go left.")["kind"], "identical")
        self.assertEqual(module.classify("Go LEFT.", "go left.")["kind"], "case_or_whitespace_only")
        self.assertEqual(module.classify("Go  left.", "Go left.")["kind"], "case_or_whitespace_only")
        self.assertEqual(module.classify("Go left.", "Go left?")["kind"], "punctuation_only")

    def test_lexical_changes_are_not_surface_changes(self):
        self.assertEqual(module.classify("Attack the enemy.", "Attach the enemy.")["kind"], "lexical_rewrite")
        self.assertEqual(module.classify("Go left", "Go left until I return")["kind"], "lexical_extension")
        self.assertTrue(module.classify("Go left", "Go left until I return")["condition_tokens_changed"])
        self.assertTrue(module.classify("shield is not down", "shield is down")["negation_tokens_changed"])
        self.assertTrue(module.classify("I can't leave", "I can leave")["negation_tokens_changed"])
        self.assertTrue(module.classify("not not", "not")["negation_tokens_changed"])
        self.assertFalse(module.classify("Go LEFT.", "go left.")["negation_tokens_changed"])


if __name__ == "__main__":
    unittest.main()
