"""
runner_io: send speech, sounds, and images from a script to the Pi Script Runner page.

    from runner_io import say, play, show

    say("Starting the square")   # the laptop speaks this
    play("beep.wav")             # the laptop plays this sound file
    show("map.png")              # the laptop displays this image

Files for play() and show() must be in the same folder as the script (drop them onto
the page along with the .py file, or have the script create them) or in /tmp
(for example show("/tmp/photo.jpg")).

Each call prints one tagged line that the Runner page acts on and hides.
Run outside the Runner page, the tagged lines simply appear as text.
"""
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
    """Display an image file (.png, .jpg, .gif, .bmp, .svg) on the laptop."""
    _send("show", filename)
