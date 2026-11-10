"""Exercise the actual LAN listener and phone access policy; never prints the pairing key."""
import json
import urllib.error
import urllib.parse
import urllib.request


def main():
    client = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with client.open("http://localhost:5099/api/state", timeout=10) as response:
        local = json.load(response)
    link = local["access"]["mobileUrl"]
    assert link, "Run Start Mobile Monitor.cmd first."
    parts = urllib.parse.urlsplit(link)
    base = f"{parts.scheme}://{parts.netloc}"
    key = urllib.parse.parse_qs(parts.fragment)["pair"][0]

    def request(path, data=None, headers=None):
        body = None if data is None else json.dumps(data).encode()
        req = urllib.request.Request(base + path, data=body, headers=headers or {})
        try:
            with client.open(req, timeout=10) as response:
                return response.status, response.read()
        except urllib.error.HTTPError as error:
            return error.code, error.read()

    assert request("/")[0] == 200, "Phone page must load without embedding its key in a request URL"
    assert request("/api/state")[0] == 403, "Unpaired peers must be rejected"
    assert request("/api/state", headers={"X-Flow-Key": "incorrect"})[0] == 403
    headers = {"X-Flow-Key": key}
    status, body = request("/api/state", headers=headers)
    assert status == 200
    remote = json.loads(body)
    assert remote["access"]["isRemote"] is True
    assert remote["access"]["canEndProcesses"] is True
    assert remote["access"]["mobileUrl"] is None, "Remote snapshots must not expose pairing links"
    assert remote["machineName"] == local["machineName"]
    assert request("/api/history?range=day", headers=headers)[0] == 200
    assert request("/api/export?range=day", headers=headers)[0] == 200
    assert request("/api/settings", {"budgetGb": 999}, {**headers, "X-Flow-Request": "1", "Content-Type": "application/json"})[0] == 403
    assert request("/api/state", headers={**headers, "Origin": "https://example.com"})[0] == 403
    for path in ("/api/controls/rules", "/api/controls/restore", "/api/controls/daily/reset", "/api/controls/apps/test/end"):
        assert request(path, {}, {**headers, "X-Flow-Request": "1", "Content-Type": "application/json"})[0] == 403, "Phone viewers cannot invoke administrator controls"
    assert request("/api/startup", {"enabled": True}, {**headers, "X-Flow-Request": "1", "Content-Type": "application/json"})[0] == 403
    assert request("/api/controls/apps/00000000000000000000/end", {"pid": 0, "startToken": "invalid"}, {**headers, "X-Flow-Request": "1", "Content-Type": "application/json"})[0] == 400, "Paired end-process route must still validate the app and process identity"
    print(f"PASS: LAN listener at {parts.hostname}:5099, paired reads/export/process capability, unpaired rejection, Windows settings protected.")


if __name__ == "__main__":
    main()
