from __future__ import annotations
import argparse
import json
import math
import sys
from pathlib import Path
from . import __version__
from .protocol import CliError, Mailbox, project_root, read_json
from . import projects

ALIASES = {"scene": "manage_scene", "object": "manage_gameobject", "component": "manage_components",
           "prefab": "manage_prefabs", "asset": "manage_asset", "material": "manage_material",
           "texture": "manage_texture", "shader": "manage_shader", "camera": "manage_camera",
           "animation": "manage_animation", "vfx": "manage_vfx", "ui": "manage_ui",
           "physics": "manage_physics", "profiler": "manage_profiler", "build": "manage_build",
           "package": "manage_packages", "script": "manage_script", "editor": "manage_editor",
           "settings": "project_settings", "references": "asset_references", "audit": "project_audit",
           "undo": "undo_history", "terrain": "manage_terrain", "audio": "manage_audio",
           "navigation": "manage_navigation", "lighting": "manage_lighting", "selection": "manage_selection"}


class Parser(argparse.ArgumentParser):
    def error(self, message):
        raise CliError("BAD_ARGUMENTS", message, exit_code=2)


def parser() -> Parser:
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--project-path", "--project", dest="project", default=argparse.SUPPRESS)
    common.add_argument("--json", action="store_true", default=argparse.SUPPRESS)
    common.add_argument("--format", choices=["json", "human", "ndjson"], default=argparse.SUPPRESS)
    common.add_argument("--timeout", type=float, default=argparse.SUPPRESS)
    p = Parser(prog="unity-compat", parents=[common], description="Unity 2021.3+ local editor CLI. No MCP server.")
    p.add_argument("--version", action="version", version=__version__)
    subs = p.add_subparsers(dest="subcommand", required=True, parser_class=Parser)
    for name in ("status", "doctor", "install", "uninstall", "editors", "setup-fast"):
        subs.add_parser(name, parents=[common])
    op = subs.add_parser("open", parents=[common])
    op.add_argument("--editor")
    op.add_argument("--headless", action="store_true")
    ls = subs.add_parser("list", parents=[common], help="Discover commands, including project extensions")
    ls.add_argument("--offline", action="store_true")
    ls.add_argument("--group")
    desc = subs.add_parser("describe", parents=[common])
    desc.add_argument("name")
    desc.add_argument("--offline", action="store_true")
    for name in ("command", *ALIASES):
        cmd = subs.add_parser(name, parents=[common])
        cmd.add_argument("name" if name == "command" else "action")
        cmd.add_argument("--params", default="{}", help="JSON object or @file.json")
        cmd.add_argument("--arg", action="append", default=[], metavar="KEY=VALUE", help="JSON-typed argument; repeatable")
        cmd.add_argument("--detach", action="store_true")
        cmd.add_argument("--wait-job", action="store_true", help="Poll an upstream build/package/test job to completion")
    ev = subs.add_parser("eval", parents=[common])
    ev.add_argument("code", nargs="?")
    ev.add_argument("--file")
    ev.add_argument("--detach", action="store_true")
    batch = subs.add_parser("batch", parents=[common])
    batch.add_argument("file", help="JSON list of {command, params} operations")
    batch.add_argument("--continue-on-error", action="store_true")
    batch.add_argument("--detach", action="store_true")
    job = subs.add_parser("job", parents=[common])
    job.add_argument("action", choices=["status", "wait", "cancel"])
    job.add_argument("id")
    con = subs.add_parser("console", parents=[common])
    con.add_argument("action", nargs="?", default="get", choices=["get", "clear"])
    con.add_argument("--count", type=int, default=50)
    test = subs.add_parser("test", parents=[common])
    test.add_argument("--mode", choices=["EditMode", "PlayMode"], default="EditMode")
    test.add_argument("--detach", action="store_true")
    return p


def load_params(value: str, overrides: list[str]) -> dict:
    try:
        data = read_json(Path(value[1:])) if value.startswith("@") else json.loads(value)
    except (ValueError, OSError) as exc:
        raise CliError("INVALID_JSON", str(exc), exit_code=2) from exc
    if not isinstance(data, dict):
        raise CliError("INVALID_PARAMS", "--params must contain a JSON object.", exit_code=2)
    for arg in overrides:
        key, sep, val = arg.partition("=")
        if not sep or not key:
            raise CliError("INVALID_ARGUMENT", "--arg expects KEY=VALUE.", exit_code=2)
        try:
            parsed = json.loads(val)
        except ValueError:
            parsed = val
        data[key] = parsed
    try:
        json.dumps(data, allow_nan=False)
    except ValueError as exc:
        raise CliError("INVALID_JSON", "NaN and Infinity are not valid command values.", exit_code=2) from exc
    return data


def offline_catalog() -> list[dict]:
    return read_json(Path(__file__).parent / "catalog.json")


