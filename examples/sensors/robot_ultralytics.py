"""
YOLO test on the Pi (64-bit Bookworm): takes a photo, finds objects in it with Ultralytics,
prints what it found, and saves a copy with boxes drawn.
Run with:  python3 yolo_test.py   (the robot_env Python, which has Ultralytics)
"""
import time

import cv2
from picamera2 import Picamera2
from ultralytics import YOLO

MODEL = "yolo11n.pt"              # "n" (nano) is the smallest and fastest; downloaded on the first run
PHOTO = "/tmp/yolo_photo.jpg"
MARKED = "/tmp/yolo_marked.jpg"   # the photo with boxes and labels drawn
PHOTO_SIZE = (640, 480)           # pixels
IMAGE_SIZE = 320                  # size YOLO works at; smaller is faster on a Pi 3B+, larger finds smaller objects
MIN_CONFIDENCE = 0.5              # 0 to 1; weaker detections are left out
WARM_UP_SECONDS = 2

camera = None
try:
    print("Loading the model...")
    model = YOLO(MODEL)

    camera = Picamera2()
    camera.configure(camera.create_still_configuration(main={"size": PHOTO_SIZE}))
    camera.start()
    time.sleep(WARM_UP_SECONDS)
    camera.capture_file(PHOTO)
    print("Photo taken. Detecting...")

    started = time.monotonic()
    result = model(PHOTO, imgsz=IMAGE_SIZE, conf=MIN_CONFIDENCE, verbose=False)[0]
    print("Detection took %.1f s" % (time.monotonic() - started))

    if 0 == len(result.boxes):
        print("Nothing found.")
    for box in result.boxes:
        print("%-15s %.2f" % (result.names[int(box.cls)], float(box.conf)))

    cv2.imwrite(MARKED, result.plot())
    print("Saved", MARKED)
    try:
        from runner_io import show    # shows the marked photo in the app when run from there
        show(MARKED)
    except ImportError:
        pass

except KeyboardInterrupt:
    print("Stopped by the Stop button (Ctrl+C)")

finally:
    if camera is not None:
        camera.close()