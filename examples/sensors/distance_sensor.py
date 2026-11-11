"""
Measure distance with a SparkFun Qwiic VL53L1X distance sensor twice a second
until Stop is pressed, and warn out loud when something comes close.

Requires: the sparkfun-qwiic-vl53l1x library and the sensor on the Qwiic (I2C) bus.
"""
import time

from qwiic_vl53l1x import QwiicVL53L1X

from runner_io import say

READ_EVERY_SECONDS = 0.5
CLOSE_MM = 200                 # closer than this (millimeters) counts as close
SENSOR_OK = (None, 0)          # sensor_init() returns 0 on success (None in older versions of the library)

tof = QwiicVL53L1X()


def distance_mm():
    """
    Purpose: read one distance from the sensor
    Inputs:  nothing (ranging must already be started)
    Returns: the distance in millimeters
    """
    while not tof.check_for_data_ready():
        time.sleep(0.005)
    distance = tof.get_distance()
    tof.clear_interrupt()
    return distance


if '__main__' == __name__:
    if tof.sensor_init() not in SENSOR_OK:
        print("The distance sensor did not respond. Check its Qwiic cable.")
        raise SystemExit(1)
    try:
        tof.start_ranging()
        was_close = False
        while True:
            distance = distance_mm()
            print("Distance: %d mm" % distance)
            is_close = distance < CLOSE_MM
            if is_close and not was_close:
                say("Too close")
            was_close = is_close
            time.sleep(READ_EVERY_SECONDS)
    except KeyboardInterrupt:
        print("Stopped by the Stop button (Ctrl+C)")
    finally:
        tof.stop_ranging()
