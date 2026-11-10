"""Real Windows controls test. Only the isolated .tools/control-probe executable is affected."""
import json
import pathlib
import subprocess
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
PROBE = ROOT / ".tools/control-probe/out/Probe.exe"
BASE = "http://localhost:5099"


def call(path, data=None):
    req = urllib.request.Request(BASE + path, data=None if data is None else json.dumps(data).encode(), headers={"Content-Type": "application/json", "X-Flow-Request": "1"})
    try:
        with urllib.request.urlopen(req, timeout=45) as res:
            return res.status, json.load(res)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)


def until(test, description):
    end = time.monotonic() + 25
    while time.monotonic() < end:
        value = test()
        if value:
            return value
        time.sleep(1)
    raise AssertionError(description)


def main():
    assert PROBE.exists(), "Build the isolated probe first."
    initial = subprocess.run([str(PROBE), "--fetch"], capture_output=True, text=True, timeout=12)
    assert initial.returncode == 0, "Baseline probe cannot reach the public test endpoint."
    worker = subprocess.Popen([str(PROBE), "--control"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
    pid = int(worker.stdout.readline())
    app_id = None
    try:
        app = until(lambda: next((a for a in call("/api/state")[1]["apps"] if a["path"].lower() == str(PROBE).lower() and pid in a["pids"]), None), "Probe was not observed by the monitor")
        app_id = app["id"]
        rule = {"appId": app_id, "dailyLimitMb": 0, "uploadLimitKbps": 0, "quotaAction": "alert", "blocked": True, "enabled": True}
        assert call("/api/controls/rules", rule)[0] == 200
        def view():
            return next(v for v in call("/api/controls")[1]["rules"] if v["rule"]["appId"] == app_id)
        assert view()["status"] == "blocked", view()
        blocked = subprocess.run([str(PROBE), "--fetch"], capture_output=True, text=True, timeout=12)
        assert blocked.returncode != 0, "Firewall did not block the isolated probe."
        print("PASS: program-specific Windows firewall block prevented a new network connection.", flush=True)
        rule.update(blocked=False, uploadLimitKbps=64)
        assert call("/api/controls/rules", rule)[0] == 200
        assert view()["status"] == "limited", view()
        print("PASS: Windows upload QoS policy applied at 64 KB/s.", flush=True)
        rule.update(uploadLimitKbps=128)
        assert call("/api/controls/rules", rule)[0] == 200
        assert view()["status"] == "limited", view()
        rule.update(uploadLimitKbps=0, dailyLimitMb=0.000001, quotaAction="block")
        assert call("/api/controls/rules", rule)[0] == 200
        assert call(f"/api/controls/rules/{app_id}/reset", {})[0] == 200
        worker.stdin.write("udp\n"); worker.stdin.flush()
        assert worker.stdout.readline().strip() == "sent"
        until(lambda: view()["status"] == "blocked", "Daily quota did not enforce a block")
        assert call(f"/api/controls/rules/{app_id}/reset", {})[0] == 200
        assert view()["status"] == "watching" and view()["usedBytes"] == 0, view()
        print("PASS: daily quota blocked the probe and reset restored access without clearing history.", flush=True)
        rule.update(enabled=False)
        assert call("/api/controls/rules", rule)[0] == 200
        assert view()["status"] == "disabled"
        targets = call(f"/api/controls/apps/{app_id}/processes")[1]
        target = next(p for p in targets if p["pid"] == pid)
        assert target["canEnd"]
        assert call(f"/api/controls/apps/{app_id}/end", {"pid": pid, "startToken": "wrong-lifetime"})[0] == 400
        assert worker.poll() is None
        assert call(f"/api/controls/apps/{app_id}/end", {"pid": pid, "startToken": target["startToken"]})[0] == 200
        worker.wait(timeout=5)
        print("PASS: stale process identity rejected; verified isolated process terminated.", flush=True)
    finally:
        if app_id:
            code, result = call(f"/api/controls/rules/{app_id}/delete", {})
            assert code == 200, result
            assert call(f"/api/controls/rules/{app_id}/delete", {})[0] == 200, "Removing an already-removed policy must be harmless"
        if worker.poll() is None:
            worker.terminate(); worker.wait(timeout=5)
    restored = subprocess.run([str(PROBE), "--fetch"], capture_output=True, text=True, timeout=12)
    assert restored.returncode == 0, "Probe's network access was not restored."
    print("PASS: rule removed and test executable network access restored.")


if __name__ == "__main__":
    main()
