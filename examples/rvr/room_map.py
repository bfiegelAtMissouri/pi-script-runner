"""
Room map: the robot maps a square area around where it starts and draws the layout as a picture.

You choose the size of the square (in meters). The robot starts in its center, facing what will be the top of the
map. At each stop it turns in place and measures the distance to whatever is in each direction with the distance
sensor. It then drives to the next stop, if the way there is clear, and scans again. Anything outside the square is
left off the map, so a large classroom is not mapped end to end.

The picture, room_map.png, shows:
  white       space the sensor saw through (free)
  black       where the sensor hit something (a wall, a chair leg, a box)
  gray        not seen
  blue        the robot's stops and its path
It is shown in the app, and saved in /tmp with room_map_points.csv (every measured point, for your own analysis).

Run it:
  From the app:  press Run and type the size when asked (Enter for the default).
  Over SSH:      type room_map.py | ssh pi@<robot> "python3 - 2.0"        (2.0 = a 2 m square)
  No robot:      python3 room_map.py 2.0 --simulate                       (a pretend room, on any computer)

Before you start:
  - Clear the floor around the robot of things lower than the distance sensor; the sensor cannot see them and the
    robot may drive into them.
  - The run takes a few minutes. The app stops runs after its time limit (Settings), 300 seconds unless changed;
    raise it for squares larger than about 2 m.
  - Press Stop (Esc) at any time: the robot stops, and the map of what it has seen so far is still drawn.
  - If the robot runs into something and its motors stall, it stops mapping, marks the spot on the map with a red X,
    and draws what it has so far. (The RVR works out its position from its wheels, so a robot pushing against
    something with its treads slipping would think it is still moving; stopping keeps that error out of the map.)

What to expect: the distance sensor sees a cone about 27 degrees wide, not a thin line, so edges look rounded and
thin things (chair legs) look wider than they are. More stops, or a smaller SCAN_STEP_DEGREES, give a fuller map.
The robot's position comes from its wheel and motion sensors, which drift slowly, so a map of a small square is more
accurate than one of a large square.

Needs: Sphero RVR or RVR+ with its Raspberry Pi (Sphero RVR SDK), the SparkFun Qwiic VL53L1X distance sensor facing
forward, numpy, and matplotlib. Works on the 32-bit and the 64-bit robots.
"""
import csv
import math
import statistics
import sys
import threading
import time

import matplotlib
matplotlib.use("Agg")                   # draws to a file; the robot has no screen
import matplotlib.pyplot as plt         # noqa: E402
import numpy as np                      # noqa: E402

# ---- Map settings: change these to change the map ---------------------------------------------------------------
DEFAULT_SIZE_M = 2.0            # side of the square to map, in meters, when none is given
SMALLEST_SIZE_M = 0.6
LARGEST_SIZE_M = 6.0
SCAN_STEP_DEGREES = 15          # turn this far between measurements; smaller is slower but fuller
STATION_SPACING_M = 0.75        # closest the stops where the robot scans may be to each other
EDGE_MARGIN_M = 0.3             # the robot stays this far inside the square's edge
MOST_STATIONS = 9               # the most stops (a square grid: 4, 9, 16); a large square spreads them out
CELL_M = 0.05                   # the map's resolution: one square of the picture is 5 cm

# ---- Robot and sensor settings ----------------------------------------------------------------------------------
DRIVE_SPEED_MPS = 0.25          # meters per second between stops
PATH_CLEARANCE_M = 0.3          # a stop is skipped unless the way there is clear by this much
SENSOR_FORWARD_M = 0.11         # distance from the robot's center to the distance sensor
ROBOT_RADIUS_M = 0.12           # the floor the robot itself covers, drawn as free where it stopped
SHORTEST_MM = 40                # readings outside this range are ignored
LONGEST_MM = 3000               # the sensor's reliable range indoors; farther readings count as "nothing seen"
READINGS_PER_DIRECTION = 3      # the middle (median) of these is used, to ignore one odd reading
TURN_TOLERANCE_DEGREES = 4      # how close a turn must get before measuring
TURN_SECONDS = 3.0              # longest to wait for a turn
SETTLE_SECONDS = 0.15           # lets the robot stop rocking before it measures
SENSOR_INTERVAL_MS = 100        # how often the RVR reports its position and heading
SENSOR_OK = (None, 0)           # distance sensor_init() result on success (None in older library versions)
BEAM_DEGREES = 27               # the sensor sees a cone this wide (the VL53L1X's full field of view)
SECONDS_PER_DIRECTION = 0.7     # used only for the time estimate printed at the start

