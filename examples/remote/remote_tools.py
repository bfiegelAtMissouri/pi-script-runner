"""
Local file: functions the robot's script can call with remote_execute().

In the app, add this file with "Local" checked beside it. It stays on the laptop, and its functions run there,
with the laptop's Python, when the robot's script asks. Needs nothing beyond Python itself.
"""
import csv
import statistics


def average(numbers):
    """Returns the mean of a list of numbers."""
    return statistics.mean(numbers)


def csv_summary(path, column):
    """
    Purpose: summarize one column of a CSV file the robot sent with shared_runner_file()
    Inputs:  path - the laptop's copy of the file; column - the column's name
    Returns: {"rows": ..., "minimum": ..., "maximum": ..., "mean": ...}
    """
    with open(path, newline="") as handle:
        values = [float(row[column]) for row in csv.DictReader(handle)]
    return {"rows": len(values), "minimum": min(values), "maximum": max(values), "mean": statistics.mean(values)}
