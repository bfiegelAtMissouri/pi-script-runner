"""
RVR position: the RVR keeps track of where it is, and this example shows how to read that and how to use it.

The RVR's "locator" adds up its wheel and motion-sensor movements into a position, in meters from where it was last
reset: X to the right and Y forward, as the robot faced at the reset. Its IMU reports the heading (yaw). This example:

  1. Resets the position to (0, 0) and starts reading position and heading 10 times a second.
  2. Drives a square by giving the RVR target positions (drive_to_position_si): it drives itself to each corner.
  3. Prints where it thinks it is at each corner, and how far it is from where it was told to go.
  4. Draws the path it reported as rvr_position.png (shown in the app, and saved in /tmp).

It also asks the RVR to report a motor stall: when the robot runs into something it cannot push, the motor stops
turning and the RVR says so. The example then stops the square and marks the spot on the picture with a red X. Try
it by putting a heavy box in the robot's way. (If the treads slip instead of stalling, the RVR does not notice: it
works out its position from its wheels, so it thinks it is still moving. Compare the picture with the floor.)

Put a mark on the floor where the robot starts. When it finishes, compare where it really stopped with where it
thinks it is: the difference is drift. It grows with distance and with turning, so the position is good for meters,
not for a long run around a room.

Run it:
  From the app:  press Run.
  Over SSH:      type rvr_position.py | ssh pi@<robot> "python3 -"

Needs: Sphero RVR or RVR+ with its Raspberry Pi (Sphero RVR SDK), and matplotlib. Works on the 32-bit and the 64-bit
robots. Clear about a meter of floor in front of and to the right of the robot.
"""
import math
import sys
import threading
import time

import matplotlib
matplotlib.use("Agg")                   # draws to a file; the robot has no screen
import matplotlib.pyplot as plt         # noqa: E402

sys.path.append('/home/pi/sphero-sdk-raspberrypi-python')

from sphero_sdk import RvrStreamingServices     # noqa: E402
from sphero_sdk import SpheroRvrObserver        # noqa: E402

SIDE_M = 0.5                    # the square's side, in meters
SPEED_MPS = 0.25                # top speed between corners, in meters per second
SENSOR_INTERVAL_MS = 100        # how often the RVR reports its position and heading
PICTURE_FILE = "/tmp/rvr_position.png"

# A square to the right: forward, right, back, and home, as (x, y, facing). Each corner ends facing the next side.
CORNERS = [(0.0, SIDE_M, -90), (SIDE_M, SIDE_M, 180), (SIDE_M, 0.0, 90), (0.0, 0.0, 0)]

rvr = SpheroRvrObserver()

# The latest values from the RVR, updated by the handlers below while the program runs.
position = {"x": 0.0, "y": 0.0}
yaw = {"degrees": 0.0}
track = []                      # every reported position, for the picture
arrived = threading.Event()     # set when the RVR reports it reached a target position
stalled = threading.Event()     # set when the RVR reports that a motor stalled
stalled_at = []                 # where it was then


class Stalled(Exception):
    """A motor stalled: the robot ran into something it cannot push."""


def on_locator(data):
    """Called by the SDK 10 times a second with the position, in meters."""
    position["x"] = data["Locator"]["X"]
    position["y"] = data["Locator"]["Y"]
    track.append((position["x"], position["y"]))


def on_imu(data):
    """Called by the SDK with the heading: degrees, counterclockwise from the start (left turns are positive)."""
    yaw["degrees"] = data["IMU"]["Yaw"]


def on_arrived(response):
    """Called by the SDK when a drive_to_position_si move finishes."""
    arrived.set()


def on_stall(data):
    """Called by the SDK when a motor stalls (is_triggered True) or recovers (False)."""
    if data["is_triggered"]:
        stalled.set()