POINTS_FILE = "/tmp/room_map_points.csv"
PICTURE_FILE = "/tmp/room_map.png"


class Stalled(Exception):
    """The RVR reported that a motor stalled: it ran into something it cannot push."""


# =================================================================================================================
# The robot. Headings are in degrees, clockwise from the direction the robot faced at the start (0 = forward,
# 90 = right). Positions are in meters from the start: x to the right, y forward.
# =================================================================================================================
class RealRobot:
    """The RVR and its distance sensor."""

    def __init__(self):
        sys.path.append('/home/pi/sphero-sdk-raspberrypi-python')
        from qwiic_vl53l1x import QwiicVL53L1X
        from sphero_sdk import RvrStreamingServices
        from sphero_sdk import SpheroRvrObserver

        self.rvr = None
        self.sensor = None
        self.x = 0.0
        self.y = 0.0
        self.yaw = None                 # the RVR's own yaw: degrees, counterclockwise
        self.start_yaw = None
        self.yaw_sign = 1               # +1 when the RVR's yaw counts counterclockwise (checked at the start)
        self.move_done = threading.Event()
        self.stalled = threading.Event()

        try:
            self._start(QwiicVL53L1X, RvrStreamingServices, SpheroRvrObserver)
        except BaseException:
            self.close()                        # release what did start, so the RVR connection is not left open
            raise

    def _start(self, QwiicVL53L1X, RvrStreamingServices, SpheroRvrObserver):
        self.sensor = QwiicVL53L1X()
        if self.sensor.sensor_init() not in SENSOR_OK:
            raise RuntimeError("The distance sensor did not respond. Check its Qwiic cable.")

        self.rvr = SpheroRvrObserver()
        self.rvr.wake()
        time.sleep(2)
        self.rvr.reset_yaw()                    # the direction the robot faces now is heading 0
        time.sleep(0.1)
        self.rvr.reset_locator_x_and_y()        # and where it is now is (0, 0)
        time.sleep(0.1)
        self.rvr.on_xy_position_drive_result_notify(handler=lambda response: self.move_done.set())
        self.rvr.enable_motor_stall_notify(is_enabled=True)
        self.rvr.on_motor_stall_notify(handler=self._on_stall)
        self.rvr.sensor_control.add_sensor_data_handler(service=RvrStreamingServices.locator,
                                                        handler=self._on_locator)
        self.rvr.sensor_control.add_sensor_data_handler(service=RvrStreamingServices.imu, handler=self._on_imu)
        self.rvr.sensor_control.start(interval=SENSOR_INTERVAL_MS)

        deadline = time.monotonic() + 3
        while self.yaw is None:
            if deadline < time.monotonic():
                raise RuntimeError("The RVR did not report its heading.")
            time.sleep(0.05)
        self.start_yaw = self.yaw
        self._check_yaw_direction()

    def _check_yaw_direction(self):
        """
        Turns a quarter turn to the right and back, to learn which way the RVR's yaw counts, so the map is not drawn
        mirrored if it counts the other way.
        """
        deadline = time.monotonic() + 2
        while time.monotonic() < deadline:
            self.rvr.drive_with_heading(0, 90, 0)
            time.sleep(0.1)
        change = (self.yaw - self.start_yaw + 180) % 360 - 180      # -90 when yaw counts counterclockwise
        if abs(abs(change) - 90) > 30:
            raise RuntimeError("The robot did not turn as expected (%.0f degrees). Is it on the floor?" % change)
        self.yaw_sign = 1 if change < 0 else -1
        self.turn_to(0)

    def _on_stall(self, data):
        """Called by the SDK when a motor stalls (is_triggered True) or recovers (False)."""
        if data.get("is_triggered"):
            self.stalled.set()

    def _stop_if_stalled(self):
        if self.stalled.is_set():
            self.rvr.raw_motors(0, 0, 0, 0)
            raise Stalled()

    def _on_locator(self, data):
        self.x = data["Locator"]["X"]
        self.y = data["Locator"]["Y"]

    def _on_imu(self, data):
        self.yaw = data["IMU"]["Yaw"]

    def pose(self):
        """(x, y, heading) where the robot is now."""
        return self.x, self.y, (self.yaw_sign * (self.start_yaw - self.yaw)) % 360

    def turn_to(self, heading):
        """Turns in place to a heading and waits until it gets there (or TURN_SECONDS pass)."""
        deadline = time.monotonic() + TURN_SECONDS
        close_readings = 0
        while time.monotonic() < deadline and close_readings < 3:
            self.rvr.drive_with_heading(0, int(round(heading)) % 360, 0)     # speed 0: turn without driving
            time.sleep(0.1)
            self._stop_if_stalled()
            if angle_between(self.pose()[2], heading) <= TURN_TOLERANCE_DEGREES:
                close_readings += 1
            else:
                close_readings = 0
        time.sleep(SETTLE_SECONDS)

    def drive_to(self, x, y):
        """Drives to (x, y) and waits until it arrives (or a time limit passes)."""
        here_x, here_y, _ = self.pose()
        distance = math.hypot(x - here_x, y - here_y)
        self.move_done.clear()
        self.rvr.drive_to_position_si(
            yaw_angle=-bearing(here_x, here_y, x, y),       # the RVR counts angles counterclockwise
            x=x, y=y, linear_speed=DRIVE_SPEED_MPS, flags=0)
        deadline = time.monotonic() + 5 + 3 * distance / DRIVE_SPEED_MPS
        while not self.move_done.is_set():
            self._stop_if_stalled()
            if deadline < time.monotonic():
                print("  (the move did not report finishing; carrying on from where the robot is)")
                break
            time.sleep(0.05)
        time.sleep(SETTLE_SECONDS)

    def distance_mm(self):
        """One reading in millimeters, or None when nothing was seen within range."""
        self.sensor.start_ranging()
        try:
            deadline = time.monotonic() + 1.0
            while not self.sensor.check_for_data_ready():
                if deadline < time.monotonic():
                    return None
                time.sleep(0.005)
            distance = self.sensor.get_distance()
            status = self.sensor.get_range_status() if hasattr(self.sensor, "get_range_status") else 0
            self.sensor.clear_interrupt()
        finally:
            self.sensor.stop_ranging()
        if 0 != status or not SHORTEST_MM <= distance <= LONGEST_MM:
            return None
        return float(distance)

    def close(self):
        steps = []
        if self.rvr is not None:
            steps += [lambda: self.rvr.raw_motors(0, 0, 0, 0), self.rvr.sensor_control.stop, self.rvr.close]
        if self.sensor is not None:
            steps.append(self.sensor.stop_ranging)
        for step in steps:
            try:
                step()
            except Exception as error:         # keep cleaning up; report what failed
                print("Cleanup:", error)


