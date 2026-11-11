# User Guide

This guide is for the people running scripts: open a web page, drop in a
Python file, and press Run.

## Open the page

1. Make sure your computer is on the same network as the Raspberry Pi.
2. Open `http://<Pi IP address>:8000` in a web browser (Chrome, Edge, Safari,
   or Firefox).
3. Enter the PIN you were given. The page remembers it in that browser.

## Run a script

1. Drag your `.py` file onto the page, or click the drop area to choose it.
   Also add any sound or image files your script uses.
2. If you added more than one `.py` file, select the one to run with the
   button next to its name.
3. Press **Run**. Output appears as the script prints it.

Each run starts with a fresh folder on the Pi containing only the files you
dropped, so include everything the script needs every time.

## Type input

When your script calls `input()`, type your answer in the box under the
output and press **Enter**. The prompt and your answer appear on the same line.

## Stop a script

Press **Stop** or the **Esc** key. This sends Ctrl+C to your script, which
raises `KeyboardInterrupt`. Code in a `finally:` block runs, which is where a
robot program should stop its motors:

```python
try:
    drive()
except KeyboardInterrupt:
    print("Stopped")
finally:
    stop_motors()        # always runs
```

If the script does not stop within 3 seconds, the runner ends it, and then
forces it to quit if needed. A script is also stopped automatically when it
reaches the time limit (300 seconds unless your instructor changed it).

Only one script can run on a Pi at a time. If someone else's script is running,
the page says so and Run is refused until it finishes.

## Speech, sound, and images

Import the helper functions at the top of your script:

```python
from runner_io import say, play, show

say("Starting the square")     # your computer speaks this
play("beep.wav")               # your computer plays this sound file
show("map.png")                # your computer displays this image
```

- `play()` and `show()` use files in the same folder as your script: files you
  dropped onto the page, or files your script creates while it runs.
- **Speak say() messages** (on by default) controls whether `say()` is spoken.
- **Speak all output** reads every printed line aloud.

See [runner-io.md](runner-io.md) for details.

## Examples

The [examples](../examples) folder has scripts to try. Each one works with the app and the browser page, and each
cleans up when you press Stop (motors off, camera closed, sensors stopped).

| File | Shows | Needs |
| :--- | :--- | :--- |
| `hello_input.py` | Keyboard input, Stop, and `finally:` | Nothing |
| `speak_play_show.py` | `say()`, `play()`, and `show()` (add `beep.wav` with it) | Nothing |
| `rvr/rvr_square.py` | Driving a square with spoken updates and a safe stop | Sphero RVR |
| `rvr/rvr_square_photos.py` | Driving a square and showing a photo taken at each corner | Sphero RVR, Pi camera |
| `rvr/color_detection.py` | Reading the color under the robot and saying the strongest of red, green, and blue | Sphero RVR |
| `rvr/ambient_light.py` | Reporting the light level until Stop, saying "Bright" or "Dark" when it changes | Sphero RVR |
| `sensors/distance_sensor.py` | Measuring distance until Stop, warning out loud when something is close | SparkFun Qwiic VL53L1X |
| `sensors/oled_display.py` | Showing two lines of text on the display, cleared when you press Stop | SparkFun Qwiic OLED (128 x 32) |
| `sensors/camera_photo.py` | Taking a photo and showing it | Pi camera |
| `sensors/camera_dominant_color.py` | Showing a photo and saying whether red, green, or blue is strongest in it | Pi camera, numpy |

The robot examples time their drives with their own loop rather than the SDK's `drive_forward_seconds`, whose
timer can fail to stop when a drive starts within a few seconds of the end of a minute.
