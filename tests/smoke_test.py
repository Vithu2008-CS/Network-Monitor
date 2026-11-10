"""API smoke checks against a running Flow instance; restores the user's budget."""
import json
import urllib.error
import urllib.request

BASE = "http://localhost:5099"


def request(path, data=None, headers=None):
    body = None if data is None else json.dumps(data).encode()
    req = urllib.request.Request(BASE + path, data=body, headers={
        "Content-Type": "application/json", **(headers or {})})
    try:
        with urllib.request.urlopen(req, timeout=10) as response:
            return response.status, response.headers, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read()


def main():
    code, headers, body = request("/api/state")
    assert code == 200
    state = json.loads(body)
    assert "frame-ancestors 'none'" in headers["Content-Security-Policy"]
    assert state["collector"]["status"] in ("running", "needs-admin", "error")
    assert state["summary"]["receivedBytes"] >= 0
    assert state["connections"], "Windows connection tables should be populated"
    assert not any("-0000" in a["name"] for a in state["interfaces"]), "Hidden filter layers should be excluded"
    assert request("/api/history?range=week")[0] == 200
    assert request("/api/history?range=invalid")[0] == 400
    code, headers, body = request("/api/export?range=day")
    assert code == 200 and "text/csv" in headers["Content-Type"]
    assert body.decode("utf-8-sig").startswith("Kind,Date,Program,")
    assert request("/api/state", headers={"Origin": "https://example.com"})[0] == 403
    assert request("/api/state", headers={"Host": "example.com:5099"})[0] == 403
    assert request("/api/settings", {"budgetGb": 2})[0] == 403
    write_headers = {"X-Flow-Request": "1"}
    assert request("/api/settings", {"budgetGb": -1}, write_headers)[0] == 400
    saved = state["settings"]["budgetGb"]
    try:
        assert request("/api/settings", {"budgetGb": 17}, write_headers)[0] == 200
        assert json.loads(request("/api/state")[2])["settings"]["budgetGb"] == 17
    finally:
        assert request("/api/settings", {"budgetGb": saved}, write_headers)[0] == 200
    reply = json.loads(request("/api/router/connect", {"username": "", "password": ""}, write_headers)[2])
    assert reply["success"] is False  # Validation only: no authentication is attempted.
    print("PASS: API, export, settings, validation, and same-origin security smoke checks.")


if __name__ == "__main__":
    main()
