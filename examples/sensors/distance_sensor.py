"""
Measure distance with a SparkFun Qwiic VL53L1X distance sensor twice a second
until Stop is pressed, graph it in the app, and warn out loud when something comes close.

Requires: the sparkfun-qwiic-vl53l1x library and the sensor on the Qwiic (I2C) bus.
"""
import time

from qwiic_vl53l1x import QwiicVL53L1X

from runner_io import plot, say

READ_EVERY_SECONDS = 0.5
CLOSE_MM = 200                 # closer than this (millimeters) counts as close
SENSOR_OK = (None, 0)          # sensor_init() returns 0 on success (None in older versions of the library)

tof = QwiicVL53L1X()


def distance_mm():
    """
    Purpose: read one distance from the sensor
    Inputs:  nothing (ranging must already be started)
    Returns: the distance in millimeters, or None when nothing is in range
    """
    while not tof.check_for_data_ready():
        time.sleep(0.005)
    distance = tof.get_distance()
    # The sensor always gives a number; its range status says whether to trust it (0 = valid). With nothing in
    # range the status is a failure and the number is meaningless.
    status = tof.get_range_status()
    tof.clear_interrupt()
    return distance if 0 == status else None


if '__main__' == __name__:
    if tof.sensor_init() not in SENSOR_OK:
        print("The distance sensor did not respond. Check its Qwiic cable.")
        raise SystemExit(1)
    try:
        tof.start_ranging()
        was_close = False
        while True:
            distance = distance_mm()
            if distance is None:
                print("Distance: nothing in range")
            else:
                print("Distance: %d mm" % distance)
            plot(distance_mm=distance)          # the app graphs it as it changes (None is left out)
            is_close = distance is not None and distance < CLOSE_MM
            if is_close and not was_close:
                say("Too close")
            was_close = is_close
            time.sleep(READ_EVERY_SECONDS)
    except KeyboardInterrupt:
        print("Stopped by the Stop button (Ctrl+C)")
    finally:
        tof.stop_ranging()