def drive_to(x, y, facing):
    """
    Drives to (x, y) in meters and ends up facing a direction: degrees counterclockwise from the start, the way the
    RVR counts (0 = the start direction, 90 = left, -90 = right).
    """
    arrived.clear()
    rvr.drive_to_position_si(yaw_angle=facing, x=x, y=y, linear_speed=SPEED_MPS, flags=0)
    distance = math.hypot(x - position["x"], y - position["y"])
    deadline = time.monotonic() + 5 + 3 * distance / SPEED_MPS
    while not arrived.is_set():
        if stalled.is_set():
            rvr.raw_motors(0, 0, 0, 0)
            stalled_at.append((position["x"], position["y"]))
            raise Stalled()
        if deadline < time.monotonic():
            print("  (no arrival report; carrying on)")
            break
        time.sleep(0.05)
    time.sleep(0.3)                     # one more position report after stopping
    missed_cm = 100 * math.hypot(x - position["x"], y - position["y"])
    print("Target (%5.2f, %5.2f)   reported (%5.2f, %5.2f) m   heading %6.1f degrees   %4.1f cm from the target"
          % (x, y, position["x"], position["y"], yaw["degrees"], missed_cm))


def draw(corners):
    figure, axes = plt.subplots(figsize=(5.5, 5.5))
    target_x, target_y = zip(*([(0.0, 0.0)] + corners))
    axes.plot(target_x, target_y, "--", color="gray", label="told to drive")
    path_x, path_y = zip(*track)
    axes.plot(path_x, path_y, color="tab:blue", label="reported by the RVR")
    axes.plot(path_x[-1], path_y[-1], "o", color="tab:blue")
    for x, y in stalled_at:
        axes.plot(x, y, "x", color="red", markersize=12, markeredgewidth=3, label="motor stalled")
    axes.annotate("start", (0, 0), textcoords="offset points", xytext=(6, -12))
    axes.set_aspect("equal")
    axes.margins(0.15)
    axes.grid(True, alpha=0.3)
    axes.set_xlabel("X, meters (right of the start)")
    axes.set_ylabel("Y, meters (ahead of the start)")
    axes.set_title("Where the RVR says it went")
    axes.legend(loc="upper right", fontsize=8)
    figure.tight_layout()
    figure.savefig(PICTURE_FILE, dpi=120)
    plt.close(figure)


try:
    rvr.wake()
    time.sleep(2)
    rvr.reset_yaw()                     # the direction the robot faces now is heading 0
    time.sleep(0.1)
    rvr.reset_locator_x_and_y()         # and where it is now is (0, 0)
    time.sleep(0.1)

    rvr.on_xy_position_drive_result_notify(handler=on_arrived)
    rvr.enable_motor_stall_notify(is_enabled=True)
    rvr.on_motor_stall_notify(handler=on_stall)
    rvr.sensor_control.add_sensor_data_handler(service=RvrStreamingServices.locator, handler=on_locator)
    rvr.sensor_control.add_sensor_data_handler(service=RvrStreamingServices.imu, handler=on_imu)
    rvr.sensor_control.start(interval=SENSOR_INTERVAL_MS)
    time.sleep(0.5)
    print("Start: (%.2f, %.2f) m, heading %.1f degrees" % (position["x"], position["y"], yaw["degrees"]))

    for x, y, facing in CORNERS:
        drive_to(x, y, facing)
    print("Back at the start, the RVR thinks it is %.1f cm from where it began."
          % (100 * math.hypot(position["x"], position["y"])))

except Stalled:
    print("A motor stalled at (%.2f, %.2f) m: the robot ran into something. Square stopped." % stalled_at[-1])
except KeyboardInterrupt:
    print("Stopped by the Stop button (Ctrl+C)")

finally:
    try:
        rvr.raw_motors(0, 0, 0, 0)
        rvr.sensor_control.stop()
    finally:
        rvr.close()

if track:
    draw([(x, y) for x, y, _ in CORNERS])
    print("Saved", PICTURE_FILE)
    try:
        from runner_io import show      # shows the picture in the app when run from there
        show(PICTURE_FILE)
    except ImportError:
        pass
