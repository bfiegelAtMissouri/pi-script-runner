"""
Take one photo with the Raspberry Pi camera and show it in the app (or browser page).

Requires: a Raspberry Pi camera and the picamera library.
"""
import time

from picamera import PiCamera

from runner_io import show

PHOTO_FILE = "photo.jpg"       # saved next to this script on the Pi
PHOTO_SIZE = (1024, 768)       # pixels
WARM_UP_SECONDS = 2            # lets the camera set its exposure

if '__main__' == __name__:
    camera = PiCamera(resolution=PHOTO_SIZE)
    try:
        time.sleep(WARM_UP_SECONDS)
        camera.capture(PHOTO_FILE)
        show(PHOTO_FILE)
        print("Saved", PHOTO_FILE)
    except KeyboardInterrupt:
        print("Stopped by the Stop button (Ctrl+C)")
    finally:
        camera.close()                    # always release the camera, or the next script cannot use it
