"""
The app's "Check robot" button runs this on the Pi (over SSH, with the same python3 students' scripts get) and shows
what it prints. It only reads: it changes nothing, and it does not move the robot.
Works with Python 3.7 (32-bit robots) and later.
"""
import glob
import os
import platform
import shutil
import socket
import subprocess
import sys
import threading
import time

OK = "OK  "
PROBLEM = "!!  "
INFO = "    "


def run(command, timeout=10):
    """A command's output, or None if it could not be run."""
    try:
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout,
                                universal_newlines=True)
        return result.stdout
    except (OSError, subprocess.SubprocessError):
        return None


def os_name():
    try:
        with open("/etc/os-release") as file:
            for line in file:
                if line.startswith("PRETTY_NAME="):
                    return line.split("=", 1)[1].strip().strip('"')
    except OSError:
        pass
    return platform.platform()


def i2c_addresses():
    """The addresses that answer on I2C bus 1, or None when the bus cannot be scanned."""
    tool = shutil.which("i2cdetect") or ("/usr/sbin/i2cdetect" if os.path.exists("/usr/sbin/i2cdetect") else None)
    if tool is None or not os.path.exists("/dev/i2c-1"):
        return None
    output = run([tool, "-y", "1"])
    if output is None:
        return None
    found = set()
    for line in output.splitlines()[1:]:
        if ":" not in line:
            continue
        row, cells = line.split(":", 1)
        for column, cell in enumerate(cells.split()):
            if cell not in ("--", "UU") and len(cell) == 2:
                found.add(int(row, 16) + column)
    return found


def camera():
    """(found, description)."""
    for tool in ("rpicam-hello", "libcamera-hello"):
        if shutil.which(tool):
            output = run([tool, "--list-cameras"]) or ""
            cameras = [line.strip() for line in output.splitlines() if line.strip()[:2] in ("0 ", "1 ")]
            if cameras:
                return True, cameras[0]
            if "No cameras available" in output:
                return False, "no camera found (check the ribbon cable at both ends, with the Pi off)"
    if shutil.which("vcgencmd"):
        output = (run(["vcgencmd", "get_camera"]) or "").strip()
        if "detected=1" in output:
            return True, "found (" + output + ")"
        if output:
            return False, "no camera found (" + output + "); check the ribbon cable at both ends, with the Pi off"
    return None, "could not check (no camera tool on this robot)"


def rvr_battery():
    """(percent or None, message)."""
    sys.path.append(os.path.expanduser("~/sphero-sdk-raspberrypi-python"))
    try:
        from sphero_sdk import SpheroRvrObserver
    except Exception as error:          # noqa: BLE001 - any import failure means the same thing here
        return None, "the Sphero SDK could not be loaded (%s)" % error
    answer = {}
    done = threading.Event()

    def on_percentage(response):
        answer["percent"] = response.get("percentage")
        done.set()

    rvr = None
    try:
        rvr = SpheroRvrObserver()
        rvr.wake()
        time.sleep(1)
        rvr.get_battery_percentage(handler=on_percentage)
        done.wait(timeout=4)
    except Exception as error:          # noqa: BLE001 - report whatever went wrong
        return None, "could not ask the RVR (%s)" % error
    finally:
        if rvr is not None:
            try:
                rvr.close()
            except Exception:           # noqa: BLE001
                pass
    if "percent" not in answer:
        return None, "the RVR did not answer: is it switched on, and its cable to the Pi connected?"
    return answer["percent"], "%s%%" % answer["percent"]


def other_python():
    """Python programs of this user that are running, other than this check."""
    output = run(["pgrep", "-a", "-u", str(os.getuid()), "-f", "^python3( |$)"]) or ""
    me = str(os.getpid())
    return [line for line in output.splitlines() if line.strip() and not line.startswith(me + " ")]


def main():
    name = socket.gethostname()
    machine = platform.machine()
    bits = "64-bit" if machine in ("aarch64", "arm64", "x86_64") else "32-bit"
    print("Robot check: %s" % name)
    print(INFO + "System:   %s, %s (%s)" % (bits, os_name(), machine))
    print(INFO + "Python:   %s, %s" % (platform.python_version(), sys.executable))
    if "raspberrypi" == name:
        print(INFO + "Name:     raspberrypi is the default name. If several robots have it, "
                     "connect by IP address to be sure which one you reach.")

    free = shutil.disk_usage(os.path.expanduser("~")).free / 1e9
    print((OK if 1 <= free else PROBLEM) + "Storage:  %.1f GB free" % free)

    addresses = i2c_addresses()
    if addresses is None:
        print(PROBLEM + "I2C:      could not scan the bus (is I2C turned on?)")
    else:
        print((OK if 0x29 in addresses else PROBLEM) + "Distance sensor (I2C 0x29): " +
              ("answers" if 0x29 in addresses else "does not answer; check its Qwiic cable at both ends"))
        oled = sorted(address for address in (0x3C, 0x3D) if address in addresses)
        print((OK if oled else PROBLEM) + "OLED display (I2C 0x3C or 0x3D): " +
              ("answers at 0x%X" % oled[0] if oled else "does not answer; check its Qwiic cable at both ends"))
        others = sorted(addresses - {0x29, 0x3C, 0x3D})
        if others:
            print(INFO + "Other I2C devices: " + ", ".join("0x%02X" % address for address in others))

    found, description = camera()
    print((INFO if found is None else OK if found else PROBLEM) + "Camera:   " + description)

    running = other_python()
    if running:
        print(PROBLEM + "Other Python programs are running (an earlier run may still be going; "
                        "\"Force stop all Python\" stops them):")
        for line in running:
            print(INFO + "  " + line)
        print(INFO + "RVR battery: not checked while another program may be using the RVR")
    else:
        percent, message = rvr_battery()
        if percent is None:
            print(PROBLEM + "RVR battery: " + message)
        else:
            print((OK if 20 <= percent else PROBLEM) + "RVR battery: " + message +
                  ("" if 20 <= percent else " (low: charge it; a low battery makes the RVR act strangely)"))
    sys.stdout.flush()


main()
