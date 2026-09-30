"""Launch pinned TabbyAPI with credential log redaction; no downloads."""
import argparse
import json
from pathlib import Path
import runpy
import sys

parser = argparse.ArgumentParser()
parser.add_argument("--server", required=True)
parser.add_argument("--config", required=True)
args = parser.parse_args()
server = Path(args.server).resolve()
config = Path(args.config).resolve()
keys = json.loads(Path("api_tokens.yml").read_text())
secrets = (keys["api_key"], keys["admin_key"])
from loguru import logger


def redact(record):
    for secret in secrets:
        record["message"] = record["message"].replace(secret, "[redacted]")


logger.configure(patcher=redact)
sys.path.insert(0, str(server))
sys.argv = [str(server / "main.py"), "--config", str(config)]
runpy.run_path(str(server / "main.py"), run_name="__main__")
