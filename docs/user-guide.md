# User Guide

This guide is for the people running scripts: open the Script Runner app, add a Python file, and press Run. To
install the app, see [app.md](app.md#install); [app.md](app.md) also describes every button and setting.

## Connect

1. Make sure your computer is on the same network as the Raspberry Pi.
2. In the app, enter the Pi's IP address, the user name (normally `pi`), and the password you were given. Check
   **Remember (secure storage)** to keep the password on this computer.

## Run a script

1. Press **Add files…** (or drag files onto the box on a desktop computer) and add your `.py` file, plus any sound,
   image, or helper files it uses. **Examples** lists ready-made scripts.
2. If you added more than one `.py` file, choose the one to run in the **Run:** list.
3. Press **Run**. Output appears as the script prints it.

Each run starts with a fresh folder on the Pi containing only the files you added, so add everything the script
needs. Each Run first reads your files again, so changes you saved in your editor are what runs.

## Type input

When your script calls `input()`, type your answer in the box under the output and press **Enter**. The prompt and
your answer appear on the same line.

## Stop a script

Press **Stop** or the **Esc** key. This sends Ctrl+C to your script, which raises `KeyboardInterrupt`. Code in a
`finally:` block runs, which is where a robot program should stop its motors:

```python
try:
    drive()
except KeyboardInterrupt:
    print("Stopped")
finally:
    stop_motors()        # always runs
```

If the script does not stop within 3 seconds, the app ends it, and then forces it to quit if needed. A script is
also stopped automatically when it reaches the time limit in **Settings** (300 seconds unless changed; 0 means no
limit).

Only one script can run on a Pi at a time. If someone else's script is running, the app says so and your script
does not start.

## Speech, sound, images, graphs, and drawings

Import the helper functions at the top of your script:

```python
from runner_io import say, play, show, plot, draw

say("Starting the square")     # your computer speaks this
play("beep.wav")               # your computer plays this sound file
show("map.png")                # your computer displays this image
show("readings.csv")           # any file: your computer shows it and can save it
plot(distance=d)               # a live graph beside the output
draw.marker(x, y, name="robot")   # a drawing beside the output, such as a map
```

- `play()` and `show()` use files in the same folder as your script: files you added, or files your script creates
  while it runs.
- **Speak say() messages** (on by default) controls whether `say()` is spoken.
- **Speak all output** reads every printed line aloud.

See [runner-io.md](runner-io.md) for details.

## Examples

The [examples](../examples) folder has scripts to try; the app also lists them under **Examples**. Each one cleans up
when you press Stop (motors off, camera closed, sensors stopped).

| File | Shows | Needs |
| :--- | :--- | :--- |
| `hello_input.py` | Keyboard input, Stop, and `finally:` | Nothing |
| `speak_play_show.py` | `say()`, `play()`, and `show()` (add `beep.wav` with it) | Nothing |
| `drawing/drawing_demo.py` | Drawing a map with `draw`: a room, a pretend robot moving along its path, markers of every shape, and the picture saved as an SVG file | Nothing |
| `drawing/drawing_save_load.py` | Saving a drawing and carrying on in a later run: `draw.save()`, `draw.load()`, blanking and reloading the canvas, `remove()`, a background color, and sending `room.json` to the laptop to add with the script next time | Nothing |
| `rvr/rvr_square.py` | Driving a square with spoken updates and a safe stop | Sphero RVR |
| `rvr/rvr_square_photos.py` | Driving a square and showing a photo taken at each corner | Sphero RVR, Pi camera |
| `rvr/color_detection.py` | Reading the color under the robot and saying the strongest of red, green, and blue | Sphero RVR |
| `rvr/ambient_light.py` | Reporting the light level until Stop, saying "Bright" or "Dark" when it changes | Sphero RVR |
| `rvr/rvr_position.py` | The RVR's own position tracking: driving a square to target positions, printing where it thinks it is at each corner, and drawing its reported path | Sphero RVR, matplotlib |
| `rvr/room_map.py` | Mapping a square around the robot with the distance sensor (type the size in meters when asked), drawing each reading live as the sensor's see-through cone (or, with `--lines`, a line) and saving the map as an SVG file; also runs without a robot (`--simulate`) | Sphero RVR, Qwiic VL53L1X, numpy |
| `sensors/distance_sensor.py` | Measuring distance until Stop, graphing it with `plot()`, and warning out loud when something is close | SparkFun Qwiic VL53L1X |
| `sensors/oled_display.py` | Showing two lines of text on the display, cleared when you press Stop | SparkFun Qwiic OLED (128 x 32) |
| `sensors/camera_photo.py` | Taking a photo and showing it | Pi camera |
| `sensors/camera_dominant_color.py` | Showing a photo and saying whether red, green, or blue is strongest in it | Pi camera, numpy |
| `sensors/camera2_photo.py` | Taking a photo with Picamera2 and showing it | 64-bit robot, Pi camera |
| `sensors/robot_ultralytics.py` | YOLO object detection on the Pi: finding and boxing the objects in a photo | 64-bit robot, Pi camera |
| `sensors/robot_classify.py` | Classifying a photo (or an added picture) with a YOLO classification model: its five best guesses of what it shows | 64-bit robot, Pi camera |
| `sensors/robot_ocr.py` | Reading the text in a photo (or an added picture) with Tesseract and printing it | 64-bit robot, Pi camera |
| `showcase/explorer.py` | A mission using everything at once: explores while avoiding obstacles, photographs each obstacle, watches the floor for a target color, turns on its headlights in the dark, shows its state on the OLED and with its lights, and gives a spoken summary | Sphero RVR+, Pi camera, Qwiic VL53L1X, Qwiic OLED |
| `remote/remote_basics.py` | `remote_execute()`: functions that run on the laptop, including one given a CSV file from the robot (add `remote_tools.py` with **Local** checked) | Nothing |
| `remote/robot_detect.py` | Looking around with YOLO object detection run on the laptop, saying what it sees (add `robot_api.py`, and `remote_vision.py` with **Local** checked) | Sphero RVR, Pi camera; Ultralytics on the laptop |
| `robot_api/sensor_check.py` | The Robot API: reading the distance and ambient light and showing the distance on the OLED (add `robot_api.py` with it) | Sphero RVR, Qwiic VL53L1X, Qwiic OLED |
| `robot_api/square_tour.py` | The Robot API: driving a square with colored lights, then at each corner reading the floor color and distance, showing a photo, and saying what it found (add `robot_api.py` with it) | Sphero RVR+, Pi camera, Qwiic VL53L1X, Qwiic OLED |

The robot examples time their drives with their own loop rather than the SDK's `drive_forward_seconds`, whose
timer can fail to stop when a drive starts within a few seconds of the end of a minute.

### The Robot API

`robot_api/robot_api.py` wraps the robot's hardware in one `Robot` class with simple commands (`drive`, `stop`,
`set_all_led`, `set_individual_led`, `read_distance`, `read_ambient_light`, `read_floor_color_name`,
`setOLED_two_lines`, `take_picture`, `cleanup`), so student scripts do not call the Sphero SDK or the sensor
libraries directly. `read_distance()` returns millimeters, or `None` when the sensor has no trustworthy reading
(usually because nothing is in range), so check for `None` before using the number. Students add `robot_api.py`
together with their script; both are copied to the Pi for each run. The app itself knows nothing about it, so each student's copy can be changed without affecting anyone else.
`Robot(simulation=True)` runs a script without hardware, printing what the robot would do and asking for sensor
values.

Create the `Robot` inside `try:` and clean up in `finally:`, so Stop always stops the motors:

```python
robot = None
try:
    robot = Robot(use_camera=False)
    ...
except KeyboardInterrupt:
    print("Stopped")
finally:
    if robot is not None:
        robot.cleanup()
```
