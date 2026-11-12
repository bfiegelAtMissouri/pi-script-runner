"""
remote_execute() basics: the robot calls functions that run on the laptop.

Add laptop_tools.py too, with "Laptop" checked beside it, then run this file. Needs only the app (the browser page
cannot run laptop functions).
"""
import random

from runner_io import remote_execute, shared_runner_file, RemoteExecutionError, say, show

try:
    readings = [random.randint(200, 900) for _ in range(10)]
    print("Readings:", readings)
    print("Average, worked out on the laptop:", remote_execute("average", readings))

    # A file made on the robot is sent with shared_runner_file(); the laptop function receives its own copy's path.
    with open("distances.csv", "w") as handle:
        handle.write("time,distance\n")
        for second, distance in enumerate(readings):
            handle.write("%d,%d\n" % (second, distance))
    show("distances.csv")
    summary = remote_execute("csv_summary", shared_runner_file("distances.csv"), "distance")
    print("Summary:", summary)
    say("The closest reading was %d millimeters." % summary["minimum"])

    # An error on the laptop arrives here as RemoteExecutionError, with the laptop's message.
    try:
        remote_execute("csv_summary", shared_runner_file("distances.csv"), "speed")
    except RemoteExecutionError as error:
        print("As expected, the laptop reported:", str(error).splitlines()[-1])

except KeyboardInterrupt:
    print("Stopped by the Stop button (Ctrl+C)")

finally:
    print("Done")
