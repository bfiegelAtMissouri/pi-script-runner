#!/usr/bin/env python3
"""
Pi Script Runner: run Python scripts on a Raspberry Pi from a web browser.

Users open http://<Pi IP address>:8000, drop in their .py file (plus any sound or
image files it uses), and press Run. The page shows output live, sends keyboard
input to the script, has a Stop button that delivers Ctrl+C (so `finally:` blocks
run, for example to stop a robot's motors), and speaks/plays/shows anything the
script sends with the runner_io helper module.

Uses only the Python standard library (works on Python 3.7).

Usage:
    python3 script_runner.py --pin 1234 [--port 8000] [--max-seconds 300]
"""
import argparse
import base64
import codecs
import fcntl
import hmac
import json
import os
import shutil
import signal
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

HERE = os.path.dirname(os.path.abspath(__file__))
MAX_TIME_LIMIT_SECONDS = 24 * 60 * 60    # longest time limit a run may ask for
RUN_ROOT = os.path.join(os.path.expanduser("~"), "script_runner_runs")
# play() and show() may also use files here, for scripts that save their photos and sounds in /tmp.
SHARED_MEDIA_DIR = "/tmp"
# Held while a script runs. The desktop and Android app's SSH mode takes the same lock (with flock),
# so a script started over SSH and one started from this page never run at the same time.
# SCRIPT_RUNNER_LOCK_FILE overrides the location (used by automated tests only).
LOCK_FILE = os.environ.get("SCRIPT_RUNNER_LOCK_FILE", "/tmp/pi-script-runner.lock")
INDEX_FILE = os.path.join(HERE, "index.html")

MAX_UPLOAD_BYTES = 20 * 1024 * 1024       # all files in one run, combined
MAX_LOG_CHARS = 2 * 1024 * 1024           # output kept for the current run
STOP_GRACE_SECONDS = 3                    # Ctrl+C, then wait this long before forcing
KEEPALIVE_SECONDS = 15

MEDIA_TYPES = {
    ".wav": "audio/wav", ".mp3": "audio/mpeg", ".ogg": "audio/ogg",
    ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg",
    ".gif": "image/gif", ".bmp": "image/bmp", ".svg": "image/svg+xml",
}


def _prepare_child():
    """Runs in the student's process just before Python starts.

    - Own process group, so Stop reaches any child processes too.
    - Default Ctrl+C handling, so Python raises KeyboardInterrupt even if this
      server was started by something that ignores SIGINT.
    """
    os.setsid()
    signal.signal(signal.SIGINT, signal.SIG_DFL)


def _take_run_lock():
    """Returns an open, locked file, or None if another script holds the lock."""
    try:
        handle = open(LOCK_FILE, "a")
    except OSError:
        return None
    try:
        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        handle.close()
        return None
    return handle


def _release_run_lock(run):
    if run.lock_handle is not None:
        run.lock_handle.close()             # closing the file releases the lock
        run.lock_handle = None


class Run:
    """One execution of a student script, and the event log the page streams."""

    def __init__(self, run_id, run_dir, main_file):
        self.run_id = run_id
        self.run_dir = run_dir
        self.main_file = main_file
        self.events = []                  # list of dicts, index == seq
        self.log_chars = 0
        self.truncated = False
        self.cond = threading.Condition()
        self.proc = None
        self.finished = False
        self.stop_requested = False
        self.lock_handle = None
        self.max_seconds = 0              # time limit for this run; 0 means no limit

    def add_event(self, event):
        with self.cond:
            event["seq"] = len(self.events)
            self.events.append(event)
            self.cond.notify_all()

    def add_output(self, text):
        if not text:
            return
        with self.cond:
            if MAX_LOG_CHARS <= self.log_chars:
                if not self.truncated:
                    self.truncated = True
                    self._append_locked({"type": "notice",
                                         "text": "Output limit reached; further output is not shown."})
                return
            self.log_chars += len(text)
            self._append_locked({"type": "out", "text": text})

    def _append_locked(self, event):
        event["seq"] = len(self.events)
        self.events.append(event)
        self.cond.notify_all()


