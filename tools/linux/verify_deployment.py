"""Exercise real systemd deployment/rollback against disposable PostgreSQL/Redis."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import pwd
import re
import shutil
import socket
import subprocess
import sys
import time
import uuid

from probe import exchange, login, ready
from release import digest, package, service_info

REPO = Path(__file__).resolve().parents[2]
CONTROLLER = Path(__file__).with_name("release.py")


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def sql(query=None, file=None):
    command = ["psql", "-X", "-q", "-A", "-t", "-v", "ON_ERROR_STOP=1"]
    command += ["-f", str(file)] if file else ["-c", query]
    return subprocess.check_output(command, text=True, timeout=20).strip()


def redis(*items):
    def command(sock, values):
        data = b"*" + str(len(values)).encode() + b"\r\n"
        for value in values:
            value = str(value).encode()
            data += b"$" + str(len(value)).encode() + b"\r\n" + value + b"\r\n"
        sock.sendall(data)

    def response(stream):
        line = stream.readline()
        kind, value = line[:1], line[1:-2]
        if kind == b"-":
            raise RuntimeError(value.decode())
        if kind == b":":
            return int(value)
        if kind == b"$":
            size = int(value)
            if size == -1:
                return None
            data = stream.read(size)
            require(stream.read(2) == b"\r\n", "Redis frame delimiter")
            return data
        if kind == b"+":
            return value
        raise ValueError("Invalid Redis response")

    with socket.create_connection(("127.0.0.1", int(os.environ["DEPLOY_TEST_REDIS_PORT"])), timeout=3) as sock:
        with sock.makefile("rb") as stream:
            command(sock, ["SELECT", 2])
            response(stream)
            command(sock, items)
            return response(stream)


def player_state(player):
    wallet = [int(redis("HGET", f"player:currency:{player}", key) or 0)
              for key in ("gold", "elleaf", "macaron")]
    power = int(redis("GET", f"player:stat:{player}") or 0)
    return {"wallet": wallet, "power": power}


def db_state(player):
    return {
        "wallet": sql(f"SELECT gold,elleaf,macaron FROM player_currency WHERE player_id={player}"),
        "power": sql(f"SELECT total_combat_power FROM player_stat WHERE player_id={player}"),
        "characters": sql(f"SELECT character_id,status,level FROM character_status WHERE player_id={player} ORDER BY character_id"),
    }


def free_ports():
    sockets = []
    try:
        for _ in range(4):
            sock = socket.socket()
            sock.bind(("127.0.0.1", 0))
            sockets.append(sock)
        return [sock.getsockname()[1] for sock in sockets]
    finally:
        for sock in sockets:
            sock.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    require(sys.platform == "linux", "This exercise requires real Linux/systemd")
    require(os.environ.get("PGHOST") == "127.0.0.1", "Use loopback PostgreSQL")
    require(re.fullmatch(r"trickcal_checks_[a-z0-9_]+", os.environ.get("PGDATABASE", "")), "Use a disposable checks DB")
    require(sql("SELECT count(*) FROM information_schema.tables WHERE table_schema='public'") == "0", "Refusing nonempty PostgreSQL DB")
    require(redis("DBSIZE") == 0, "Refusing nonempty Redis DB 2")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    root = output / "host"
    shared = root / "shared"
    shared.mkdir(parents=True)
    sql(file=REPO / "DB/schema.sql")
    sql(file=REPO / "DB/seed_characters.sql")
    token = uuid.uuid4().hex[:10]
    unit = f"trickcal-deploy-{token}.service"
    unit_path = Path("/etc/systemd/system") / unit
    require(not unit_path.exists(), "Refusing to replace an existing systemd unit")
    tcp, silo, gateway, dashboard = free_ports()
    account = "deploy_probe_" + token
    env_path = shared / "server.env"
    settings = {
        "TRICKCAL_POSTGRES_CONN": f"Host=127.0.0.1;Port={os.environ['PGPORT']};Database={os.environ['PGDATABASE']};Username={os.environ['PGUSER']};Password={os.environ['PGPASSWORD']}",
        "TRICKCAL_REDIS_CONN": f"127.0.0.1:{os.environ['DEPLOY_TEST_REDIS_PORT']},defaultDatabase=2",
        "TCP_PORT": str(tcp), "ORLEANS_SILO_PORT": str(silo), "ORLEANS_GATEWAY_PORT": str(gateway),
        "DASHBOARD_PORT": str(dashboard), "ORLEANS_CLUSTER_ID": "deploy-" + token,
        "ORLEANS_SERVICE_ID": "TrickcalDeployCheck", "APM_ENABLED": "0", "WATCH_PLAYER_NAME": "",
        "TRICKCAL_ENABLE_DEBUG_COMMANDS": "0", "TRICKCAL_ENABLE_FAULT_INJECTION": "0",
    }
    env_path.write_text("\n".join(f'{key}={json.dumps(value)}' for key, value in settings.items()) + "\n")
    env_path.chmod(0o600)
    env_hash = digest(env_path)
    username = pwd.getpwuid(os.getuid()).pw_name
    definition = f"""[Unit]
