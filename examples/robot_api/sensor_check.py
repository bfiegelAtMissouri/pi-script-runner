"""
Sensor check with the Robot API: reads the distance and ambient light,
prints them, and shows the distance on the OLED.
"""
import time

from robot_api import Robot

robot = None
try:
    robot = Robot(use_camera=False)

    distance = robot.read_distance()
    light1 = robot.read_ambient_light()
    time.sleep(2)
    light2 = robot.read_ambient_light()
    light = (light1 + light2) / 2

    print("Distance:", distance)
    print("Ambient light:", light)

    robot.setOLED_two_lines(
        "Distance:",
        str(distance)
    )
    time.sleep(3)                       # time to read the screen before cleanup clears it

except KeyboardInterrupt:
    print("Stopped by the Stop button (Ctrl+C)")

finally:
    if robot is not None:
        robot.cleanup()
