"""Export non-executable generated deltas for the native consumer contract smoke."""
import argparse
import random
from pathlib import Path

from delta import RIDS, generate, package_name, unpack
from test_delta import DeltaTests
from verify import expected_names, verify


def export(directory):
    output, history = directory / "artifacts", directory / "history"
    output.mkdir(parents=True); previous = history / "2.0.0.alpha.5"; previous.mkdir(parents=True)
    old = random.Random(21).randbytes(256 * 1024)
    for name in expected_names("2.0.0.alpha.6"):
        (output / name).write_bytes(b"installer test fixture, not executable")
    fixtures = DeltaTests()
    for rid in RIDS:
        fixtures.make_archive(previous, previous.name, rid, old)
        fixtures.make_archive(output, "2.0.0.alpha.6", rid, old + b"new tail")
        for label, package in (("source", previous / package_name(previous.name, rid)),
                               ("target", output / package_name("2.0.0.alpha.6", rid))):
            tree = directory / label / rid; tree.mkdir(parents=True)
            unpack(package, tree, rid)
    generate(output, "2.0.0.alpha.6", history)
    verify(output, "2.0.0.alpha.6")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    export(args.directory)
