# Pi Script Runner

Run Python scripts on a Raspberry Pi from the Script Runner app for Windows, macOS, and Android, with live output,
keyboard input, a working Stop button, and speech, sound, images, graphs, and drawings shown on the user's own
computer. The app connects to the Pi over SSH, so nothing is installed on the Pi: when the app is updated, students
install the new release and every robot works with it.

## Features

- **Add files:** a script plus the sound, image, and helper files it uses, or a built-in example.
- **Live output:** output appears as it is printed, including `input()` prompts.
- **Keyboard input:** an input box sends lines to the script's `input()` calls.
- **Real Stop:** Stop (or Esc) sends Ctrl+C, so the script's `finally:` block runs (for example, to stop a robot's
  motors). Scripts that ignore Ctrl+C are terminated, then forced to quit.
- **Speech, sound, images, graphs, and drawings:** `say()`, `play()`, `show()`, `plot()`, and `draw` from the
  `runner_io` helper module are spoken, played, displayed, graphed, and drawn on the user's computer.
- **Help with errors:** a Python error is shown with the line it points to and a hint.
- **Safety limits:** one script at a time per Pi, and a per-run time limit.

## Quick start

1. Download the app for your computer from the repository's **Releases** page (see [docs/app.md](docs/app.md#install)).
2. Enter the Pi's IP address, the user name (normally `pi`), and the account's password.
3. Open **Examples**, choose **Use** beside `hello_input.py`, and press **Run**.

## Documentation

| Document | For |
| :--- | :--- |
| [docs/app.md](docs/app.md) | The app: installing, using, building, and releasing |
| [docs/user-guide.md](docs/user-guide.md) | Students running scripts, and the examples |
| [docs/runner-io.md](docs/runner-io.md) | The `say()`, `play()`, `show()`, `plot()`, and `draw` helper functions |
| [docs/installation.md](docs/installation.md) | Getting a Pi ready (nothing to install), and removing the old runner service |

## Repository layout

```text
runner/                  runner_io.py, the helper module the app uploads with every script, and its package
                         (pyproject.toml) for editor completion on students' computers
app/                     The Windows, macOS, and Android app (C#, .NET 10, Avalonia)
docs/                    Documentation
examples/                Example scripts, built into the app (rvr/: Sphero RVR; sensors/: camera and Qwiic;
                         drawing/: draw; showcase/: all together; robot_api/: a simple Robot class for student
                         scripts; remote/: functions that run on the laptop, called with remote_execute())
tests/                   Automated tests for runner_io
```

## How it works

The app uploads the script, its files, and `runner_io.py` to the Pi with SFTP, then runs the script with
`python3 -u` in a terminal session over SSH, so `input()` works. It shows the script's output as it arrives and
sends typed input to it. Stop sends `SIGINT` (Ctrl+C) to the script, followed by `SIGTERM` and `SIGKILL` if it does
not exit.

`runner_io` functions print tagged lines (for example `@@runner say Hello`) that the app recognizes, hides, and acts
on with the computer's text-to-speech, audio, and image support.

## Development

Run the `runner_io` tests:

```bash
python3 -m unittest discover -s tests -v
```

The app's tests and builds are described in [docs/app.md](docs/app.md#building-and-releasing).
GitHub Actions runs all the tests on every push and publishes the app on every push to `main`.

## Security

The app signs in to the Pi with the account's SSH password, and the connection is encrypted. Anyone who can run a
script can do anything that account can do, so give the password only to the people who should use the robot.

## Authorship

Written in collaboration with Claude, a generative AI model from Anthropic.

## License

[MIT](LICENSE)
