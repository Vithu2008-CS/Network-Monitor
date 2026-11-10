# Flow · Network Monitor

A local Windows dashboard for this laptop's network usage. Open **[http://localhost:5099](http://localhost:5099)** while the monitor is running.

## Run

**Automatic startup is now configured** as the current user's `Flow Network Monitor` Windows scheduled task. It starts in the background at sign-in and session unlock, with administrator capture, no stored password, battery use allowed, retries after startup failures and no duplicate instances. The monitor normally continues across sleep; an unlock can restart it if it has stopped. Change this under **Settings → Automatic startup**. Disabling startup affects future launches, not the currently running monitor. Task registration is handled by `Start-at-SignIn.ps1`; the actual start action uses `Start-Monitor.ps1 -NoBrowser` and preserves the configured sharing mode.

Double-click **Start Monitor.cmd** and approve the Windows administrator prompt. Keep its console open; Ctrl+C stops collection. Alternatively use **Stop Monitor.cmd** or the dashboard's stop control. Running the launcher again opens the existing dashboard.

The project-local .NET 10 SDK has already been installed in `.tools/dotnet`. On a fresh copy the launcher downloads it from Microsoft and builds the app. No global SDK installation is required. Launch without elevation using `Start Monitor.cmd -NoAdmin`; adapter usage and endpoints remain available, but Windows requires elevation for per-program byte accounting.

## What it shows

- Live download/upload chart, laptop session totals, today's totals, calendar-month totals, active programs and connection counts.
- Programs grouped by executable path, live transfer rates, cumulative bytes, protocols, process IDs and executable locations. Several browser processes belonging to the same executable are grouped together.
- TCP IPv4/IPv6 connections and UDP bindings, with owning program, local/remote endpoint and state. UDP remote peers are not available from the Windows connection table and are shown as unspecified.
- Wi-Fi, Ethernet, mobile and virtual adapters, link speed, addresses, gateway, MAC, Wi-Fi SSID and signal when Windows exposes them. Hidden filter layers are excluded. Virtual adapters are visible but do not inflate physical-adapter totals.
- Daily, weekly and monthly history, program rankings and CSV export; a monthly laptop data budget is a visual alert, not a traffic limit.
- App controls: per-executable network blocks, upload-rate caps, daily allowances with alert/block actions, edit/pause/remove rules, manual quota reset, and a local activity log. Guarded individual-process termination verifies both executable path and process start time.
- A separate daily laptop budget with update/reset controls and automatic local-midnight renewal; resets retain raw usage history. Light and dark themes are saved per browser.
- Optional ZLT E200 router login: available model, firmware, temperature, cellular network type, whole-router monthly traffic, and connected devices with band, signal and link speed. These are separate from laptop totals. No per-device usage is inferred from the router.

The website's assets are entirely local. The interface adapts to small windows; the server is deliberately bound to this laptop's loopback addresses.

## Open on an actual phone

1. Connect the laptop and phone to the same trusted Wi-Fi.
2. Run **Start Mobile Monitor.cmd** on the laptop and approve Windows elevation. This enables an authenticated LAN listener and a Windows firewall rule scoped to the current adapter, current laptop IP, port 5099 and local subnet. It does not change the router.
3. On the laptop, open **Settings → Phone view → Copy phone link**. Open that full private pairing link in the phone browser. `localhost` on a phone refers to the phone, not the laptop.
4. The phone shows this laptop's programs, traffic, history and connected-router status. It can also end a verified app process through **Applications → End process**, with confirmation. It does not measure the phone's own data use. Router login, network limits, startup changes and stopping the service stay on the laptop.

The link changes on restart. Keep it private: its holder can end nonprotected app processes. This is HTTP on a trusted local network, not an internet-facing service. Pairing keys remain in server memory and the phone's session storage. A router with wireless client isolation may block phone-to-laptop connections even on the same SSID; do not change router settings to test this app. Close the shared monitor and run **Start Monitor.cmd** to return to laptop-only access and remove the sharing firewall rule. If you want laptop-only access at future sign-ins too, re-enable automatic startup while running in that mode.

Browser mobile-layout checks use a 390 × 844 viewport. That verifies layout and controls, not actual phone hardware or Wi-Fi reachability. Testing a real phone requires opening the pairing link as described above.

## Check real TCP/UDP program traffic

Keep the monitor running as administrator. Open Applications and sort by total usage. To test TCP, download a small known file with a browser or `curl.exe`; that executable's downloaded bytes should increase. For a repeatable test, run this in a second PowerShell window:

```powershell
python tests/traffic_check.py
```

The script downloads Microsoft's TraceEvent package (about 5.5 MB) over HTTPS/TCP, sends a harmless DNS query to the default IPv4 gateway over UDP, then checks the receiving/sending program's usage deltas. Totals are grouped by executable, so other running instances of Python can also contribute. It reports actual measured bytes and requires administrator capture. It does not upload local files. Kernel traffic includes overhead, so it need not exactly equal the file length. Loopback transfers are intentionally excluded; testing against a server on this same laptop will not increase app usage.

## Using application controls

Open **App controls → Add app rule**, or select a program under Applications and choose **Set limits & access**. Daily app allowances count download + upload in decimal MB. Choose **Alert only** or **Block network access** at the quota. The upload cap is decimal KB/s; 0 disables that cap. Windows QoS controls outgoing traffic only—no download-speed cap or traffic-shaping driver is installed.

**Reset today** starts that quota's allowance again from the current measured count; it does not delete history or lift an independent manual block. Quotas renew at local midnight. The laptop-wide daily budget is alert-only. **Pause rule** removes that rule's enforcement while retaining its settings; **Delete rule** removes it. **Restore all app access** disables all Flow rules and removes only their Windows firewall/QoS controls.

