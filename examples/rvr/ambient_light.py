"""
Report the light level from a Sphero RVR's ambient light sensor every second
until Stop is pressed, and say whether it is bright or dark.

Requires: the Sphero RVR SDK.
"""
import sys
import time

sys.path.append('/home/pi/sphero-sdk-raspberrypi-python')

from sphero_sdk import RvrStreamingServices   # noqa: E402
from sphero_sdk import SpheroRvrObserver      # noqa: E402

from runner_io import say                     # noqa: E402

SAMPLE_INTERVAL_MS = 1000      # how often the RVR reports the light level
WAKE_SECONDS = 2               # time for the RVR to wake up
BRIGHT_LEVEL = 100             # readings at or above this count as bright; adjust for your room

rvr = SpheroRvrObserver()
was_bright = None


def ambient_light_handler(sensor_data):
    """Called by the SDK each time the RVR reports the light level."""
    global was_bright
    light = sensor_data['AmbientLight']['Light']
    print("Ambient light: %.1f" % light)
    is_bright = BRIGHT_LEVEL <= light
    if is_bright != was_bright:               # speak only when it changes
        say("Bright" if is_bright else "Dark")
        was_bright = is_bright


if '__main__' == __name__:
    try:
        rvr.wake()
        time.sleep(WAKE_SECONDS)
        rvr.sensor_control.add_sensor_data_handler(
            service=RvrStreamingServices.ambient_light,
            handler=ambient_light_handler)
        rvr.sensor_control.start(interval=SAMPLE_INTERVAL_MS)
        while True:                           # readings arrive in the handler until Stop
            time.sleep(1)
    except KeyboardInterrupt:
        print("Stopped by the Stop button (Ctrl+C)")
    finally:
        rvr.sensor_control.clear()
        time.sleep(0.5)
        rvr.close()
