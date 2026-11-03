#!/usr/bin/env python3
"""Logitech G300s (046D:C246) CLI Entry Point."""

import sys
import os

# Prevent this file from shadowing the g300s package in src/
cwd = os.path.dirname(os.path.abspath(__file__))
while cwd in sys.path:
    sys.path.remove(cwd)

src_dir = os.path.join(cwd, "src")
if src_dir not in sys.path:
    sys.path.insert(0, src_dir)

from g300s.cli import main

if __name__ == "__main__":
    main(sys.argv[1:])
