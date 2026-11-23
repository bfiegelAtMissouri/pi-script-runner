"""
runner_io: send speech, sounds, and images from a script to the Pi Script Runner page.

    from runner_io import say, play, show

    say("Starting the square")   # the laptop speaks this
    play("beep.wav")             # the laptop plays this sound file
    show("map.png")              # the laptop displays this image
    show("readings.csv")         # the laptop shows its first lines and can save it
    plot(distance=d, light=l)    # the app adds these numbers to a live graph

A script can also draw a picture, such as a map, that the app shows as it is drawn and can save as an SVG file:

    from runner_io import draw

    draw.canvas(0, 2, 0, 2, grid=0.5)          # a 2 m square, with grid lines every 0.5 m
    draw.rect(0, 0, 2, 2, color="gray")
    draw.marker(0.4, 1.2, shape="x", color="red")
    draw.marker(x, y, shape="triangle", color="blue", name="robot")   # a name replaces what was drawn before
    draw.save_svg("/tmp/map.svg")              # the same picture as a file on the Pi

Files for play() and show() must be in the same folder as the script (drop them onto
the page along with the .py file, or have the script create them) or in /tmp
(for example show("/tmp/photo.jpg")).

Each call prints one tagged line that the Runner page acts on and hides.
Run outside the Runner page, the tagged lines simply appear as text.

With the app, a script can also call Python functions that run on the laptop:

    from runner_io import remote_execute, shared_runner_file

    seen = remote_execute("detect", shared_runner_file("/tmp/view.jpg"), 0.5)

The functions are in .py files added in the app with "Local" checked. See remote_execute().

On a computer, install this module (pip install runner-io, from this project; see docs/runner-io.md) so editors
such as VS Code can complete these names and show this help. Scripts still run on the Pi, where the app or the
runner provides the module.
"""
import json
import math
import os
import sys
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

__version__ = "2026.11.22"
__all__ = ["say", "play", "show", "plot", "draw", "Drawing", "remote_execute", "shared_runner_file", "RemoteExecutionError"]

TAG = "@@runner "


def _send(kind: str, value: object) -> None:
    text = str(value).replace("\r", " ").replace("\n", " ")
    sys.stdout.write("%s%s %s\n" % (TAG, kind, text))
    sys.stdout.flush()


def say(text: object) -> None:
    """
    Speak text through the laptop's speakers, for example say("Turning left").

    It returns at once, without waiting for the speech; several calls are spoken in order.
    """
    _send("say", text)


def play(filename: str) -> None:
    """
    Play a sound file (.wav, .mp3, .ogg) on the laptop, for example play("beep.wav").

    The file must be in the script's folder (added with it, or made by the script) or in /tmp.
    """
    _send("play", filename)


def show(filename: str) -> None:
    """
    Send a file to the laptop, for example show("/tmp/photo.jpg"): images are displayed, text files previewed,
    and any file can be saved there.

    The file must be in the script's folder (added with it, or made by the script) or in /tmp.
    """
    _send("show", filename)


def plot(*values: float, **named_values: float) -> None:
    """
    Purpose: add numbers to the app's live graph, one point per call for each name
    Inputs:  named_values - numbers to plot, each with its own line, for example plot(distance=d, light=l)
             values - numbers without names, plotted as "value" (or "value 1", "value 2", ... for several)
    Returns: nothing

    Call it in a loop, for example once per sensor reading. Values that are not numbers are skipped. The app shows
    the graph beside the output and can save the numbers as a CSV file; the browser page shows them as text.
    """
    points = {}
    for index, value in enumerate(values, 1):
        points["value" if 1 == len(values) else "value %d" % index] = value
    points.update(named_values)
    numbers = {}
    for name, value in points.items():
        try:
            number = float(value)
        except (TypeError, ValueError):
            continue
        if number == number and abs(number) != float("inf"):    # JSON has no NaN or infinity
            numbers[str(name)] = number
    if numbers:
        _send("plot", json.dumps(numbers))


# ---- Drawing ----------------------------------------------------------------------------------------------
# The app (ScriptRunner.Core/ScriptDrawing.cs) and the browser page (index.html) draw the same messages the same way;
# the app's saved SVG is the same, character for character, as save_svg() writes here.

