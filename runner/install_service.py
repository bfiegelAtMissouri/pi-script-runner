#!/usr/bin/env python3
"""
Install Pi Script Runner as a service that starts when the Pi boots.

Usage (on the Pi, from the runner folder):
    sudo python3 install_service.py <PIN> [--port 8000] [--max-seconds 300] [--python PATH]
    python3 install_service.py <PIN> --print      (show the service file only)

Written in Python rather than shell so it still works if the files were
copied from Windows with Windows line endings.
"""
import argparse
import os
import pwd
import subprocess
import sys

SERVICE_PATH = "/etc/systemd/system/pi-script-runner.service"

TEMPLATE = """[Unit]
Description=Pi Script Runner (run Python scripts from a browser)
After=network-online.target
Wants=network-online.target

[Service]
User={user}
WorkingDirectory={folder}
ExecStart={python} {folder}/script_runner.py --pin {pin} --port {port} --max-seconds {max_seconds}
Restart=on-failure
RestartSec=3

[Install]
WantedBy=multi-user.target
"""


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("pin", help="PIN students enter on the page")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--max-seconds", type=int, default=300)
    parser.add_argument("--python", default="/usr/bin/python3",
                        help="Python that runs the server and the scripts, for example a virtual environment's "
                             "bin/python3 (default /usr/bin/python3)")
    parser.add_argument("--print", action="store_true", help="only print the service file")
    args = parser.parse_args()

    if not args.pin.isalnum():
        sys.exit("Use letters and digits only for the PIN.")

    folder = os.path.dirname(os.path.abspath(__file__))
    user = os.environ.get("SUDO_USER") or pwd.getpwuid(os.getuid()).pw_name
    if not os.path.isfile(args.python):
        sys.exit("No Python at %s." % args.python)
    service = TEMPLATE.format(user=user, folder=folder, python=args.python,
                              pin=args.pin, port=args.port, max_seconds=args.max_seconds)
    if args.print:
        print(service)
        return
    if 0 != os.geteuid():
        sys.exit("Run with sudo:  sudo python3 install_service.py %s" % args.pin)

    with open(SERVICE_PATH, "w") as handle:
        handle.write(service)
    for command in (["systemctl", "daemon-reload"],
                    ["systemctl", "enable", "pi-script-runner"],
                    ["systemctl", "restart", "pi-script-runner"]):
        subprocess.check_call(command)
    print("Installed. Pi Script Runner runs as user '%s' on port %d." % (user, args.port))
    print("Students open:  http://<this Pi's IP address>:%d" % args.port)
    print("Check it with:  systemctl status pi-script-runner")


if "__main__" == __name__:
    main()
