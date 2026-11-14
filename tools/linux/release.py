"""Stage immutable .NET publish directories and activate them through systemd.

This tool changes application releases only; it never migrates or restores a DB.
The unit and external environment file are provisioned separately.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import uuid

from probe import ready


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def release_id(value):
    if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}", value):
        raise ValueError("Invalid release ID")
    return value


def inventory(directory):
    result = {}
    for file in sorted(directory.rglob("*")):
        if file.is_symlink():
            raise ValueError("Release artifacts may not contain symlinks")
        if file.is_file() and file != directory / "release.json":
            result[file.relative_to(directory).as_posix()] = digest(file)
    return result


def package(directory, name, commit):
    release_id(name)
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Source commit must be a full SHA")
    if not (directory / "GameServer").is_file() or not (directory / "GameServer.dll").is_file():
        raise ValueError("Expected linux-x64 self-contained publish output")
    manifest = {"release": name, "source_commit": commit, "rid": "linux-x64",
                "self_contained": True, "files": inventory(directory)}
    (directory / "release.json").write_text(json.dumps(manifest, indent=2) + "\n")
    return {"release": name, "files": len(manifest["files"])}


def verify(directory, expected=None):
    manifest = json.loads((directory / "release.json").read_text())
    release_id(manifest["release"])
    if expected is not None and manifest["release"] != expected:
        raise ValueError("Release ID differs from directory")
    if manifest["files"] != inventory(directory):
        raise ValueError("Release checksum/file inventory mismatch")
    if not os.access(directory / "GameServer", os.X_OK):
        raise ValueError("GameServer apphost is not executable")
    return manifest


def stage(root, artifact):
    manifest = verify(artifact)
    releases = root / "releases"
    releases.mkdir(parents=True, exist_ok=True)
    target = releases / manifest["release"]
    if target.exists():
        raise ValueError("Release ID already exists; published releases are immutable")
    temporary = releases / (".staging-" + uuid.uuid4().hex)
    try:
        shutil.copytree(artifact, temporary)
        verify(temporary, manifest["release"])
        temporary.rename(target)
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)
    return {"staged": manifest["release"], "files": len(manifest["files"])}


def pointer(root, name):
    link = root / name
    if not link.is_symlink():
        if link.exists():
            raise ValueError(f"{name} is not a managed symlink")
        return None
    target = link.resolve(strict=True)
    if target.parent != (root / "releases").resolve():
        raise ValueError(f"{name} points outside the releases directory")
    return release_id(target.name)


def switch(root, name, target):
    temporary = root / ("." + name + "-" + uuid.uuid4().hex)
    temporary.symlink_to(Path("releases") / release_id(target))
    try:
        os.replace(temporary, root / name)
    finally:
        temporary.unlink(missing_ok=True)


def control(unit, action):
    subprocess.run(["sudo", "-n", "systemctl", action, unit], check=True,
                   capture_output=True, text=True, timeout=35)


def service_info(unit):
    output = subprocess.check_output(
        ["systemctl", "show", unit, "--property=MainPID,ActiveState,SubState,WorkingDirectory,ExecStart"],
        text=True, timeout=5)
    return dict(line.split("=", 1) for line in output.splitlines() if "=" in line)


def wait_ready(root, unit, target, port, account, timeout):
    deadline = time.monotonic() + timeout
    last_error = "Probe not attempted"
    while time.monotonic() < deadline:
        info = service_info(unit)
        if info.get("ActiveState") in ("failed", "inactive"):
            raise RuntimeError("systemd service exited before readiness")
        try:
            pid = int(info.get("MainPID", "0"))
            executable = Path(f"/proc/{pid}/exe").resolve(strict=True)
            if executable != root / "releases" / target / "GameServer":
                raise RuntimeError("MainPID is not running the requested release")
            result = ready(port, account)
            return {"release": target, "pid": pid, "executable": str(executable), **result}
        except (OSError, EOFError, ValueError, RuntimeError) as error:
            last_error = str(error)
            time.sleep(0.2)
    raise TimeoutError("Readiness failed: " + last_error)


def activate(root, unit, target, port, account, timeout, action):
    import fcntl

    root.mkdir(parents=True, exist_ok=True)
    with (root / ".deploy.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if not re.fullmatch(r"trickcal-deploy-[a-z0-9-]+\.service", unit):
            raise ValueError("Use a dedicated trickcal-deploy-*.service unit")
        info = service_info(unit)
        if info.get("WorkingDirectory") != str(root / "shared") or str(root / "current/GameServer") not in info.get("ExecStart", ""):
            raise ValueError("Service does not belong to this deployment root")
        old = pointer(root, "current")
        previous = pointer(root, "previous")
        target = previous if action == "rollback" else release_id(target)
        if target is None:
            raise ValueError("No previous healthy release to roll back to")
        manifest = verify(root / "releases" / target, target)
        # A damaged rollback target must be detected before stopping the healthy process.
        if old is not None:
            verify(root / "releases" / old, old)
        started = time.monotonic()
        event = {"action": action, "from": old, "target": target,
                 "source_commit": manifest["source_commit"], "timestamp_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
        try:
            switch(root, "current", target)
            control(unit, "restart")
            event["ready"] = wait_ready(root, unit, target, port, account, timeout)
            if old is not None and old != target:
                switch(root, "previous", old)
            event["outcome"] = "healthy"
            code = 0
        except Exception as error:
            event["error"] = str(error)
            try:
                if old is None:
                    control(unit, "stop")
                    (root / "current").unlink()
                    event["outcome"] = "failed_no_previous_release"
                else:
                    switch(root, "current", old)
                    control(unit, "restart")
                    event["ready"] = wait_ready(root, unit, old, port, account, timeout)
                    event["outcome"] = "automatically_rolled_back"
                code = 1
            except Exception as recovery_error:
                event.update(outcome="rollback_failed", recovery_error=str(recovery_error))
                code = 2
        event["elapsed_seconds"] = round(time.monotonic() - started, 3)
        with (root / "events.jsonl").open("a") as stream:
            stream.write(json.dumps(event) + "\n")
        return event, code


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    pack = commands.add_parser("package")
    pack.add_argument("artifact", type=Path)
    pack.add_argument("release")
    pack.add_argument("commit")
    stage_parser = commands.add_parser("stage")
    stage_parser.add_argument("root", type=Path)
    stage_parser.add_argument("artifact", type=Path)
    for name in ("activate", "rollback"):
        command = commands.add_parser(name)
        command.add_argument("root", type=Path)
        command.add_argument("--unit", required=True)
        command.add_argument("--port", type=int, required=True)
        command.add_argument("--account", required=True)
        command.add_argument("--timeout", type=int, default=45, choices=range(1, 121))
        if name == "activate":
            command.add_argument("release")
    args = parser.parse_args()
    try:
        code = 0
        if args.action == "package":
            result = package(args.artifact.resolve(), args.release, args.commit)
        elif args.action == "stage":
            result = stage(args.root.resolve(), args.artifact.resolve())
        else:
            result, code = activate(args.root.resolve(), args.unit, getattr(args, "release", None),
                                    args.port, args.account, args.timeout, args.action)
        print(json.dumps(result))
        return code
    except Exception as error:
        print(json.dumps({"outcome": "refused", "error": str(error)}))
        return 2


if __name__ == "__main__":
    sys.exit(main())
