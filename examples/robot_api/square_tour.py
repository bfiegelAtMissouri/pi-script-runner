"""
Square tour with the Robot API: drives a square, changing its lights on each
side, and at each corner reads the floor color, checks the distance ahead,
takes a photo (shown in the app), and says what it found.

Each side drives for 3 seconds, longer than the RVR's 2-second limit for a
single drive command, so it also shows that drive() keeps the robot moving.
"""
from robot_api import Robot
from runner_io import say, show

SPEED = 50                  # 0 to 255
SECONDS_PER_SIDE = 3
SIDE_COLORS = [(0, 255, 0), (0, 0, 255), (255, 255, 0), (255, 0, 255)]   # (red, green, blue) lights for each side

robot = None
try:
    robot = Robot()

    for side, (red, green, blue) in enumerate(SIDE_COLORS):
        heading = 90 * side
        robot.set_all_led(red, green, blue)
        robot.setOLED_two_lines("Side %d" % (side + 1), "Heading %d" % heading)
        robot.drive(SPEED, heading, SECONDS_PER_SIDE)

        floor = robot.read_floor_color_name()
        distance_cm = robot.read_distance() / 10
        photo = robot.take_picture("/tmp/corner_%d.jpg" % (side + 1))
        show(photo)
        say("Corner %d. The floor is %s, and the nearest thing ahead is %d centimeters away."
            % (side + 1, floor, distance_cm))
        robot.wait(2)                   # time for the speech before driving on

    robot.setOLED("Tour complete")
    say("Tour complete.")

except KeyboardInterrupt:
    print("Stopped by the Stop button (Ctrl+C)")

finally:
    if robot is not None:
        robot.cleanup()