class SimulatedRobot:
    """A pretend robot in a pretend room, for trying the program without a robot."""

    # The room's walls and a box on the floor, as line segments ((x1, y1), (x2, y2)) in meters from the start.
    WALLS = [((-1.6, -1.2), (2.4, -1.2)), ((2.4, -1.2), (2.4, 1.9)), ((2.4, 1.9), (-1.6, 1.9)),
             ((-1.6, 1.9), (-1.6, -1.2)),
             ((0.5, 0.6), (0.9, 0.6)), ((0.9, 0.6), (0.9, 0.9)), ((0.9, 0.9), (0.5, 0.9)), ((0.5, 0.9), (0.5, 0.6))]

    def __init__(self):
        self.random = np.random.default_rng(1)
        self.x, self.y, self.heading = 0.0, 0.0, 0.0
        print("Simulated robot in a pretend 4 m by 3.1 m room with a box in it.")

    def pose(self):
        return self.x, self.y, self.heading

    def turn_to(self, heading):
        self.heading = (heading + self.random.normal(0, 1.5)) % 360

    def drive_to(self, x, y):
        self.x, self.y = x + self.random.normal(0, 0.02), y + self.random.normal(0, 0.02)

    def distance_mm(self):
        x, y, heading = self.pose()
        dx, dy = math.sin(math.radians(heading)), math.cos(math.radians(heading))
        x, y = x + SENSOR_FORWARD_M * dx, y + SENSOR_FORWARD_M * dy
        nearest = None
        for offset in np.linspace(-BEAM_DEGREES / 2, BEAM_DEGREES / 2, 9):   # the nearest thing anywhere in the cone
            ray_dx, ray_dy = math.sin(math.radians(heading + offset)), math.cos(math.radians(heading + offset))
            for (x1, y1), (x2, y2) in self.WALLS:
                hit = ray_hits_segment(x, y, ray_dx, ray_dy, x1, y1, x2, y2)
                if hit is not None and (nearest is None or hit < nearest):
                    nearest = hit
        if nearest is None:
            return None
        distance = 1000 * nearest + self.random.normal(0, 10)
        return distance if SHORTEST_MM <= distance <= LONGEST_MM else None

    def close(self):
        pass


