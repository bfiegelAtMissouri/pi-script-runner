"""
End-to-end tests for runner/script_runner.py over HTTP.

Starts a real server on a free port with a temporary home folder, then runs
scripts through the same HTTP API the browser page uses.

    python3 -m unittest discover -s tests -v
"""
import base64
import fcntl
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SERVER = os.path.join(REPO, "runner", "script_runner.py")
PIN = "1234"
MAX_SECONDS = 6

PROMPT_SCRIPT = """\
import time
from runner_io import say, show
try:
    speed = input("Enter speed: ")
    print("speed is", speed)
    say("Starting the square")
    show("dot.png")
    for side in range(100):
        print("side", side)
        time.sleep(0.5)
except KeyboardInterrupt:
    print("User ended by pressing Ctrl+C")
finally:
    print("FINALLY: cleanup ran")
"""

IGNORES_CTRL_C_SCRIPT = """\
import signal, time
signal.signal(signal.SIGINT, signal.SIG_IGN)
while True:
    time.sleep(0.2)
"""


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def encode(name, data):
    if isinstance(data, str):
        data = data.encode("utf-8")
    return {"name": name, "data": base64.b64encode(data).decode("ascii")}


class ServerTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.home = tempfile.mkdtemp()
        cls.port = free_port()
        cls.base = "http://127.0.0.1:%d" % cls.port
        env = dict(os.environ, HOME=cls.home)
        cls.server = subprocess.Popen(
            [sys.executable, SERVER, "--pin", PIN, "--port", str(cls.port),
             "--max-seconds", str(MAX_SECONDS)],
            env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            try:
                urllib.request.urlopen(cls.base + "/api/status").close()
                return
            except OSError:
                time.sleep(0.1)
        raise RuntimeError("server did not start")

    @classmethod
    def tearDownClass(cls):
        cls.server.terminate()
        cls.server.wait(5)
        shutil.rmtree(cls.home, ignore_errors=True)

    # ---- helpers -------------------------------------------------------
    def post(self, path, body, pin=PIN):
        request = urllib.request.Request(
            self.base + path, data=json.dumps(body).encode("utf-8"),
            headers={"Content-Type": "application/json", "X-PIN": pin})
        try:
            with urllib.request.urlopen(request) as response:
                return response.status, json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as error:
            return error.code, json.loads(error.read().decode("utf-8"))

    def get_status(self, path):
        try:
            with urllib.request.urlopen(self.base + path) as response:
                return response.status
        except urllib.error.HTTPError as error:
            return error.code

    def start_run(self, files, main):
        status, body = self.post("/api/run", {"files": files, "main": main})
        self.assertEqual(200, status, body)
        events = []
        thread = threading.Thread(target=self.collect, args=(body["run"], events), daemon=True)
        thread.start()
        return events, thread

    def collect(self, run_id, events):
        url = "%s/api/events?pin=%s&run=%s&since=0" % (self.base, PIN, run_id)
        with urllib.request.urlopen(url) as response:
            for raw in response:
                line = raw.decode("utf-8").strip()
                if line.startswith("data: "):
                    events.append(json.loads(line[6:]))

    @staticmethod
    def started_event(events):
        return next(e for e in events if "status" == e["type"] and "running" == e.get("state"))

    @staticmethod
    def text(events):
        return "".join(e.get("text", "") for e in events if e["type"] in ("out", "echo", "notice"))

    def wait_for(self, events, needle, seconds=5):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if needle in self.text(events):
                return
            time.sleep(0.05)
        self.fail("timed out waiting for %r; output was:\n%s" % (needle, self.text(events)))

    # ---- tests ---------------------------------------------------------
    def test_wrong_pin_is_rejected(self):
        status, _ = self.post("/api/run", {"files": [encode("a.py", "print(1)")], "main": "a.py"}, pin="0000")
        self.assertEqual(403, status)
        self.assertEqual(403, self.get_status("/api/media?pin=0000&name=a.py"))

    def test_missing_main_file_is_rejected(self):
        status, _ = self.post("/api/run", {"files": [encode("a.py", "print(1)")], "main": "b.py"})
        self.assertEqual(400, status)

    def test_full_session(self):
        """Prompt, input, say/show, media, one-run lock, and Stop running finally:."""
        with open(os.path.join(REPO, "tests", "data", "dot.png"), "rb") as handle:
            dot = handle.read()
        events, thread = self.start_run(
            [encode("square.py", PROMPT_SCRIPT), encode("dot.png", dot)], "square.py")

        self.wait_for(events, "Enter speed: ")
        status, _ = self.post("/api/run", {"files": [encode("a.py", "print(1)")], "main": "a.py"})
        self.assertEqual(409, status, "a second run must be refused while one is active")

        self.assertEqual(200, self.post("/api/input", {"text": "30"})[0])
        self.wait_for(events, "speed is 30")
        output = self.text(events)
        self.assertLess(output.index("30\n"), output.index("speed is 30"),
                        "the typed input must be logged before the script's reply")
        self.wait_for(events, "@@runner say Starting the square")
        self.wait_for(events, "@@runner show dot.png")

        self.assertEqual(200, self.get_status("/api/media?pin=%s&name=dot.png" % PIN))
        self.assertEqual(404, self.get_status("/api/media?pin=%s&name=../../etc/passwd" % PIN))
        self.assertEqual(404, self.get_status("/api/media?pin=%s&name=/etc/passwd" % PIN))

        # Files in /tmp can be served too, but not through a link in /tmp that points elsewhere.
        with tempfile.TemporaryDirectory(dir="/tmp") as tmp:
            photo = os.path.join(tmp, "photo.png")
            with open(photo, "wb") as handle:
                handle.write(dot)
            link = os.path.join(tmp, "passwd.png")
            os.symlink("/etc/passwd", link)
            self.assertEqual(200, self.get_status("/api/media?pin=%s&name=%s" % (PIN, photo)))
            self.assertEqual(404, self.get_status("/api/media?pin=%s&name=%s" % (PIN, link)))

        self.assertEqual(200, self.post("/api/stop", {})[0])
        thread.join(10)
        output = self.text(events)
        self.assertIn("User ended by pressing Ctrl+C", output)
        self.assertIn("FINALLY: cleanup ran", output)
        self.assertEqual("exited", events[-1]["state"])
        self.assertEqual("stopped", events[-1]["reason"])

    def test_script_started_another_way_blocks_run(self):
        """While another program (such as SSH mode) holds the shared lock, a run is refused."""
        with open("/tmp/pi-script-runner.lock", "a") as handle:
            fcntl.flock(handle, fcntl.LOCK_EX)
            status, body = self.post("/api/run", {"files": [encode("a.py", "print(1)")], "main": "a.py"})
        self.assertEqual(409, status)
        self.assertIn("another way", body["error"])

    def test_time_limit_and_escalation(self):
        """A script that ignores Ctrl+C is still stopped after the time limit."""
        started = time.monotonic()
        events, thread = self.start_run([encode("stubborn.py", IGNORES_CTRL_C_SCRIPT)], "stubborn.py")
        thread.join(MAX_SECONDS + 15)
        output = self.text(events)
        self.assertIn("Time limit", output)
        self.assertIn("terminating", output)
        self.assertEqual("exited", events[-1]["state"])
        self.assertLess(time.monotonic() - started, MAX_SECONDS + 10)

    def test_time_limit_per_run(self):
        """A run can ask for its own time limit: shorter than the default, none (0), or an invalid value."""
        status, body = self.post("/api/run", {"files": [encode("a.py", "print(1)")], "main": "a.py",
                                              "max_seconds": -1})
        self.assertEqual(400, status, body)

        loop = "import time\nprint('looping', flush=True)\nwhile True:\n    time.sleep(0.2)\n"
        started = time.monotonic()
        status, body = self.post("/api/run", {"files": [encode("loop.py", loop)], "main": "loop.py",
                                              "max_seconds": 1})
        self.assertEqual(200, status, body)
        events = []
        thread = threading.Thread(target=self.collect, args=(body["run"], events), daemon=True)
        thread.start()
        thread.join(10)
        self.assertIn("Time limit of 1 s", self.text(events))
        self.assertEqual(1, self.started_event(events)["max_seconds"])
        self.assertLess(time.monotonic() - started, MAX_SECONDS)       # sooner than the server's default

        status, body = self.post("/api/run", {"files": [encode("loop.py", loop)], "main": "loop.py",
                                              "max_seconds": 0})
        self.assertEqual(200, status, body)
        events = []
        thread = threading.Thread(target=self.collect, args=(body["run"], events), daemon=True)
        thread.start()
        self.wait_for(events, "looping")
        time.sleep(MAX_SECONDS + 1)                                      # past the server's default
        self.assertNotIn("Time limit", self.text(events))
        self.assertEqual(0, self.started_event(events)["max_seconds"])
        self.assertEqual(200, self.post("/api/stop", {})[0])
        thread.join(15)


if "__main__" == __name__:
    unittest.main()