def invoke(mailbox: Mailbox, command: str, params: dict, args, wait_job: bool = False):
    timeout = getattr(args, "timeout", 60.0)
    job_id = mailbox.submit(command, params, timeout)
    if getattr(args, "detach", False):
        return {"success": True, "data": {"id": job_id, "state": "queued"}}
    try:
        result = mailbox.wait(job_id, timeout)
    except KeyboardInterrupt:
        mailbox.cancel(job_id)
        raise
    if wait_job and result.get("success"):
        # Upstream polling is performed in Unity so detached jobs work identically.
        pass
    return result


def run(args) -> dict:
    cmd = args.subcommand
    if cmd == "setup-fast":
        from .native import setup
        return {"success": True, "data": setup()}
    if cmd == "editors":
        return {"success": True, "data": projects.editors()}
    if cmd in {"list", "describe"} and args.offline:
        entries = offline_catalog()
        if cmd == "describe":
            entries = [x for x in entries if x["name"] == args.name]
            if not entries:
                raise CliError("UNKNOWN_COMMAND", args.name, exit_code=2)
        elif args.group:
            entries = [x for x in entries if x["group"] == args.group]
        return {"success": True, "data": entries, "catalog": "offline; extension commands require a live editor"}
    project = project_root(getattr(args, "project", None))
    if cmd in {"install", "uninstall"}:
        return {"success": True, "data": getattr(projects, cmd)(project)}
    if cmd == "open":
        return {"success": True, "data": projects.open_editor(project, args.editor, args.headless)}
    mailbox = Mailbox(project)
    if cmd == "doctor":
        data = {"project": str(project), "unity": projects.version(project), "python": sys.version.split()[0],
                "bridgeInstalled": (project / "Packages/com.unitycompat.cli/package.json").is_file(),
                "editors": projects.editors(), "transport": "atomic-file-mailbox", "externalServer": False}
        projects.check_version(data["unity"])
        try:
            data["bridge"] = mailbox.status()
        except CliError as exc:
            data["bridge"] = exc.envelope()
        return {"success": data["bridge"].get("success", True), "data": data}
    if cmd == "status":
        return {"success": True, "data": mailbox.status()}
    if cmd == "job":
        if args.action == "wait":
            return mailbox.wait(args.id, getattr(args, "timeout", 60.0))
        return {"success": True, "data": getattr(mailbox, "job" if args.action == "status" else "cancel")(args.id)}
    if cmd == "list":
        return invoke(mailbox, "cli_list", {"group": args.group}, args)
    if cmd == "describe":
        return invoke(mailbox, "cli_describe", {"name": args.name}, args)
    if cmd == "eval":
        if bool(args.code) == bool(args.file):
            raise CliError("BAD_ARGUMENTS", "Supply either C# code or --file, exclusively.", exit_code=2)
        code = Path(args.file).read_text(encoding="utf-8-sig") if args.file else args.code
        return invoke(mailbox, "execute_code", {"action": "execute", "code": code}, args)
    if cmd == "batch":
        operations = read_json(Path(args.file))
        if not isinstance(operations, list) or not operations:
            raise CliError("INVALID_BATCH", "Batch file must be a nonempty JSON array.", exit_code=2)
        return invoke(mailbox, "cli_batch", {"operations": operations, "continue_on_error": args.continue_on_error}, args)
    if cmd == "console":
        return invoke(mailbox, "read_console", {"action": args.action, "count": args.count}, args)
    if cmd == "test":
        return invoke(mailbox, "cli_poll", {"command": "run_tests", "params": {"mode": args.mode}}, args)
    name = args.name if cmd == "command" else ALIASES[cmd]
    params = load_params(args.params, args.arg)
    if cmd != "command":
        params["action"] = args.action
    if args.wait_job:
        return invoke(mailbox, "cli_poll", {"command": name, "params": params}, args)
    return invoke(mailbox, name, params, args)


def main(argv: list[str] | None = None) -> int:
    values = argv if argv is not None else sys.argv[1:]
    machine = "--json" in values or any(x in values for x in ("json", "ndjson"))
    result = None
    try:
        args = parser().parse_args(values)
        timeout = getattr(args, "timeout", 60.0)
        if not math.isfinite(timeout) or not 0 < timeout <= 86400:
            raise CliError("INVALID_TIMEOUT", "Timeout must be finite and between 0 and 86400 seconds.", exit_code=2)
        result = run(args)
        code = 0 if result.get("success", True) else 6
    except CliError as exc:
        result, code = exc.envelope(), exc.exit_code
    except KeyboardInterrupt:
        result, code = {"success": False, "error": {"code": "INTERRUPTED", "message": "Interrupted; inspect the job before retrying."}}, 130
    except (OSError, ValueError) as exc:
        result, code = {"success": False, "error": {"code": "IO_OR_JSON_ERROR", "message": str(exc)}}, 6
    if result is not None:
        # Explicit JSON always stays on stdout, including failures; human failures use stderr.
        stream = sys.stdout if machine or code == 0 else sys.stderr
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
        print(json.dumps(result, ensure_ascii=False, indent=None if "ndjson" in values else 2), file=stream)
    return code
