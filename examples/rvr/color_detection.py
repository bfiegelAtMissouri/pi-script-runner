"""
Read the color under a Sphero RVR with its color sensor, and say which of red,
green, or blue is strongest.

Requires: the Sphero RVR SDK.
"""
import sys
import time

sys.path.append('/home/pi/sphero-sdk-raspberrypi-python')

from sphero_sdk import RvrStreamingServices   # noqa: E402
from sphero_sdk import SpheroRvrObserver      # noqa: E402

from runner_io import say                     # noqa: E402

SAMPLE_INTERVAL_MS = 250       # how often the RVR reports the color
WAKE_SECONDS = 2               # time for the RVR to wake up
TIMEOUT_SECONDS = 5            # give up if no color arrives in this time

rvr = SpheroRvrObserver()
detected_color = None


def color_detected_handler(color_detected_data):
    """Called by the SDK each time the RVR reports a color."""
    global detected_color
    color = color_detected_data['ColorDetection']
    detected_color = (color['R'], color['G'], color['B'])


def get_single_color():
    """
    Purpose: read one color from the RVR's color sensor
    Inputs:  nothing
    Returns: (red, green, blue), each 0 to 255, or None if no color arrived in time
    """
    global detected_color
    detected_color = None
    rvr.enable_color_detection(is_enabled=True)
    rvr.sensor_control.start(interval=SAMPLE_INTERVAL_MS)
    deadline = time.monotonic() + TIMEOUT_SECONDS
    while None is detected_color and time.monotonic() < deadline:
        time.sleep(0.1)
    rvr.sensor_control.stop()
    rvr.enable_color_detection(is_enabled=False)
    return detected_color


def strongest_channel(color):
    """Returns "red", "green", or "blue": whichever value in (red, green, blue) is largest."""
    red, green, blue = color
    if red >= green and red >= blue:
        return "red"
    if green >= blue:
        return "green"
    return "blue"


if '__main__' == __name__:
    try:
        rvr.wake()
        time.sleep(WAKE_SECONDS)
        # Register the handler once, before streaming starts.
        rvr.sensor_control.add_sensor_data_handler(
            service=RvrStreamingServices.color_detection,
            handler=color_detected_handler)

        color = get_single_color()
        if None is color:
            say("No color was detected")
        else:
            print("Red, green, blue:", color)
            say("Mostly " + strongest_channel(color))
    except KeyboardInterrupt:
        print("Stopped by the Stop button (Ctrl+C)")
    finally:
        rvr.sensor_control.clear()
        time.sleep(0.5)
        rvr.close()
