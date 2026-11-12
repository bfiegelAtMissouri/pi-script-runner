# Installation

The runner server is installed once on each Raspberry Pi. Nothing is installed
on the computers or phones that use it; they only need a web browser. (The
Script Runner app's SSH mode needs no installation on the Pi at all; see
[app.md](app.md).)

## Requirements

- A Raspberry Pi running Raspberry Pi OS (or another Linux with systemd)
- Python 3.7 or newer (the server uses only the Python standard library)
- SSH access to the Pi with an account that can use `sudo`
- A network on which the browser can reach the Pi on the chosen port (default 8000)

## Install

The steps below copy the `runner` folder to the Pi and register it as a service
that starts when the Pi boots. Run them from a clone of this repository.

**Windows (PowerShell)**

```powershell
scp -r .\runner pi@<IP>:~/pi-script-runner
ssh -t pi@<IP> "cd ~/pi-script-runner && sudo python3 install_service.py <PIN>"
```

**macOS or Linux**

```bash
scp -r ./runner pi@<IP>:~/pi-script-runner
ssh -t pi@<IP> "cd ~/pi-script-runner && sudo python3 install_service.py <PIN>"
```

Replace `<IP>` with the Pi's IP address and `<PIN>` with the PIN users will
enter on the page (letters and digits only). `sudo` asks for the Pi account's
password once.

Then open `http://<IP>:8000` in a browser.

### Installer options

```text
sudo python3 install_service.py <PIN> [--port 8000] [--max-seconds 300] [--max-upload-mb 20] [--python PATH]
python3 install_service.py <PIN> --print     # show the service file without installing
```

| Option | Default | Meaning |
| :--- | :--- | :--- |
| `<PIN>` | (required) | PIN users enter on the page |
| `--port` | 8000 | TCP port the page is served on |
| `--max-upload-mb` | 20 | Largest upload per run, all files combined, in megabytes |
| `--max-seconds` | 300 | Default time limit per run, in seconds (0 = no limit); the app and page can set their own per run |
| `--python` | `/usr/bin/python3` | Python that runs the server and the scripts; use a virtual environment's `bin/python3` when the robot libraries are installed in one |

The service runs as the user who ran `sudo` (normally `pi`), so scripts can use
Python packages installed for that user, such as packages in `~/.local`.

## Manage the service

```bash
systemctl status pi-script-runner          # is it running?
sudo systemctl restart pi-script-runner    # restart it
journalctl -u pi-script-runner -n 50       # recent log lines
```

## Change the PIN, port, or time limit

Run the installer again with the new values. It replaces the service file and
restarts the service.

## Update to a newer version

Copy the `runner` folder again (the same `scp` command), then restart the service:

```bash
ssh -t pi@<IP> "sudo systemctl restart pi-script-runner"
```

## Uninstall

```bash
sudo systemctl disable --now pi-script-runner
sudo rm /etc/systemd/system/pi-script-runner.service
rm -r ~/pi-script-runner ~/script_runner_runs
```

## Troubleshooting

| Symptom | Likely cause and fix |
| :--- | :--- |
| The page does not load | Check that the service is running (`systemctl status`). If SSH to the Pi works but the page does not, the network may block the port; try another `--port`. |
| "Wrong PIN" | The PIN is case-sensitive. Re-run the installer to set a new one. |
| A script cannot import a package | Install the package for the account the service runs as (normally `pi`), for example `pip3 install --user <package>`. |
| `ModuleNotFoundError: runner_io` when running a script without the page | `runner_io.py` is found automatically only when the script runs through the page. Copy it next to the script to run it another way. |
| "A script is already running" | Only one script runs at a time. Press Stop on the page that started it, or wait for the time limit. |