Description=Isolated Trickcal deployment verification
After=network.target
[Service]
Type=simple
User={username}
WorkingDirectory={shared}
EnvironmentFile={env_path}
ExecStart={root}/current/GameServer
KillSignal=SIGTERM
KillMode=mixed
TimeoutStopSec=20
Restart=no
NoNewPrivileges=true
PrivateTmp=true
LimitNOFILE=16384
"""
    (output / "service.unit").write_text(definition)
    events = []
    observations = []
    sockets = []
    installed = False
    report = {"status": "running", "unit": unit, "baseline_commit": None,
              "candidate_commit": None, "events": events, "state_checks": observations,
              "scope": "single-host stop/restart with schema-compatible artifacts; not zero downtime"}

    def run_controller(*arguments, expected=0):
        process = subprocess.run([sys.executable, str(CONTROLLER), *map(str, arguments)],
                                 capture_output=True, text=True, timeout=180)
        data = json.loads(process.stdout.strip())
        events.append(data)
        require(process.returncode == expected, f"Controller returned {process.returncode}: {data}")
        return data

    def activate(name=None, action="activate", expected=0):
        parameters = [action, root, "--unit", unit, "--port", tcp,
                      "--account", account, "--timeout", 45]
        if name is not None:
            parameters.append(name)
        return run_controller(*parameters, expected=expected)

    try:
        subprocess.run(["sudo", "-n", "install", "-m", "0644", str(output / "service.unit"), str(unit_path)], check=True)
        installed = True
        subprocess.run(["sudo", "-n", "systemctl", "daemon-reload"], check=True)
        a = json.loads((args.baseline / "release.json").read_text())
        b = json.loads((args.candidate / "release.json").read_text())
        report.update(baseline_commit=a["source_commit"], candidate_commit=b["source_commit"],
                      baseline_dll_sha256=a["files"]["GameServer.dll"], candidate_dll_sha256=b["files"]["GameServer.dll"])
        require(a["files"]["GameServer.dll"] != b["files"]["GameServer.dll"], "Publish artifacts must have distinct build identities")
        run_controller("stage", root, args.baseline)
        run_controller("stage", root, args.candidate)
        first = activate(a["release"])
        business_name = "deploy_player_" + token
        sock, player = login(tcp, business_name, timeout=5)
        sockets.append(sock)
        request = uuid.uuid4().bytes_le
        receipt = exchange(sock, 7000, request)
        require(receipt[0] == 7001 and receipt[1][0] == 1, "Initial gacha must succeed")
        characters = exchange(sock, 5000)
        state = player_state(player)
        require(state["power"] > 0, "Gacha must acquire characters and update power")

        second = activate(b["release"])
        require(second["ready"]["pid"] != first["ready"]["pid"], "Deployment must replace the process")
        try:
            require(sock.recv(1) == b"", "Old TCP session should close on restart")
        except ConnectionResetError:
            pass
        sock.close()

        def check_state(label):
            connection, found = login(tcp, business_name, timeout=5)
            with connection:
                require(found == player, "Player ID changed across deployment")
                require(exchange(connection, 7000, request) == receipt, "Gacha receipt replay changed")
                require(exchange(connection, 5000) == characters, "Owned characters changed")
                require(exchange(connection, 9000) == (9000, b""), "Heartbeat failed")
            require(player_state(player) == state, "Redis wallet/power changed")
            stored = db_state(player)
            require(stored["wallet"] == "|".join(map(str, state["wallet"])), "Graceful stop did not persist wallet")
            require(stored["power"] == str(state["power"]), "Graceful stop did not persist power")
            observations.append({"phase": label, "player_id": player, "state": state,
                                 "sql": stored, "receipt_sha256": hashlib.sha256(receipt[1]).hexdigest()})
            return stored

        saved = check_state("after_candidate")
        manual = activate(action="rollback")
        require(manual["ready"]["release"] == a["release"], "Manual rollback did not restore baseline")
        require(check_state("after_manual_rollback") == saved, "SQL state changed after rollback")

        broken = output / "broken-publish"
        shutil.copytree(args.candidate, broken)
        # Deliberate startup failure: native .NET host cannot parse this config.
        (broken / "GameServer.runtimeconfig.json").write_text("{invalid-json")
        package(broken, "broken-runtime-config", b["source_commit"])
        run_controller("stage", root, broken)
        failed = activate("broken-runtime-config", expected=1)
        require(failed["outcome"] == "automatically_rolled_back", "Candidate failure was not recovered")
        require(failed["ready"]["release"] == a["release"], "Automatic rollback restored wrong release")
        require(check_state("after_automatic_rollback") == saved, "SQL state changed after failure recovery")

        (broken / "GameServer.dll").write_bytes((broken / "GameServer.dll").read_bytes() + b"tampered")
        before_pid = service_info(unit)["MainPID"]
        refused = run_controller("stage", root, broken, expected=2)
        require("checksum" in refused["error"], "Damaged artifact not refused by checksum")
        require(service_info(unit)["MainPID"] == before_pid, "Checksum refusal restarted healthy service")
        ready(tcp, account)
        require(digest(env_path) == env_hash, "External environment changed across releases")
        require((root / "current").resolve().name == a["release"], "Final release differs from baseline")
        report.update(status="passed", external_config_unchanged=True, damaged_artifact_refused=True,
                      final_release=a["release"], final_pid=int(before_pid))
        print("PASS: systemd deploy, upgrade, manual rollback, failed-release automatic rollback, unchanged player state and checksum refusal")
    except Exception as error:
        report.update(status="failed", error=str(error))
        raise
    finally:
        for sock in sockets:
            sock.close()
        if installed:
            subprocess.run(["sudo", "-n", "systemctl", "stop", unit], check=False, timeout=35)
            with (output / "journal.log").open("w") as stream:
                subprocess.run(["sudo", "-n", "journalctl", "-u", unit, "--no-pager", "-o", "short-iso"], stdout=stream, check=False)
            (output / "service-final.json").write_text(json.dumps(service_info(unit), indent=2) + "\n")
            # Only the unique unit created by this invocation is removed; release evidence stays.
            subprocess.run(["sudo", "-n", "rm", "--", str(unit_path)], check=True)
            subprocess.run(["sudo", "-n", "systemctl", "daemon-reload"], check=True)
            subprocess.run(["sudo", "-n", "systemctl", "reset-failed", unit], capture_output=True, check=False)
        (output / "summary.json").write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
