"""Offline composition-fixture observations; never upgrades fixture scope to physical acceptance."""
import argparse
import hashlib
import json
import math
import re
import statistics
from pathlib import Path

from verify import number, require, unique_object

MAX_JSON = 1024 * 1024
MAX_SAMPLES = 32 * 1024 * 1024
MAX_LINE = 8192
MAX_RECORDS = 28802
WINDOW_SECONDS = 300
METRICS = (
    "working_set_bytes", "private_bytes", "managed_live_bytes", "gc_committed_bytes",
    "handles", "threads", "scene_entities", "state_cells", "log_entries", "terminal_tasks",
)
COUNTERS = ("allocated_bytes", "process_cpu_ms", "gc_gen0_count", "gc_gen1_count", "gc_gen2_count")


def parse_json(data):
    return json.loads(data.decode("utf-8"), object_pairs_hook=unique_object,
                      parse_constant=lambda value: require(False, f"non-finite JSON: {value}"))


def read_json(path):
    require(path.is_file() and not path.is_symlink(), "JSON must be a regular non-linked file")
    with path.open("rb") as stream:
        data = stream.read(MAX_JSON + 1)
    require(len(data) <= MAX_JSON, "JSON exceeds budget")
    value = parse_json(data)
    require(isinstance(value, dict), "JSON object required")
    return value, hashlib.sha256(data).hexdigest()


def read_samples(path, schema):
    require(path.is_file() and not path.is_symlink(), "samples must be a regular non-linked file")
    digest = hashlib.sha256()
    rows, size = [], 0
    with path.open("rb") as stream:
        while line := stream.readline(MAX_LINE + 1):
            size += len(line)
            require(len(line) <= MAX_LINE and size <= MAX_SAMPLES and len(rows) < MAX_RECORDS,
                    "sample line, byte or record budget exceeded")
            digest.update(line)
            row = parse_json(line)
            require(isinstance(row, dict) and number(row.get("seconds")), "invalid sample clock")
            require(row["seconds"] <= 29400, "sample clock exceeds eight-hour observation budget")
            selected = {"seconds": row["seconds"]}
            for key in METRICS + COUNTERS:
                value = row.get(key)
                require((key == "handles" and value is None) or number(value), f"invalid metric: {key}")
                if key != "process_cpu_ms" and value is not None:
                    require(type(value) is int and value <= 2**63 - 1, f"integer metric required: {key}")
                selected[key] = value
            sampler = row.get("sampler_allocated_bytes") if schema == 4 else None
            if schema == 4:
                require(type(sampler) is int and 0 <= sampler <= selected["allocated_bytes"],
                        "invalid aligned sampler allocation counter")
            selected["sampler_allocated_bytes"] = sampler
            selected["unattributed_allocated_bytes"] = selected["allocated_bytes"] - sampler if sampler is not None else None
            if rows:
                require(selected["seconds"] > rows[-1]["seconds"], "sample clock did not advance")
                for key in COUNTERS:
                    require(selected[key] >= rows[-1][key], f"counter decreased: {key}")
                if schema == 4:
                    for key in ("sampler_allocated_bytes", "unattributed_allocated_bytes"):
                        require(selected[key] >= rows[-1][key], f"aligned allocation counter decreased: {key}")
            rows.append(selected)
    require(len(rows) >= 2 and rows[0]["seconds"] == 0, "baseline and final samples required")
    return rows, digest.hexdigest()


def metric_summary(rows, key):
    values = [row[key] for row in rows if row[key] is not None]
    return dict(measured_samples=len(values), minimum=min(values) if values else None,
                median=statistics.median(values) if values else None, maximum=max(values) if values else None)


def allocation_rate(first, last, key):
    elapsed = last["seconds"] - first["seconds"]
    return (last[key] - first[key]) / elapsed if elapsed > 0 and first[key] is not None and last[key] is not None else None


