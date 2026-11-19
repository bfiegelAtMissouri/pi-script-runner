"""
Classifying what the camera sees: takes a photo and names what is in it, with an Ultralytics YOLO classification
model and PyTorch.

Classification answers "what is this a picture of?" for the whole picture: it gives its five best guesses from 1000
kinds of things (the ImageNet classes: "coffee mug", "computer keyboard", "banana", "golden retriever", ...), each with
a confidence from 0 to 1. To find several objects in one picture and where each is, use detection instead
(robot_ultralytics.py).

Fill most of the picture with one object for the best result.

Run it:
  From the app:  press Run. To classify a picture instead of taking one, add the picture file too and type its name
                 when asked.
  Over SSH:      type robot_classify.py | ssh pi@<robot> "python3 -"

Needs: the 64-bit robot (Picamera2, PyTorch, Ultralytics). robot_setup.sh installs them and downloads the model into
~/models; without it, the model is downloaded on the first run.

The first classification after loading is slow (a few seconds on a Pi 3B+); later ones in the same run are faster.
"""
import os
import sys
import time

from ultralytics import YOLO

MODEL = os.path.expanduser("~/models/yolo11n-cls.pt")   # "n" (nano) is the smallest and fastest
PHOTO = "/tmp/classify_photo.jpg"
PHOTO_SIZE = (640, 480)                 # pixels
IMAGE_SIZE = 224                        # the size the model was trained at
WARM_UP_SECONDS = 2
GUESSES = 5


def take_photo():
    from picamera2 import Picamera2
    camera = Picamera2()
    try:
        camera.configure(camera.create_still_configuration(main={"size": PHOTO_SIZE}))
        camera.start()
        time.sleep(WARM_UP_SECONDS)
        camera.capture_file(PHOTO)
    finally:
        camera.close()
    return PHOTO


def which_picture():
    """A picture named on the command line or typed in, or else a new photo."""
    name = sys.argv[1] if 1 < len(sys.argv) else ""
    if not name:
        try:
            name = input("Picture file to classify (Enter to take a photo): ").strip()
        except EOFError:                        # nothing to read, as with "python3 -" over SSH
            name = ""
    if name and not os.path.exists(name):
        print("There is no file named %s; taking a photo instead." % name)
        name = ""
    return name or take_photo()


try:
    print("Loading the model...")
    model = YOLO(MODEL if os.path.exists(MODEL) else os.path.basename(MODEL))   # downloads it if it is not there

    picture = which_picture()
    started = time.monotonic()
    result = model(picture, imgsz=IMAGE_SIZE, verbose=False)[0]
    print("Classifying took %.1f s" % (time.monotonic() - started))

    # result.probs holds a confidence for every class; top5 are the indexes of the five highest.
    for index in result.probs.top5[:GUESSES]:
        print("%-25s %.2f" % (result.names[index], float(result.probs.data[index])))
    best = result.names[result.probs.top1]
    print("Best guess:", best)

    try:
        from runner_io import say, show         # shows the photo and says the answer in the app
        show(picture)
        say("I think this is a %s." % best)
    except ImportError:
        pass

except KeyboardInterrupt:
    print("Stopped by the Stop button (Ctrl+C)")
