"""
Drive a Sphero RVR in a square, with spoken updates and a safe Stop.

Requires the Sphero RVR SDK on the Pi. Pressing Stop (or Esc) on the page
raises KeyboardInterrupt here, so the finally: block always stops the motors.
"""
import sys
import time

sys.path.append('/home/pi/sphero-sdk-raspberrypi-python')

from sphero_sdk import SpheroRvrObserver   # noqa: E402

from runner_io import say                   # noqa: E402

DEGREES_PER_SIDE = 90
SIDES = 4
DRIVE_UPDATE_SECONDS = 0.1     # how often the drive command is repeated
DRIVE_FORWARD = 0              # drive flags: 0 means forward

rvr = SpheroRvrObserver()


def drive_for_seconds(speed, heading, seconds):
    """
    Purpose: drive straight at a heading for a set time, then stop
    Inputs:  speed (0-255), heading (degrees), seconds
    Returns: nothing
    """
    end_time = time.monotonic() + seconds
    while time.monotonic() < end_time:
        rvr.drive_with_heading(speed, heading % 360, DRIVE_FORWARD)
        time.sleep(DRIVE_UPDATE_SECONDS)
    rvr.drive_with_heading(0, heading % 360, DRIVE_FORWARD)


def drive_square(speed, heading, seconds):
    """
    Purpose: drive the robot in a square
    Inputs:  speed (0-255), heading (degrees), seconds per side
    Returns: nothing
    """
    for side in range(SIDES):
        say("Side %d" % (side + 1))
        drive_for_seconds(speed, heading + side * DEGREES_PER_SIDE, seconds)


if '__main__' == __name__:
    try:
        speed = int(input("Speed (0-255)? "))
        rvr.wake()
        time.sleep(2)
        drive_square(speed, 0, 2)
        say("Square complete")
    except KeyboardInterrupt:
        say("Stopped")
    finally:
        rvr.raw_motors(0, 0, 0, 0)          # motors off, whatever happened
        time.sleep(0.5)
        rvr.close()
