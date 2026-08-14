#!/usr/bin/env python3
"""Capture one XIAO's received UDP audio as a diagnostic WAV.

This needs only Python's standard library.  It announces itself with a HELLO
from the same UDP socket it receives on, so firmware using learned-unicast
delivery sends this tool the same audio stream it sends the companion app.
"""

import argparse
import math
import socket
import struct
import time
import wave


MESH_ID = 0x4D455348
UDP_PORT = 45678
MULTICAST_GROUP = "239.255.42.99"
HEADER = struct.Struct(">4sBBHIIIIIHH")
NODE_ID = 0xD1A60001
TYPE_HELLO = 5
TYPE_AUDIO = 3
FRAME_SAMPLES = 320
SAMPLE_RATE = 16000
STEP_TABLE = (
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34,
    37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
    157,
    173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544,
    598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707,
    1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871,
    5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635,
    13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
)
INDEX_TABLE = (-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8)


def clamp(value, low, high):
    return max(low, min(high, value))


def decode_frame(payload):
    predictor, index, _reserved = struct.unpack(">hBB", payload[:4])
    samples = []
    for byte in payload[4:]:
        for delta in (byte & 0x0F, byte >> 4):
            step = STEP_TABLE[index]
            difference = step >> 3
            if delta & 4:
                difference += step
            if delta & 2:
                difference += step >> 1
            if delta & 1:
                difference += step >> 2
            predictor = clamp(predictor - difference if delta & 8 else predictor + difference,
                              -32768, 32767)
            index = clamp(index + INDEX_TABLE[delta], 0, 88)
            samples.append(predictor)
    return samples


def make_packet(packet_type, sequence=0, payload=b""):
    now_ms = int(time.monotonic() * 1000) & 0xFFFFFFFF
    return HEADER.pack(b"PTT1", packet_type, 0, HEADER.size, MESH_ID, NODE_ID,
                       0, sequence, now_ms, len(payload), 0) + payload


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--device-id", default="0x90688edc",
                        help="decimal or 0x-prefixed sender ID of the XIAO")
    parser.add_argument("--seconds", type=float, default=30)
    parser.add_argument("--output", default="device-capture.wav")
    args = parser.parse_args()
    device_id = int(args.device_id, 0)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("0.0.0.0", 0))
    sock.settimeout(0.25)
    deadline = time.monotonic() + args.seconds
    last_hello = 0.0
    next_report = 0.0
    frames, sequences, samples = 0, [], []
    print(f"Listening for device {device_id:08x} for {args.seconds:.0f} seconds...", flush=True)
    while time.monotonic() < deadline:
        now = time.monotonic()
        if now - last_hello >= 1.0:
            sock.sendto(make_packet(TYPE_HELLO, payload=b"audio-capture"),
                        (MULTICAST_GROUP, UDP_PORT))
            last_hello = now
        if now >= next_report:
            remaining = max(0, int(deadline - now))
            print(f"  {remaining:2d}s remaining — received {frames} audio frames", flush=True)
            next_report = now + 1.0
        try:
            packet, _address = sock.recvfrom(2048)
        except socket.timeout:
            continue
        if len(packet) < HEADER.size:
            continue
        magic, packet_type, _flags, header_len, mesh_id, sender, _session, sequence, _timestamp, payload_len, _ = HEADER.unpack(packet[:HEADER.size])
        payload = packet[header_len:header_len + payload_len]
        if (magic, mesh_id, packet_type, sender, len(payload)) != (b"PTT1", MESH_ID, TYPE_AUDIO, device_id, 164):
            continue
        sequences.append(sequence)
        samples.extend(decode_frame(payload))
        frames += 1

    if samples:
        with wave.open(args.output, "wb") as output:
            output.setnchannels(1)
            output.setsampwidth(2)
            output.setframerate(SAMPLE_RATE)
            output.writeframes(struct.pack("<%dh" % len(samples), *samples))
        chunks = [samples[i:i + FRAME_SAMPLES] for i in range(0, len(samples), FRAME_SAMPLES)]
        rms = [math.isqrt(sum(value * value for value in chunk) // len(chunk)) for chunk in chunks]
        gaps = [later - earlier for earlier, later in zip(sequences, sequences[1:])]
        print(f"saved {args.output}: {frames} frames / {frames / 50:.2f}s; "
              f"sequence max gap={max(gaps, default=0)}; "
              f"RMS first/last={rms[:3]}/{rms[-3:]}")
    else:
        print("No device audio received. Hold the device Broadcast button during the capture.")


if __name__ == "__main__":
    main()
