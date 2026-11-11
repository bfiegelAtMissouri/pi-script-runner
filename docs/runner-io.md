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
| `show(filename)` | Displays an image (`.png`, `.jpg`, `.jpeg`, `.gif`, `.bmp`, `.svg`; the app does not show `.svg`) |

`filename` is either relative to the script's folder on the Pi (for example
`photo.jpg`) or an absolute path in `/tmp` (for example `/tmp/photo.jpg`).
Files anywhere else cannot be served.

The script's folder is erased at the start of the next run. `/tmp` is shared by
everyone who uses the Pi and is normally emptied when the Pi restarts, so files
there can be overwritten by another group's script.

Speech is queued: several `say()` calls in a row are spoken in order. `say()`
returns immediately, so the script does not wait for speech to finish; add a
`time.sleep()` if the timing matters.

## How it works

Each function prints one line to standard output in this form:

```text
@@runner <kind> <value>
```

For example, `say("Hello")` prints `@@runner say Hello`. The page recognizes
these lines, hides them, and acts on them. Newlines in the value are replaced
with spaces so each message stays on one line.

Because the messages are ordinary output, a script that uses `runner_io` still
runs anywhere. Run some other way (for example over SSH), the tagged lines
simply appear as text.

## Using the protocol without the module

Any program that prints a line in the format above gets the same behavior, so
a script can do this without importing `runner_io`:

```python
print("@@runner say Hello", flush=True)
```
