# The `runner_io` helper module

`runner_io` lets a script send speech, sounds, images, graphs, and drawings to the Script Runner app that started
it. The app uploads it with every script, next to it, so it is always importable and always the app's own version.

```python
from runner_io import say, play, show, plot, draw
```

## Functions

| Function | Effect in the app |
| :--- | :--- |
| `say(text)` | Speaks `text` with the computer's text-to-speech |
| `play(filename)` | Plays a sound file (`.wav` everywhere; see [app.md](app.md#speech-sound-and-images) for `.mp3` and `.ogg`) |
| `show(filename)` | Sends any file to the app, which keeps it beside the output and can save it: images are displayed, text files (`.csv`, `.txt`, `.json`, ...) show their first lines, and other files show their name and size |
| `plot(name=value, ...)` | Adds numbers to a live graph, one line per name. `plot(value)` with no name plots it as "value" |
| `draw.line(...)`, `draw.marker(...)`, ... | Draws a picture, such as a map, shown as it is drawn and saved as an SVG file. See [Drawing](#drawing) |

`plot()` is meant for a loop, one call per reading:

```python
while True:
    plot(distance=robot.read_distance(), light=robot.read_ambient_light())
    time.sleep(0.2)
```

The graph's horizontal axis is the seconds since the first `plot()` of the run, and its vertical scale fits the
values. **Save data…** beside the graph saves every value as a CSV file, one row per call and one column per name.
Values that are not numbers are skipped.

## Drawing

`draw` makes a picture of lines, shapes, markers, and text, for example a map of a room. The app shows it beside the
output as it is drawn, and **Save drawing…** saves it as an SVG file (it opens in a web browser and in most drawing
programs). `draw.save_svg(path)` writes the same file on the Pi, so a
script run without the app still has its picture.

```python
from runner_io import draw

draw.canvas(0, 3, 0, 2, grid=0.5)               # show x from 0 to 3 and y from 0 to 2, grid lines every 0.5
draw.rect(0, 0, 3, 2, color="dimgray", width=3)  # the walls
draw.circle(0.8, 1.2, 0.15, fill="lightgreen")
draw.marker(2.9, 0.6, shape="x", color="crimson")
while driving:
    draw.polyline(path, color="royalblue", name="path")
    draw.marker(x, y, shape="triangle", color="red", name="robot")
draw.save_svg("/tmp/map.svg")
```

The drawing uses the script's own units (for example meters), with **x to the right and y up**, and the same scale
both ways, so a square looks square.

| Call | Draws |
| :--- | :--- |
| `draw.line(x1, y1, x2, y2, color, width)` | A straight line |
| `draw.polyline(points, color, width)` | Lines joining the points in order, for example a path: `[(0, 0), (1, 0), (1, 1)]` |
| `draw.polygon(points, color, fill, width)` | A closed shape through the points |
| `draw.rect(x, y, w, h, color, fill, width)` | A rectangle `w` wide and `h` tall whose lower-left corner is `(x, y)` |
| `draw.circle(x, y, r, color, fill, width)` | A circle with radius `r` |
| `draw.text(x, y, text, color, size)` | Text whose line starts at `(x, y)` |
| `draw.sector(x, y, r, direction, spread, color, fill, width, inner=0, angles="compass")` | A wedge from `(x, y)` out to radius `r`, centered on `direction` and `spread` degrees wide (more than 0, less than 360), for example a distance sensor's cone. With `inner` it starts that far out instead (a band) |
| `draw.arc(x, y, r, direction, spread, color, width, angles="compass")` | Part of a circle's edge, for example where something a distance sensor measured could be |
| `draw.marker(x, y, shape, size, color)` | A marker centered on `(x, y)`: `"circle"`, `"square"`, `"triangle"`, `"diamond"`, `"x"`, or `"plus"` |
| `draw.markers(points, shape, size, color)` | The same marker at many points, sent as one shape (faster than many `marker()` calls) |
| `draw.canvas(xmin, xmax, ymin, ymax, grid, background)` | Fixes the part shown, draws grid lines `grid` apart, and fills the picture with `background`. Without the edges (or with `draw.canvas()`), the view fits everything drawn. Each call sets all three again |
| `draw.clear()` | Erases everything drawn (the `canvas()` settings stay) |
| `draw.remove(name)` | Erases the shape drawn with that name |
| `draw.save_svg(path)` | Writes the drawing as an SVG file on the Pi and returns `path`, so `show(draw.save_svg("/tmp/map.svg"))` sends it to the laptop too |
| `draw.save(path)` | Saves the drawing itself (the `canvas()` settings and every shape, with its name) so a later run can load it |
| `draw.load(path)` | Replaces the drawing with one `save()` wrote, shows it, and returns the number of shapes, so the script can carry on drawing |

`direction` counts in compass degrees by default (`angles="compass"`): 0 is up (+y) and angles grow clockwise, like
the robot's heading. With `angles="math"`, 0 is to the right (+x) and angles grow counterclockwise, like
`math.atan2()`. Each call says which it uses, so a script can see on the line itself how its angle counts. Getting it
wrong does not look broken (the wedge points somewhere else), so when a wedge points the wrong way, check this first.

```python
draw.sector(x, y, distance, heading, 27, fill="lightskyblue")                   # the VL53L1X's cone, about 27 degrees
draw.arc(x, y, distance, heading, 27, color="red")                                # the thing it measured is on this arc
draw.sector(x, y, 0.5, math.degrees(math.atan2(dy, dx)), 27, angles="math")       # a direction from atan2
```

Every shape also takes `opacity`, from 0 (invisible) to 1 (solid, the default). `opacity=0.2` makes a shape
see-through, for example a sensor's cone; where see-through shapes overlap, the color builds up, so areas seen more
often look darker. Leave the things that are certainly there, such as the points a sensor hit, solid.

Every shape also takes `name`. A shape drawn with a name replaces the earlier shape with that name and is drawn on
top, which moves something, such as the robot, without leaving copies behind.

Sizes:

- Lengths of lines, rectangles, and circles are in the drawing's units, so a 0.5 m table stays 0.5 m.
- Line `width`, marker `size`, and text `size` are in **pixels**, so they stay readable however large the drawing is.
- The defaults are 2-pixel lines, 10-pixel markers, and 14-pixel text.

Colors are web color names (`"red"`, `"gray"`, `"steelblue"`, `"lightgreen"`, ...), `"#RRGGBB"`, or `"none"`. The
default `color` is black and the default `fill` is none. The default `background` is none: the saved SVG file is
transparent, and the app shows it on white, as a web browser shows the file. The grid is drawn
over the background and under the shapes, so shapes filled with a color hide it. A color that is not one of these raises `ValueError`, as
does a point that is not a pair of numbers.

Fixed edges (`canvas(xmin, xmax, ymin, ymax)`) keep a map from rescaling every time something is drawn near its
edge; the view does not grow to show shapes outside them. A run keeps up to 20000 shapes (the oldest are dropped after that), and one
shape can have up to 5000 points. A shape drawn again with the same `name` does not add to the count.

The app clears the drawing at the start of each run. **Save run…** includes it as `drawing.svg`.

### Carrying on in a later run

`save()` and `load()` let one run continue another's drawing, for example a second mapping run that adds to the
first:

```python
import os
from runner_io import draw

MAP = "~/maps/room.json"                     # ~ is the home folder on the Pi
if os.path.exists(os.path.expanduser(MAP)):
    draw.load(MAP)                           # the earlier map, with its canvas settings and names
else:
    draw.canvas(-1, 1, -1, 1, grid=0.5)
...
draw.marker(x, y, shape="triangle", name="robot")   # replaces the loaded robot
draw.save(MAP)
```

They also work within one run: `draw.save("/tmp/before.json")`, then `draw.clear()` for a blank canvas (add
`draw.canvas()` to reset its settings too), then later `draw.load("/tmp/before.json")` to bring the saved drawing back.

To keep a drawing with the student rather than on the robot, send it to the laptop and add it with the script next
time: `show(draw.save("/tmp/room.json"))` sends it (save it from its card in the app), and a `room.json` added with the script is put next to it on the Pi, so `draw.load("room.json")`
finds it. The example `drawing/drawing_save_load.py` works this way.

To keep a drawing on the robot for a later run, save somewhere that lasts: the script's own folder is emptied at the start of each run, and `/tmp` when the Pi
restarts. `save()` makes the folder if it is missing. The file is JSON, so a script can also read it. `load()` checks
every shape as it was checked when first drawn, and raises `ValueError` (naming the shape) for a file that is not a
saved drawing.

`filename` is either relative to the script's folder on the Pi (for example
`photo.jpg`) or an absolute path in `/tmp` (for example `/tmp/photo.jpg`).
Files anywhere else cannot be served.

The script's folder is erased at the start of the next run. `/tmp` is shared by
everyone who uses the Pi and is normally emptied when the Pi restarts, so files
there can be overwritten by another group's script.

Speech is queued: several `say()` calls in a row are spoken in order. `say()`
returns immediately, so the script does not wait for speech to finish; add a
`time.sleep()` if the timing matters.

## Completion and help in your editor

Editors such as VS Code, PyCharm, and Thonny look up `from runner_io import say` in the Python on your computer. If
`runner_io` is not installed there, the import is underlined as missing and the editor offers no completion or help
for it. The script still runs on the robot either way: the app and the runner provide `runner_io` there.

To get completion and help, install `runner_io` into the Python your editor uses. Download
`runner_io-<version>-py3-none-any.whl` from the same Releases page as the app, then, in a terminal in the folder
where you saved it:

```bash
python -m pip install runner_io-<version>-py3-none-any.whl     # Windows: py -m pip install ...
```

Use the file's full name in place of `runner_io-<version>-py3-none-any.whl`. Without a release file, install it from
a copy of the repository (on GitHub, **Code > Download ZIP**, then unzip it):

```bash
python -m pip install <repository folder>/runner
```

- In VS Code, the Python it uses is the one shown in the status bar (**Python: Select Interpreter** changes it).
  Install into that one; if the project uses a virtual environment, install into the environment.
- On a computer where installing for everyone is not allowed, add `--user` after `install`.
- To update, install the newer file the same way.
- Without installing anything, a copy of `runner_io.py` in the same folder as the script also works for the editor.
  Adding that copy in the app does no harm: the app always sends its own `runner_io` to the robot.

## Local functions

`remote_execute()` calls a Python function that runs on the laptop, and waits for its result. The functions are
in `.py` files added in the app with **Local** checked beside them. Those files stay on the laptop and are imported
there, with the laptop's Python, when the run starts, so slow setup (such as loading a YOLO model) happens once.

```python
# vision.py, added with Local checked: runs on the laptop
from ultralytics import YOLO
model = YOLO("yolo11n.pt")

def detect(photo, min_confidence):
    result = model(photo, verbose=False)[0]
    return [result.names[int(b.cls)] for b in result.boxes if min_confidence <= float(b.conf)]
```

```python
# robot script: runs on the Pi
from runner_io import remote_execute, shared_runner_file, RemoteExecutionError

seen = remote_execute("detect", shared_runner_file("/tmp/view.jpg"), 0.5)   # for example ["person"]
```

| Function | Effect |
| :--- | :--- |
| `remote_execute(name, *args)` | Calls the local function `name` with `args` and returns its result |
| `shared_runner_file(path)` | Marks an argument as a file on the Pi. The runner copies it to the laptop, and the function receives the copy's path as a string. |

- Arguments and results can be numbers, strings, `True`/`False`/`None`, lists, and dictionaries (as in JSON).
  Results such as numpy numbers and arrays are converted to plain numbers and lists.
- If the local function raises an error, `remote_execute()` raises `RemoteExecutionError` with the laptop's message,
  so `try`/`except` works across the two computers.
- Only functions defined in the local files can be called, by name. Names starting with `_`, and functions a local
  file imports from elsewhere (for example `os.system`), cannot. The robot chooses which of these functions runs and
  sends it data; it never sends code to the laptop.
- The script waits for the answer, so stop the motors before a call that takes a while. Input typed in the app while
  a call is running is held until the answer arrives.
- `print()` in a local function appears in the app's output, marked `[local]`.
- Local functions need a computer with Python: on Android, a run with Local files does not start, and the app says
  why.

Limits (app Settings): the time the local files may take to load, and the time one call may take. A call that runs
over its limit stops the local functions for the rest of that run.

## How it works

Each function prints one line to standard output in this form:

```text
@@runner <kind> <value>
```

For example, `say("Hello")` prints `@@runner say Hello`, `plot(distance=412)` prints
`@@runner plot {"distance": 412.0}`, and `draw.marker(1, 2, name="robot")` prints
`@@runner draw {"op": "marker", "points": [[1.0, 2.0]], "shape": "circle", "size": 10.0, "color": "black", "name": "robot"}`.
The app recognizes
these lines, hides them, and acts on them. Newlines in the value are replaced
with spaces so each message stays on one line.

`remote_execute()` prints a `call` line with its request as JSON, then reads its
answer from standard input as a `reply` line. A long answer arrives
as a file next to the script (a `reply-file` line), because a terminal line has
a length limit.

Because the messages are ordinary output, a script that uses `say()`, `play()`,
`show()`, `plot()`, and `draw` still runs anywhere. Run some other way (for example from a terminal), the tagged
lines simply appear as text.

## Using the protocol without the module

Any program that prints a line in the format above gets the same behavior, so
a script can do this without importing `runner_io`:

```python
print("@@runner say Hello", flush=True)
```