Rules are enforced approximately every five seconds while Flow runs. A fast transfer may overshoot its quota. Normal shutdown removes enforcement; saved enabled rules return next time Flow starts. **If the monitor crashes, a Windows block can remain active. Run Restore Access.cmd to remove Flow controls and disable saved rules.** Other Windows firewall rules are left untouched. Do not use Flow as an unattended billing guarantee.

Every application row now has visible **Limits** and **End process** actions. End process opens the running PID list with RAM and cumulative CPU time. Choose one verified PID and confirm termination; unsaved work can be lost. Windows components, critical processes and the monitor executable are protected. Paired phones can perform this one action using their private pairing key; all other Windows/network mutations remain local desktop administrator actions.

Use **Find app** or **Ctrl+K** for quick search, and filter the application list by current connections or active transfers. The Actions column stays pinned to the right of horizontally scrolling tables. Real telemetry and process controls retain their behavior across the OpenDesign/Inspo-based visual update; see `docs/design-sources.md` for references and attribution.

The Windows integration test uses a separate `.tools/control-probe` executable, never your regular applications: `python tests/control_smoke.py`. It verifies blocking, QoS policy changes, quota enforcement/reset, process identity checks, termination and idempotent cleanup. Upload policy installation is tested; a throughput benchmark on a second physical machine is not included.

Ideas and native behavior were checked against [NetLimiter rules](https://netlimiter.com/docs/basic-concepts/rules), [NetLimiter quotas](https://netlimiter.com/docs/basic-concepts/quota-rule) and [Microsoft QoS policies](https://learn.microsoft.com/powershell/module/netqos/new-netqospolicy). UI improvements use the installed UI/UX Pro Max skill: legible type, contrast, touch targets, stable live controls, and explicit destructive-action feedback. Segoe UI remains local; no external fonts or analytics are loaded.

## Measurement boundaries

**History begins when this app first runs.** It does not import the usage Windows recorded before installation and cannot record while stopped or while the laptop sleeps. Session counters reset on restart; local history survives. SQLite saves measurements about every two seconds in `data/usage.db`. Recorded usage is retained until the local data is removed. Keep that directory to retain your history. A failed database write is retained in memory and retried; closing during a persistent storage failure loses those unsaved measurements.

Open **History** for daily totals, download/upload charts, averages per recorded day, comparisons with the preceding period, and searchable application rankings. Choose Today, 7 days, This month, 30 days, or a custom inclusive range of up to 366 days. Select a date under Daily records to inspect that day's applications. CSV export follows the selected date range.

Expand **Storage details** to see the local database file, allocated size, saved dates, and write status. Sample markers distinguish idle collection from dates without saved samples. Older records retain their usage totals, and show “Saved usage” when sample markers are unavailable. Comparisons can include partially collected days. Application percentages use application counters as their denominator.

The header menu button shows or hides navigation on desktop and phone. Desktop menu visibility is remembered in that browser. The sidebar close button hides the menu, and the phone's More button opens it.

Program bytes come from Windows ETW TCP/IP and UDP/IP send/receive events, including IPv6 and browser QUIC traffic. Loopback addresses are excluded. Raw packet contents, URLs, browser history and messages are never captured. This measures applications, not individual browser tabs/websites. Short-lived processes retain their ETW image name; protected processes may lack a full executable path. Kernel-reported dropped events appear as a warning.

Laptop totals come from physical network adapters and include their network overhead and LAN traffic. App counters measure a different network layer. They can differ from adapter totals, particularly with VPNs, virtual machines, offloading and local services. Do not sum physical and virtual adapter counters together. These are monitoring measurements, not an ISP billing guarantee. Wi-Fi link speed is the negotiated connection capacity, not current traffic speed.

## Router

Connect on the Router page using your router account. The app communicates only with `http://192.168.8.1/cgi-bin/http.cgi`; only known status reads and login/logout commands are allowed. Credentials are used for the login challenge and are never written to disk. The session lasts in memory until logout, expiry or shutdown. If the router has another active session, log out of its admin page first. Failed authentication is never retried automatically; repeated failed logins can lock the router account.

The router's previously observed `flow=0` counters and hidden usage column do not establish that accounting can be enabled or is impossible on every firmware. This app reports the status fields the account actually exposes and makes no router configuration changes.

## Development and verification

```powershell
& .tools/dotnet/dotnet.exe build Flow.csproj -c Release
& .tools/dotnet/dotnet.exe bin/Release/net10.0-windows/Flow.dll --self-test
node --check wwwroot/app.js
python tests/smoke_test.py # with the server running
& .tools/dotnet/dotnet.exe run --project Flow.csproj
```

The self-tests cover counter resets, IPv4/IPv6 loopback filtering, CSV injection/escaping, process-ID reuse, persistent aggregation, date ranges, invalid budgets, database write-failure recovery, native connection-table decoding and router JSON framing. No router credentials or test traffic are bundled.

`tests/browser-smoke.js` is a Playwright page function for this laptop. It verifies live program-button stability, app details, history, Wi-Fi data and mobile layout. Tested at 1440 px and 390 px. Screenshots are in `docs/`.

The server rejects unexpected Host/Origin values and requires a same-origin custom header for changes. Usage history contains executable paths and activity; it stays in the local database. The application is a local personal tool, with no cloud service or remote account.

Technical references: [Microsoft ETW network events](https://learn.microsoft.com/windows/win32/etw/tcpip), [Microsoft TraceEvent](https://github.com/microsoft/perfview/blob/main/documentation/TraceEvent/TraceEventProgrammersGuide.md), [Windows interface flags](https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_if_row2).
