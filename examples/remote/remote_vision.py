"""
Local file: object detection with Ultralytics YOLO, run on the laptop instead of the robot's Pi.

In the app, add this file with "Local" checked beside it. The laptop's Python needs Ultralytics
(pip install ultralytics); if it is not the Python the app finds, choose it under Settings, Local Python.
The model is loaded once, when the run starts; the first run downloads it.
"""
from ultralytics import YOLO

MODEL = "yolo11n.pt"            # any Ultralytics detection model; "n" (nano) is the fastest
model = YOLO(MODEL)


def detect(photo, min_confidence):
    """
    Purpose: find objects in a photo the robot sent with shared_runner_file()
    Inputs:  photo - the laptop's copy of the photo; min_confidence - 0 to 1, weaker detections are left out
    Returns: a list of [name, confidence] pairs, most confident first, for example [["person", 0.91]]
    """
    result = model(photo, verbose=False)[0]
    found = []
    for box in result.boxes:
        confidence = float(box.conf)
        if min_confidence <= confidence:
            found.append([result.names[int(box.cls)], round(confidence, 2)])
    return sorted(found, key=lambda pair: pair[1], reverse=True)
