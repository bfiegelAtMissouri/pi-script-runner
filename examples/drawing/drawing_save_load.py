"""
Saving a drawing and carrying on later, with runner_io's draw object. Needs no robot: a pretend robot explores a
pretend room a few steps per run, and each run carries on where the last one stopped.

The first run:
  1. Draws the room on a colored background, and the robot at its start.
  2. Drives the robot to its next few targets. Each target is marked with a "plus" until the robot gets there, then
     removed with draw.remove().
  3. Saves the drawing, blanks the canvas for a moment, and loads it back: draw.save(), draw.clear(), draw.load().
  4. Saves the drawing as room.json and sends it to the laptop with show().

To carry on in the next run:
  - In the app, click "Save..." on the room.json card (in the browser page, its Save link) and keep the file.
  - Add room.json with this script, then press Run. The script finds room.json next to it, loads it, and the robot
    carries on from where it stopped. Add the new room.json each time to keep going; after the last target the
    tour starts again.
"""
import json
import os
import time

from runner_io import draw, show

SAVED_HERE = "room.json"         # a room.json added with the script (files added with it are put next to it)
SAVE_TO = "/tmp/room.json"       # where this run saves its drawing before sending it to the laptop
BLANK_COPY = "/tmp/room_before_blank.json"
STEPS_PER_RUN = 3                # targets the robot drives to in each run
STEP = 0.05                      # meters the robot moves each update
PAUSE = 0.04                     # seconds between updates

# The targets the robot visits, in order, in meters (x to the right, y up). Each run visits the next STEPS_PER_RUN.
TARGETS = [(0.4, 0.4), (2.6, 0.4), (2.6, 1.6), (1.8, 1.6), (1.8, 0.9), (0.9, 0.9), (0.9, 1.7), (0.4, 1.7)]
RUN_COLORS = ["royalblue", "seagreen", "darkorange", "purple", "crimson"]   # each run's path in its own color


def new_drawing():
    """The room, for a first run."""
    draw.canvas(-0.2, 3.2, -0.5, 2.2, grid=0.5, background="aliceblue")
    draw.rect(0, 0, 3, 2, color="dimgray", fill="white", width=3)                       # the room
    draw.rect(1.3, 1.25, 0.4, 0.75, color="saddlebrown", fill="burlywood")              # a bookcase
    draw.circle(2.1, 0.9, 0.18, color="darkgreen", fill="lightgreen")                   # a plant
    draw.text(0, -0.25, "Each run's path is a new color. Targets still to visit are marked with +.", size=12)


def where_it_stopped(path):
    """
    Reads a saved drawing to find where the last run left off: the robot's position and how many targets it has
    visited. draw.save() writes JSON, so a script can read it like any JSON file.
    """
    with open(path) as file:
        saved = json.load(file)
    position, visited = TARGETS[0], 0
    for shape in saved["shapes"]:
        if "robot" == shape.get("name"):
            position = tuple(shape["points"][0])
        if "visited" == shape.get("name"):
            visited = int(shape["text"].split()[1])                 # the text reads "Visited 5 targets"
    return position, visited


def drive(start, goal, path_color):
    """Moves the robot from start to goal in small steps, drawing its path as it goes. Returns where it ends."""
    x, y = start
    while True:
        dx, dy = goal[0] - x, goal[1] - y
        distance = (dx * dx + dy * dy) ** 0.5
        if distance < 1e-9:
            return x, y
        move = min(STEP, distance)
        new_x, new_y = x + dx / distance * move, y + dy / distance * move
        draw.line(x, y, new_x, new_y, color=path_color, width=3)
        draw.marker(new_x, new_y, shape="triangle", size=16, color="red", name="robot")    # replaces the last one
        x, y = new_x, new_y
        time.sleep(PAUSE)


def blank_and_reload():
    """Saves the drawing, blanks the canvas for a moment, and brings the drawing back."""
    draw.save(BLANK_COPY)
    draw.clear()                       # erases every shape; the canvas() settings (edges, grid, background) stay
    draw.text(0.5, 1.0, "Blank: the drawing is saved and comes back in 2 seconds", size=16)
    time.sleep(2)
    count = draw.load(BLANK_COPY)      # erases the canvas again and draws everything that was saved
    print("Loaded %d shapes back from %s" % (count, BLANK_COPY))


def main():
    if os.path.exists(SAVED_HERE):
        count = draw.load(SAVED_HERE)
        position, visited = where_it_stopped(SAVED_HERE)
        print("Carrying on from %s: %d shapes, robot at (%.2f, %.2f), %d targets visited so far."
              % (SAVED_HERE, count, position[0], position[1], visited))
    else:
        print("No room.json added, so this is a first run.")
        new_drawing()
        position, visited = TARGETS[0], 0
        draw.marker(position[0], position[1], shape="triangle", size=16, color="red", name="robot")

    run = visited // STEPS_PER_RUN                     # 0 for the first run
    color = RUN_COLORS[run % len(RUN_COLORS)]
    targets = [TARGETS[(visited + step) % len(TARGETS)] for step in range(1, STEPS_PER_RUN + 1)]
    for number, (x, y) in enumerate(targets):          # mark the targets still to visit
        draw.marker(x, y, shape="plus", size=14, color=color, name="target %d" % number)

    for number, target in enumerate(targets):
        print("Driving to (%.1f, %.1f)" % target)
        position = drive(position, target, color)
        draw.remove("target %d" % number)              # reached: its marker is no longer needed
        visited += 1
        draw.text(2.2, -0.4, "Visited %d targets" % visited, size=12, name="visited")      # replaces the last one

    blank_and_reload()

    show(draw.save(SAVE_TO))
    draw.save_svg("/tmp/room.svg")
    print("Saved the drawing as %s and sent it to the laptop as room.json." % SAVE_TO)
    print("To carry on: save room.json from the app (or the browser page), add it with this script, and Run again.")


main()
