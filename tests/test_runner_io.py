"""
Tests for runner_io's draw object: the messages it prints, the checks on what it is given, and the SVG it saves.
(The app's tests check that the app saves the same SVG from the same messages.)

    python3 -m unittest discover -s tests -v
"""
import contextlib
import io
import json
import os
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ElementTree

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "runner"))
import runner_io  # noqa: E402

SVG = "{http://www.w3.org/2000/svg}"


def messages(action):
    """The draw messages printed while action runs."""
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        action()
    lines = out.getvalue().splitlines()
    assert all(line.startswith("@@runner draw ") for line in lines), lines
    return [json.loads(line[len("@@runner draw "):]) for line in lines]


class DrawTests(unittest.TestCase):

    def setUp(self):
        self.draw = runner_io.Drawing()

    def test_messages(self):
        sent = messages(lambda: (self.draw.canvas(0, 2, 0, 1, grid=0.5),
                                 self.draw.rect(1, 1, -0.5, -0.25, color="Gray", fill="#FC0"),
                                 self.draw.marker(1, 2, name="robot"),
                                 self.draw.text(0, 0, "two\nlines", size=500),
                                 self.draw.remove("robot"),
                                 self.draw.clear()))
        self.assertEqual({"op": "canvas", "bounds": [0.0, 2.0, 0.0, 1.0], "grid": 0.5, "background": "none"}, sent[0])
        self.assertEqual({"op": "rect", "x": 0.5, "y": 0.75, "w": 0.5, "h": 0.25, "color": "gray", "fill": "#fc0",
                          "width": 2.0}, sent[1])                       # a negative width or height is turned around
        self.assertEqual({"op": "marker", "points": [[1.0, 2.0]], "shape": "circle", "size": 10.0, "color": "black",
                          "name": "robot"}, sent[2])
        self.assertEqual(("two lines", 96), (sent[3]["text"], sent[3]["size"]))   # one line; size kept readable
        self.assertEqual([{"op": "remove", "name": "robot"}, {"op": "clear"}], sent[4:])

    def test_mistakes_are_explained(self):
        cases = [(lambda: self.draw.line(0, 0, "a", 1), "line: x of point 2 must be a number, not 'a'"),
                 (lambda: self.draw.line(0, 0, 1, float("nan")), "finite"),
                 (lambda: self.draw.marker(0, 0, shape="star"), "not one of circle"),
                 (lambda: self.draw.circle(0, 0, 1, color="bleu"), "is not a color"),
                 (lambda: self.draw.polygon([(0, 0), (1, 1)]), "at least 3 points"),
                 (lambda: self.draw.polyline([(0, 0), (1, 1, 2)]), "must be a pair"),
                 (lambda: self.draw.canvas(0, 1, 0), "all four"),
                 (lambda: self.draw.canvas(1, 0, 0, 1), "xmin < xmax"),
                 (lambda: self.draw.markers([(0, 0)] * 5001), "at most 5000")]
        for action, words in cases:
            with self.assertRaises(ValueError) as caught:
                messages(action)
            self.assertIn(words, str(caught.exception))

    def test_names_replace_and_save_svg(self):
        def make():
            self.draw.canvas(0, 4, 0, 2, grid=1)
            self.draw.marker(1, 1, shape="triangle", color="red", name="robot")
            self.draw.circle(2, 1, 0.5, fill="lightgreen")
            self.draw.marker(3, 1, shape="triangle", color="red", name="robot")
            self.draw.text(0.5, 0.5, "A & <B>")
        messages(make)
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "map.svg")
            self.assertEqual(path, self.draw.save_svg(path))
            root = ElementTree.parse(path).getroot()
        self.assertEqual(("800", "420"), (root.get("width"), root.get("height")))   # 4 x 2 units at 190 pixels each
        polygons = root.findall(SVG + "polygon")
        self.assertEqual(1, len(polygons), "the named marker replaced the first one")
        self.assertTrue(polygons[0].get("points").startswith("590,"), "at x = 3: 20 + 3 * 190")
        circle = root.find(SVG + "circle")
        self.assertEqual(("400", "210", "95"), (circle.get("cx"), circle.get("cy"), circle.get("r")))  # y up
        self.assertEqual("A & <B>", root.findall(SVG + "text")[-1].text)

    def test_background(self):
        messages(lambda: self.draw.marker(0, 0))
        self.assertNotIn("<rect", self.draw.svg(), "transparent unless a background is set")
        messages(lambda: self.draw.canvas(background="Navy"))
        root = ElementTree.fromstring(self.draw.svg())
        self.assertEqual("navy", root[0].get("fill"), "the background is the first thing drawn")
        with self.assertRaises(ValueError):
            messages(lambda: self.draw.canvas(background="transparent-ish"))

    def test_fits_everything_drawn_without_canvas(self):
        messages(lambda: (self.draw.line(0, 0, 10, 0), self.draw.marker(5, 5)))
        root = ElementTree.fromstring(self.draw.svg())
        # x from 0 to 10 and y from 0 to 5, with 0.5 of room on each side: 11 x 6 units at 760 / 11 pixels each
        self.assertEqual(("800", "454.55"), (root.get("width"), root.get("height")))


if __name__ == "__main__":
    unittest.main()