class Runner:
    """Holds the current run and enforces one script at a time."""

    def __init__(self, max_seconds):
        self.max_seconds = max_seconds
        self.lock = threading.Lock()
        self.current = None
        self.next_id = int(time.time())

    def is_busy(self):
        return self.current is not None and not self.current.finished

    def start(self, files, main_name, max_seconds=None):
        with self.lock:
            if self.is_busy():
                return None, "A script is already running on this Pi. Stop it first."
            lock_handle = _take_run_lock()
            if lock_handle is None:
                return None, "A script started another way (for example over SSH) is running on this Pi."
            self.next_id += 1
            run_id = str(self.next_id)
            run_dir = os.path.join(RUN_ROOT, "current")
            if os.path.isdir(run_dir):
                shutil.rmtree(run_dir, ignore_errors=True)
            os.makedirs(run_dir)
            for name, data in files:
                with open(os.path.join(run_dir, name), "wb") as handle:
                    handle.write(data)
            run = Run(run_id, run_dir, main_name)
            run.lock_handle = lock_handle
            run.max_seconds = self.max_seconds if max_seconds is None else max_seconds
            self.current = run

        env = dict(os.environ)
        env["PYTHONUNBUFFERED"] = "1"
        env["PYTHONIOENCODING"] = "utf-8"
        env["PYTHONPATH"] = HERE + os.pathsep + env.get("PYTHONPATH", "")
        try:
            run.proc = subprocess.Popen(
                [sys.executable, "-u", main_name],
                cwd=run_dir, env=env,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                preexec_fn=_prepare_child,
            )
        except OSError as error:
            run.add_event({"type": "status", "state": "exited", "code": None,
                           "text": "Could not start Python: %s" % error})
            run.finished = True
            _release_run_lock(run)
            return run, None

        run.add_event({"type": "status", "state": "running", "file": main_name,
                       "max_seconds": run.max_seconds})
        threading.Thread(target=self._pump_output, args=(run,), daemon=True).start()
        threading.Thread(target=self._watch_time_limit, args=(run,), daemon=True).start()
        return run, None

    def _pump_output(self, run):
        decoder = codecs.getincrementaldecoder("utf-8")(errors="replace")
        fd = run.proc.stdout.fileno()
        while True:
            chunk = os.read(fd, 4096)
            if not chunk:
                break
            run.add_output(decoder.decode(chunk))
        run.add_output(decoder.decode(b"", final=True))
        code = run.proc.wait()
        reason = "stopped" if run.stop_requested else "finished"
        _release_run_lock(run)
        run.finished = True
        run.add_event({"type": "status", "state": "exited", "code": code, "reason": reason})

    def _watch_time_limit(self, run):
        if 0 == run.max_seconds:
            return                                  # no limit for this run
        deadline = time.monotonic() + run.max_seconds
        while not run.finished:
            if deadline <= time.monotonic():
                run.add_event({"type": "notice",
                               "text": "Time limit of %d s reached; stopping the script." % run.max_seconds})
                self.stop(run)
                return
            time.sleep(0.5)

    def send_input(self, text):
        run = self.current
        if run is None or run.finished or run.proc is None:
            return "No script is running."
        # Log the echo before the script can respond, so the typed text appears first.
        run.add_event({"type": "echo", "text": text + "\n"})
        try:
            run.proc.stdin.write((text + "\n").encode("utf-8"))
            run.proc.stdin.flush()
        except (BrokenPipeError, OSError):
            run.add_event({"type": "notice", "text": "The script is not accepting input."})
            return "The script is not accepting input."
        return None

    def stop(self, run=None):
        run = run or self.current
        if run is None or run.finished or run.proc is None:
            return "No script is running."
        run.stop_requested = True
        threading.Thread(target=self._stop_sequence, args=(run,), daemon=True).start()
        return None

    def _stop_sequence(self, run):
        """Ctrl+C first (runs finally: blocks), then terminate, then kill."""
        for sig, label in ((signal.SIGINT, None),
                           (signal.SIGTERM, "Script did not stop after Ctrl+C; terminating it."),
                           (signal.SIGKILL, "Script still running; forcing it to quit.")):
            if run.finished:
                return
            if label:
                run.add_event({"type": "notice", "text": label})
            try:
                os.killpg(run.proc.pid, sig)
            except ProcessLookupError:
                return
            deadline = time.monotonic() + STOP_GRACE_SECONDS
            while not run.finished and time.monotonic() < deadline:
                time.sleep(0.1)