# =================================================================================================================
# Geometry
# =================================================================================================================
def angle_between(a, b):
    """How many degrees apart two headings are (0 to 180)."""
    return abs((a - b + 180) % 360 - 180)


def bearing(from_x, from_y, to_x, to_y):
    """The heading (clockwise from forward) that points from one place to another."""
    return math.degrees(math.atan2(to_x - from_x, to_y - from_y)) % 360


def ray_hits_segment(x, y, dx, dy, x1, y1, x2, y2):
    """How far along the ray from (x, y) in direction (dx, dy) it meets the segment, or None if it misses."""
    sx, sy = x2 - x1, y2 - y1
    denominator = dx * sy - dy * sx
    if abs(denominator) < 1e-12:
        return None
    t = ((x1 - x) * sy - (y1 - y) * sx) / denominator
    u = ((x1 - x) * dy - (y1 - y) * dx) / denominator
    return t if 0 <= t and 0 <= u <= 1 else None


def stations_for(size):
    """The stops: the start (the center) first, then a grid inside the square, in a back-and-forth order."""
    inner = max(0.0, size - 2 * EDGE_MARGIN_M)
    count = min(int(inner // STATION_SPACING_M) + 1, int(math.sqrt(MOST_STATIONS)))     # stops along each side
    spacing = inner / (count - 1) if 1 < count else 0.0     # spread evenly when there are fewer than would fit
    offsets = [(i - (count - 1) / 2) * spacing for i in range(count)]
    grid = []
    for row, y in enumerate(reversed(offsets)):             # far row first
        row_x = offsets if 0 == row % 2 else list(reversed(offsets))
        grid += [(x, y) for x in row_x if 0.2 < math.hypot(x, y)]
    return [(0.0, 0.0)] + grid


# =================================================================================================================
# Mapping
# =================================================================================================================
def scan(robot, readings):
    """Turns all the way around, measuring in each direction. Each reading is (sensor x, sensor y, heading, mm)."""
    for heading in range(0, 360, SCAN_STEP_DEGREES):
        robot.turn_to(heading)
        values = [robot.distance_mm() for _ in range(READINGS_PER_DIRECTION)]
        seen = [value for value in values if value is not None]
        x, y, actual = robot.pose()
        sensor_x = x + SENSOR_FORWARD_M * math.sin(math.radians(actual))
        sensor_y = y + SENSOR_FORWARD_M * math.cos(math.radians(actual))
        # Most readings must agree that something is there; otherwise it counts as nothing seen.
        mm = statistics.median(seen) if len(seen) * 2 > len(values) else None
        readings.append((sensor_x, sensor_y, actual, mm))


def way_is_clear(robot, to_x, to_y):
    """Faces the next stop and checks with the distance sensor that nothing is in the way."""
    x, y, _ = robot.pose()
    robot.turn_to(bearing(x, y, to_x, to_y))
    values = [robot.distance_mm() for _ in range(READINGS_PER_DIRECTION)]
    seen = [value for value in values if value is not None]
    if not seen:
        return True                                         # nothing within the sensor's range
    free = min(seen) / 1000 + SENSOR_FORWARD_M
    return math.hypot(to_x - x, to_y - y) + PATH_CLEARANCE_M <= free


def build_map(readings, size):
    """
    The map as two grids over the square: how many times the beam saw through each cell, and how many times it hit
    something in it.

    A reading of d millimeters means the nearest thing anywhere in the sensor's cone is d away, so every cell in the
    cone closer than that is free. Where in the cone the thing is, the sensor cannot say, so only the cell straight
    ahead at d counts as a hit.
    """
    cells = int(round(size / CELL_M))
    passes = np.zeros((cells, cells), dtype=int)
    hits = np.zeros((cells, cells), dtype=int)
    half = size / 2

    def cell(x, y):
        column, row = int(math.floor((x + half) / CELL_M)), int(math.floor((half - y) / CELL_M))   # row 0 = top
        return (row, column) if 0 <= row < cells and 0 <= column < cells else None

    for x, y, heading, mm in readings:
        reach = (mm if mm is not None else LONGEST_MM) / 1000
        end = None
        if mm is not None:
            end = cell(x + reach * math.sin(math.radians(heading)), y + reach * math.cos(math.radians(heading)))
        seen_through = set()
        rays = max(3, int(BEAM_DEGREES / 2) + 1)                     # about 2 degrees apart
        for offset in np.linspace(-BEAM_DEGREES / 2, BEAM_DEGREES / 2, rays):
            dx, dy = math.sin(math.radians(heading + offset)), math.cos(math.radians(heading + offset))
            for step in range(int((reach - CELL_M) / (CELL_M / 2))):     # stop a cell short of the hit
                place = cell(x + dx * step * CELL_M / 2, y + dy * step * CELL_M / 2)
                if place is not None:
                    seen_through.add(place)
        seen_through.discard(end)
        for row, column in seen_through:
            passes[row, column] += 1
        if end is not None:
            hits[end] += 1
    return passes, hits


def save_points(readings):
    with open(POINTS_FILE, "w", newline="") as file:
        writer = csv.writer(file)
        writer.writerow(["sensor_x_m", "sensor_y_m", "heading_deg", "distance_mm", "hit_x_m", "hit_y_m"])
        for x, y, heading, mm in readings:
            if mm is None:
                writer.writerow(["%.3f" % x, "%.3f" % y, "%.1f" % heading, "", "", ""])
            else:
                hit_x = x + mm / 1000 * math.sin(math.radians(heading))
                hit_y = y + mm / 1000 * math.cos(math.radians(heading))
                writer.writerow(["%.3f" % x, "%.3f" % y, "%.1f" % heading, "%.0f" % mm,
                                 "%.3f" % hit_x, "%.3f" % hit_y])


def draw(readings, visited, size, stalled_at=None):
    passes, hits = build_map(readings, size)
    # 0.5 = not seen (gray), 1 = free (white), 0 = something there (black). A cell counts as "something there" when
    # it was hit at least half as often as the beam saw through it (the wide beam sees through many cells near a hit).
    picture = np.full(passes.shape, 0.5)
    picture[0 < passes] = 1.0
    picture[(0 < hits) & (passes <= 2 * hits)] = 0.0
    rows, columns = np.indices(picture.shape)
    centers_x = -size / 2 + (columns + 0.5) * CELL_M
    centers_y = size / 2 - (rows + 0.5) * CELL_M
    for x, y in visited:                                    # where the robot stood is free too
        picture[np.hypot(centers_x - x, centers_y - y) <= ROBOT_RADIUS_M] = 1.0

    half = size / 2
    figure, axes = plt.subplots(figsize=(6.5, 6.5))
    axes.imshow(picture, cmap="gray", vmin=0, vmax=1, extent=(-half, half, -half, half), interpolation="nearest")
    hit_x = [x + mm / 1000 * math.sin(math.radians(h)) for x, y, h, mm in readings if mm is not None]
    hit_y = [y + mm / 1000 * math.cos(math.radians(h)) for x, y, h, mm in readings if mm is not None]
    axes.scatter(hit_x, hit_y, s=6, color="tab:red", label="measured points")
    path_x, path_y = zip(*visited)
    axes.plot(path_x, path_y, "-o", color="tab:blue", markersize=5, label="robot stops and path")
    if stalled_at is not None:
        axes.plot([path_x[-1], stalled_at[0]], [path_y[-1], stalled_at[1]], ":", color="tab:blue")
        axes.plot(*stalled_at, "x", color="red", markersize=12, markeredgewidth=3, label="ran into something")
    axes.annotate("start", (0, 0), textcoords="offset points", xytext=(6, -12), color="tab:blue")
    axes.annotate("", xy=(0, 0.25), xytext=(0, 0), arrowprops={"arrowstyle": "->", "color": "tab:blue"})
    axes.set_xlim(-half, half)
    axes.set_ylim(-half, half)
    axes.set_aspect("equal")
    axes.set_xlabel("meters (right of the start)")
    axes.set_ylabel("meters (ahead of the start)")
    axes.set_title("Room map, %.1f m square, %d readings" % (size, len(readings)))
    axes.legend(loc="upper right", fontsize=8, framealpha=0.9)
    figure.tight_layout()
    figure.savefig(PICTURE_FILE, dpi=120)
    plt.close(figure)


def ask_size():
    """The square's size: from the command line, else asked for, else the default."""
    words = [word for word in sys.argv[1:] if not word.startswith("--")]
    text = words[0] if words else None
    if text is None:
        try:
            text = input("Size of the square to map, in meters (Enter for %.1f): " % DEFAULT_SIZE_M).strip()
        except EOFError:                                     # nothing to read, as with "python3 -" over SSH
            text = ""
    try:
        size = float(text) if text else DEFAULT_SIZE_M
    except ValueError:
        print("'%s' is not a number; using %.1f m." % (text, DEFAULT_SIZE_M))
        size = DEFAULT_SIZE_M
    if not SMALLEST_SIZE_M <= size <= LARGEST_SIZE_M:
        size = min(LARGEST_SIZE_M, max(SMALLEST_SIZE_M, size))
        print("The size must be from %.1f to %.1f m; using %.1f m." % (SMALLEST_SIZE_M, LARGEST_SIZE_M, size))
    return size


def main():
    size = ask_size()
    stations = stations_for(size)
    directions = 360 // SCAN_STEP_DEGREES
    estimate = len(stations) * directions * SECONDS_PER_DIRECTION + len(stations) * size / DRIVE_SPEED_MPS
    print("Mapping a %.1f m square with %d stops, %d directions each; about %d minutes."
          % (size, len(stations), directions, max(1, round(estimate / 60))))

    readings = []
    visited = []
    stalled_at = None
    robot = None
    try:
        robot = SimulatedRobot() if "--simulate" in sys.argv else RealRobot()
        # A stop that is blocked from where the robot is gets one more try at the end, from another stop.
        to_visit = [(number, x, y, True) for number, (x, y) in enumerate(stations, 1)]
        while to_visit:
            number, x, y, may_retry = to_visit.pop(0)
            if 1 < number:
                if not way_is_clear(robot, x, y):
                    if may_retry:
                        print("Stop %d at (%.2f, %.2f): something is in the way; trying again later." % (number, x, y))
                        to_visit.append((number, x, y, False))
                    else:
                        print("Stop %d at (%.2f, %.2f): still blocked; skipped." % (number, x, y))
                    continue
                robot.drive_to(x, y)
            here_x, here_y, _ = robot.pose()
            visited.append((here_x, here_y))
            print("Stop %d of %d at (%.2f, %.2f): scanning..." % (number, len(stations), here_x, here_y))
            scan(robot, readings)
        if 1 < len(visited) and way_is_clear(robot, 0, 0):
            print("Returning to the start.")
            robot.drive_to(0, 0)
            visited.append(robot.pose()[:2])
    except Stalled:
        stalled_at = robot.pose()[:2]
        print("The robot ran into something at (%.2f, %.2f) and a motor stalled; mapping stopped there."
              % stalled_at)
        print("Move the robot away from it by hand. The map shows what was mapped before, and a red X where it hit.")
    except KeyboardInterrupt:
        print("Stopped by the Stop button (Ctrl+C); drawing what was mapped so far.")
    finally:
        if robot is not None:
            robot.close()

    if not readings:
        print("No readings, so there is no map.")
        return
    save_points(readings)
    draw(readings, visited or [(0.0, 0.0)], size, stalled_at)
    seen = sum(1 for reading in readings if reading[3] is not None)
    print("%d readings, %d of them hit something. Saved %s and %s." % (len(readings), seen, PICTURE_FILE,
                                                                      POINTS_FILE))
    try:
        from runner_io import show              # shows the map in the app when run from there
        show(PICTURE_FILE)
    except ImportError:
        pass


if __name__ == "__main__":
    main()
