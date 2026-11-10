"""Diagnostics, quality metrics, speedtest, DNS, and JSON export verification."""
import json
import urllib.parse
import urllib.request

BASE = "http://localhost:5099"


def call(path, data=None, headers=None):
    req_headers = {"Content-Type": "application/json", "X-Flow-Request": "1"}
    if headers:
        req_headers.update(headers)
    body = None if data is None else json.dumps(data).encode("utf-8")
    req = urllib.request.Request(BASE + path, data=body, headers=req_headers)
    with urllib.request.urlopen(req, timeout=30) as res:
        return res.status, json.loads(res.read())


def main():
    # 1. Health check
    code, health = call("/api/health")
    assert code == 200 and health["status"] == "ok"

    # 2. Network quality & latency telemetry
    code, quality = call("/api/diagnostics/quality")
    assert code == 200
    assert "quality" in quality
    assert quality["packetLossPercent"] >= 0
    assert isinstance(quality["history"], list)
    print(f"PASS: quality = {quality['quality']}, gateway = {quality['gatewayAddress']}, loss = {quality['packetLossPercent']}%.")

    # 3. DNS benchmark
    code, dns = call("/api/diagnostics/dns?name=google.com")
    assert code == 200 and len(dns["tests"]) >= 3
    sys_dns = next(t for t in dns["tests"] if t["server"] == "System Resolver")
    assert sys_dns["success"] and sys_dns["elapsedMs"] >= 0
    print(f"PASS: DNS benchmark: {sys_dns['server']} resolved in {sys_dns['elapsedMs']} ms.")

    # 4. Ping tool
    code, ping = call("/api/diagnostics/ping?host=1.1.1.1&count=2")
    assert code == 200 and ping["sent"] == 2 and ping["received"] >= 1
    print(f"PASS: Ping tool: sent = {ping['sent']}, received = {ping['received']}, avg = {ping['avgMs']} ms.")

    # 5. Speed test
    code, speed = call("/api/diagnostics/speedtest", {})
    assert code == 200 and speed["success"]
    assert speed["downloadMbps"] > 0
    print(f"PASS: Speed test: {speed['downloadMbps']} Mbps, server = {speed['server']}.")

    # 6. JSON Export
    req = urllib.request.Request(BASE + "/api/export?range=day&format=json")
    with urllib.request.urlopen(req, timeout=10) as res:
        assert res.status == 200
        assert "application/json" in res.headers["Content-Type"]
        export_data = json.loads(res.read())
        assert "exportedAt" in export_data and "totals" in export_data and "apps" in export_data
        print(f"PASS: JSON export returned {len(export_data['apps'])} recorded apps.")

    # 7. Reverse DNS and well-known services on connections
    code, state = call("/api/state")
    assert code == 200
    assert "connections" in state
    assert "networkQuality" in state["diagnostics"]
    print(f"PASS: state contains {len(state['connections'])} connections with networkQuality diagnostics.")


if __name__ == "__main__":
    main()
