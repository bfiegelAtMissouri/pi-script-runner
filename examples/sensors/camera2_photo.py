"""
Camera test for 64-bit Bookworm (Picamera2): takes one photo and saves it.
Run with:  python3 camera_test.py
"""
import time

from picamera2 import Picamera2

PHOTO = "/tmp/camera_test.jpg"
PHOTO_SIZE = (640, 480)           # pixels
WARM_UP_SECONDS = 2               # lets the camera set its exposure

camera = None
try:
    camera = Picamera2()
    camera.configure(camera.create_still_configuration(main={"size": PHOTO_SIZE}))
    camera.start()
    time.sleep(WARM_UP_SECONDS)
    camera.capture_file(PHOTO)
    print("Saved", PHOTO)

    try:
        from runner_io import show    # shows the photo in the app when run from there
        show(PHOTO)
    except ImportError:
        pass

except KeyboardInterrupt:
    print("Stopped by the Stop button (Ctrl+C)")

finally:
    if camera is not None:
        camera.close()