#!/usr/bin/env python3
"""Exercise a new assessment through the actual previous Core/Runtime, using synthetic data only."""
import argparse
import json
import pathlib
import shutil
import subprocess
import sys
import tarfile
import tempfile
import uuid


BASE = "7b03098f730d263f22a1d183b7fee7dc57c42017"
REPO = pathlib.Path(__file__).resolve().parent.parent
PROJECT = REPO / "tests/DingTalkInsights.AssessmentRollbackFixture/DingTalkInsights.AssessmentRollbackFixture.csproj"


def run(command):
    subprocess.run([str(arg) for arg in command], cwd=REPO, check=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--report", type=pathlib.Path)
    args = parser.parse_args()
    subprocess.run(["git", "cat-file", "-e", BASE + "^{commit}"], cwd=REPO, check=True)
    checks = []
    with tempfile.TemporaryDirectory(prefix="dti-assessment-build-") as temp:
        work = pathlib.Path(temp)
        legacy = work / "previous-source"
        legacy.mkdir()
        archive = work / "previous.tar"
        run(["git", "archive", "--format=tar", "-o", archive, BASE, "--", "Directory.Build.props", "src/DingTalkInsights.Core", "src/DingTalkInsights.Runtime"])
        with tarfile.open(archive) as source:
            source.extractall(legacy, filter="data")
        binaries = {}
        for name, source in (("current", REPO), ("previous", legacy)):
            output = work / name
            run([args.dotnet, "build", PROJECT, "-c", "Release", "-o", output, "-v:q", "-p:UseSharedCompilation=false",
                 "-p:ArtifactsPath=" + str(work / (name + "-build")), "-p:DtiSourceRoot=" + str(source)])
            binaries[name] = output / "DingTalkInsights.AssessmentRollbackFixture.dll"
        fixture = pathlib.Path(tempfile.gettempdir()) / "DingTalkInsights-AssessmentRollback" / uuid.uuid4().hex
        fixture.mkdir(parents=True)
        (fixture / ".fixture-owner").write_text(fixture.name, encoding="utf-8")
        try:
            for cipher in (["synthetic", "dpapi"] if sys.platform == "win32" else ["synthetic"]):
                sequence = [("current", "write", "model"), ("previous", "legacy-rewrite", "model"),
                            ("current", "read", "model"), ("previous", "write", "legacy"), ("current", "read", "legacy")]
                for version, mode, expected in sequence:
                    run([args.dotnet, binaries[version], mode, fixture, cipher, expected])
                    checks.append({"binary": version, "mode": mode, "cipher": cipher, "expected": expected})
        finally:
            if (fixture / ".fixture-owner").read_text(encoding="utf-8") == fixture.name:
                shutil.rmtree(fixture)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps({"succeeded": True, "previousCommit": BASE, "checks": checks}, indent=2) + "\n", encoding="utf-8")
    print(f"PASS {len(checks)} exact-previous-version assessment rollback checks; no external services")


if __name__ == "__main__":
    main()
