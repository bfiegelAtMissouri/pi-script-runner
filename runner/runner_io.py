"""
runner_io: send speech, sounds, and images from a script to the Pi Script Runner page.

    from runner_io import say, play, show

    say("Starting the square")   # the laptop speaks this
    play("beep.wav")             # the laptop plays this sound file
    show("map.png")              # the laptop displays this image
    show("readings.csv")         # the laptop shows its first lines and can save it

Files for play() and show() must be in the same folder as the script (drop them onto
the page along with the .py file, or have the script create them) or in /tmp
(for example show("/tmp/photo.jpg")).

Each call prints one tagged line that the Runner page acts on and hides.
Run outside the Runner page, the tagged lines simply appear as text.

With the app, a script can also call Python functions that run on the laptop:

    from runner_io import remote_execute, shared_runner_file

    seen = remote_execute("detect", shared_runner_file("/tmp/view.jpg"), 0.5)

The functions are in .py files added in the app with "Laptop" checked. See remote_execute().
"""
import json
import os
import sys

TAG = "@@runner "


def _send(kind, value):
    text = str(value).replace("\r", " ").replace("\n", " ")
    sys.stdout.write("%s%s %s\n" % (TAG, kind, text))
    sys.stdout.flush()


def say(text):
    """Speak text through the laptop's speakers."""
    _send("say", text)


def play(filename):
    """Play a sound file (.wav, .mp3, .ogg) on the laptop."""
    _send("play", filename)


def show(filename):
    """Send a file to the laptop: images are displayed, text files previewed, and any file can be saved there."""
    _send("show", filename)


# ---- Functions on the laptop ------------------------------------------------------------------------------

class RemoteExecutionError(Exception):
    """A laptop function failed, or could not be called. The message says why."""


_next_call_id = [0]


def shared_runner_file(path):
    """
    Purpose: mark an argument of remote_execute() as a file on the Pi to send to the laptop
    Inputs:  path - a file in the script's folder, or an absolute path in /tmp
    Returns: a marker; the laptop function receives the path of the laptop's copy, as a string
    """
    return {"__shared_runner_file__": str(path)}


def remote_execute(function_name, *args):
    """
    Purpose: call a function defined in one of the app's Laptop files, and wait for its result
    Inputs:  function_name - the function's name, as a string, for example "detect"
             args - its arguments: numbers, strings, True/False/None, lists, dictionaries, and shared_runner_file(...)
    Returns: the function's return value (numbers, strings, lists, and dictionaries come back as they are)
    Raises:  RemoteExecutionError if the function raised an error on the laptop or could not be called

    The script waits until the laptop answers, so stop the motors before a call that takes a while.
    Works only when the script was started from the app (the browser page cannot run laptop functions).
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