_COLOR_NAMES = frozenset("""
aliceblue antiquewhite aqua aquamarine azure beige bisque black blanchedalmond blue blueviolet brown burlywood
cadetblue chartreuse chocolate coral cornflowerblue cornsilk crimson cyan darkblue darkcyan darkgoldenrod darkgray
darkgreen darkgrey darkkhaki darkmagenta darkolivegreen darkorange darkorchid darkred darksalmon darkseagreen
darkslateblue darkslategray darkslategrey darkturquoise darkviolet deeppink deepskyblue dimgray dimgrey dodgerblue
firebrick floralwhite forestgreen fuchsia gainsboro ghostwhite gold goldenrod gray grey green greenyellow honeydew
hotpink indianred indigo ivory khaki lavender lavenderblush lawngreen lemonchiffon lightblue lightcoral lightcyan
lightgoldenrodyellow lightgray lightgreen lightgrey lightpink lightsalmon lightseagreen lightskyblue lightslategray
lightslategrey lightsteelblue lightyellow lime limegreen linen magenta maroon mediumaquamarine mediumblue
mediumorchid mediumpurple mediumseagreen mediumslateblue mediumspringgreen mediumturquoise mediumvioletred
midnightblue mintcream mistyrose moccasin navajowhite navy oldlace olive olivedrab orange orangered orchid
palegoldenrod palegreen paleturquoise palevioletred papayawhip peachpuff peru pink plum powderblue purple
rebeccapurple red rosybrown royalblue saddlebrown salmon sandybrown seagreen seashell sienna silver skyblue
slateblue slategray slategrey snow springgreen steelblue tan teal thistle tomato turquoise violet wheat white
whitesmoke yellow yellowgreen none""".split())

MARKER_SHAPES = ("circle", "square", "triangle", "diamond", "x", "plus")
_MOST_SHAPES = 20000            # shapes kept; the oldest are dropped past this
_MOST_POINTS = 5000             # points in one line, polygon, or markers() call
_SVG_BOX = 760.0                # the saved picture fits in this many pixels each way, inside a 20-pixel margin
_MARGIN = 20.0
_MOST_GRID_LINES = 200          # each way; with more, the grid is left out


def _number(value: object, what: str) -> float:
    try:
        number = float(value)                   # type: ignore[arg-type]
    except (TypeError, ValueError):
        raise ValueError("%s must be a number, not %r" % (what, value))
    if number != number or abs(number) == float("inf"):
        raise ValueError("%s must be a finite number, not %r" % (what, value))
    return number


def _color(value: object, what: str) -> str:
    text = str(value).strip().lower()
    if text in _COLOR_NAMES:
        return text
    if text.startswith("#") and len(text) in (4, 7) and all(c in "0123456789abcdef" for c in text[1:]):
        return text
    raise ValueError("%s %r is not a color: use a name such as \"red\" or \"gray\", or \"#RRGGBB\"" % (what, value))


def _points(points: Iterable[Sequence[float]], fewest: int, what: str) -> List[List[float]]:
    result = []
    for number, point in enumerate(points, 1):
        try:
            x, y = point
        except (TypeError, ValueError):
            raise ValueError("%s: point %d must be a pair (x, y), not %r" % (what, number, point))
        result.append([_number(x, "%s: x of point %d" % (what, number)), _number(y, "%s: y of point %d" % (what, number))])
    if len(result) < fewest:
        raise ValueError("%s needs at least %d points" % (what, fewest))
    if _MOST_POINTS < len(result):
        raise ValueError("%s can have at most %d points; draw more than one" % (what, _MOST_POINTS))
    return result


