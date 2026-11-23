# Getting a Pi ready

Nothing is installed on the Pi for Script Runner. The app connects over SSH, uploads each script with the
`runner_io` helper module, and runs it. When the app is updated, students install the new release, and the robots
need no change.

## What the Pi needs

- Raspberry Pi OS (or another Linux) with SSH turned on (`sudo raspi-config`, **Interface Options**, **SSH**).
- An account with a password that students use to sign in, normally `pi`.
- Python 3.7 or newer as `python3` for that account, with the libraries the scripts import (for example the Sphero
  SDK and the Qwiic libraries). The app runs `python3` the way an SSH login finds it, so a virtual environment put
  first on the account's `PATH` (as the course's robot setup does) is the one scripts use.
- `flock` and `pkill`, which Raspberry Pi OS has.
- A network on which students' computers can reach the Pi on the SSH port (22).

## Removing the old runner service

Earlier versions installed a runner server on each Pi for a browser page and the app's HTTP mode. Neither exists
any more. The service does no harm if it is left running, but it keeps listening on its port (8000) for a page that
no longer exists. To remove it, run these on the Pi (for example in the app's command box, or over SSH):

```bash
sudo systemctl disable --now pi-script-runner
sudo rm /etc/systemd/system/pi-script-runner.service
sudo systemctl daemon-reload
rm -rf ~/pi-script-runner ~/script_runner_runs
```

Leave `~/script_runner_ssh`: it is the folder the app uploads scripts to.

## Troubleshooting

| Symptom | Likely cause and fix |
| :--- | :--- |
| The app cannot connect | Check the IP address and that the computer is on the same network as the Pi. Check that SSH is on: `ssh pi@<IP>` from a terminal should ask for the password. |
| "The Pi did not accept the user name or password" | Check the user name (normally `pi`); the password is case-sensitive. |
| The app refuses to connect because the Pi's SSH key changed | Expected after the Pi's system is reinstalled; press **Forget saved SSH key** and connect again. Otherwise another computer may be using that address. |
| A script cannot import a package | Install the package for the account students sign in as, in the Python that `python3` runs for an SSH login. |
| "Another script is already running on this Pi" | Only one script from the app runs on a Pi at a time, from any computer. Stop it in the app that started it, wait for its time limit, or use **Force stop all Python**. |
| `ModuleNotFoundError: runner_io` when running a script outside the app | The app uploads `runner_io.py` with each script; elsewhere, copy it next to the script or install the `runner_io` package ([runner-io.md](runner-io.md#completion-and-help-in-your-editor)). |
