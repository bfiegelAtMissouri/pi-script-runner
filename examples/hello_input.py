"""
Keyboard input and Stop.

Type your name in the box under the output, then press Stop (or Esc)
while the counter runs to see the finally: block run.
"""
import time

name = input("What is your name? ")
print("Hello,", name)

try:
    count = 0
    while True:
        count += 1
        print("Counting:", count)
        time.sleep(1)
except KeyboardInterrupt:
    print("Stopped by the Stop button (Ctrl+C).")
finally:
    print("finally: block ran; this is where a robot would stop its motors.")
