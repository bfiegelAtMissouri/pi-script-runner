"""
Explorer: a Sphero RVR+ mission that uses every peripheral at once.

The robot explores until it finds a target color on the floor (for example, a
sheet of red paper) or until time runs out:

- It drives forward, watching the distance sensor.
- When something is close, it stops, takes a photo (shown in the app), says how
  far away the obstacle is, and turns to search a new direction.
- The color sensor under the robot watches the floor for the target color.
- The ambient light sensor turns the headlights on when the room is dark.
- The lights show what it is doing: green while exploring, red at an obstacle,
  blue when the target is found. The OLED shows the state and the distance.
- At the end it gives a spoken summary.

Press Stop (or Esc) at any time: the motors stop, the lights and screen turn
off, and the camera and sensors are released.

Needs:
- Sphero RVR+ with its Raspberry Pi (Sphero RVR SDK)
- Raspberry Pi camera (picamera)
- SparkFun Qwiic VL53L1X distance sensor, facing forward
- SparkFun Qwiic OLED display (128 x 32)
"""
import sys
import time

sys.path.append('/home/pi/sphero-sdk-raspberrypi-python')

import qwiic_oled_display                       # noqa: E402
from picamera import PiCamera                   # noqa: E402
from qwiic_vl53l1x import QwiicVL53L1X          # noqa: E402
from sphero_sdk import Colors                   # noqa: E402
from sphero_sdk import DriveFlagsBitmask        # noqa: E402
from sphero_sdk import RvrLedGroups             # noqa: E402
from sphero_sdk import RvrStreamingServices     # noqa: E402
from sphero_sdk import SpheroRvrObserver        # noqa: E402

from runner_io import say, show                 # noqa: E402

# ---- Mission settings: change these to change the mission -----------------
TARGET_COLOR = "red"            # "red", "green", or "blue": the floor color that ends the mission
MISSION_SECONDS = 90            # give up after this long
DRIVE_SPEED = 40                # 0 to 255
OBSTACLE_MM = 250               # stop when something is closer than this (millimeters)
TURN_DEGREES = 90               # how far to turn after an obstacle
TURN_SECONDS = 1.5              # time allowed for the turn

# ---- Sensor settings -------------------------------------------------------
SENSOR_SAMPLE_MS = 250          # how often the RVR reports color and light
TARGET_STRENGTH = 1.6           # the target channel must be this many times the other two
TARGET_MINIMUM = 60             # and at least this bright (0 to 255), so dark floors do not count
TARGET_READINGS_NEEDED = 2      # readings in a row that must match, to ignore a single odd reading
DARK_LEVEL = 50                 # ambient light below this turns the headlights on; adjust for your room
SENSOR_OK = (None, 0)           # distance sensor_init() result on success (None in older library versions)

# ---- Hardware settings -----------------------------------------------------
OLED_ADDRESS = 0x3C
OLED_SECOND_LINE_Y = 12         # pixels down; the screen is 32 pixels tall
OLED_MOUNTED_UPSIDE_DOWN = True
PHOTO_SIZE = (640, 480)         # pixels
CAMERA_WARM_UP_SECONDS = 2      # also gives the RVR time to wake up
SETTLE_SECONDS = 0.5            # lets the robot stop rocking before a photo
LOOP_SECONDS = 0.1              # how often the main loop checks the sensors
NO_FLAGS = DriveFlagsBitmask.none.value
HEADLIGHTS = [RvrLedGroups.headlight_left, RvrLedGroups.headlight_right]

# ---- Hardware objects (created in main) ------------------------------------
rvr = SpheroRvrObserver()
camera = None
oled = None
tof = None

# ---- Values the RVR's sensor handlers update while the mission runs -------
floor_color = None              # latest (red, green, blue) from the color sensor
target_streak = 0               # how many readings in a row matched the target color
is_dark = False


class Mission:
    """What has happened so far on this run."""

    def __init__(self):
        self.start_time = time.monotonic()
        self.heading = 0
        self.obstacles = 0
        self.photos = []
        self.found_target = False
        self.body_color = Colors.green

    def seconds_elapsed(self):
        return time.monotonic() - self.start_time


# ---- Sensors -----------------------------------------------------------------

