"""
Starts the user's script for Script Runner's SSH mode.

    python3 -u _script_runner_shim.py <script.py> [arguments...]

If the SSH connection drops (Wi-Fi loss, laptop closed), the script receives
SIGHUP. This turns the first SIGHUP into KeyboardInterrupt, the same as the
Stop button, so the script's finally: block still runs (for example, to stop a
robot's motors). Later SIGHUPs are ignored so they cannot interrupt that block.
"""
import runpy
import signal
import sys
import traceback


def _hang_up(signum, frame):
    signal.signal(signal.SIGHUP, signal.SIG_IGN)
    raise KeyboardInterrupt


signal.signal(signal.SIGHUP, _hang_up)
signal.signal(signal.SIGINT, signal.default_int_handler)


def _is_runner_frame(tb):
    # __file__ is relative on older Pythons and absolute on newer ones, so compare the end of the name.
    name = tb.tb_frame.f_code.co_filename
    return name.endswith("_script_runner_shim.py") or name.endswith("runpy.py") or name.startswith("<frozen runpy")


script = sys.argv[1]
sys.argv = sys.argv[1:]
sys.path.insert(0, ".")
try:
    runpy.run_path(script, run_name="__main__")
except SystemExit:
    raise
except BaseException as error:
    # Show the traceback as if the script had been run directly, without this file's and runpy's lines.
    tb = error.__traceback__
    while tb is not None and _is_runner_frame(tb):
        tb = tb.tb_next
    traceback.print_exception(type(error), error, tb)
    sys.exit(130 if isinstance(error, KeyboardInterrupt) else 1)
