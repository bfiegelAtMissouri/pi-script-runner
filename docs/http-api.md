# HTTP API

The browser page uses the HTTP API below. Other clients (for example a desktop
or mobile app) can use the same API.

## Authentication

Every endpoint except `GET /` and `GET /api/status` requires the PIN, sent
either as an `X-PIN` request header or as a `pin` query parameter. A wrong PIN
returns `403`.

## Endpoints

### `GET /`

The browser page (`runner/index.html`).

### `GET /api/status`

```json
{"busy": false, "pin_required": true}
```

### `POST /api/run`

Uploads files and starts a script. Body:

```json
{
  "files": [
    {"name": "square.py", "data": "<base64>"},
    {"name": "beep.wav",  "data": "<base64>"}
  ],
  "main": "square.py",
  "max_seconds": 600
}
```

`max_seconds` is optional: the run's time limit in seconds, from 0 (no limit) to 86400. Without it, the run uses
the server's `--max-seconds` default.

| Response | Meaning |
| :--- | :--- |
| `200 {"run": "<run id>"}` | Started |
| `400` | `main` is missing or not one of the uploaded `.py` files |
| `409` | Another script is already running |
| `413` | Upload larger than 20 MB |

File names are reduced to their base name; folders are not preserved. Each run
replaces the previous run's folder.

### `GET /api/events?pin=<PIN>&run=<run id>&since=<seq>`

A [Server-Sent Events](https://html.spec.whatwg.org/multipage/server-sent-events.html)
stream of the run's events, starting at sequence number `since`. Clients that
reconnect pass the next sequence number they need, so no output is lost or
repeated. If `run` does not match the current run, the stream starts at 0.

Each event is a `data:` line containing JSON. Every event except `hello` has a
`seq` field.

| `type` | Fields | Meaning |
| :--- | :--- | :--- |
| `hello` | `run` | First event on every connection; the current run id |
| `status` | `state: "running"`, `file`, `max_seconds` | The script started |
| `out` | `text` | Output from the script (standard output and standard error). Not split on line boundaries; `input()` prompts arrive without a newline. |
| `echo` | `text` | Input sent with `/api/input`, logged before it is delivered |
| `notice` | `text` | A message from the runner (time limit, escalation, output limit) |
| `status` | `state: "exited"`, `code`, `reason` | The script ended. `reason` is `"finished"` or `"stopped"`. The stream closes after this event. |

The stream sends a comment line (`: keepalive`) every 15 seconds while idle.

Output lines beginning with `@@runner ` are `runner_io` messages; see
[runner-io.md](runner-io.md). Clients are responsible for recognizing them.

### `POST /api/input`

Sends one line to the script's standard input. Body: `{"text": "30"}`.
A newline is added. Returns `409` if no script is running.

### `POST /api/stop`

Stops the current script: Ctrl+C (`SIGINT`) first, then `SIGTERM` after
3 seconds, then `SIGKILL` after 3 more. Signals go to the script's whole
process group. Returns `409` if no script is running.

### `GET /api/media?pin=<PIN>&name=<file name>`

Returns a file for `play()` and `show()`: `name` is relative to the current
run's folder, or an absolute path in `/tmp`. Returns `404` for files that do
not exist or are outside both. Links are followed before the check, so a link
in `/tmp` to a file elsewhere is refused.
