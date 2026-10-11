"""Atomic disk IPC. Commands are never replayed after timeout or domain reload."""
from __future__ import annotations
import json
import os
import time
import uuid
from pathlib import Path
from typing import Any

PROTOCOL = 1
MAX_REQUEST = 4 * 1024 * 1024


class CliError(Exception):
    def __init__(self, code: str, message: str, data: Any = None, exit_code: int = 6):
        super().__init__(message)
        self.code, self.data, self.exit_code = code, data, exit_code

    def envelope(self) -> dict:
        return {"success": False, "error": {"code": self.code, "message": str(self)}, "data": self.data}


def read_json(path: Path) -> Any:
    # Windows can briefly deny opening a file while the Editor atomically replaces it.
    # Retry only this read, never the command or a mutation that might already have run.
    for attempt in range(9):
        try:
            return json.loads(path.read_text(encoding="utf-8-sig"))
        except (PermissionError, FileNotFoundError):
            if attempt == 8:
                raise
            time.sleep(0.005)


def atomic_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    content = json.dumps(value, ensure_ascii=False, allow_nan=False).encode("utf-8")
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temp.open("xb") as f:
            f.write(content)
            f.flush()
            os.fsync(f.fileno())
        for attempt in range(9):
            try:
                os.replace(temp, path)
                break
            except PermissionError:
                if attempt == 8:
                    raise
                time.sleep(0.005)
    finally:
        temp.unlink(missing_ok=True)


def project_root(path: str | Path | None = None) -> Path:
    current = Path(path or os.getcwd()).expanduser().resolve()
    if path:
        candidates = [current]
    else:
        candidates = [current, *current.parents]
    for candidate in candidates:
        if (candidate / "Assets").is_dir() and (candidate / "ProjectSettings/ProjectVersion.txt").is_file():
            return candidate
    raise CliError("NOT_UNITY_PROJECT", "Specify a Unity project with --project-path.", exit_code=4)


class Mailbox:
    def __init__(self, project: Path):
        self.project = project.resolve()
        self.root = self.project / "Library/UnityCompat"

    def status(self, require_ready: bool = False) -> dict:
        path = self.root / "status.json"
        try:
            data = read_json(path)
            age = time.time() - float(data["heartbeat"])
            if data.get("protocol") != PROTOCOL:
                raise CliError("PROTOCOL_MISMATCH", "Upgrade the CLI and bridge together.", data, 4)
            if Path(data["project"]).resolve() != self.project:
                raise CliError("PROJECT_MISMATCH", "The bridge belongs to a different project.", exit_code=4)
            if age > 12 or age < -5 or data.get("state") in {"closed", "reloading"}:
                raise CliError("EDITOR_UNAVAILABLE", "The editor is closed, reloading, or not ticking.", data, 4)
            if require_ready and data.get("state") != "ready":
                raise CliError("EDITOR_BUSY", "The editor is compiling, importing, or running a command.", data, 4)
            return data
        except FileNotFoundError as exc:
            raise CliError("BRIDGE_NOT_RUNNING", "Install the bridge and open this project in Unity.", exit_code=4) from exc
        except (ValueError, KeyError, OSError) as exc:
            raise CliError("INVALID_STATUS", f"Cannot read bridge status: {exc}", exit_code=4) from exc

    @staticmethod
    def validate_id(job_id: str) -> str:
        if len(job_id) != 32 or any(c not in "0123456789abcdef" for c in job_id):
            raise CliError("INVALID_JOB_ID", "Job IDs are 32 lowercase hexadecimal characters.", exit_code=2)
        return job_id

    def submit(self, command: str, params: dict, timeout: float = 60) -> str:
        if not isinstance(params, dict):
            raise CliError("INVALID_PARAMS", "Command parameters must be a JSON object.", exit_code=2)
        if not 0 < timeout <= 86400:
            raise CliError("INVALID_TIMEOUT", "Timeout must be greater than 0 and at most 86400 seconds.", exit_code=2)
        status = self.status()
        job_id = uuid.uuid4().hex
        request = {"protocol": PROTOCOL, "id": job_id, "session": status["session"],
                   "command": command, "params": params, "created": time.time(), "deadline": time.time() + timeout}
        if len(json.dumps(request, ensure_ascii=False, allow_nan=False).encode("utf-8")) > MAX_REQUEST:
            raise CliError("REQUEST_TOO_LARGE", "Requests are limited to 4 MiB.", exit_code=2)
        atomic_json(self.root / "requests" / (job_id + ".json"), request)
        return job_id

    def job(self, job_id: str) -> dict:
        self.validate_id(job_id)
        response = self.root / "responses" / (job_id + ".json")
        if response.is_file():
            return {"id": job_id, "state": "completed", "result": read_json(response)}
        for state, folder in (("running", "running"), ("queued", "requests")):
            path = self.root / folder / (job_id + ".json")
            if path.is_file():
                return {"id": job_id, "state": state, "request": read_json(path)}
        # Rename-to-running can race a read. Recheck the terminal response once.
        if response.is_file():
            return {"id": job_id, "state": "completed", "result": read_json(response)}
        raise CliError("JOB_NOT_FOUND", f"No job {job_id} in this project.", exit_code=4)

    def cancel(self, job_id: str) -> dict:
        state = self.job(job_id)
        if state["state"] == "completed":
            return state
        atomic_json(self.root / "cancellations" / (job_id + ".json"), {"id": job_id})
        return {"id": job_id, "state": "cancel_requested",
                "message": "Queued work will not run. Running work is cooperative; applied changes are not rolled back."}

    def wait(self, job_id: str, timeout: float = 60) -> dict:
        self.validate_id(job_id)
        until = time.monotonic() + timeout
        while time.monotonic() < until:
            path = self.root / "responses" / (job_id + ".json")
            if path.is_file():
                response = read_json(path)
                if response.get("id") != job_id or response.get("protocol") != PROTOCOL:
                    raise CliError("INVALID_RESPONSE", "Response ID or protocol does not match.")
                return response
            time.sleep(min(0.1, max(0, until - time.monotonic())))
        raise CliError("TIMEOUT", "The command may have started. Inspect the job; do not automatically retry mutations.",
                       {"jobId": job_id})
