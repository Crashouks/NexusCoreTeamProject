# Real-time stream telemetry (SignalR)

Pushes the state of a cloud-gaming session (**Starting → Running → Stopped**) and its latency from the
Windows streaming agent, through the API, to the player who started the stream.

```
 Windows agent ──REST──▶ POST /api/cloud/agent/session-status   (starting / running / stopped)
      │
      └──SignalR──▶ /hubs/cloud-stream  AgentJoin, ReportTelemetry(latency, fps, quality) every 2 s
                              │
                     SessionTelemetryService  (latest snapshot per session)
                              │  only to group  stream-p-{sessionId}
                              ▼
 React client ◀──SignalR── SessionStatusChanged / SessionTelemetry      (+ Ping for browser RTT)
```

## Hub: `/hubs/cloud-stream`

| Direction | Method / event | Notes |
|---|---|---|
| client → hub | `PlayerJoin(sessionId)` | `[Authorize]`. Succeeds only if the session is **active and owned by the caller**, so the player group has exactly one audience: the owner. Server replies with the current `SessionTelemetry` snapshot. |
| client → hub | `Ping()` | Client times the invoke → browser↔API round-trip. |
| agent → hub | `AgentJoin(sessionId, serverId, password)` | Verified against the server's agent password; binds the connection to that one session. |
| agent → hub | `ReportTelemetry(sessionId, latencyMs, fps, quality)` | Rejected unless this connection completed `AgentJoin` for the same session. A sample implies `running`. |
| hub → player | `SessionStatusChanged` | `{ session_id, status, reason, agent_online, agent_latency_ms, fps, quality, updated_at }` — sent immediately on every transition. |
| hub → player | `SessionTelemetry` | Same payload — on (re)join, every latency sample, agent connect/disconnect. |

`status` is `starting`, `running` or `stopped`. `reason` (on `stopped`) is one of `launch_failed`,
`capture_unavailable`, `game_closed`, `user_ended`, `time_limit`, `admin_ended`.

Who changes the state:

* `starting` — API, when a real session is created; agent, when it picks up the launch job.
* `running` — agent, once the game window is found and frames are flowing (also implied by any telemetry sample).
* `stopped` — API on user end / time limit / admin force-end; agent on game exit or launch failure.
  A stopped session stays stopped (a late "running" from a dying agent is ignored).

Spectators join `stream-s-{id}`, never `stream-p-{id}`, and are never sent telemetry.

## Connection handling

* **Server** (`CloudStreamHub`): every connect, clean disconnect and *dropped* connection (with the exception) is written to
  the Cloud diagnostics log (Admin → Cloud → Connection log) with connection id, role and session. Keep-alive 15 s /
  timeout 30 s detects dead peers. If the agent's connection drops, `agent_online` becomes `false` and the player is told;
  a stale disconnect from an old connection id cannot mark a reconnected agent offline.
* **React client** (`useRealStream`): reconnects automatically — immediately, then 1 s, 2 s, 5 s, then every 10 s (+jitter) for up
  to 5 minutes; every drop / attempt / reconnect / give-up is logged to the console. After a reconnect it re-joins the
  session (groups are per connection) and gets the fresh snapshot. If the window is exhausted, the overlay offers
  **Try again**, and the browser's `online` event restarts the connection automatically.
* **Agent** (`agent/stream.js`): same retry policy, and it now **re-runs `AgentJoin` after every reconnect** — previously the
  agent silently stopped receiving player input after its first reconnect.

## Networking (Tailscale / LAN — no tunnelling service)

Nothing in the code depends on a public tunnel. All three parties just need a reachable URL:

1. `start-site-network.bat` binds the API on `0.0.0.0:5000`.
2. In `nexuscore/.env` set `PUBLIC_API_URL=http://<host-tailscale-ip>:5000/api` and
   `PUBLIC_WEB_URL=http://<host-tailscale-ip>:5173` (or the `https://…ts.net` URL from `start-site-https`).
   Admin → Cloud → *Download config.json* puts the API URL into the agent's config.
3. If players reach the site through more than one address (IP *and* MagicDNS name), list the extras in
   `CORS_EXTRA_ORIGINS` — the credentialed SignalR negotiate request is CORS-checked.

SignalR uses WebSockets (falling back to SSE / long-polling), so the Vite proxy (`ws: true` for `/hubs`) and Tailscale Serve
need no extra setup.

## Manual test

1. Start API + site; start the agent on the gaming PC; sign in and start a real stream.
2. Overlay shows **Starting game on host…** → **Game running** → video; the HUD shows `Host: Running` and live latency.
3. Stop the API for ~20 s and start it again: the overlay shows *Connection lost — reconnecting*, then recovers on its own;
   the Connection log shows the drop. Check that keyboard/mouse input still reaches the game (agent re-joined).
4. Sign in as a second user, `SpectatorJoin` the same session: no `SessionStatusChanged` / `SessionTelemetry` arrives.
5. End the session: status `stopped (user_ended)`.
