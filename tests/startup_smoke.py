"""Validate sign-in task enable/disable, preserving the requested enabled final state."""
import json
import urllib.request


def request(path, data=None):
    req = urllib.request.Request("http://localhost:5099" + path, data=None if data is None else json.dumps(data).encode(), headers={"Content-Type": "application/json", "X-Flow-Request": "1"})
    with urllib.request.urlopen(req, timeout=40) as response:
        return json.load(response)


def main():
    before = request("/api/state")["startedAt"]
    enabled = request("/api/startup", {"enabled": True})
    assert enabled["enabled"] and enabled["owned"]
    assert set(enabled["triggers"]) == {"Sign-in", "Session unlock"}
    try:
        disabled = request("/api/startup", {"enabled": False})
        assert not disabled["enabled"], "Disable must turn off future automatic launches"
        assert request("/api/state")["startedAt"] == before, "Disabling startup must not stop the current monitor"
    finally:
        final = request("/api/startup", {"enabled": True})
        assert final["enabled"] and final["owned"]
    print("PASS: sign-in/unlock startup enabled, disable leaves running monitor intact, enabled state restored.")


if __name__ == "__main__":
    main()
