"""
Speech, sound, and images on the laptop.

Drop this file AND beep.wav onto the page (or the app), then press Run.
The script draws its own image (a PNG file), so no image file is needed.
"""
import struct
import time
import zlib

from runner_io import play, say, show

SIDE_PIXELS = 200
BORDER_PIXELS = 20
LINE_PIXELS = 8
LINE_COLOR = (31, 111, 235)
BACKGROUND_COLOR = (255, 255, 255)


def write_png(filename, width, height, pixel):
    """
    Purpose: save an image as a PNG file using only the standard library
    Inputs:  filename, width and height in pixels, pixel(x, y) returning an (r, g, b) color
    Returns: nothing
    """
    rows = b"".join(b"\x00" + b"".join(bytes(pixel(x, y)) for x in range(width)) for y in range(height))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    with open(filename, "wb") as handle:
        handle.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


def square_outline(x, y):
    """The path the robot will drive: a square outline."""
    near = BORDER_PIXELS <= x < SIDE_PIXELS - BORDER_PIXELS and BORDER_PIXELS <= y < SIDE_PIXELS - BORDER_PIXELS
    inner = (BORDER_PIXELS + LINE_PIXELS <= x < SIDE_PIXELS - BORDER_PIXELS - LINE_PIXELS and
             BORDER_PIXELS + LINE_PIXELS <= y < SIDE_PIXELS - BORDER_PIXELS - LINE_PIXELS)
    return LINE_COLOR if near and not inner else BACKGROUND_COLOR


if '__main__' == __name__:
    try:
        say("Hello from the Raspberry Pi")
        time.sleep(2)

        play("beep.wav")
        time.sleep(1)

        write_png("square.png", SIDE_PIXELS, SIDE_PIXELS, square_outline)
        show("square.png")
        say("Here is the path I will drive")
    except KeyboardInterrupt:
        print("Stopped by the Stop button (Ctrl+C)")
    finally:
        print("Done.")                    # runs whether the script finished or was stopped
