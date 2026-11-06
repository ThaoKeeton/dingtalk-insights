#!/usr/bin/env python3
"""Exercise real Git checkout filters against the exact pinned Workbench blobs.

No network, platform edits, global Git configuration, credentials or business data.
The fixture has its own index and only the pinned export plus repository attributes.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


PIN = Path("eng/vendor/workbench-6565180b")


def git_blob(data: bytes) -> str:
    return hashlib.sha1(b"blob " + str(len(data)).encode("ascii") + b"\0" + data).hexdigest()


def child_environment(empty_config: Path) -> dict[str, str]:
    allowed = (
        "PATH", "SystemRoot", "SYSTEMROOT", "windir", "WINDIR", "ComSpec", "COMSPEC",
        "SystemDrive", "SYSTEMDRIVE", "TEMP", "TMP", "PATHEXT", "DOTNET_ROOT",
        "DOTNET_CLI_HOME", "XDG_DATA_HOME", "NUGET_PACKAGES", "NUGET_SCRATCH",
    )
    result = {name: os.environ[name] for name in allowed if name in os.environ}
    result.update({
        "GIT_CONFIG_NOSYSTEM": "1",
        "GIT_CONFIG_GLOBAL": str(empty_config),
        "GIT_TERMINAL_PROMPT": "0",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
    })
    return result


def command(args: list[str], environment: dict[str, str]) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(args, env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          timeout=30, check=False)


def git(repo: Path, args: list[str], environment: dict[str, str]) -> bytes:
    result = command(["git", "-C", str(repo), *args], environment)
    if result.returncode:
        raise RuntimeError(f"Fixture Git operation failed: {args[0]}: "
                           + result.stderr.decode("utf-8", errors="replace"))
    return result.stdout


def check(args: argparse.Namespace, evidence: dict) -> None:
    repo = Path(__file__).resolve().parents[1]
    provenance = json.loads((repo / PIN / "provenance.json").read_bytes())
    evidence["platformCommit"] = provenance["commit"]
    with tempfile.TemporaryDirectory(prefix="dingtalk source checkout ") as temporary:
        sandbox = Path(temporary)
        empty_config = sandbox / "empty.gitconfig"
        empty_config.write_bytes(b"")
        environment = child_environment(empty_config)
        evidence["productCommit"] = git(repo, ["rev-parse", "HEAD"], environment).decode().strip()
        fixture = sandbox / "repository"
        fixture.mkdir()
        templates = sandbox / "empty-templates"
        templates.mkdir()
        git(fixture, ["init", "--quiet", f"--template={templates}"], environment)
        expected: dict[str, bytes] = {}
        for relative, facts in provenance["files"].items():
            path = (PIN / relative).as_posix()
            data = git(repo, ["show", f"HEAD:{path}"], environment)
            if git_blob(data) != facts["gitBlob"] or hashlib.sha256(data).hexdigest() != facts["sha256"]:
                raise RuntimeError(f"Committed source does not match pinned provenance: {relative}")
            expected[relative] = data
            target = fixture / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        for name in ("provenance.json", "ATTRIBUTION.md"):
            (fixture / PIN / name).write_bytes(git(repo, ["show", f"HEAD:{(PIN / name).as_posix()}"], environment))
        attributes = repo / ".gitattributes"
        if attributes.exists():
            data = attributes.read_bytes()
            (fixture / ".gitattributes").write_bytes(data)
            evidence["attributesSha256"] = hashlib.sha256(data).hexdigest()
        git(fixture, ["-c", "core.autocrlf=false", "add", "--all"], environment)
        evidence["sourceFiles"] = len(expected)
        evidence["gitTextAttribute"] = git(fixture, ["check-attr", "text", "--",
            (PIN / "Directory.Build.props").as_posix()], environment).decode().strip()
        failures: list[str] = []
        for autocrlf, eol in (("true", "crlf"), ("input", "lf"), ("false", "lf")):
            output = sandbox / ("checkout-" + autocrlf)
            output.mkdir()
            git(fixture, ["-c", f"core.autocrlf={autocrlf}", "-c", f"core.eol={eol}",
                "checkout-index", "--all", "--prefix=" + str(output) + os.sep], environment)
            mode = {"autocrlf": autocrlf, "eol": eol, "files": [], "succeeded": True}
            evidence["modes"].append(mode)
            for relative, original in expected.items():
                actual = (output / PIN / relative).read_bytes()
                facts = provenance["files"][relative]
                passed = actual == original and git_blob(actual) == facts["gitBlob"] and hashlib.sha256(actual).hexdigest() == facts["sha256"]
                mode["files"].append({"path": relative, "succeeded": passed,
                    "expectedBytes": len(original), "actualBytes": len(actual),
                    "expectedGitBlob": facts["gitBlob"], "actualGitBlob": git_blob(actual),
                    "expectedSha256": facts["sha256"], "actualSha256": hashlib.sha256(actual).hexdigest(),
                    "changedOnlyByLfToCrlf": actual != original and actual == original.replace(b"\n", b"\r\n")})
                if not passed:
                    mode["succeeded"] = False
                    failures.append(f"autocrlf={autocrlf} changed {relative}")
            if args.package_tool:
                result = command([args.dotnet, str(Path(args.package_tool).resolve()), "verify-source",
                                  str(output / PIN)], environment)
                mode["productionVerifier"] = {"exitCode": result.returncode,
                    "stdout": result.stdout.decode("utf-8", errors="replace").strip(),
                    "stderr": result.stderr.decode("utf-8", errors="replace").strip()}
                if result.returncode:
                    failures.append(f"Production verifier rejected autocrlf={autocrlf} checkout")
                if autocrlf == "true" and mode["succeeded"]:
                    changed = output / PIN / "Directory.Build.props"
                    changed.write_bytes(expected["Directory.Build.props"].replace(b"\n", b"\r\n"))
                    rejected = command([args.dotnet, str(Path(args.package_tool).resolve()), "verify-source",
                                        str(output / PIN)], environment)
                    evidence["crlfMutationRejected"] = rejected.returncode != 0 and b"Immutable source changed: Directory.Build.props" in rejected.stderr
                    if not evidence["crlfMutationRejected"]:
                        failures.append("Production verifier accepted changed CRLF bytes")
                    changed.write_bytes(expected["Directory.Build.props"])
            print(f"{'PASS' if mode['succeeded'] else 'FAIL'} exact {len(expected)}-file Git checkout: "
                  f"core.autocrlf={autocrlf}, core.eol={eol}")
        if failures:
            raise RuntimeError(f"{len(failures)} checkout/source verification failures; first: {failures[0]}")
        evidence["succeeded"] = True
        print(f"PASS {len(expected) * len(evidence['modes'])} exact checkout blob/SHA256 checks")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--package-tool", help="Optional existing production PackageTool DLL")
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    evidence: dict = {"succeeded": False, "modes": [], "kind": "actual-git-checkout-pinned-source-bytes"}
    try:
        check(args, evidence)
        return 0
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        evidence["failure"] = str(error)
        print("FAIL " + str(error), file=sys.stderr)
        return 1
    finally:
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(main())
