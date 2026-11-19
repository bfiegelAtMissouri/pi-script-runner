"""
Reading text with the camera (OCR): takes a photo, finds the text in it with Tesseract, and prints it as a string.

Hold a printed page, a sign, or a card about 20 to 40 cm in front of the camera, with even light. Large, dark,
printed letters on a plain light background read best; handwriting, curved surfaces, and glare read poorly.

The photo is cleaned up first (gray, enlarged, and turned into black and white), because Tesseract reads crisp
black text on white far better than a raw camera photo. Both pictures are shown in the app and saved in /tmp, so you
can see what Tesseract was given.

Run it:
  From the app:  press Run. To read a picture instead of taking one, add the picture file too and type its name
                 when asked.
  Over SSH:      type robot_ocr.py | ssh pi@<robot> "python3 -"

Needs: the 64-bit robot (Picamera2), Tesseract (the tesseract-ocr package), and pytesseract. robot_setup.sh installs
them.
"""
import os
import sys
import time

import cv2
import pytesseract

PHOTO = "/tmp/ocr_photo.jpg"
CLEANED = "/tmp/ocr_cleaned.png"        # what Tesseract reads
PHOTO_SIZE = (1640, 1232)               # pixels; more pixels per letter reads better
WARM_UP_SECONDS = 2
ENLARGE = 2                             # small letters read better enlarged
# Page segmentation mode: 6 = one block of text (a page or a card); 7 = a single line; 11 = scattered words (a sign)
TESSERACT_OPTIONS = "--psm 6"


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
            name = input("Picture file to read (Enter to take a photo): ").strip()
        except EOFError:                        # nothing to read, as with "python3 -" over SSH
            name = ""
    if name and not os.path.exists(name):
        print("There is no file named %s; taking a photo instead." % name)
        name = ""
    return name or take_photo()


def clean_up(picture):
    """Gray, enlarged, and black and white: what Tesseract reads best."""
    image = cv2.imread(picture)
    if image is None:
        raise RuntimeError("Could not open %s as a picture." % picture)
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    gray = cv2.resize(gray, None, fx=ENLARGE, fy=ENLARGE, interpolation=cv2.INTER_CUBIC)
    gray = cv2.GaussianBlur(gray, (3, 3), 0)
    # Otsu's method picks the gray level that best splits the letters from the background.
    _, black_and_white = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    cv2.imwrite(CLEANED, black_and_white)
    return black_and_white


try:
    picture = which_picture()
    cleaned = clean_up(picture)

    started = time.monotonic()
    text = pytesseract.image_to_string(cleaned, config=TESSERACT_OPTIONS).strip()
    print("Reading took %.1f s" % (time.monotonic() - started))

    if text:
        print("Text found:")
        print(text)
    else:
        print("No text found. Try more light, larger letters, or holding the page closer and flatter.")

    try:
        from runner_io import show              # shows the pictures in the app when run from there
        show(picture)
        show(CLEANED)
    except ImportError:
        pass

except KeyboardInterrupt:
    print("Stopped by the Stop button (Ctrl+C)")
