"""
Take a photo, work out whether red, green, or blue is strongest in it on
average, show the photo, and say the answer.

Requires: a Raspberry Pi camera, the picamera library, and numpy.
"""
import time

from picamera import PiCamera
from picamera.array import PiRGBArray

from runner_io import say, show

PHOTO_FILE = "photo.jpg"
PHOTO_SIZE = (640, 480)        # pixels
WARM_UP_SECONDS = 2            # lets the camera set its exposure


def dominant_color(camera):
    """
    Purpose: find which color is strongest, on average, in what the camera sees
    Inputs:  camera - an open PiCamera
    Returns: "RED", "GREEN", or "BLUE"
    """
    raw_capture = PiRGBArray(camera)
    camera.capture(raw_capture, format="bgr")
    blue, green, red = raw_capture.array.mean(axis=(0, 1))
    print("Average red: %.1f  green: %.1f  blue: %.1f" % (red, green, blue))
    if red > green and red > blue:
        return "RED"
    if green > blue:
        return "GREEN"
    return "BLUE"


if '__main__' == __name__:
    camera = PiCamera(resolution=PHOTO_SIZE)
    try:
        time.sleep(WARM_UP_SECONDS)
        camera.capture(PHOTO_FILE)
        show(PHOTO_FILE)
        color = dominant_color(camera)
        print("Dominant color:", color)
        say("Mostly " + color.lower())
    except KeyboardInterrupt:
        print("Stopped by the Stop button (Ctrl+C)")
    finally:
        camera.close()                    # always release the camera, or the next script cannot use it