def matches_target(color):
    """
    Purpose: decide whether a color sensor reading is the target color
    Inputs:  color - (red, green, blue), each 0 to 255
    Returns: True if the target channel is bright and clearly stronger than the other two
    """
    channels = dict(zip(("red", "green", "blue"), color))
    target = channels.pop(TARGET_COLOR)
    return TARGET_MINIMUM <= target and all(TARGET_STRENGTH * other <= target for other in channels.values())


def color_handler(sensor_data):
    """Called by the SDK with each color sensor reading."""
    global floor_color, target_streak
    reading = sensor_data['ColorDetection']
    floor_color = (reading['R'], reading['G'], reading['B'])
    target_streak = target_streak + 1 if matches_target(floor_color) else 0


def light_handler(sensor_data):
    """Called by the SDK with each ambient light reading."""
    global is_dark
    is_dark = sensor_data['AmbientLight']['Light'] < DARK_LEVEL


def distance_mm():
    """
    Purpose: read one distance from the distance sensor
    Inputs:  nothing (ranging must already be started)
    Returns: the distance in millimeters
    """
    while not tof.check_for_data_ready():
        time.sleep(0.005)
    distance = tof.get_distance()
    tof.clear_interrupt()
    return distance


# ---- Outputs -------------------------------------------------------------------

def show_status(first_line, second_line):
    """Shows two lines on the OLED (about 21 characters fit on each)."""
    oled.clear(oled.PAGE)
    oled.set_cursor(0, 0)
    oled.print(first_line)
    oled.set_cursor(0, OLED_SECOND_LINE_Y)
    oled.print(second_line)
    oled.display()


def set_lights(mission, body_color=None):
    """
    Purpose: color the robot's lights for its state, with white headlights when it is dark
    Inputs:  mission; body_color - a Colors value, or None to keep the current one
    Returns: nothing
    """
    if None is not body_color:
        mission.body_color = body_color
    rvr.led_control.set_all_leds_color(mission.body_color)
    if is_dark:
        rvr.led_control.set_multiple_leds_with_enums(leds=HEADLIGHTS, colors=[Colors.white, Colors.white])


def take_photo(mission, label):
    """
    Purpose: take a photo, keep its name, and send it to the laptop
    Inputs:  mission; label - a short name for the photo
    Returns: the photo's file name
    """
    filename = "%02d_%s.jpg" % (len(mission.photos) + 1, label)
    time.sleep(SETTLE_SECONDS)
    camera.capture(filename)
    mission.photos.append(filename)
    show(filename)
    return filename


# ---- Driving -------------------------------------------------------------------

def stop_driving(mission):
    rvr.drive_with_heading(0, mission.heading, NO_FLAGS)


def turn(mission, degrees):
    """Turns in place by the given number of degrees (positive is clockwise)."""
    mission.heading = (mission.heading + degrees) % 360
    rvr.drive_with_heading(0, mission.heading, NO_FLAGS)     # speed 0 at a new heading turns in place
    time.sleep(TURN_SECONDS)


