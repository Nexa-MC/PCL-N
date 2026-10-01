"""Synthetic statistics/validation fixtures; these are never long-run acceptance records."""
import copy
import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import analyze_soak


class SoakObservationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.seconds = 900
        self.rows = [self.row(index) for index in range(self.seconds)] + [self.row(self.seconds)]
        self.rows[-1]["seconds"] += .125
        self.rows[-1]["managed_live_bytes"] = 100  # Forced final GC must not improve window statistics.
        self.run = dict(schema=3, scope="desktop-composition-fixture", real_desktop_acceptance=False,
                        mode="idle", passed=True, requested_seconds=self.seconds,
                        elapsed_seconds=self.rows[-1]["seconds"], samples=len(self.rows))

    @staticmethod
    def row(index):
        row = {key: 100 for key in analyze_soak.METRICS}
        row.update(seconds=index, working_set_bytes=64 * 1024 * 1024, private_bytes=80 * 1024 * 1024,
                   managed_live_bytes=16 * 1024 * 1024, gc_committed_bytes=32 * 1024 * 1024,
                   handles=5, threads=4, scene_entities=285, state_cells=210,
                   log_entries=2, terminal_tasks=0, allocated_bytes=index * 1024,
                   process_cpu_ms=index * 2, gc_gen0_count=index // 100, gc_gen1_count=0, gc_gen2_count=0)
        return row

    def write(self):
        (self.root / "run.json").write_text(json.dumps(self.run), encoding="utf-8")
        raw = "".join(json.dumps(row) + "\n" for row in self.rows)
        (self.root / "samples.jsonl").write_text(raw, encoding="utf-8")
        return raw.encode()

    def result(self):
        self.write()
        return analyze_soak.analyze(self.root)

    def test_stable_windows_keep_peaks_and_sampler_scope(self):
        raw = self.write()
        result = analyze_soak.analyze(self.root)
        self.assertEqual(hashlib.sha256(raw).hexdigest(), result["input_sha256"]["samples"])
        self.assertEqual(3, len(result["windows"]))
        self.assertEqual(0, result["median_trends"]["private_bytes"]["slope_units_per_hour"])
        self.assertEqual(16 * 1024 * 1024, result["ordinary_peaks"]["managed_live_bytes"]["minimum"])
        self.assertEqual(100, result["endpoints"]["final_after_gc"]["managed_live_bytes"])
        self.assertEqual(1024, result["allocation_bytes_per_second"]["median"])
        self.assertEqual(1024, result["windows"][0]["allocation_bytes_per_second"])
        self.assertAlmostEqual(.2, result["windows"][0]["cpu_percent_one_core"])
        self.assertAlmostEqual(.2, result["cpu_percent_one_core"]["median"])
        self.assertFalse(result["runtime_kpi_certified"])
        self.assertFalse(result["real_desktop_acceptance"])
        self.assertTrue(result["fixture_endpoint_gate_passed"])
        self.assertEqual(3, result["source_schema"])
        self.assertIsNone(result["sampler_allocation_bytes_per_second"]["median"])
        self.assertIsNone(result["unattributed_allocation_bytes_per_second"]["median"])
        self.assertIsNone(result["windows"][0]["sampler_allocation_bytes_per_second"])
        self.assertIsNone(result["endpoints"]["baseline"]["sampler_allocated_bytes"])

    def test_schema_four_aligned_sampler_rates_preserve_scope(self):
        self.run["schema"] = 4
        for row in self.rows:
            row["sampler_allocated_bytes"] = int(row["seconds"]) * 768
        result = self.result()
        self.assertEqual(4, result["source_schema"])
        self.assertEqual(768, result["sampler_allocation_bytes_per_second"]["median"])
        self.assertEqual(256, result["unattributed_allocation_bytes_per_second"]["median"])
        self.assertEqual(768, result["windows"][0]["sampler_allocation_bytes_per_second"])
        self.assertEqual(256, result["windows"][0]["unattributed_allocation_bytes_per_second"])
        self.assertEqual(900 * 256, result["endpoints"]["final_after_gc"]["unattributed_allocated_bytes"])
        self.assertFalse(result["runtime_kpi_certified"])
        self.assertFalse(result["real_desktop_acceptance"])

    def test_schema_four_missing_or_inconsistent_sampler_is_rejected(self):
        self.run["schema"] = 4
        for row in self.rows:
            row["sampler_allocated_bytes"] = int(row["seconds"]) * 768
        original = copy.deepcopy(self.rows)
        for value in (None, False, -1, 1, 1.5, 9000, 11000):
            with self.subTest(value=value):
                self.rows = copy.deepcopy(original)
                self.rows[10]["sampler_allocated_bytes"] = value
                self.write()
                with self.assertRaises(ValueError):
                    analyze_soak.analyze(self.root)
        self.rows = copy.deepcopy(original)
        del self.rows[10]["sampler_allocated_bytes"]
        self.write()
        with self.assertRaises(ValueError):
            analyze_soak.analyze(self.root)

    def test_legacy_sampler_field_cannot_retroactively_supply_attribution(self):
        for row in self.rows:
            row["sampler_allocated_bytes"] = int(row["seconds"]) * 768
        result = self.result()
        self.assertIsNone(result["sampler_allocation_bytes_per_second"]["median"])
        self.assertIsNone(result["unattributed_allocation_bytes_per_second"]["maximum"])
        self.assertIsNone(result["windows"][0]["sampler_allocation_bytes_per_second"])

    def test_growth_is_visible_despite_a_final_gc_drop(self):
        for row in self.rows[1:-1]:
            row["private_bytes"] += int(row["seconds"]) * 4096
            row["managed_live_bytes"] += int(row["seconds"]) * 4096
        result = self.result()
        for key in ("private_bytes", "managed_live_bytes"):
            self.assertAlmostEqual(4096 * 3600, result["median_trends"][key]["slope_units_per_hour"], delta=1)
            self.assertGreater(result["ordinary_peaks"][key]["maximum"], result["endpoints"]["final_after_gc"][key])

    def test_nursery_sawtooth_is_reported_without_a_no_leak_verdict(self):
        for row in self.rows[1:-1]:
            row["managed_live_bytes"] = 1000 + int(row["seconds"]) % 100 * 4096
        result = self.result()
        self.assertEqual(1000 + 99 * 4096, result["ordinary_peaks"]["managed_live_bytes"]["maximum"])
        self.assertIsNotNone(result["median_trends"]["managed_live_bytes"]["slope_units_per_hour"])
        self.assertFalse(result["runtime_kpi_certified"])

    def test_short_partial_window_and_missing_handles_remain_unknown(self):
        self.rows = self.rows[:10] + [self.rows[-1]]
        self.rows[-1]["seconds"] = 10.125
        self.run.update(requested_seconds=10, elapsed_seconds=10.125, samples=len(self.rows))
        for row in self.rows:
            row["handles"] = None
        result = self.result()
        self.assertFalse(result["windows"][0]["complete"])
        self.assertIsNone(result["median_trends"]["threads"]["slope_units_per_hour"])
        self.assertIsNone(result["ordinary_peaks"]["handles"]["maximum"])
        self.assertEqual(0, result["ordinary_peaks"]["handles"]["measured_samples"])

    def test_sampling_gaps_are_not_filled_or_certified(self):
        self.rows = self.rows[:150] + self.rows[460:]
        self.run["samples"] = len(self.rows)
        result = self.result()
        self.assertEqual(311, result["maximum_sampling_gap_seconds"])
        self.assertEqual(1, result["sampling_gaps_over_five_seconds"])
        self.assertIsNone(result["median_trends"]["private_bytes"]["slope_units_per_hour"])

    def test_failed_fixture_gate_is_preserved(self):
        self.run["passed"] = False
        self.assertFalse(self.result()["fixture_endpoint_gate_passed"])

    def test_incomplete_and_mismatched_summaries_are_rejected(self):
        original = copy.deepcopy(self.run)
        for key, value in (("real_desktop_acceptance", True), ("schema", True), ("scope", "physical-desktop"),
                           ("elapsed_seconds", 899), ("samples", 10), ("requested_seconds", False), ("passed", "true")):
            with self.subTest(key=key):
                self.run = original | {key: value}
                self.write()
                with self.assertRaises(ValueError):
                    analyze_soak.analyze(self.root)
        (self.root / "run.json").unlink()
        with self.assertRaises(ValueError):
            analyze_soak.analyze(self.root)

    def test_clock_counters_and_integer_metrics_are_validated(self):
        original = copy.deepcopy(self.rows)
        for key, value in (("seconds", 0), ("seconds", float("inf")), ("allocated_bytes", -1),
                           ("process_cpu_ms", 0), ("threads", False), ("handles", -1), ("private_bytes", 1.5)):
            with self.subTest(key=key):
                self.rows = copy.deepcopy(original)
                self.rows[10][key] = value
                self.write()
                with self.assertRaises(ValueError):
                    analyze_soak.analyze(self.root)

    def test_duplicate_keys_and_input_budgets_are_rejected(self):
        self.write()
        path = self.root / "samples.jsonl"
        path.write_text('{"seconds":0,"seconds":1}\n', encoding="utf-8")
        with self.assertRaises(ValueError):
            analyze_soak.analyze(self.root)
        for name, budget in (("MAX_SAMPLES", 16), ("MAX_LINE", 8), ("MAX_RECORDS", 2), ("MAX_JSON", 8)):
            self.write()
            with self.subTest(name=name), mock.patch.object(analyze_soak, name, budget), self.assertRaises(ValueError):
                analyze_soak.analyze(self.root)

    def test_frozen_binary_binding_preserves_recorded_worktree_identity(self):
        self.write()
        content = b"synthetic analyzer fixture, never a launcher execution"
        binary = self.root / "frozen.bin"
        binary.write_bytes(content)
        receipt = dict(scope="desktop-composition-fixture", real_desktop_acceptance=False,
                       base_commit="a" * 40, build_identity="base commit plus source worktree; not a clean commit",
                       binary_sha256=hashlib.sha256(content).hexdigest(), source_input_manifest_sha256="b" * 64,
                       source_inputs={"Source.cs": "c" * 64})
        path = self.root / "build-receipt.json"
        path.write_text(json.dumps(receipt), encoding="utf-8")
        result = analyze_soak.analyze(self.root, binary)
        self.assertTrue(result["build"]["binary_bytes_verified"])
        self.assertFalse(result["build"]["source_reproduction_verified"])
        self.assertEqual(receipt["build_identity"], result["build"]["build_identity"])
        self.assertFalse(analyze_soak.analyze(self.root)["build"]["binary_bytes_verified"])
        binary.write_bytes(b"different bytes")
        with self.assertRaises(ValueError):
            analyze_soak.analyze(self.root, binary)
        path.unlink()
        with self.assertRaises(ValueError):
            analyze_soak.analyze(self.root, binary)

    def test_cli_writes_only_new_validated_outputs(self):
        self.write()
        output = self.root / "observations.json"
        command = [sys.executable, str(Path(analyze_soak.__file__)), "--run-dir", str(self.root), "--output", str(output)]
        result = subprocess.run(command, capture_output=True)
        self.assertEqual(0, result.returncode, result.stderr)
        original = output.read_bytes()
        self.assertEqual(2, subprocess.run(command, capture_output=True).returncode)
        self.assertEqual(original, output.read_bytes())
        output.unlink()
        (self.root / "run.json").write_bytes(b"unfinished JSON")
        self.assertEqual(2, subprocess.run(command, capture_output=True).returncode)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
