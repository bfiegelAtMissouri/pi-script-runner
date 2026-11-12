"""
Student-friendly hardware interface for Sphero RVR robots.

Students import Robot from this file rather than calling the Sphero SDK or the
VL53L1X library directly. Keep this file instructor-managed.

The runner provides this file to every script, the same way it provides
runner_io.py, so students only add their own script. A robot_api.py added
together with a script is used instead, for trying a changed version.

    from robot_api import Robot

    robot = None
    try:
        robot = Robot(use_camera=False)
        robot.drive(speed=60, heading=0, duration=3)
        print(robot.read_distance())
    except KeyboardInterrupt:
        print("Stopped")
    finally:
        if None is not robot:
            robot.cleanup()
"""
import sys
import time

sys.path.append('/home/pi/sphero-sdk-raspberrypi-python')

import qwiic_oled_display                       # noqa: E402
from qwiic_vl53l1x import QwiicVL53L1X          # noqa: E402
from sphero_sdk import RvrLedGroups             # noqa: E402
from sphero_sdk import RvrStreamingServices     # noqa: E402
from sphero_sdk import SpheroRvrObserver        # noqa: E402

# The camera libraries are imported only when a Robot uses the camera (see _initialize_camera), so robots and
# scripts that do not use the camera do not need them.

NO_FLAGS = 0                        # DriveFlagsBitmask.none: drive forward, no special behavior
DRIVE_REPEAT_SECONDS = 0.1          # the RVR stops on its own if a drive command is not repeated within 2 seconds
SENSOR_INTERVAL_MS = 250            # how often the RVR reports ambient light and floor color while streaming
DISTANCE_TIMEOUT_SECONDS = 1.0      # longest wait for one distance reading
DISTANCE_SENSOR_OK = (None, 0)      # sensor_init() result on success (None in older library versions)
CAMERA_WARM_UP_SECONDS = 2          # lets the camera set its exposure before the first picture
OLED_SECOND_LINE_Y = 12             # pixels down; the screen is 32 pixels tall


