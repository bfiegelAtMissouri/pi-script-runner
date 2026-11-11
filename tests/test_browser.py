"""
Browser tests for runner/index.html, driven by a headless Chromium.

Skipped automatically when Playwright is not installed. To run them:

    pip install playwright
    python3 -m playwright install chromium
    python3 -m unittest discover -s tests -v
"""
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.request

from test_server import PIN, PROMPT_SCRIPT, REPO, SERVER, free_port

try:
    from playwright.sync_api import sync_playwright
except ImportError:                                   # pragma: no cover
    sync_playwright = None


@unittest.skipIf(sync_playwright is None, "Playwright is not installed")
class BrowserTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.home = tempfile.mkdtemp()
        cls.work = tempfile.mkdtemp()
        cls.port = free_port()
        cls.base = "http://127.0.0.1:%d" % cls.port
        cls.server = subprocess.Popen(
            [sys.executable, SERVER, "--pin", PIN, "--port", str(cls.port)],
            env=dict(os.environ, HOME=cls.home),
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            try:
                urllib.request.urlopen(cls.base + "/api/status").close()
                break
            except OSError:
                time.sleep(0.1)
        cls.script = os.path.join(cls.work, "square.py")
        with open(cls.script, "w") as handle:
            handle.write(PROMPT_SCRIPT)
        cls.image = os.path.join(cls.work, "dot.png")
        shutil.copy(os.path.join(REPO, "tests", "data", "dot.png"), cls.image)

    @classmethod
    def tearDownClass(cls):
        cls.server.terminate()
        cls.server.wait(5)
        shutil.rmtree(cls.home, ignore_errors=True)
        shutil.rmtree(cls.work, ignore_errors=True)

    def test_run_input_media_and_stop(self):
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch()
            page = browser.new_page()
            errors = []
            page.on("pageerror", lambda error: errors.append(str(error)))
            page.goto(self.base + "/")
            page.fill("#pin", PIN)
            page.set_input_files("#picker", [self.script, self.image])
            self.assertIn("(runs)", page.inner_text("#files"))

            page.click("#runBtn")
            page.wait_for_function(
                "document.getElementById('output').textContent.includes('Enter speed: ')", timeout=5000)
            page.fill("#inputText", "30")
            page.press("#inputText", "Enter")
            page.wait_for_function(
                "document.getElementById('output').textContent.includes('side 1')", timeout=5000)
            output = page.inner_text("#output")
            self.assertIn("Enter speed: 30", output, "prompt and typed answer share a line")
            self.assertNotIn("@@runner", output, "tag lines are hidden")
            self.assertIn("Starting the square", output)

            page.wait_for_selector("#media img", timeout=5000)
            loaded = page.evaluate(
                "(() => { const i = document.querySelector('#media img');"
                " return i.complete && 0 < i.naturalWidth; })()")
            self.assertTrue(loaded, "image from show() is displayed")

            page.keyboard.press("Escape")
            page.wait_for_function(
                "document.getElementById('output').textContent.includes('Stopped')", timeout=10000)
            self.assertIn("FINALLY: cleanup ran", page.inner_text("#output"))
            self.assertTrue(page.is_enabled("#runBtn"))
            self.assertEqual([], errors)
            browser.close()


if "__main__" == __name__:
    unittest.main()
