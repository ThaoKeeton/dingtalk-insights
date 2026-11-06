"""Check public command parameters with an independently installed official DWS.

Only help and schema run; no auth, data reads, send, or export commands.
Use a disposable DWS_CONFIG_DIR when running this developer check.
"""
import argparse
import json
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("dws", help="Absolute path to the official DWS executable")
args = parser.parse_args()

checks = {
    "chat +chat-messages": {"start", "end", "order", "page-all", "page-limit", "max-items"},
    "chat +search-msg": {"group", "query", "start", "end", "order", "page-all", "page-limit", "limit"},
    "chat +recent-conversations": {"start", "end", "page-limit"},
}
for leaf, required in checks.items():
    result = subprocess.run(
        [args.dws, "schema", "--cli-path", leaf, "--compact", "--format", "json"],
        capture_output=True, text=True, encoding="utf-8", timeout=30, check=True,
    )
    schema = json.loads(result.stdout)
    missing = required - set(schema["parameters"])
    if missing:
        raise SystemExit(f"Unsupported published flags for {leaf}: {sorted(missing)}")
    if leaf == "chat +chat-messages":
        help_result = subprocess.run([args.dws, "chat", "+chat-messages", "--help"],
            capture_output=True, text=True, encoding="utf-8", timeout=30, check=True)
        if "--conversation-id" not in help_result.stdout:
            raise SystemExit("Stable conversation-id alias was not documented in leaf constraints")
    print(f"PASS {leaf}: read-only schema matches application parameters")
