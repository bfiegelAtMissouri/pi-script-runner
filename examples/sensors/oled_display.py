"""
Show two lines of text on a SparkFun Qwiic OLED display (128 x 32 pixels), with
a counter on the second line, until Stop is pressed. Stop clears the screen.

Requires: the sparkfun-qwiic-oled-display library and the display on the Qwiic (I2C) bus.
"""
import time

import qwiic_oled_display

FIRST_LINE_Y = 0
SECOND_LINE_Y = 12             # pixels down; the screen is 32 pixels tall
SMALL_FONT = 0
MOUNTED_UPSIDE_DOWN = True     # set to False if the text appears upside down

oled = qwiic_oled_display.QwiicOledDisplay()

def show_lines(first_line, second_line):
    """
    Purpose: replace what the display shows with two lines of text
    Inputs:  first_line, second_line - short strings (about 21 characters fit)
    Returns: nothing
    """
    oled.clear(oled.PAGE)
    oled.set_cursor(0, FIRST_LINE_Y)
    oled.print(first_line)
    oled.set_cursor(0, SECOND_LINE_Y)
    oled.print(second_line)
    oled.display()


if '__main__' == __name__:
    oled.begin()
    oled.set_font_type(SMALL_FONT)
    oled.flip_vertical(MOUNTED_UPSIDE_DOWN)
    oled.flip_horizontal(MOUNTED_UPSIDE_DOWN)
    oled.clear(oled.PAGE)
    oled.display()

    try:
        count = 0
        while True:
            show_lines("Hello from the Pi", "Count: %d" % count)
            count += 1
            time.sleep(1)
    except KeyboardInterrupt:
        print("Stopped by the Stop button (Ctrl+C)")
    finally:
        oled.clear(oled.PAGE)
        oled.display()
