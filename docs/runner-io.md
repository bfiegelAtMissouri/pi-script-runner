# The `runner_io` helper module

`runner_io` lets a script send speech, sounds, and images to the browser page
or app that started it. The runner server and the app's SSH mode both put it on
the script's import path automatically.

```python
from runner_io import say, play, show
```

## Functions

| Function | Effect in the browser |
| :--- | :--- |
| `say(text)` | Speaks `text` with the browser's built-in text-to-speech |
| `play(filename)` | Plays a sound file (`.wav`, `.mp3`, `.ogg`) |
| `show(filename)` | Sends any file to the page and keeps it there with a Save link: images are displayed, text files (`.csv`, `.txt`, `.json`, ...) show their first lines, and other files show their name and size |

`filename` is either relative to the script's folder on the Pi (for example
`photo.jpg`) or an absolute path in `/tmp` (for example `/tmp/photo.jpg`).
Files anywhere else cannot be served.

The script's folder is erased at the start of the next run. `/tmp` is shared by
everyone who uses the Pi and is normally emptied when the Pi restarts, so files
there can be overwritten by another group's script.

Speech is queued: several `say()` calls in a row are spoken in order. `say()`
returns immediately, so the script does not wait for speech to finish; add a
`time.sleep()` if the timing matters.

## Local functions (app only)

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
- The browser page cannot run local functions: there, `remote_execute()` raises `RemoteExecutionError` at once.

Limits (app Settings): the time the local files may take to load, and the time one call may take. A call that runs
over its limit stops the local functions for the rest of that run.

## How it works

Each function prints one line to standard output in this form:

```text
@@runner <kind> <value>
```

For example, `say("Hello")` prints `@@runner say Hello`. The page recognizes
these lines, hides them, and acts on them. Newlines in the value are replaced
with spaces so each message stays on one line.

`remote_execute()` prints a `call` line with its request as JSON, then reads its
answer from standard input as a `reply` line. A long answer in SSH mode arrives
as a file next to the script (a `reply-file` line), because a terminal line has
a length limit.

Because the messages are ordinary output, a script that uses `say()`, `play()`,
and `show()` still runs anywhere. Run some other way (for example over SSH), the tagged lines
simply appear as text.

## Using the protocol without the module

Any program that prints a line in the format above gets the same behavior, so
a script can do this without importing `runner_io`:

```python
print("@@runner say Hello", flush=True)
```