class Handler(BaseHTTPRequestHandler):
    runner = None
    pin = ""
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass                                     # keep the service log quiet

    # ---- helpers -------------------------------------------------------
    def _query(self):
        return parse_qs(urlparse(self.path).query)

    def _pin_ok(self):
        given = self.headers.get("X-PIN") or (self._query().get("pin") or [""])[0]
        return hmac.compare_digest(given.encode(), self.pin.encode())

    def _send(self, status, body, content_type="application/json"):
        data = body if isinstance(body, bytes) else body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def _json(self, status, obj):
        self._send(status, json.dumps(obj))

    def _read_json(self):
        length = int(self.headers.get("Content-Length") or 0)
        if MAX_UPLOAD_BYTES * 2 < length:
            return None
        return json.loads(self.rfile.read(length).decode("utf-8") or "{}")

    # ---- routes --------------------------------------------------------
    def do_GET(self):
        route = urlparse(self.path).path
        if "/" == route:
            with open(INDEX_FILE, "rb") as handle:
                self._send(200, handle.read(), "text/html; charset=utf-8")
        elif "/api/status" == route:
            self._json(200, {"busy": self.runner.is_busy(), "pin_required": bool(self.pin)})
        elif "/api/events" == route:
            if not self._pin_ok():
                self._json(403, {"error": "Wrong PIN."})
                return
            self._stream_events()
        elif "/api/media" == route:
            if not self._pin_ok():
                self._json(403, {"error": "Wrong PIN."})
                return
            self._serve_media()
        else:
            self._json(404, {"error": "Not found."})

    def do_POST(self):
        route = urlparse(self.path).path
        if not self._pin_ok():
            self._json(403, {"error": "Wrong PIN."})
            return
        try:
            body = self._read_json()
        except ValueError:
            self._json(400, {"error": "Bad request."})
            return
        if body is None:
            self._json(413, {"error": "Upload too large."})
            return

        if "/api/run" == route:
            self._start_run(body)
        elif "/api/input" == route:
            error = self.runner.send_input(str(body.get("text", "")))
            self._json(409 if error else 200, {"error": error} if error else {"ok": True})
        elif "/api/stop" == route:
            error = self.runner.stop()
            self._json(409 if error else 200, {"error": error} if error else {"ok": True})
        else:
            self._json(404, {"error": "Not found."})

    def _start_run(self, body):
        files = []
        total = 0
        for item in body.get("files", []):
            name = os.path.basename(str(item.get("name", "")))
            if not name or name.startswith("."):
                continue
            data = base64.b64decode(item.get("data", ""))
            total += len(data)
            files.append((name, data))
        if MAX_UPLOAD_BYTES < total:
            self._json(413, {"error": "Upload too large."})
            return
        main_name = os.path.basename(str(body.get("main", "")))
        if not main_name.endswith(".py") or main_name not in [name for name, _ in files]:
            self._json(400, {"error": "Include the .py file to run."})
            return
        max_seconds = body.get("max_seconds")
        if max_seconds is not None:
            if not isinstance(max_seconds, int) or isinstance(max_seconds, bool) \
                    or not 0 <= max_seconds <= MAX_TIME_LIMIT_SECONDS:
                self._json(400, {"error": "max_seconds must be a whole number from 0 (no limit) to %d."
                                          % MAX_TIME_LIMIT_SECONDS})
                return
        run, error = self.runner.start(files, main_name, max_seconds)
        if error:
            self._json(409, {"error": error})
        else:
            self._json(200, {"run": run.run_id})

    def _stream_events(self):
        run = self.runner.current
        if run is None:
            self._json(200, {"idle": True})
            return
        query = self._query()
        since = int((query.get("since") or ["0"])[0])
        if (query.get("run") or [""])[0] != run.run_id:
            since = 0
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True
        try:
            self._write_event({"type": "hello", "run": run.run_id})
            while True:
                with run.cond:
                    if len(run.events) <= since:
                        run.cond.wait(KEEPALIVE_SECONDS)
                    pending = run.events[since:]
                if pending:
                    for event in pending:
                        self._write_event(event)
                    since = pending[-1]["seq"] + 1
                    if run.finished and "exited" == pending[-1].get("state"):
                        return
                else:
                    self.wfile.write(b": keepalive\n\n")
                    self.wfile.flush()
                if self.runner.current is not run:
                    return
        except (BrokenPipeError, ConnectionResetError):
            return

    def _write_event(self, event):
        self.wfile.write(("data: %s\n\n" % json.dumps(event)).encode("utf-8"))
        self.wfile.flush()

    def _serve_media(self):
        run = self.runner.current
        name = (self._query().get("name") or [""])[0]
        if run is None or not name:
            self._json(404, {"error": "Not found."})
            return
        path = media_path(run.run_dir, name)
        if path is None:
            self._json(404, {"error": "File not found: %s" % name})
            return
        content_type = MEDIA_TYPES.get(os.path.splitext(path)[1].lower(), "application/octet-stream")
        with open(path, "rb") as handle:
            self._send(200, handle.read(), content_type)


def media_path(run_dir, name):
    """
    Purpose: find the file that play() or show() named
    Inputs:  run_dir - the current run's folder; name - a path relative to it, or an absolute path in /tmp
    Returns: the file's real path, or None if it does not exist or is outside the run folder and /tmp
    """
    path = os.path.realpath(os.path.join(run_dir, name))         # an absolute name replaces run_dir
    for root in (run_dir, SHARED_MEDIA_DIR):
        if path.startswith(os.path.realpath(root) + os.sep) and os.path.isfile(path):
            return path
    return None


def main():
    parser = argparse.ArgumentParser(description="Run Python scripts on this Pi from a browser.")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--pin", default="", help="PIN students must enter (blank = no PIN)")
    parser.add_argument("--max-seconds", type=int, default=300,
                        help="default time limit per run, in seconds (0 = no limit); a run may ask for its own")
    args = parser.parse_args()

    os.makedirs(RUN_ROOT, exist_ok=True)
    Handler.runner = Runner(args.max_seconds)
    Handler.pin = args.pin
    server = ThreadingHTTPServer(("0.0.0.0", args.port), Handler)
    server.daemon_threads = True
    print("Pi Script Runner listening on port %d" % args.port, flush=True)
    server.serve_forever()


if "__main__" == __name__:
    main()