def _clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def _format(value: float, places: int = 2) -> str:
    """value rounded to places decimals (ties to even), without trailing zeros: the app writes numbers the same way."""
    scale = 10 ** places
    whole = int(round(value * scale))
    sign = "-" if whole < 0 else ""
    whole = abs(whole)
    text = "%s%d" % (sign, whole // scale)
    fraction = whole % scale
    if fraction:
        text += ("." + "%0*d" % (places, fraction)).rstrip("0")
    return text


def _escape(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


class _Frame:
    """Where the drawing's units land in pixels: x to the right, y up, the same scale both ways."""

    def __init__(self, bounds: Tuple[float, float, float, float], width: float, height: float, scale: float) -> None:
        self.xmin, self.xmax, self.ymin, self.ymax = bounds
        self.scale = scale
        self.left = (width - (self.xmax - self.xmin) * scale) / 2
        self.top = (height - (self.ymax - self.ymin) * scale) / 2

    def x(self, x: float) -> float:
        return self.left + (x - self.xmin) * self.scale

    def y(self, y: float) -> float:
        return self.top + (self.ymax - y) * self.scale


class Drawing:
    """
    A picture made of lines, shapes, markers, and text, in the script's own units (for example meters) with x to the
    right and y up. Each call sends the shape to the app, which shows the picture as it is drawn and can save it
    as an SVG file; save_svg() writes the same file on the Pi. Use the module's draw object: draw.line(...).

    Sizes of lines, rectangles, and circles are in the drawing's units. Line widths, marker sizes, and text sizes
    are in pixels, so they stay readable however large the drawing is.

    A shape drawn with a name replaces the earlier shape with that name (and is drawn on top), which moves
    something, such as the robot's marker, without leaving copies behind.

    Colors are names such as "red", "gray", or "steelblue" (the web's color names), "#RRGGBB", or "none".
    """

    def __init__(self) -> None:
        self._shapes = []               # type: List[Dict[str, Any]]
        self._bounds = None             # type: Optional[Tuple[float, float, float, float]]
        self._grid = None               # type: Optional[float]

    # ---- Settings and erasing ----

    def canvas(self, xmin: Optional[float] = None, xmax: Optional[float] = None, ymin: Optional[float] = None,
               ymax: Optional[float] = None, grid: Optional[float] = None) -> None:
        """
        Purpose: fix the part of the drawing that is shown, and choose grid lines
        Inputs:  xmin, xmax, ymin, ymax - the edges shown, in the drawing's units; leave all four out to fit the
                 view to everything drawn (the default)
                 grid - the distance between grid lines, in the drawing's units, or None for no grid
        Returns: nothing

        Fixed edges keep a map from rescaling each time something new is drawn near its edge.
        """
        edges = (xmin, xmax, ymin, ymax)
        if all(edge is None for edge in edges):
            bounds = None
        elif any(edge is None for edge in edges):
            raise ValueError("canvas() needs all four of xmin, xmax, ymin, ymax, or none of them")
        else:
            bounds = (_number(xmin, "xmin"), _number(xmax, "xmax"), _number(ymin, "ymin"), _number(ymax, "ymax"))
            if bounds[1] <= bounds[0] or bounds[3] <= bounds[2]:
                raise ValueError("canvas() needs xmin < xmax and ymin < ymax")
        step = None
        if grid is not None:
            step = _number(grid, "grid")
            if step <= 0:
                raise ValueError("grid must be more than 0")
        self._bounds = bounds
        self._grid = step
        self._send({"op": "canvas", "bounds": None if bounds is None else list(bounds), "grid": step})

    def clear(self) -> None:
        """Erases everything drawn. The canvas() settings stay."""
        self._shapes = []
        self._send({"op": "clear"})

    def remove(self, name: str) -> None:
        """Erases the shape drawn with this name, if there is one."""
        name = str(name)
        self._shapes = [shape for shape in self._shapes if shape.get("name") != name]
        self._send({"op": "remove", "name": name})

    # ---- Shapes ----

    def line(self, x1: float, y1: float, x2: float, y2: float, color: str = "black", width: float = 2,
             name: Optional[str] = None) -> None:
        """A straight line from (x1, y1) to (x2, y2). width is in pixels."""
        self._add({"op": "line", "points": _points([(x1, y1), (x2, y2)], 2, "line"),
                   "color": _color(color, "color"), "width": self._width(width)}, name)

    def polyline(self, points: Iterable[Sequence[float]], color: str = "black", width: float = 2,
                 name: Optional[str] = None) -> None:
        """Lines joining the points in order, for example a path: polyline([(0, 0), (1, 0), (1, 1)])."""
        self._add({"op": "polyline", "points": _points(points, 2, "polyline"),
                   "color": _color(color, "color"), "width": self._width(width)}, name)

    def polygon(self, points: Iterable[Sequence[float]], color: str = "black", fill: str = "none",
                width: float = 2, name: Optional[str] = None) -> None:
        """A closed shape through the points, outlined in color and filled with fill."""
        self._add({"op": "polygon", "points": _points(points, 3, "polygon"), "color": _color(color, "color"),
                   "fill": _color(fill, "fill"), "width": self._width(width)}, name)

    def rect(self, x: float, y: float, w: float, h: float, color: str = "black", fill: str = "none",
             width: float = 2, name: Optional[str] = None) -> None:
        """A rectangle w wide and h tall whose lower-left corner is (x, y)."""
        x, y, w, h = _number(x, "x"), _number(y, "y"), _number(w, "w"), _number(h, "h")
        if w < 0:
            x, w = x + w, -w
        if h < 0:
            y, h = y + h, -h
        self._add({"op": "rect", "x": x, "y": y, "w": w, "h": h, "color": _color(color, "color"),
                   "fill": _color(fill, "fill"), "width": self._width(width)}, name)

    def circle(self, x: float, y: float, r: float, color: str = "black", fill: str = "none", width: float = 2,
               name: Optional[str] = None) -> None:
        """A circle centered on (x, y) with radius r, in the drawing's units."""
        r = _number(r, "r")
        if r < 0:
            raise ValueError("r must not be negative")
        self._add({"op": "circle", "x": _number(x, "x"), "y": _number(y, "y"), "r": r,
                   "color": _color(color, "color"), "fill": _color(fill, "fill"), "width": self._width(width)}, name)

    def text(self, x: float, y: float, text: object, color: str = "black", size: float = 14,
             name: Optional[str] = None) -> None:
        """Text starting at (x, y), which is the left end of the line the letters sit on. size is in pixels."""
        words = str(text).replace("\r", " ").replace("\n", " ")
        self._add({"op": "text", "x": _number(x, "x"), "y": _number(y, "y"), "text": words,
                   "color": _color(color, "color"), "size": _clamp(_number(size, "size"), 4, 96)}, name)

    def marker(self, x: float, y: float, shape: str = "circle", size: float = 10, color: str = "black",
               name: Optional[str] = None) -> None:
        """
        A marker centered on (x, y): shape is "circle", "square", "triangle", "diamond", "x", or "plus".
        size is its width in pixels.
        """
        self.markers([(x, y)], shape, size, color, name)

    def markers(self, points: Iterable[Sequence[float]], shape: str = "circle", size: float = 10,
                color: str = "black", name: Optional[str] = None) -> None:
        """The same marker at each of the points, sent as one shape (faster than many marker() calls)."""
        shape = str(shape).strip().lower()
        if shape not in MARKER_SHAPES:
            raise ValueError("shape %r is not one of %s" % (shape, ", ".join(MARKER_SHAPES)))
        self._add({"op": "marker", "points": _points(points, 1, "markers"), "shape": shape,
                   "size": _clamp(_number(size, "size"), 2, 200), "color": _color(color, "color")}, name)

    # ---- The picture as a file ----

    def save_svg(self, path: str) -> str:
        """
        Purpose: save the drawing as an SVG file on the Pi (it opens in a web browser and in most drawing programs)
        Inputs:  path - the file, for example "/tmp/map.svg"; a name without a folder is saved next to the script
        Returns: the path, so show(draw.save_svg("/tmp/map.svg")) sends it to the laptop too
        """
        with open(path, "w", encoding="utf-8", newline="\n") as file:
            file.write(self.svg())
        return path

    def svg(self) -> str:
        """The drawing as SVG text."""
        bounds = self._view()
        spanx, spany = bounds[1] - bounds[0], bounds[3] - bounds[2]
        scale = min(_SVG_BOX / spanx, _SVG_BOX / spany)
        width, height = spanx * scale + 2 * _MARGIN, spany * scale + 2 * _MARGIN
        frame = _Frame(bounds, width, height, scale)
        f = _format
        out = ['<svg xmlns="http://www.w3.org/2000/svg" width="%s" height="%s" viewBox="0 0 %s %s">'
               % (f(width), f(height), f(width), f(height)),
               '<rect width="%s" height="%s" fill="white"/>' % (f(width), f(height))]
        out.extend(self._grid_svg(frame))
        for shape in self._shapes:
            out.extend(_shape_svg(shape, frame))
        out.append("</svg>")
        return "\n".join(out) + "\n"

    # ---- Inside ----

    @staticmethod
    def _width(width: float) -> float:
        return _clamp(_number(width, "width"), 0.5, 50)

    def _add(self, shape: Dict[str, Any], name: Optional[str]) -> None:
        if name is not None:
            shape["name"] = str(name)
            self._shapes = [old for old in self._shapes if old.get("name") != shape["name"]]
        self._shapes.append(shape)
        if _MOST_SHAPES < len(self._shapes):
            del self._shapes[:len(self._shapes) - _MOST_SHAPES]
        self._send(shape)

    @staticmethod
    def _send(message: Dict[str, Any]) -> None:
        _send("draw", json.dumps(message))

    def _view(self) -> Tuple[float, float, float, float]:
        """The edges shown: those set with canvas(), or everything drawn with a little room around it."""
        if self._bounds is not None:
            return self._bounds
        xs, ys = [], []                 # type: List[float], List[float]
        for shape in self._shapes:
            if "points" in shape:
                xs.extend(point[0] for point in shape["points"])
                ys.extend(point[1] for point in shape["points"])
            elif "rect" == shape["op"]:
                xs.extend((shape["x"], shape["x"] + shape["w"]))
                ys.extend((shape["y"], shape["y"] + shape["h"]))
            elif "circle" == shape["op"]:
                xs.extend((shape["x"] - shape["r"], shape["x"] + shape["r"]))
                ys.extend((shape["y"] - shape["r"], shape["y"] + shape["r"]))
            else:
                xs.append(shape["x"])
                ys.append(shape["y"])
        if not xs:
            xs, ys = [0.0, 1.0], [0.0, 1.0]
        xmin, xmax, ymin, ymax = min(xs), max(xs), min(ys), max(ys)
        pad = 0.05 * max(xmax - xmin, ymax - ymin)
        if 0 == pad:
            pad = 1.0
        return xmin - pad, xmax + pad, ymin - pad, ymax + pad

    def _grid_svg(self, frame: _Frame) -> List[str]:
        lines = _grid_lines(self._grid, frame)
        if lines is None:
            return []
        f = _format
        xs, ys = lines
        left, right, top, bottom = frame.x(frame.xmin), frame.x(frame.xmax), frame.y(frame.ymax), frame.y(frame.ymin)
        out = ['<g stroke="#DDDDDD" stroke-width="1">']
        out.extend('<line x1="%s" y1="%s" x2="%s" y2="%s"/>' % (f(frame.x(x)), f(top), f(frame.x(x)), f(bottom))
                   for x in xs)
        out.extend('<line x1="%s" y1="%s" x2="%s" y2="%s"/>' % (f(left), f(frame.y(y)), f(right), f(frame.y(y)))
                   for y in ys)
        out.append('</g>')
        out.append('<rect x="%s" y="%s" width="%s" height="%s" fill="none" stroke="#BBBBBB" stroke-width="1"/>'
                   % (f(left), f(top), f(right - left), f(bottom - top)))
        out.append('<g font-family="sans-serif" font-size="10" fill="#888888">')
        out.extend('<text x="%s" y="%s">%s</text>' % (f(frame.x(x) + 2), f(bottom - 3), _format(x, 3)) for x in xs)
        out.extend('<text x="%s" y="%s">%s</text>' % (f(left + 2), f(frame.y(y) - 2), _format(y, 3)) for y in ys)
        out.append('</g>')
        return out


def _grid_lines(step: Optional[float], frame: _Frame) -> Optional[Tuple[List[float], List[float]]]:
    """The x and y of the grid lines inside the edges, or None for no grid."""
    if step is None:
        return None
    first_x, last_x = math.ceil(frame.xmin / step - 1e-9), math.floor(frame.xmax / step + 1e-9)
    first_y, last_y = math.ceil(frame.ymin / step - 1e-9), math.floor(frame.ymax / step + 1e-9)
    if _MOST_GRID_LINES < last_x - first_x + 1 or _MOST_GRID_LINES < last_y - first_y + 1:
        return None
    return ([k * step for k in range(first_x, last_x + 1)], [k * step for k in range(first_y, last_y + 1)])


def _shape_svg(shape: Dict[str, Any], frame: _Frame) -> List[str]:
    f = _format
    op = shape["op"]
    color = shape["color"]
    if "marker" == op:
        return [_marker_svg(shape["shape"], frame.x(x), frame.y(y), shape["size"], color) for x, y in shape["points"]]
    if "text" == op:
        return ['<text x="%s" y="%s" font-family="sans-serif" font-size="%s" fill="%s">%s</text>'
                % (f(frame.x(shape["x"])), f(frame.y(shape["y"])), f(shape["size"]), color, _escape(shape["text"]))]
    stroke = 'stroke="%s" stroke-width="%s"' % (color, f(shape["width"]))
    if "line" == op:
        (x1, y1), (x2, y2) = shape["points"]
        return ['<line x1="%s" y1="%s" x2="%s" y2="%s" %s stroke-linecap="round"/>'
                % (f(frame.x(x1)), f(frame.y(y1)), f(frame.x(x2)), f(frame.y(y2)), stroke)]
    if op in ("polyline", "polygon"):
        points = " ".join("%s,%s" % (f(frame.x(x)), f(frame.y(y))) for x, y in shape["points"])
        fill = "none" if "polyline" == op else shape["fill"]
        return ['<%s points="%s" fill="%s" %s stroke-linecap="round" stroke-linejoin="round"/>'
                % (op, points, fill, stroke)]
    if "rect" == op:
        return ['<rect x="%s" y="%s" width="%s" height="%s" fill="%s" %s/>'
                % (f(frame.x(shape["x"])), f(frame.y(shape["y"] + shape["h"])), f(shape["w"] * frame.scale),
                   f(shape["h"] * frame.scale), shape["fill"], stroke)]
    return ['<circle cx="%s" cy="%s" r="%s" fill="%s" %s/>'
            % (f(frame.x(shape["x"])), f(frame.y(shape["y"])), f(shape["r"] * frame.scale), shape["fill"], stroke)]


def _marker_svg(shape: str, x: float, y: float, size: float, color: str) -> str:
    f = _format
    h = size / 2
    if "circle" == shape:
        return '<circle cx="%s" cy="%s" r="%s" fill="%s"/>' % (f(x), f(y), f(h), color)
    if "square" == shape:
        return '<rect x="%s" y="%s" width="%s" height="%s" fill="%s"/>' % (f(x - h), f(y - h), f(size), f(size), color)
    if "triangle" == shape:
        corners = ((x, y - h), (x + h, y + h), (x - h, y + h))
    elif "diamond" == shape:
        corners = ((x, y - h), (x + h, y), (x, y + h), (x - h, y))
    else:
        if "x" == shape:
            d = "M%s %sL%s %sM%s %sL%s %s" % (f(x - h), f(y - h), f(x + h), f(y + h), f(x - h), f(y + h), f(x + h), f(y - h))
        else:
            d = "M%s %sL%s %sM%s %sL%s %s" % (f(x - h), f(y), f(x + h), f(y), f(x), f(y - h), f(x), f(y + h))
        return '<path d="%s" fill="none" stroke="%s" stroke-width="2" stroke-linecap="round"/>' % (d, color)
    return '<polygon points="%s" fill="%s"/>' % (" ".join("%s,%s" % (f(cx), f(cy)) for cx, cy in corners), color)


draw = Drawing()
"""The script's drawing: draw.line(...), draw.marker(...), draw.save_svg(...). See Drawing."""


# ---- Functions on the laptop ------------------------------------------------------------------------------

class RemoteExecutionError(Exception):
    """A local function failed, or could not be called. The message says why."""


_next_call_id = [0]


def shared_runner_file(path: str) -> Dict[str, str]:
    """
    Purpose: mark an argument of remote_execute() as a file on the Pi to send to the laptop
    Inputs:  path - a file in the script's folder, or an absolute path in /tmp
    Returns: a marker; the local function receives the path of the laptop's copy, as a string
    """
    return {"__shared_runner_file__": str(path)}


def remote_execute(function_name: str, *args: Any) -> Any:
    """
    Purpose: call a function defined in one of the app's local files, and wait for its result
    Inputs:  function_name - the function's name, as a string, for example "detect"
             args - its arguments: numbers, strings, True/False/None, lists, dictionaries, and shared_runner_file(...)
    Returns: the function's return value (numbers, strings, lists, and dictionaries come back as they are)
    Raises:  RemoteExecutionError if the function raised an error on the laptop or could not be called

    The script waits until the laptop answers, so stop the motors before a call that takes a while.
    Works only when the script was started from the app (the browser page cannot run local functions).
    """
    _next_call_id[0] += 1
    call_id = _next_call_id[0]
    request = json.dumps({"id": call_id, "function": str(function_name), "args": list(args)})
    sys.stdout.write("%scall %s\n" % (TAG, request))
    sys.stdout.flush()
    while True:
        line = sys.stdin.readline()
        if not line:
            raise RemoteExecutionError("The connection to the laptop closed before %s answered." % function_name)
        line = line.strip()
        if line.startswith(TAG + "reply-file "):
            # A long answer arrives as a file next to the script, because a terminal line has a length limit.
            name = os.path.basename(line[len(TAG + "reply-file "):].strip())
            with open(name) as handle:
                line = TAG + "reply " + handle.read().strip()
            os.remove(name)
        if not line.startswith(TAG + "reply "):
            continue                    # not an answer (the app holds typed input until the call is answered)
        reply = json.loads(line[len(TAG + "reply "):])
        if call_id != reply.get("id"):
            continue
        if reply.get("ok"):
            return reply.get("value")
        raise RemoteExecutionError(reply.get("error", "%s failed on the laptop." % function_name))
