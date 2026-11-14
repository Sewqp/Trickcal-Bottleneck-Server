"""Bounded TCP protocol probes for the isolated deployment exercise."""
import socket
import struct
import time


def exchange(sock, packet_id, payload=b""):
    sock.sendall(struct.pack("<HH", len(payload) + 4, packet_id) + payload)

    def exact(size):
        data = bytearray()
        while len(data) < size:
            chunk = sock.recv(size - len(data))
            if not chunk:
                raise EOFError("Server closed the probe connection")
            data.extend(chunk)
        return bytes(data)

    header = exact(4)
    size, response_id = struct.unpack("<HH", header)
    if size < 4:
        raise ValueError("Invalid packet length")
    return response_id, exact(size - 4)


def login(port, name, timeout=2):
    if not name.startswith("deploy_") or len(name.encode()) > 30:
        raise ValueError("Use a dedicated deploy_ test account (maximum 30 bytes)")
    sock = socket.create_connection(("127.0.0.1", port), timeout=timeout)
    try:
        encoded = name.encode()
        packet_id, payload = exchange(sock, 1000, struct.pack("<H", len(encoded)) + encoded)
        if packet_id != 1001 or len(payload) != 45 or payload[0] != 1:
            raise ValueError("Login probe rejected")
        return sock, struct.unpack_from("<q", payload, 1)[0]
    except Exception:
        sock.close()
        raise


def ready(port, name):
    started = time.monotonic()
    sock, player = login(port, name)
    with sock:
        for _ in range(3):
            if exchange(sock, 9000) != (9000, b""):
                raise ValueError("Heartbeat response mismatch")
    return {"player_id": player, "heartbeats": 3,
            "probe_ms": round((time.monotonic() - started) * 1000, 3)}
