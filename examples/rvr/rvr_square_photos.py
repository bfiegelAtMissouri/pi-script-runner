"""
Drive a Sphero RVR in a square and take a photo at each corner.

Each photo appears in the app (or browser page) as soon as it is taken, and the
laptop says which corner it is. Pressing Stop (or Esc) stops the motors and
closes the camera.

Requires: the Sphero RVR SDK and a Raspberry Pi camera (the picamera library).
"""
import sys
import time

sys.path.append('/home/pi/sphero-sdk-raspberrypi-python')

from picamera import PiCamera                 # noqa: E402
from sphero_sdk import DriveFlagsBitmask      # noqa: E402
from sphero_sdk import SpheroRvrObserver      # noqa: E402

from runner_io import say, show               # noqa: E402

SPEED = 30                    # 0 to 255
START_HEADING = 0             # degrees
SECONDS_PER_SIDE = 4
DEGREES_PER_TURN = 90
CORNERS = 4
PHOTO_SIZE = (640, 480)       # pixels; small photos reach the laptop quickly
CAMERA_WARM_UP_SECONDS = 2    # lets the camera set its exposure (and the RVR wake up)
SETTLE_SECONDS = 0.5          # lets the robot stop rocking before each photo
DRIVE_UPDATE_SECONDS = 0.1    # how often the drive command is repeated

rvr = SpheroRvrObserver()
camera = None


def drive_for_seconds(speed, heading, seconds):
    """
    Purpose: drive straight at a heading for a set time, then stop
    Inputs:  speed (0-255), heading (degrees), seconds
    Returns: nothing
    """
    end_time = time.monotonic() + seconds
    while time.monotonic() < end_time:
        rvr.drive_with_heading(speed, heading % 360, DriveFlagsBitmask.none.value)
        time.sleep(DRIVE_UPDATE_SECONDS)
    rvr.drive_with_heading(0, heading % 360, DriveFlagsBitmask.none.value)


def take_corner_photo(corner):
    """
    Purpose: photograph one corner and send the photo to the laptop
    Inputs:  corner - the corner number, 1 to 4
    Returns: the photo's file name
    """
    filename = "corner_%d.jpg" % corner
    time.sleep(SETTLE_SECONDS)
    camera.capture(filename)
    say("Corner %d" % corner)
    show(filename)
    return filename


def square_with_photos(speed, heading, seconds):
    """
    Purpose: drive a square, taking a photo at each corner
    Inputs:  speed (0-255), starting heading (degrees), seconds per side
    Returns: nothing
    """
    for corner in range(1, CORNERS + 1):
        drive_for_seconds(speed, heading + (corner - 1) * DEGREES_PER_TURN, seconds)
        take_corner_photo(corner)


if '__main__' == __name__:
    try:
        rvr.wake()
        camera = PiCamera(resolution=PHOTO_SIZE)
        time.sleep(CAMERA_WARM_UP_SECONDS)
        rvr.reset_yaw()                       # the direction the robot faces now is heading 0
        square_with_photos(SPEED, START_HEADING, SECONDS_PER_SIDE)
        say("Square complete")
    except KeyboardInterrupt:
        print("Stopped by the Stop button (Ctrl+C)")
    finally:
        rvr.raw_motors(0, 0, 0, 0)            # motors off, whatever happened
        if None is not camera:
            camera.close()
        time.sleep(0.5)
        rvr.close()
