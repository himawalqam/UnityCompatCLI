"""Install the native entry point directly, avoiding a Python parent on every call."""
from __future__ import annotations
import os
import shutil
import sysconfig
import sys
from pathlib import Path
from .protocol import CliError, atomic_json


def setup() -> dict:
    if os.name != "nt":
        raise CliError("UNSUPPORTED_PLATFORM", "The native release currently supports Windows x64.", exit_code=4)
    package = Path(__file__).resolve().parent
    source = package / "bin/win-x64/ucl.exe"
    if not source.is_file():
        raise CliError("NATIVE_BINARY_MISSING", "Build scripts/build_native.ps1 or install the Windows release wheel.", exit_code=4)
    scripts = Path(sysconfig.get_path("scripts")).resolve()
    target = scripts / "ucl.exe"
    backup = scripts / "ucl-python-launcher.exe.bak"
    if target.is_file() and not backup.exists():
        shutil.copy2(target, backup)
    atomic_json(scripts / "ucl.paths.json", {"package": str(package), "python": sys.executable})
    shutil.copy2(source, target)
    return {"installed": str(target), "version": "0.3.0", "transport": "named-pipe", "backup": str(backup)}
