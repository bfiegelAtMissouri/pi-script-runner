"""
Robot that looks around: takes a photo, has the laptop find objects in it with YOLO, and says what it sees. Its
lights turn red when it sees a person. Stop (or Esc) ends it.

Add robot_api.py, and remote_vision.py with "Local" checked beside it. Needs the RVR and the Pi camera on the
robot, and Ultralytics on the laptop (see remote_vision.py).
"""
from robot_api import Robot
from runner_io import remote_execute, shared_runner_file, say, show

MIN_CONFIDENCE = 0.5
LOOKS = 8                   # photos to take
TURN_DEGREES = 45           # turn between photos
PHOTO = "/tmp/robot_view.jpg"

robot = None
try:
    robot = Robot(use_distance_sensor=False)
    heading = 0
    for look in range(LOOKS):
        robot.stop()                    # the robot waits for the laptop, so it should not be moving
        robot.take_picture(PHOTO)
        show(PHOTO)
        seen = remote_execute("detect", shared_runner_file(PHOTO), MIN_CONFIDENCE)
        names = [name for name, confidence in seen]
        print("Look %d:" % (look + 1), seen or "nothing")
        robot.setOLED_two_lines("Look %d" % (look + 1), ", ".join(names[:3]) or "nothing")
        if "person" in names:
            robot.set_all_led(255, 0, 0)
        else:
            robot.set_all_led(0, 0, 255)
        say("I see " + (", ".join(sorted(set(names))) if names else "nothing"))
        robot.wait(2)
        heading = (heading + TURN_DEGREES) % 360
        robot.drive(0, heading, 1)      # speed 0 at a new heading turns in place

except KeyboardInterrupt:
    print("Stopped by the Stop button (Ctrl+C)")

finally:
    if robot is not None:
        robot.cleanup()
