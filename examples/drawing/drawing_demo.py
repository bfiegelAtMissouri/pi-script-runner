"""
Drawing a map with runner_io's draw object. Needs no robot: a pretend robot drives around a pretend room.

The app shows the drawing as it is made (beside the output) and can save it as an SVG file with "Save drawing...".
The script also saves the same picture on the Pi as /tmp/drawing_demo.svg. (show(draw.save_svg(...)) would also send
that file to the laptop, but the app already has the drawing.)

Things to notice:
  - The drawing uses the script's own units (meters here), with x to the right and y up.
  - canvas() fixes the part shown, so the view does not jump as the robot moves.
  - Sizes of rectangles and circles are in meters; marker sizes, line widths, and text sizes are in pixels.
  - Drawing with name="robot" replaces the earlier robot marker instead of adding another one.
  - draw.sector() draws the pretend distance sensor's cone. Its direction here comes from math.atan2(), which counts
    in math angles (0 to the right, counterclockwise), so the call says angles="math".
"""
import math
import time

from runner_io import draw, plot

ROOM_WIDTH = 3.0                # meters
ROOM_DEPTH = 2.0
STEP = 0.05                     # meters the robot moves each step
PAUSE = 0.05                    # seconds between steps
SVG_FILE = "/tmp/drawing_demo.svg"
SENSOR_DEGREES = 27              # how wide the pretend distance sensor's cone is (as the VL53L1X's)
SENSOR_REACH = 0.6               # meters the cone is drawn out to

# The corners the pretend robot drives to, in order.
WAYPOINTS = [(0.3, 0.3), (2.6, 0.3), (2.6, 1.0), (1.4, 1.0), (1.4, 1.7), (0.3, 1.7), (0.3, 0.3)]

# What a pretend distance sensor "sees" on the way: (meters traveled when it is seen, x, y of the point seen).
SEEN = [(2.0, 2.95, 0.25), (2.4, 2.95, 0.6), (2.8, 2.95, 1.0),         # the right wall
        (3.5, 2.1, 1.35), (3.7, 1.9, 1.35),                          # the table
        (5.3, 1.0, 1.95), (5.7, 0.6, 1.95),                          # the far wall
        (6.5, 0.05, 1.2), (7.1, 0.05, 0.6)]                          # the left wall


def draw_room():
    draw.canvas(-0.2, ROOM_WIDTH + 0.2, -0.7, ROOM_DEPTH + 0.2, grid=0.5)
    draw.rect(0, 0, ROOM_WIDTH, ROOM_DEPTH, color="dimgray", width=3)                    # walls
    draw.rect(1.8, 1.4, 0.6, 0.4, color="saddlebrown", fill="burlywood")                # a table
    draw.circle(0.8, 1.15, 0.15, color="darkgreen", fill="lightgreen")                  # a plant
    draw.polygon([(1.7, 0.45), (2.3, 0.45), (2.4, 0.8), (1.6, 0.8)], color="none", fill="lavender")  # a rug
    draw.text(1.85, 1.6, "table", size=12)

    # A legend below the room: every marker shape, with its name, in two rows of three.
    shapes = ["circle", "square", "triangle", "diamond", "x", "plus"]
    for number, shape in enumerate(shapes):
        x = 0.1 + 1.0 * (number % 3)
        y = -0.25 if number < 3 else -0.55
        draw.marker(x, y, shape=shape, size=12, color="steelblue")
        draw.text(x + 0.1, y - 0.05, shape, size=12)


def drive():
    """Moves the pretend robot through WAYPOINTS, drawing its path and position as it goes."""
    x, y = WAYPOINTS[0]
    path = [(x, y)]
    traveled = 0.0
    seen = 0
    for goal_x, goal_y in WAYPOINTS[1:]:
        while True:
            dx, dy = goal_x - x, goal_y - y
            distance = math.hypot(dx, dy)
            if distance < 1e-9:
                break
            move = min(STEP, distance)
            x, y = x + dx / distance * move, y + dy / distance * move
            traveled += move
            path.append((x, y))

            draw.polyline(path, color="royalblue", width=2, name="path")                # the path so far
            facing = math.degrees(math.atan2(dy, dx))                                   # math angle of travel
            draw.sector(x, y, SENSOR_REACH, facing, SENSOR_DEGREES, color="goldenrod", fill="lightyellow",
                        width=1, name="sensor", angles="math")                          # what the sensor sees
            draw.marker(x, y, shape="triangle", size=16, color="red", name="robot")      # where it is now
            plot(traveled_m=round(traveled, 2))

            # Mark each point the pretend sensor sees, once the robot has come far enough to see it.
            while seen < len(SEEN) and SEEN[seen][0] <= traveled:
                draw.marker(SEEN[seen][1], SEEN[seen][2], shape="x", size=10, color="crimson")
                seen += 1
            time.sleep(PAUSE)
    return traveled


def main():
    print("Drawing the room")
    draw_room()
    print("Driving the pretend robot")
    traveled = drive()
    draw.remove("sensor")
    draw.marker(*WAYPOINTS[0], shape="diamond", size=14, color="darkorange", name="robot")
    draw.text(0.4, 0.15, "home", size=12, color="darkorange")
    print("Traveled %.2f m" % traveled)
    draw.save_svg(SVG_FILE)
    print("Saved", SVG_FILE)


main()