def observe(rows, requested_seconds):
    # Only ordinary samples enter windows; forcing a final GC cannot improve the trend.
    ordinary = rows[1:-1]
    groups = {}
    for row in ordinary:
        groups.setdefault(int(row["seconds"] // WINDOW_SECONDS), []).append(row)
    windows = []
    for index, samples in sorted(groups.items()):
        start = index * WINDOW_SECONDS
        duration = min(WINDOW_SECONDS, max(0, requested_seconds - start))
        complete = duration == WINDOW_SECONDS
        rate_interval = samples[-1]["seconds"] - samples[0]["seconds"]
        windows.append(dict(start_seconds=start, end_seconds=max(start + duration, samples[-1]["seconds"]),
                            first_sample_seconds=samples[0]["seconds"], last_sample_seconds=samples[-1]["seconds"],
                            samples=len(samples), complete=complete,
                            coverage_ratio=len(samples) / duration if duration > 0 else None,
                            rate_interval_seconds=rate_interval,
                            cpu_percent_one_core=(samples[-1]["process_cpu_ms"] - samples[0]["process_cpu_ms"]) / (rate_interval * 10) if rate_interval > 0 else None,
                            allocation_bytes_per_second=(samples[-1]["allocated_bytes"] - samples[0]["allocated_bytes"]) / rate_interval if rate_interval > 0 else None,
                            sampler_allocation_bytes_per_second=allocation_rate(samples[0], samples[-1], "sampler_allocated_bytes"),
                            unattributed_allocation_bytes_per_second=allocation_rate(samples[0], samples[-1], "unattributed_allocated_bytes"),
                            metrics={key: metric_summary(samples, key) for key in METRICS}))
    eligible = [window for window in windows if window["complete"] and window["coverage_ratio"] >= .8]
    trends = {}
    for key in METRICS:
        points = [(window, window["metrics"][key]["median"]) for window in eligible
                  if window["metrics"][key]["measured_samples"] == window["samples"]]
        slope = None
        if len(points) >= 3:
            times = [(w["first_sample_seconds"] + w["last_sample_seconds"]) / 7200 for w, _ in points]
            values = [value for _, value in points]
            center_x, center_y = statistics.mean(times), statistics.mean(values)
            slope = sum((x - center_x) * (y - center_y) for x, y in zip(times, values)) / sum((x - center_x)**2 for x in times)
        trends[key] = dict(eligible_windows=len(points), slope_units_per_hour=slope,
                           first_median=points[0][1] if points else None, last_median=points[-1][1] if points else None)
    gaps = [right["seconds"] - left["seconds"] for left, right in zip(rows[:-2], rows[1:-1])]
    observed_end = rows[-2]["seconds"] if ordinary else 0
    rates = []
    previous = rows[0]
    for row in ordinary:
        elapsed = row["seconds"] - previous["seconds"]
        rates.append(dict(seconds=row["seconds"], cpu=(row["process_cpu_ms"] - previous["process_cpu_ms"]) / (elapsed * 10),
                          allocation=(row["allocated_bytes"] - previous["allocated_bytes"]) / elapsed,
                          sampler=allocation_rate(previous, row, "sampler_allocated_bytes"),
                          unattributed=allocation_rate(previous, row, "unattributed_allocated_bytes")))
        previous = row
    def rate_summary(key):
        values = [rate[key] for rate in rates if rate[key] is not None]
        return dict(median=statistics.median(values) if values else None, maximum=max(values) if values else None)
    return dict(
        ordinary_samples=len(ordinary), observed_end_seconds=observed_end,
        samples_after_requested_end=sum(row["seconds"] >= requested_seconds for row in ordinary),
        maximum_sampling_gap_seconds=max(gaps) if gaps else None,
        sampling_gaps_over_five_seconds=sum(gap > 5 for gap in gaps),
        ordinary_peaks={key: metric_summary(ordinary, key) for key in METRICS},
        cpu_percent_one_core=dict(median=statistics.median(r["cpu"] for r in rates) if rates else None,
                                  maximum=max(r["cpu"] for r in rates) if rates else None),
        allocation_bytes_per_second=dict(median=statistics.median(r["allocation"] for r in rates) if rates else None,
                                         maximum=max(r["allocation"] for r in rates) if rates else None),
        sampler_allocation_bytes_per_second=rate_summary("sampler"),
        unattributed_allocation_bytes_per_second=rate_summary("unattributed"),
        windows=windows, median_trends=trends,
    )


def bind_receipt(root, binary):
    path = root / "build-receipt.json"
    if not path.exists():
        require(binary is None, "binary verification requires a build receipt")
        return None
    receipt, digest = read_json(path)
    require(receipt.get("scope") == "desktop-composition-fixture"
            and receipt.get("real_desktop_acceptance") is False, "invalid receipt scope")
    for key, length in (("base_commit", 40), ("binary_sha256", 64), ("source_input_manifest_sha256", 64)):
        require(isinstance(receipt.get(key), str) and re.fullmatch(r"[0-9a-f]{" + str(length) + r"}", receipt[key]),
                f"invalid receipt identity: {key}")
    identity = receipt.get("build_identity")
    require(isinstance(identity, str) and 0 < len(identity) <= 512, "build identity required")
    sources = receipt.get("source_inputs")
    require(isinstance(sources, dict) and 0 < len(sources) <= 16384, "bounded source manifest required")
    for name, source_hash in sources.items():
        require(isinstance(name, str) and 0 < len(name) <= 512 and isinstance(source_hash, str)
                and re.fullmatch(r"[0-9a-f]{64}", source_hash), "invalid recorded source input")
    verified = False
    if binary is not None:
        require(binary.is_file() and not binary.is_symlink(), "binary must be a regular non-linked file")
        actual, length = hashlib.sha256(), 0
        with binary.open("rb") as stream:
            while chunk := stream.read(65536):
                length += len(chunk)
                require(length <= 256 * 1024 * 1024, "binary exceeds budget")
                actual.update(chunk)
        require(length > 0 and actual.hexdigest() == receipt["binary_sha256"], "frozen binary hash mismatch")
        verified = True
    return dict(receipt_sha256=digest, base_commit=receipt["base_commit"], build_identity=identity,
                binary_sha256=receipt["binary_sha256"], binary_bytes_verified=verified,
                recorded_source_input_manifest_sha256=receipt["source_input_manifest_sha256"],
                recorded_source_inputs=len(sources), source_reproduction_verified=False)


def analyze(root, binary=None):
    run, run_hash = read_json(root / "run.json")
    require(type(run.get("schema")) is int and run["schema"] in (3, 4)
            and run.get("scope") == "desktop-composition-fixture" and run.get("real_desktop_acceptance") is False,
            "only completed schema-3/4 composition fixtures are supported")
    require(run.get("mode") in ("idle", "navigation") and type(run.get("passed")) is bool, "invalid fixture summary")
    seconds = run.get("requested_seconds")
    require(type(seconds) is int and 1 <= seconds <= 28800, "invalid requested duration")
    rows, samples_hash = read_samples(root / "samples.jsonl", run["schema"])
    require(type(run.get("samples")) is int and run["samples"] == len(rows), "summary/sample count mismatch")
    elapsed = run.get("elapsed_seconds")
    require(number(elapsed) and elapsed >= seconds and abs(elapsed - rows[-1]["seconds"]) < .001,
            "incomplete run or mismatched elapsed time")
    require(all(row["seconds"] < elapsed for row in rows[:-1]), "invalid sample interval")
    return dict(schema=1, scope="desktop-composition-fixture-observations", real_desktop_acceptance=False,
                mode=run["mode"], requested_seconds=seconds, elapsed_seconds=elapsed,
                fixture_endpoint_gate_passed=run["passed"], runtime_kpi_certified=False,
                input_sha256=dict(run=run_hash, samples=samples_hash), build=bind_receipt(root, binary),
                endpoint_scope="baseline/final after explicit full GC; excluded from ordinary windows",
                endpoints=dict(baseline=rows[0], final_after_gc=rows[-1]),
                managed_live_bytes_scope="GC.GetTotalMemory(false) estimate; ordinary windows include the allocation nursery",
                gc_committed_bytes_scope="runtime GC committed memory; not platform commit or native memory",
                source_schema=run["schema"],
                rate_scope="includes fixture and Process/JSON sampler; CPU observer cost was not separately measured",
                sampler_allocation_scope="schema 4: synchronous fixture-thread capture and sample writes, aligned before each write; schema 3: unmeasured/null",
                unattributed_allocation_scope="remaining process allocation includes other fixture work and background activity; not product-only",
                trend_scope="least-squares window medians; at least three populated full windows; diagnostic, not a no-leak gate",
                **observe(rows, seconds))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-dir", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--binary", type=Path)
    args = parser.parse_args()
    try:
        result = analyze(args.run_dir, args.binary)
        data = (json.dumps(result, ensure_ascii=False, allow_nan=False, indent=2) + "\n").encode("utf-8")
        with args.output.open("xb") as stream:
            stream.write(data)
    except (OSError, ValueError, KeyError, TypeError) as error:
        parser.exit(2, f"Soak observation rejected: {error}\n")
    print("Soak observation written; fixture scope retained, runtime KPI and physical acceptance remain unverified.")


if __name__ == "__main__":
    main()
