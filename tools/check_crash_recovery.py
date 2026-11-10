"""Kill/restart the real server after SQL acquisition; keep Redis/PG alive.
Requires the isolated database seeded by GameServer.Checks --integration.
Uses only Python's standard library. Never clears databases or existing keys.
"""
import base64
import json
import os
from pathlib import Path
import re
import socket
import struct
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
POSTGRES = os.environ["TRICKCAL_TEST_POSTGRES"]
REDIS = os.environ["TRICKCAL_TEST_REDIS"]
if not re.search(r"(?i)(?:^|;)\s*Database\s*=\s*trickcal_checks_[^;]+", POSTGRES):
    raise SystemExit("Refusing PostgreSQL database without trickcal_checks_ prefix.")
parts = REDIS.split(",")
endpoint = parts[0].rsplit(":", 1)
if endpoint[0] not in ("127.0.0.1", "localhost"):
    raise SystemExit("Crash check requires a local, disposable Redis endpoint.")
REDIS_DB = int(next((p.split("=")[1] for p in parts[1:] if p.lower().startswith("defaultdatabase=")), "0"))

def redis(*command):
    def send(sock, items):
        data = b"*" + str(len(items)).encode() + b"\r\n"
        for item in items:
            value = str(item).encode()
            data += b"$" + str(len(value)).encode() + b"\r\n" + value + b"\r\n"
        sock.sendall(data)

    def receive(stream):
        line = stream.readline()
        if not line:
            raise RuntimeError("Redis connection closed")
        kind, value = line[:1], line[1:-2]
        if kind == b"-":
            raise RuntimeError(value.decode())
        if kind == b":":
            return int(value)
        if kind == b"$":
            size = int(value)
            if size == -1:
                return None
            result = stream.read(size)
            assert stream.read(2) == b"\r\n"
            return result
        return value

    with socket.create_connection((endpoint[0], int(endpoint[1])), timeout=5) as sock:
        stream = sock.makefile("rb")
        send(sock, ("SELECT", REDIS_DB))
        receive(stream)
        send(sock, command)
        return receive(stream)

def port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]

def exact(sock, size):
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise EOFError("Server closed connection")
        data.extend(chunk)
    return bytes(data)

def send_packet(sock, packet_id, payload=b""):
    sock.sendall(struct.pack("<HH", len(payload) + 4, packet_id) + payload)

def read_packet(sock):
    header = exact(sock, 4)
    size, _ = struct.unpack("<HH", header)
    return header + exact(sock, size - 4)

def exchange(sock, packet_id, payload=b""):
    send_packet(sock, packet_id, payload)
    return read_packet(sock)

ARTIFACTS = ROOT / ".verification"
ARTIFACTS.mkdir(exist_ok=True)
processes = []
logs = []

def start(fault):
    tcp = port()
    env = dict(os.environ, TRICKCAL_POSTGRES_CONN=POSTGRES, TRICKCAL_REDIS_CONN=REDIS,
               TCP_PORT=str(tcp), ORLEANS_SILO_PORT=str(port()), ORLEANS_GATEWAY_PORT=str(port()),
               DASHBOARD_PORT=str(port()), ORLEANS_CLUSTER_ID="crash-check-" + uuid.uuid4().hex,
               TRICKCAL_ENABLE_FAULT_INJECTION="1", TRICKCAL_ENABLE_DEBUG_COMMANDS="0",
               TRICKCAL_GACHA_FAULT=fault, WATCH_PLAYER_NAME="", APM_ENABLED="false")
    log = open(ARTIFACTS / ("crash-" + ("injected" if fault else "recovered") + ".log"), "w", encoding="utf-8")
    logs.append(log)
    process = subprocess.Popen(["dotnet", str(ROOT / "src/GameServer/bin/Release/net10.0/GameServer.dll")],
                               cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT,
                               creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    processes.append(process)
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError("Server exited; inspect .verification/crash-*.log")
        try:
            with socket.create_connection(("127.0.0.1", tcp), timeout=0.25):
                return process, tcp
        except OSError:
            time.sleep(0.1)
    raise TimeoutError("Server startup timed out")

def login(tcp, name):
    sock = socket.create_connection(("127.0.0.1", tcp), timeout=15)
    encoded = name.encode()
    result = exchange(sock, 1000, struct.pack("<H", len(encoded)) + encoded)
    assert result[4] == 1, "login failed"
    return sock, struct.unpack_from("<q", result, 5)[0]

try:
    assert redis("DBSIZE") == 0, "Crash check requires an empty Redis database"
    name = "crash_" + uuid.uuid4().hex[:12]
    request = uuid.uuid4().bytes_le
    first, tcp = start("after-sql")
    sock, player = login(tcp, name)
    with sock:
        send_packet(sock, 7000, request)
        assert sock.recv(1) == b"", "Injected failure must close the request connection"
    journal = json.loads(redis("GET", f"player:gacha:pending:{player}"))
    assert redis("HGET", f"player:currency:{player}", "elleaf") == b"0", "reserve must debit once"
    first.kill()
    first.wait(timeout=10)

    second, tcp = start("")
    sock, recovered_player = login(tcp, name)
    with sock:
        assert recovered_player == player
        response = exchange(sock, 7000, request)
        assert response == base64.b64decode(journal["Response"]), "restart must replay the exact journal result"
        assert exchange(sock, 7000, request) == response, "repeated response must stay identical"
        assert int(redis("HGET", f"player:currency:{player}", "elleaf")) == journal["Compensation"]
        assert redis("GET", f"player:gacha:pending:{player}") is None
        expected = sum({1: 440, 2: 455, 3: 450, 4: 440}[cid] for cid in journal["NewIds"])
        assert int(redis("GET", f"player:stat:{player}")) == expected
    print("PASS: real GameServer process kill/restart, exact gacha receipt, one debit/credit, consistent power")
finally:
    for process in processes:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=10)
    for log in logs:
        log.close()
