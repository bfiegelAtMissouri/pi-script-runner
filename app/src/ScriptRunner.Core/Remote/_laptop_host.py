"""
Runs on the laptop for the Script Runner app: imports the user's Laptop files and calls their functions when the
robot's script asks (runner_io.remote_execute).

    python -u _laptop_host.py module_a module_b ...

The working folder holds the Laptop files. Messages are JSON, one per line:
  out: {"ready": true, "functions": [...]}   or   {"ready": false, "error": "..."}
  in:  {"id": 1, "function": "detect", "args": [...]}
  out: {"id": 1, "ok": true, "value": ...}   or   {"id": 1, "ok": false, "error": "..."}

Only functions defined in the Laptop files themselves can be called, by plain name. Functions a file imports from
elsewhere (for example os.system) and names starting with _ cannot, so the robot can choose which of the user's
functions runs and send it data, but never run anything else.

Anything the functions print goes to standard error, which the app shows in the output; standard output carries
only the messages above.
"""
import importlib
import inspect
import json
import os
import sys
import traceback

PROTOCOL_OUT = sys.stdout
sys.stdout = sys.stderr                     # print() in the user's functions must not mix into the messages


def send(message):
    PROTOCOL_OUT.write(json.dumps(message, default=to_json) + "\n")
    PROTOCOL_OUT.flush()


def to_json(value):
    """Turns values JSON cannot hold into ones it can: numpy numbers and arrays, tuples of them, and the like."""
    if hasattr(value, "tolist"):
        return value.tolist()
    if hasattr(value, "item"):
        return value.item()
    if isinstance(value, (set, frozenset)):
        return list(value)
    return str(value)


def load_functions(module_names):
    """Imports each module and returns {name: function} for the functions it defines."""
    sys.path.insert(0, "")
    functions = {}
    for module_name in module_names:
        module = importlib.import_module(module_name)
        for name, value in vars(module).items():
            if name.startswith("_") or not inspect.isfunction(value) or value.__module__ != module.__name__:
                continue
            if name in functions:
                raise ValueError("Two Laptop files define a function named %s. Rename one of them." % name)
            functions[name] = value
    return functions


def short_traceback(error):
    """The error, with only the lines from the user's own Laptop files (by file name, without folders)."""
    folder = os.getcwd() + os.sep
    helper = os.path.abspath(__file__)
    frames = [frame for frame in traceback.extract_tb(error.__traceback__)
              if not frame.filename.startswith("<")                      # not Python's own <frozen ...> code
              and os.path.abspath(frame.filename).startswith(folder) and helper != os.path.abspath(frame.filename)]
    lines = traceback.format_list(frames) + traceback.format_exception_only(type(error), error)
    return "".join(lines).rstrip().replace(folder, "")


def main():
    try:
        functions = load_functions(sys.argv[1:])
    except BaseException as error:              # noqa: B902 - report any failure to load, then stop
        send({"ready": False, "error": "Could not load the Laptop files:\n" + short_traceback(error)})
        return
    send({"ready": True, "functions": sorted(functions)})

    for line in sys.stdin:
        if not line.strip():
            continue
        request = json.loads(line)
        call_id = request.get("id")
        name = request.get("function", "")
        function = functions.get(name)
        if function is None:
            known = ", ".join(sorted(functions)) or "none"
            send({"id": call_id, "ok": False,
                  "error": "No Laptop function named %s. Functions available: %s." % (name, known)})
            continue
        try:
            value = function(*request.get("args", []))
            send({"id": call_id, "ok": True, "value": value})
        except Exception as error:              # noqa: B902 - the robot's script gets the error instead
            send({"id": call_id, "ok": False, "error": "%s failed on the laptop:\n%s" % (name, short_traceback(error))})


if "__main__" == __name__:
    main()
