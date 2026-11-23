"""
Room map: the robot maps a square area around where it starts and draws the layout as it goes.

You choose the size of the square (in meters). The robot starts in its center, facing what will be the top of the
map. At each stop it turns in place and measures the distance to whatever is in each direction with the distance
sensor. It then drives to the next stop, if the way there is clear, and scans again. Anything outside the square is
left off the map, so a large classroom is not mapped end to end.

The app draws the map as the robot works (with runner_io's draw), beside the output:
  blue        the sensor's cone at each measurement while the robot scans, drawn out to the distance measured.
              The cones are see-through, so where they overlap (seen more often) the blue is darker; the latest
              cone is outlined.
  red arcs    each distance measured: the sensor says how far the nearest thing in its cone is, not where in the
              cone, so the thing is somewhere on the arc
  With --lines instead: a see-through line along the middle of each cone, and a red dot where it hit. Compare the two
  to see how much the cone's width matters.
  white       space the sensor saw through (free)
  black       where the sensor hit something (a wall, a chair leg, a box)
  gray        not seen
  blue        the robot's stops and its path
  orange      the robot now, with a line showing which way it faces
After each stop, the white, black, and gray map is redrawn from everything seen so far. At the end the map is saved
on the robot as /tmp/room_map.svg (it opens in a web browser), with room_map_points.csv (every measured point, for
your own analysis); "Save drawing..." in the app saves the same picture on your computer.

Run it:
  From the app:  press Run and type the size when asked (Enter for the default), or give it in Settings,
                 "Arguments for room_map.py" (for example 2.0), so it is not asked each time.
  No robot:      add --simulate (in Settings, "Arguments for room_map.py": 2.0 --simulate) for a pretend room.
  Lines:         add --lines to draw each measurement as a line instead of a cone (for example 2.0 --simulate --lines).
                 On a computer with runner_io installed (see docs/runner-io.md): python3 room_map.py 2.0 --simulate

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
forward, and numpy. Works on the 32-bit and the 64-bit robots.
"""
import csv
import math
import statistics
import sys
import threading
import time

import numpy as np

try:
    from runner_io import draw
except ImportError:
    sys.exit("room_map.py draws with runner_io: run it from the app (or the runner), or install runner_io "
             "(see docs/runner-io.md).")

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

# ---- Drawing ----------------------------------------------------------------------------------------------------
UNSEEN_COLOR = "lightgray"
FREE_COLOR = "white"
SOMETHING_COLOR = "black"
BEAM_COLOR = "dodgerblue"
BEAM_OPACITY = 0.12             # see-through, so overlapping cones build up
LINE_OPACITY = 0.5
BEAM_EDGE_COLOR = "steelblue"
HIT_COLOR = "red"
PATH_COLOR = "royalblue"
ROBOT_COLOR = "darkorange"
GRID_COLOR = "silver"

LINES = "--lines" in sys.argv   # lines of sight instead of cones (see the top of the file)

POINTS_FILE = "/tmp/room_map_points.csv"
MAP_FILE = "/tmp/room_map.svg"


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
        time.sleep(0.05)                        # slow enough to watch the map being drawn
        self.heading = (heading + self.random.normal(0, 1.5)) % 360

    def drive_to(self, x, y):
        time.sleep(0.5)
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
def scan(robot, readings, size):
    """
    Turns all the way around, measuring in each direction, and draws each measurement. Each reading is
    (sensor x, sensor y, heading, mm).
    """
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
        draw_reading(readings[-1], size)
        draw_robot(robot)


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


def map_picture(readings, visited, size):
    """
    The map as a grid of cells (row 0 at the top): 0.5 = not seen, 1 = free, 0 = something there. A cell counts as
    "something there" when it was hit at least half as often as the beam saw through it (the wide beam sees through
    many cells near a hit).
    """
    passes, hits = build_map(readings, size)
    picture = np.full(passes.shape, 0.5)
    picture[0 < passes] = 1.0
    picture[(0 < hits) & (passes <= 2 * hits)] = 0.0
    rows, columns = np.indices(picture.shape)
    centers_x = -size / 2 + (columns + 0.5) * CELL_M
    centers_y = size / 2 - (rows + 0.5) * CELL_M
    for x, y in visited:                                    # where the robot stood is free too
        picture[np.hypot(centers_x - x, centers_y - y) <= ROBOT_RADIUS_M] = 1.0
    return picture


def hit_point(reading):
    """Where a reading hit something, or None when it saw nothing in range."""
    x, y, heading, mm = reading
    if mm is None:
        return None
    return x + mm / 1000 * math.sin(math.radians(heading)), y + mm / 1000 * math.cos(math.radians(heading))


def inside(point, size):
    return abs(point[0]) <= size / 2 and abs(point[1]) <= size / 2


def start_drawing(size):
    """The square to map, gray (not seen yet)."""
    half = size / 2
    draw.canvas(-half, half, -half, half)
    draw.rect(-half, -half, size, size, color="none", fill=UNSEEN_COLOR)


def draw_grid(size):
    """
    Grid lines with their distances from the start, in meters. They are drawn here, over the map, because the
    canvas() grid would be hidden under the map's white, black, and gray.
    """
    half = size / 2
    step = 0.5 if size <= 3 else 1.0
    lines = [k * step for k in range(int(math.ceil(-half / step)), int(math.floor(half / step)) + 1)]
    for value in lines:
        draw.line(value, -half, value, half, color=GRID_COLOR, width=1)
        draw.line(-half, value, half, value, color=GRID_COLOR, width=1)
    for value in lines:
        draw.text(value + 0.02, -half + 0.03, "%g" % value, size=10, color="gray")
        if value != lines[0]:
            draw.text(-half + 0.02, value + 0.03, "%g" % value, size=10, color="gray")


