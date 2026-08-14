#!/usr/bin/env python3
"""Capture an ESP serial log for a bounded period.

Useful for hardware-assisted firmware diagnosis without an interactive serial
monitor. Example: python tools/serial_log.py --port COM6 --seconds 45
"""

from __future__ import annotations

import argparse
import time

import serial


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", required=True)
    parser.add_argument("--seconds", type=float, default=30)
    args = parser.parse_args()

    deadline = time.monotonic() + args.seconds
    with serial.Serial(args.port, 115200, timeout=0.2) as port:
        print(f"Listening on {args.port} for {args.seconds:g} seconds...", flush=True)
        while time.monotonic() < deadline:
            line = port.readline()
            if line:
                print(line.decode("utf-8", errors="replace"), end="", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
