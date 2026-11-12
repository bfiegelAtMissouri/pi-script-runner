# Pi Script Runner

Run Python scripts on a Raspberry Pi from a web browser or an app, with live
output, keyboard input, a working Stop button, and speech, sound, and images
played on the user's own computer.

Two ways to use it:

- **Browser page:** install the small runner server on the Pi once; users open
  `http://<Pi IP address>:8000`, drop in a `.py` file, and press Run. Nothing is
  installed on their computers, and no SSH or Pi password is needed.
- **Script Runner app** for Windows, macOS, and Android: connects either through
  the runner server (HTTP) or directly over SSH, which needs nothing installed on
  the Pi. See [docs/app.md](docs/app.md).

## Features

- **Drag and drop:** upload a script plus the sound and image files it uses.
- **Live output:** output streams to the page as it is printed, including
  `input()` prompts.
- **Keyboard input:** an input box sends lines to the script's `input()` calls.
- **Real Stop:** Stop (or Esc) sends Ctrl+C, so the script's `finally:` block
  runs (for example, to stop a robot's motors). Scripts that ignore Ctrl+C are
  terminated, then forced to quit.
- **Speech, sound, and images:** `say()`, `play()`, and `show()` from the
  `runner_io` helper module are spoken, played, and displayed in the browser.
- **Safety limits:** one script at a time per Pi, a per-run time limit, and a PIN.
- **No dependencies:** the server uses only the Python standard library and runs
  on Python 3.7 or newer.

## Quick start

Install on a Pi (from a clone of this repository; see
[docs/installation.md](docs/installation.md) for details):

```bash
scp -r ./runner pi@<IP>:~/pi-script-runner
ssh -t pi@<IP> "cd ~/pi-script-runner && sudo python3 install_service.py <PIN>"
```

Then open `http://<IP>:8000`, enter the PIN, drop in
[examples/hello_input.py](examples/hello_input.py), and press Run.

## Documentation

| Document | For |
| :--- | :--- |
| [docs/installation.md](docs/installation.md) | Installing, configuring, updating, and troubleshooting on a Pi |
| [docs/user-guide.md](docs/user-guide.md) | People running scripts from the page |
| [docs/runner-io.md](docs/runner-io.md) | The `say()`, `play()`, and `show()` helper functions |
| [docs/http-api.md](docs/http-api.md) | The HTTP API, for other clients |
| [docs/app.md](docs/app.md) | The Windows, macOS, and Android app: installing, using, building, and releasing |

## Repository layout

```text
runner/                  Everything that is copied to the Pi
  script_runner.py         HTTP server that runs scripts
  index.html               The browser page
  runner_io.py             Helper module scripts import
  install_service.py       Registers the server as a systemd service
app/                     The Windows, macOS, and Android app (C#, .NET 10, Avalonia)
docs/                    Documentation
examples/                Example scripts (rvr/: Sphero RVR; sensors/: camera and Qwiic; showcase/: all together;
                         robot_api/: a simple Robot class for student scripts;
                         remote/: functions that run on the laptop, called with remote_execute())
tests/                   Automated tests for the Pi server
```

## How it works

The server runs each script with `python3 -u` in its own process group, in a
fresh folder holding the uploaded files. It streams the script's output to the
browser with Server-Sent Events and writes the browser's input lines to the
script's standard input. Stop sends `SIGINT` (Ctrl+C) to the process group,
followed by `SIGTERM` and `SIGKILL` if the script does not exit.

`runner_io` functions print tagged lines (for example `@@runner say Hello`)
that the page recognizes, hides, and acts on with the browser's text-to-speech,
audio, and image support.

## Development

Run the Pi server's tests (the browser test runs only if Playwright is installed):

```bash
python3 -m unittest discover -s tests -v
```

To include the browser test:

```bash
pip install playwright
python3 -m playwright install chromium
```

The app's tests and builds are described in [docs/app.md](docs/app.md#building-and-releasing).
GitHub Actions runs all the tests on every push and publishes the app on every push to `main`.

## Security

The PIN keeps other people on the same network from running code on the Pi.
It is not strong security: the page uses plain HTTP, and anyone who can run a
script can do anything the service's account can do. Use it only on trusted
networks.

## Authorship

Written in collaboration with Claude, a generative AI model from Anthropic.

## License

[MIT](LICENSE)
