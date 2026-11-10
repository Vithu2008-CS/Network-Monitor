"""Generate known public TCP and DNS/UDP traffic and inspect this executable's usage deltas."""
import ipaddress
import json
import os
import socket
import sys
import time
import urllib.request


def state():
    with urllib.request.urlopen("http://localhost:5099/api/state", timeout=10) as response:
        return json.load(response)


def program(snapshot):
    rows = [app for app in snapshot["apps"] if app["path"].lower() == sys.executable.lower()]
    return {
        "received": sum(app["receivedBytes"] for app in rows),
        "sent": sum(app["sentBytes"] for app in rows),
        "protocols": {value for app in rows for value in app["protocols"]},
    }


def wait_for(before, field, minimum):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        measured = program(state())
        if measured[field] - before[field] >= minimum:
            return measured
        time.sleep(1)
    raise AssertionError(f"No expected {field} increase. Confirm administrator capture is running.")


def main():
    snapshot = state()
    assert snapshot["collector"]["status"] == "running", "Run Start Monitor.cmd with administrator access first."
    gateways = [value.strip() for nic in snapshot["interfaces"] if nic["isDefault"] for value in nic["gateway"].split(",")]
    gateway = next((value for value in gateways if ipaddress.ip_address(value).version == 4), None)
    assert gateway, "An active IPv4 gateway is required for the UDP DNS test."
    time.sleep(3)
    baseline = program(state())
    url = "https://api.nuget.org/v3-flatcontainer/microsoft.diagnostics.tracing.traceevent/3.2.8/microsoft.diagnostics.tracing.traceevent.3.2.8.nupkg"
    with urllib.request.urlopen(url, timeout=30) as response:
        downloaded = sum(len(block) for block in iter(lambda: response.read(65536), b""))
    tcp = wait_for(baseline, "received", int(downloaded * 0.9))
    assert "TCP" in tcp["protocols"]
    print(f"TCP PASS: downloaded {downloaded:,} bytes; program RX increased {tcp['received'] - baseline['received']:,} bytes.")
    # Allow delayed TCP close/ACK events to settle before the UDP stage.
    time.sleep(3)
    baseline = program(state())
    query = bytes.fromhex("123401000001000000000000076578616d706c6503636f6d0000010001")
    assert len(query) == 29
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
        assert udp.sendto(query, (gateway, 53)) == len(query)
        measured = wait_for(baseline, "sent", len(query))
    assert "UDP" in measured["protocols"]
    print(f"UDP PASS: sent {len(query)}-byte DNS query; program TX increased {measured['sent'] - baseline['sent']:,} bytes.")
    print(f"Program: {sys.executable} (test PID {os.getpid()}). Totals are grouped by executable, including any other running instances.")


if __name__ == "__main__":
    main()