class Robot:
    """Simple wrapper around the Sphero RVR, the SparkFun VL53L1X sensor, the OLED, and the Pi camera."""

    def __init__(self, simulation=False, use_oled=True, use_distance_sensor=True, use_camera=True):
        self.simulation_mode = bool(simulation)
        self.use_oled = bool(use_oled)
        self.use_distance_sensor = bool(use_distance_sensor)
        self.use_camera = bool(use_camera)

        self.oled = None
        self.rvr = None
        self.distance_sensor = None
        self.camera = None
        self.heading = 0                # the heading of the last drive; stop() holds it

        # Values used by sensors
        self.ambient_light = None
        self.detected_color = None

        print("Initializing robot...")

        if self.simulation_mode:
            print("Robot ready in simulation mode.")
            return

        # If any part fails to start, release the parts that did start, so the RVR connection is not left open.
        try:
            # ORDER IS IMPORTANT
            if self.use_oled:
                self._initialize_oled()

            self._initialize_rvr()

            if self.use_distance_sensor:
                self._initialize_distance_sensor()
            if self.use_camera:
                self._initialize_camera()
            if self.use_oled:
                self.setOLED("Robot Ready!")
        except BaseException:
            self.cleanup()
            raise

        print("Robot ready.")

    # -------------------------
    # HARDWARE INITIALIZATION
    # -------------------------

    def _initialize_oled(self):
        self.oled = qwiic_oled_display.QwiicOledDisplay()
        self.oled.begin()
        self.oled.set_font_type(0)
        self.oled.flip_vertical(True)
        self.oled.flip_horizontal(True)
        self.oled.clear(self.oled.PAGE)
        self.oled.display()

    def _initialize_rvr(self):
        """Initialize after the OLED."""
        self.rvr = SpheroRvrObserver()
        self.rvr.wake()
        time.sleep(2)
        self.rvr.reset_yaw()            # the direction the robot faces now is heading 0

    def _initialize_distance_sensor(self):
        self.distance_sensor = QwiicVL53L1X()
        if self.distance_sensor.sensor_init() not in DISTANCE_SENSOR_OK:
            self.distance_sensor = None
            raise RuntimeError("The distance sensor did not respond. Check its Qwiic cable.")

    def _initialize_camera(self):
        """Initialize the Raspberry Pi camera."""
        from picamera import PiCamera
        self.camera = PiCamera()
        time.sleep(CAMERA_WARM_UP_SECONDS)

    # -------------------------
    # SENSOR HANDLERS
    # -------------------------

    def _ambient_light_handler(self, sensor_data):
        """Store the ambient light value received from the RVR."""
        self.ambient_light = sensor_data["AmbientLight"]["Light"]

    def _color_detected_handler(self, color_detected_data):
        """Store the RGB color detected by the RVR floor sensor."""
        my_color = color_detected_data["ColorDetection"]
        self.detected_color = (
            my_color["R"],
            my_color["G"],
            my_color["B"]
        )

    # -------------------------
    # LED Dictionary
    # -------------------------

    LED_NAMES = {
        "right_headlight": RvrLedGroups.headlight_right,
        "left_headlight": RvrLedGroups.headlight_left,
        "battery_door_front": RvrLedGroups.battery_door_front,
        "battery_door_rear": RvrLedGroups.battery_door_rear,
        "power_button_front": RvrLedGroups.power_button_front,
        "power_button_rear": RvrLedGroups.power_button_rear,
        "brakelight_left": RvrLedGroups.brakelight_left,
        "brakelight_right": RvrLedGroups.brakelight_right,
        "status_left": RvrLedGroups.status_indication_left,
        "status_right": RvrLedGroups.status_indication_right,
        "undercarriage": RvrLedGroups.undercarriage_white,
    }

    # -------------------------
    # STUDENT COMMANDS
    # -------------------------

    @staticmethod
    def _clamp(value, low, high):
        return max(low, min(high, int(value)))

    def drive(self, speed, heading, duration):
        """Drive using speed 0-255, heading 0-359 degrees, and seconds."""
        speed = self._clamp(speed, 0, 255)
        heading = int(heading) % 360
        duration = max(0.0, float(duration))

        if self.simulation_mode:
            print(f"Driving: speed={speed}, heading={heading}, duration={duration}")
            time.sleep(duration)
            return

        # The drive command is repeated until the time is up, timed with the monotonic clock.
        self.heading = heading
        end_time = time.monotonic() + duration
        try:
            while time.monotonic() < end_time:
                self.rvr.drive_with_heading(speed, heading, NO_FLAGS)
                time.sleep(max(0.0, min(DRIVE_REPEAT_SECONDS, end_time - time.monotonic())))
        finally:
            self.stop()

    def stop(self):
        """Stop the RVR immediately, still facing the heading it was driving."""
        if self.simulation_mode:
            print("Robot stopped.")
            return

        if self.rvr is not None:
            self.rvr.drive_with_heading(
                speed=0,
                heading=self.heading,
                flags=NO_FLAGS
            )

    def set_all_led(self, red, green, blue):
        """Set all RVR LEDs to one RGB color; each value is 0-255."""
        red = self._clamp(red, 0, 255)
        green = self._clamp(green, 0, 255)
        blue = self._clamp(blue, 0, 255)

        if self.simulation_mode:
            print("LED:", red, green, blue)
            return

        self.rvr.led_control.set_all_leds_rgb(
            red=red,
            green=green,
            blue=blue
        )

    def set_individual_led(self, led_name, red, green, blue):
        """Set one RVR LED to an RGB color."""
        led_name = str(led_name).lower()

        if led_name not in self.LED_NAMES:
            valid_names = ", ".join(self.LED_NAMES.keys())
            raise ValueError(
                f"Unknown LED name: {led_name}. "
                f"Valid names are: {valid_names}"
            )

        red = self._clamp(red, 0, 255)
        green = self._clamp(green, 0, 255)
        blue = self._clamp(blue, 0, 255)

        if self.simulation_mode:
            print(
                f"LED {led_name}: "
                f"red={red}, green={green}, blue={blue}"
            )
            return

        self.rvr.led_control.set_led_rgb(
            self.LED_NAMES[led_name],
            red,
            green,
            blue
        )

    def read_distance(self):
        """Take one VL53L1X reading and return distance in millimeters."""
        if self.simulation_mode:
            return float(input("Enter simulated distance in millimeters: "))
        if self.distance_sensor is None:
            raise RuntimeError("This Robot was started without a distance sensor.")

        self.distance_sensor.start_ranging()
        try:
            # Wait for the measurement to finish; reading earlier returns an old or empty value.
            deadline = time.monotonic() + DISTANCE_TIMEOUT_SECONDS
            while not self.distance_sensor.check_for_data_ready():
                if deadline < time.monotonic():
                    raise RuntimeError("No reading from the distance sensor.")
                time.sleep(0.005)
            distance = self.distance_sensor.get_distance()
            self.distance_sensor.clear_interrupt()
            return float(distance)
        finally:
            self.distance_sensor.stop_ranging()

    def setOLED(self, message):
        """Display one line of text on the OLED."""
        if self.simulation_mode or self.oled is None:
            print("OLED:", message)
            return
        self.oled.clear(self.oled.PAGE)
        self.oled.set_cursor(0, 0)
        self.oled.print(str(message))
        self.oled.display()

    def setOLED_two_lines(self, line1, line2):
        """Display two lines of text on the OLED."""
        if self.simulation_mode or self.oled is None:
            print("OLED:", line1, "|", line2)
            return
        self.oled.clear(self.oled.PAGE)
        self.oled.set_cursor(0, 0)
        self.oled.print(str(line1))
        self.oled.set_cursor(0, OLED_SECOND_LINE_Y)
        self.oled.print(str(line2))
        self.oled.display()

    def read_ambient_light(self, timeout=3):
        """Return one ambient light measurement."""
        if self.simulation_mode:
            return float(input("Enter simulated ambient light: "))

        self.ambient_light = None
        self.rvr.sensor_control.add_sensor_data_handler(
            service=RvrStreamingServices.ambient_light,
            handler=self._ambient_light_handler
        )
        self.rvr.sensor_control.start(interval=SENSOR_INTERVAL_MS)
        try:
            deadline = time.monotonic() + timeout
            while self.ambient_light is None:
                if deadline < time.monotonic():
                    raise RuntimeError("No ambient light data received.")
                time.sleep(0.1)
            return self.ambient_light
        finally:
            self.rvr.sensor_control.stop()

    def read_floor_color_rgb(self, timeout=3):
        """Return the floor color detected by the RVR as (R, G, B)."""
        if self.simulation_mode:
            values = input("Enter simulated floor color as R,G,B: ").split(",")
            return tuple(self._clamp(value, 0, 255) for value in values)

        self.detected_color = None
        self.rvr.sensor_control.add_sensor_data_handler(
            service=RvrStreamingServices.color_detection,
            handler=self._color_detected_handler
        )
        self.rvr.enable_color_detection(is_enabled=True)
        self.rvr.sensor_control.start(interval=SENSOR_INTERVAL_MS)
        try:
            deadline = time.monotonic() + timeout
            while self.detected_color is None:
                if deadline < time.monotonic():
                    raise RuntimeError("No floor color data received.")
                time.sleep(0.1)
            return self.detected_color
        finally:
            self.rvr.enable_color_detection(is_enabled=False)
            self.rvr.sensor_control.stop()

    def read_floor_color_name(self):
        """Return a simple name for the detected floor color."""
        red, green, blue = self.read_floor_color_rgb()
        # Very dark surface
        if red < 50 and green < 50 and blue < 50:
            return "black"
        # Very light surface
        elif red > 180 and green > 180 and blue > 180:
            return "white"
        # Red is dominant
        elif red > green * 1.4 and red > blue * 1.4:
            return "red"
        # Green is dominant
        elif green > red * 1.4 and green > blue * 1.4:
            return "green"
        # Blue is dominant
        elif blue > red * 1.4 and blue > green * 1.4:
            return "blue"
        # Red + green produces yellow
        elif red > 100 and green > 100 and blue < 100:
            return "yellow"
        else:
            return "unknown"

    def _require_camera(self):
        if self.camera is None:
            raise RuntimeError("This Robot was started without the camera (use_camera=False).")

    def take_picture(self, filename="/tmp/robot_image.jpg"):
        """Take a picture and save it to a file."""
        self._require_camera()
        self.camera.capture(filename)
        return filename

    def get_camera_image(self):
        """Take a picture and return it as an OpenCV image (a numpy array in blue, green, red order)."""
        import cv2
        filename = self.take_picture("/tmp/robot_image.jpg")
        image = cv2.imread(filename)
        if image is None:
            raise RuntimeError("Camera image could not be loaded.")
        return image

    def wait(self, seconds):
        """Pause the program for a nonnegative number of seconds."""
        time.sleep(max(0.0, float(seconds)))

    def cleanup(self):
        """Safely shut down all robot hardware. Each step runs even if an earlier one fails."""
        if self.simulation_mode:
            print("Robot shutdown complete.")
            return

        steps = []
        if self.rvr is not None:
            steps += [
                ("stop the motors", lambda: self.rvr.raw_motors(0, 0, 0, 0)),
                ("stop sensor streaming", self.rvr.sensor_control.stop),
                ("turn off the LEDs", lambda: self.rvr.led_control.set_all_leds_rgb(red=0, green=0, blue=0)),
            ]
        if self.camera is not None:
            steps.append(("close the camera", self.camera.close))
        if self.distance_sensor is not None:
            steps.append(("stop the distance sensor", self.distance_sensor.stop_ranging))
        if self.oled is not None:
            steps.append(("clear the OLED", lambda: (self.oled.clear(self.oled.PAGE), self.oled.display())))
        if self.rvr is not None:
            steps.append(("close the RVR connection", self.rvr.close))

        for description, step in steps:
            try:
                step()
            except Exception as error:      # keep cleaning up; report what failed
                print(f"Cleanup could not {description}: {error}")

        self.camera = None
        print("Robot shutdown complete.")

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        self.cleanup()
        return False
