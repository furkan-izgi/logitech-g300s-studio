"""Click at screen coordinates. Usage: python tools/click.py X Y [X Y ...]"""
import sys, time
from pynput.mouse import Controller, Button

m = Controller()
args = list(map(int, sys.argv[1:]))
for i in range(0, len(args), 2):
    x, y = args[i], args[i + 1]
    m.position = (x, y)
    time.sleep(0.35)
    m.click(Button.left)
    print(f"clicked {x},{y}", flush=True)
    time.sleep(0.6)