def handle_obstacle(mission, distance):
    """Stops at an obstacle, photographs it, reports it, and turns to a new direction."""
    stop_driving(mission)
    mission.obstacles += 1
    set_lights(mission, Colors.red)
    show_status("Obstacle %d" % mission.obstacles, "%d cm away" % (distance // 10))
    take_photo(mission, "obstacle")
    say("Obstacle %d, %d centimeters away. Turning." % (mission.obstacles, distance // 10))
    turn(mission, TURN_DEGREES)
    set_lights(mission, Colors.green)


def explore(mission):
    """
    Purpose: drive and react to the sensors until the target is found or time runs out
    Inputs:  mission
    Returns: nothing (mission.found_target says how it ended)
    """
    was_dark = is_dark
    while mission.seconds_elapsed() < MISSION_SECONDS:
        if TARGET_READINGS_NEEDED <= target_streak:
            mission.found_target = True
            return
        if was_dark != is_dark:                            # the room got darker or lighter
            was_dark = is_dark
            set_lights(mission)
        distance = distance_mm()
        if distance < OBSTACLE_MM:
            handle_obstacle(mission, distance)
            continue
        rvr.drive_with_heading(DRIVE_SPEED, mission.heading, NO_FLAGS)
        seconds_left = MISSION_SECONDS - mission.seconds_elapsed()
        show_status("Exploring  %3d s left" % seconds_left, "Ahead: %d cm" % (distance // 10))
        time.sleep(LOOP_SECONDS)


def spoken_duration(total_seconds):
    """Returns a duration as words, for example "1 minute and 5 seconds" or "42 seconds"."""
    minutes, seconds = divmod(int(total_seconds), 60)
    parts = []
    if 0 < minutes:
        parts.append("%d minute%s" % (minutes, "" if 1 == minutes else "s"))
    if 0 < seconds or not parts:
        parts.append("%d second%s" % (seconds, "" if 1 == seconds else "s"))
    return " and ".join(parts)


def finish(mission):
    """Stops, celebrates (or not), and gives a spoken summary."""
    stop_driving(mission)
    minutes, seconds = divmod(int(mission.seconds_elapsed()), 60)
    if mission.found_target:
        set_lights(mission, Colors.blue)
        show_status("Found %s!" % TARGET_COLOR, "%d:%02d, %d obstacles" % (minutes, seconds, mission.obstacles))
        take_photo(mission, "target")
        say("I found %s after %s." % (TARGET_COLOR, spoken_duration(mission.seconds_elapsed())))
    else:
        set_lights(mission, Colors.yellow)
        show_status("Time is up", "%d obstacles" % mission.obstacles)
        say("Time is up. I did not find %s." % TARGET_COLOR)
    say("I avoided %d obstacles and took %d photos." % (mission.obstacles, len(mission.photos)))
    print("Photos:", ", ".join(mission.photos) or "none")


# ---- Start and clean up ----------------------------------------------------------

def start_hardware():
    """Starts the camera, display, distance sensor, and RVR sensors. Exits with a message if one is missing."""
    global camera, oled, tof
    tof = QwiicVL53L1X()
    if tof.sensor_init() not in SENSOR_OK:
        print("The distance sensor did not respond. Check its Qwiic cable.")
        raise SystemExit(1)
    tof.start_ranging()

    oled = qwiic_oled_display.QwiicOledDisplay(address=OLED_ADDRESS)
    oled.begin()
    oled.set_font_type(0)
    oled.flip_vertical(OLED_MOUNTED_UPSIDE_DOWN)
    oled.flip_horizontal(OLED_MOUNTED_UPSIDE_DOWN)
    show_status("Explorer", "Starting...")

    rvr.wake()
    camera = PiCamera(resolution=PHOTO_SIZE)
    time.sleep(CAMERA_WARM_UP_SECONDS)
    rvr.reset_yaw()                                      # the direction the robot faces now is heading 0

    rvr.sensor_control.add_sensor_data_handler(service=RvrStreamingServices.color_detection, handler=color_handler)
    rvr.sensor_control.add_sensor_data_handler(service=RvrStreamingServices.ambient_light, handler=light_handler)
    rvr.enable_color_detection(is_enabled=True)
    rvr.sensor_control.start(interval=SENSOR_SAMPLE_MS)


def release_hardware():
    """Stops the motors and releases everything, whatever happened. Each step runs even if an earlier one fails."""
    steps = [
        lambda: rvr.raw_motors(0, 0, 0, 0),
        lambda: rvr.sensor_control.clear(),
        lambda: rvr.enable_color_detection(is_enabled=False),
        lambda: rvr.led_control.turn_leds_off(),
        lambda: None is not camera and camera.close(),
        lambda: None is not tof and tof.stop_ranging(),
        lambda: None is not oled and (oled.clear(oled.PAGE), oled.display()),
    ]
    for step in steps:
        try:
            step()
        except Exception as error:                     # keep cleaning up; report what failed
            print("Cleanup step failed:", error)
    time.sleep(0.5)
    rvr.close()


if '__main__' == __name__:
    try:
        start_hardware()
        mission = Mission()
        set_lights(mission, Colors.green)
        say("Exploring. Looking for %s." % TARGET_COLOR)
        explore(mission)
        finish(mission)
    except KeyboardInterrupt:
        print("Stopped by the Stop button (Ctrl+C)")
    finally:
        release_hardware()