def draw_reading(reading, size):
    """
    One measurement as the sensor saw it. With cones (the default): its cone (BEAM_DEGREES wide) out to the distance
    measured, see-through, and a red arc at that distance, because the sensor says how far the nearest thing in the
    cone is, not where in the cone. With --lines: a line along the middle of the cone, and a red dot where it hit.
    Cones and lines stop at the square's edge.
    """
    x, y, heading, mm = reading
    reach = (mm if mm is not None else LONGEST_MM) / 1000
    point = hit_point(reading)
    hit_inside = point is not None and inside(point, size)
    if LINES:
        length = min(reach, distance_to_edge(x, y, heading, size))
        end_x, end_y = x + length * math.sin(math.radians(heading)), y + length * math.cos(math.radians(heading))
        draw.line(x, y, end_x, end_y, color=BEAM_EDGE_COLOR, width=1, opacity=LINE_OPACITY)
        if hit_inside:
            draw.marker(point[0], point[1], shape="circle", size=5, color=HIT_COLOR)
        return
    for side in (-BEAM_DEGREES / 2, 0, BEAM_DEGREES / 2):  # keep the cone's sides and middle inside the square
        reach = min(reach, distance_to_edge(x, y, heading + side, size))
    if 0 < reach:
        draw.sector(x, y, reach, heading, BEAM_DEGREES, color="none", fill=BEAM_COLOR, opacity=BEAM_OPACITY)
        draw.sector(x, y, reach, heading, BEAM_DEGREES, color=BEAM_EDGE_COLOR, width=1, name="latest cone")
    if hit_inside:
        draw.arc(x, y, mm / 1000, heading, BEAM_DEGREES, color=HIT_COLOR, width=2)


def distance_to_edge(x, y, heading, size):
    """How far from (x, y), inside the square, it is to the square's edge in a direction (compass degrees)."""
    dx, dy = math.sin(math.radians(heading)), math.cos(math.radians(heading))
    half = size / 2
    limits = [float("inf")]
    for d, position in ((dx, x), (dy, y)):
        if 1e-9 < abs(d):
            limits.append(((half if 0 < d else -half) - position) / d)
    return max(0.0, min(limits))


def draw_robot(robot):
    """The robot where it is now, with a line toward where it faces."""
    x, y, heading = robot.pose()
    draw.line(x, y, x + 0.2 * math.sin(math.radians(heading)), y + 0.2 * math.cos(math.radians(heading)),
              color=ROBOT_COLOR, width=3, name="robot heading")
    draw.marker(x, y, shape="circle", size=12, color=ROBOT_COLOR, name="robot")


def draw_map(readings, visited, size, robot=None, stalled_at=None):
    """Redraws the whole map from everything measured so far: free, something there, not seen, and the path."""
    draw.clear()
    start_drawing(size)
    picture = map_picture(readings, visited, size)
    half = size / 2
    # Each row's runs of free cells and of "something there" cells, as rectangles. Each is outlined in its own
    # color so that no thin seams show between neighboring rows.
    for row in range(picture.shape[0]):
        y = half - (row + 1) * CELL_M
        column = 0
        while column < picture.shape[1]:
            value = picture[row, column]
            end = column
            while end < picture.shape[1] and picture[row, end] == value:
                end += 1
            if 0.5 != value:
                color = FREE_COLOR if 1.0 == value else SOMETHING_COLOR
                draw.rect(-half + column * CELL_M, y, (end - column) * CELL_M, CELL_M, color=color, fill=color,
                          width=1)
            column = end
    draw_grid(size)
    hits = [point for point in map(hit_point, readings) if point is not None and inside(point, size)]
    if hits:
        draw.markers(hits, shape="circle", size=4, color=HIT_COLOR)
    if 1 < len(visited):
        draw.polyline(visited, color=PATH_COLOR, width=2)
    draw.markers(visited, shape="circle", size=8, color=PATH_COLOR)
    if stalled_at is not None:
        draw.line(visited[-1][0], visited[-1][1], stalled_at[0], stalled_at[1], color=PATH_COLOR, width=1)
        draw.marker(stalled_at[0], stalled_at[1], shape="x", size=18, color="red")
        draw.text(stalled_at[0] + 0.06, stalled_at[1] + 0.06, "ran into something", size=12, color="red")
    draw.line(0, 0, 0, 0.25, color=PATH_COLOR, width=2)    # the start, and which way the robot first faced
    draw.marker(0, 0.25, shape="triangle", size=10, color=PATH_COLOR)
    draw.text(0.05, -0.1, "start", size=12, color=PATH_COLOR)
    if robot is not None:
        draw_robot(robot)


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
    start_drawing(size)
    draw_grid(size)
    try:
        robot = SimulatedRobot() if "--simulate" in sys.argv else RealRobot()
        draw_robot(robot)
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
                draw_robot(robot)
            here_x, here_y, _ = robot.pose()
            visited.append((here_x, here_y))
            print("Stop %d of %d at (%.2f, %.2f): scanning..." % (number, len(stations), here_x, here_y))
            scan(robot, readings, size)
            draw_map(readings, visited, size, robot)
        if 1 < len(visited) and way_is_clear(robot, 0, 0):
            print("Returning to the start.")
            robot.drive_to(0, 0)
            visited.append(robot.pose()[:2])
            draw_robot(robot)
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
    draw_map(readings, visited or [(0.0, 0.0)], size, stalled_at=stalled_at)
    draw.save_svg(MAP_FILE)
    seen = sum(1 for reading in readings if reading[3] is not None)
    print("%d readings, %d of them hit something. Saved %s and %s." % (len(readings), seen, MAP_FILE, POINTS_FILE))


if __name__ == "__main__":
    main()
