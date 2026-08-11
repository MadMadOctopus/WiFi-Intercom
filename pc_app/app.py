#!/usr/bin/env python3
"""
Wi-Fi half-duplex push-to-talk intercom - PC peer node.

This is one node of a two-peer intercom. The other peer is a Seeed XIAO
ESP32-C3 running the firmware in ../firmware. Both nodes speak the exact same
UDP wire protocol and IMA ADPCM codec (implemented independently, byte-for-byte
compatible - see protocol notes below and firmware/main/adpcm.c).

Audio internals: 16 kHz, mono, 16-bit PCM, 20 ms frames (320 samples).
Transport:       IMA ADPCM, packet-independent frames over UDP.

Requires: sounddevice, numpy. Everything else is standard library.
Run:      python app.py
"""

import os
import sys
import time
import wave
import queue
import random
import socket
import struct
import threading
import tkinter as tk
from datetime import datetime

import numpy as np

try:
    import sounddevice as sd
except Exception as exc:  # pragma: no cover - only hit without the dependency
    sd = None
    _SD_IMPORT_ERROR = exc

# ---------------------------------------------------------------------------
# Static configuration.  Hardcoded values are fine for this proof of concept.
# Keep these in sync with firmware/main/config.h
# ---------------------------------------------------------------------------
MESH_ID = 0x4D455348          # "MESH" - must match firmware MESH_ID
NODE_ID = 0x00000002          # this PC node's device id (ESP is 0x00000001)
PEER_IP = "192.168.0.122"      # IPv4 of the other peer (the ESP32-C3)
UDP_PORT = 45678              # single UDP port used for all packets
BIND_IP = "0.0.0.0"           # listen on all interfaces

# Audio format (do not change without changing the firmware too).
SAMPLE_RATE = 16000
FRAME_SAMPLES = 320           # 20 ms at 16 kHz
FRAME_MS = 20

# Floor-control / jitter-buffer timing.
CLAIM_COUNT = 3
CLAIM_INTERVAL_MS = 30
PRE_AUDIO_DELAY_MS = 100
END_COUNT = 3
RX_TIMEOUT_MS = 750           # release floor if remote silent this long
JITTER_PREBUFFER = 4          # buffer 4 frames (80 ms) before playout
REORDER_WINDOW = 4            # accept out-of-order frames within this window

RECORDINGS_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                              "recordings")

# ---------------------------------------------------------------------------
# UDP protocol - fixed 32-byte header, network byte order, packed explicitly.
#
#   magic[4]     = "PTT1"
#   type         = uint8   (1=CLAIM 2=BUSY 3=AUDIO 4=END)
#   flags        = uint8
#   header_len   = uint16  (= 32)
#   mesh_id      = uint32
#   sender_id    = uint32
#   session_id   = uint32
#   sequence     = uint32
#   timestamp_ms = uint32
#   payload_len  = uint16
#   reserved     = uint16  (= 0)
# ---------------------------------------------------------------------------
MAGIC = b"PTT1"
HEADER_LEN = 32
HEADER_FMT = ">4sBBHIIIIIHH"   # big-endian, exactly 32 bytes
assert struct.calcsize(HEADER_FMT) == HEADER_LEN

TYPE_CLAIM = 1
TYPE_BUSY = 2
TYPE_AUDIO = 3
TYPE_END = 4


def pack_packet(ptype, session_id, sequence, timestamp_ms, payload=b""):
    """Serialise one packet (header + optional payload)."""
    header = struct.pack(
        HEADER_FMT,
        MAGIC, ptype & 0xFF, 0, HEADER_LEN,
        MESH_ID & 0xFFFFFFFF, NODE_ID & 0xFFFFFFFF,
        session_id & 0xFFFFFFFF, sequence & 0xFFFFFFFF,
        timestamp_ms & 0xFFFFFFFF, len(payload) & 0xFFFF, 0)
    return header + payload


def parse_packet(data):
    """Validate and unpack a packet.  Returns a dict or None if invalid."""
    if len(data) < HEADER_LEN:
        return None
    (magic, ptype, flags, header_len, mesh_id, sender_id,
     session_id, sequence, timestamp_ms, payload_len, reserved) = \
        struct.unpack(HEADER_FMT, data[:HEADER_LEN])

    if magic != MAGIC or header_len != HEADER_LEN:
        return None
    if mesh_id != (MESH_ID & 0xFFFFFFFF):
        return None
    if sender_id == (NODE_ID & 0xFFFFFFFF):     # ignore our own packets
        return None
    payload = data[HEADER_LEN:HEADER_LEN + payload_len]
    if len(payload) != payload_len:             # malformed payload length
        return None
    return {
        "type": ptype, "flags": flags,
        "sender_id": sender_id, "session_id": session_id,
        "sequence": sequence, "timestamp_ms": timestamp_ms,
        "payload": payload,
    }


# ---------------------------------------------------------------------------
# IMA ADPCM codec - packet-independent frames.
#
# Each frame is self-describing so a single lost packet never corrupts later
# ones: the frame header carries the predictor and step index captured *before*
# the first sample of the frame, followed by 320 4-bit nibbles.
#
#   predictor   int16  (big-endian)
#   step_index  uint8
#   reserved    uint8  (= 0)
#   nibbles     160 bytes  (320 samples, low nibble first)
#
# This is the same layout the firmware produces/consumes.
# ---------------------------------------------------------------------------
_STEP_TABLE = [
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
    19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
    50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
    130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
    337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
    876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
    2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
    5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
    15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
]
_INDEX_TABLE = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8]

ADPCM_HEADER_LEN = 4
ADPCM_PAYLOAD_LEN = ADPCM_HEADER_LEN + FRAME_SAMPLES // 2   # 4 + 160 = 164


def _clamp(v, lo, hi):
    return lo if v < lo else (hi if v > hi else v)


class AdpcmEncoder:
    """Continuous IMA ADPCM encoder; state persists across a session for
    quality, but each frame stamps the state it started from (packet
    independence)."""

    def __init__(self):
        self.predictor = 0
        self.index = 0

    def reset(self):
        self.predictor = 0
        self.index = 0

    def encode_frame(self, pcm):
        """pcm: iterable of 320 int16 samples -> 164-byte ADPCM frame."""
        start_pred = self.predictor
        start_index = self.index
        nibbles = bytearray(FRAME_SAMPLES // 2)

        predictor = start_pred
        index = start_index
        for i, sample in enumerate(pcm):
            sample = int(sample)
            step = _STEP_TABLE[index]
            diff = sample - predictor
            sign = 8 if diff < 0 else 0
            if diff < 0:
                diff = -diff
            delta = 0
            vpdiff = step >> 3
            if diff >= step:
                delta |= 4
                diff -= step
                vpdiff += step
            step >>= 1
            if diff >= step:
                delta |= 2
                diff -= step
                vpdiff += step
            step >>= 1
            if diff >= step:
                delta |= 1
                vpdiff += step
            if sign:
                predictor -= vpdiff
            else:
                predictor += vpdiff
            predictor = _clamp(predictor, -32768, 32767)
            delta |= sign
            index = _clamp(index + _INDEX_TABLE[delta], 0, 88)

            if i & 1:
                nibbles[i >> 1] |= (delta & 0x0F) << 4
            else:
                nibbles[i >> 1] = delta & 0x0F

        self.predictor = predictor
        self.index = index
        header = struct.pack(">hBB", _clamp(start_pred, -32768, 32767),
                             start_index & 0xFF, 0)
        return header + bytes(nibbles)


def decode_frame(payload):
    """164-byte ADPCM frame -> numpy int16 array of 320 samples, or None."""
    if len(payload) != ADPCM_PAYLOAD_LEN:
        return None
    predictor, index, _reserved = struct.unpack(">hBB",
                                                payload[:ADPCM_HEADER_LEN])
    index = _clamp(index, 0, 88)
    nibbles = payload[ADPCM_HEADER_LEN:]
    out = np.empty(FRAME_SAMPLES, dtype=np.int16)

    predictor = int(predictor)
    for i in range(FRAME_SAMPLES):
        byte = nibbles[i >> 1]
        delta = (byte >> 4) if (i & 1) else (byte & 0x0F)
        step = _STEP_TABLE[index]
        vpdiff = step >> 3
        if delta & 4:
            vpdiff += step
        if delta & 2:
            vpdiff += step >> 1
        if delta & 1:
            vpdiff += step >> 2
        if delta & 8:
            predictor -= vpdiff
        else:
            predictor += vpdiff
        predictor = _clamp(predictor, -32768, 32767)
        index = _clamp(index + _INDEX_TABLE[delta], 0, 88)
        out[i] = predictor
    return out


# ---------------------------------------------------------------------------
# Jitter buffer + packet-loss concealment for the receive path.
# ---------------------------------------------------------------------------
class JitterBuffer:
    """Simple four-frame (80 ms) reorder + playout buffer.

    On a new session it prebuffers JITTER_PREBUFFER frames before playout
    begins.  Every 20 ms pop() returns the expected frame if present,
    otherwise conceals the loss by replaying the previous frame attenuated
    (or silence)."""

    def __init__(self):
        self.lock = threading.Lock()
        self.reset()

    def reset(self):
        with self.lock:
            self.frames = {}          # sequence -> np.int16[320]
            self.expected = 0
            self.started = False
            self.last_frame = None

    def push(self, sequence, pcm):
        with self.lock:
            # Ignore duplicates and very late frames outside the window.
            if self.started and sequence < self.expected:
                return
            if sequence in self.frames:
                return
            self.frames[sequence] = pcm
            if not self.started and len(self.frames) >= JITTER_PREBUFFER:
                self.started = True
                self.expected = min(self.frames.keys())

    def pop(self):
        """Return (pcm, produced_real_audio).  None if not playing yet."""
        with self.lock:
            if not self.started:
                return None, False
            frame = self.frames.pop(self.expected, None)
            self.expected += 1
            # Drop anything now hopelessly late.
            stale = [s for s in self.frames if s < self.expected - REORDER_WINDOW]
            for s in stale:
                del self.frames[s]
            if frame is not None:
                self.last_frame = frame
                return frame, True
            # Packet-loss concealment.
            if self.last_frame is not None:
                concealed = (self.last_frame.astype(np.float32) * 0.5)
                concealed = concealed.astype(np.int16)
                self.last_frame = concealed
                return concealed, False
            return np.zeros(FRAME_SAMPLES, dtype=np.int16), False


# ---------------------------------------------------------------------------
# The intercom node: floor control, networking, capture, playback, recording.
# ---------------------------------------------------------------------------
STATE_IDLE = "Idle"
STATE_CLAIMING = "Claiming"
STATE_TALKING = "Talking"
STATE_RECEIVING = "Receiving"


class IntercomNode:
    def __init__(self, on_state_change=None):
        self.on_state_change = on_state_change
        self.lock = threading.RLock()
        self.state = STATE_IDLE

        # Transmit session bookkeeping.
        self.tx_session = 0
        self.tx_sequence = 0
        self.encoder = AdpcmEncoder()
        self._claim_cancel = threading.Event()

        # Receive session bookkeeping.
        self.rx_sender = 0
        self.rx_session = 0
        self.rx_last_ms = 0
        self.jitter = JitterBuffer()
        self.rx_pcm_log = []          # accumulated played frames for the WAV

        self.running = True
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        except OSError:
            pass
        self.sock.bind((BIND_IP, UDP_PORT))
        self.sock.settimeout(0.2)

        os.makedirs(RECORDINGS_DIR, exist_ok=True)

        self._threads = [
            threading.Thread(target=self._rx_loop, daemon=True),
            threading.Thread(target=self._timeout_loop, daemon=True),
        ]

    # -- lifecycle ----------------------------------------------------------
    def start(self):
        for t in self._threads:
            t.start()

    def close(self):
        self.running = False
        try:
            self.sock.close()
        except OSError:
            pass

    # -- helpers ------------------------------------------------------------
    @staticmethod
    def _now_ms():
        return int(time.monotonic() * 1000) & 0xFFFFFFFF

    def _set_state(self, state):
        if self.state != state:
            self.state = state
            if self.on_state_change:
                self.on_state_change(state)

    def _send(self, ptype, session, sequence, payload=b""):
        pkt = pack_packet(ptype, session, sequence, self._now_ms(), payload)
        try:
            self.sock.sendto(pkt, (PEER_IP, UDP_PORT))
        except OSError:
            pass

    def _remote_active(self):
        return self.state == STATE_RECEIVING

    # -- transmit (PTT) -----------------------------------------------------
    def ptt_press(self):
        with self.lock:
            if self.state != STATE_IDLE:
                return                       # busy: receiving or already talking
            self.tx_session = random.getrandbits(32)
            self.tx_sequence = 0
            self.encoder.reset()
            self._claim_cancel.clear()
            self._set_state(STATE_CLAIMING)
            session = self.tx_session
        threading.Thread(target=self._claim_sequence, args=(session,),
                         daemon=True).start()

    def _claim_sequence(self, session):
        for _ in range(CLAIM_COUNT):
            if self._claim_cancel.is_set():
                return
            with self.lock:
                if self.state != STATE_CLAIMING or self.tx_session != session:
                    return
            self._send(TYPE_CLAIM, session, 0)
            time.sleep(CLAIM_INTERVAL_MS / 1000.0)
        time.sleep(max(0, (PRE_AUDIO_DELAY_MS -
                          CLAIM_COUNT * CLAIM_INTERVAL_MS)) / 1000.0)
        with self.lock:
            if self._claim_cancel.is_set():
                return
            if self.state == STATE_CLAIMING and self.tx_session == session:
                self._set_state(STATE_TALKING)

    def ptt_release(self):
        with self.lock:
            if self.state not in (STATE_CLAIMING, STATE_TALKING):
                return
            self._claim_cancel.set()
            session = self.tx_session
            seq = self.tx_sequence
            self._set_state(STATE_IDLE)
        # Signal end of our session (harmless even if we were only claiming).
        for _ in range(END_COUNT):
            self._send(TYPE_END, session, seq)
            time.sleep(0.005)

    def capture_frame(self, pcm_int16):
        """Called by the audio input callback with 320 int16 samples.
        Encodes and transmits one AUDIO frame if we currently hold the floor."""
        with self.lock:
            if self.state != STATE_TALKING:
                return
            session = self.tx_session
            seq = self.tx_sequence
            self.tx_sequence += 1
        payload = self.encoder.encode_frame(pcm_int16)
        self._send(TYPE_AUDIO, session, seq, payload)

    # -- receive ------------------------------------------------------------
    def _rx_loop(self):
        while self.running:
            try:
                data, _addr = self.sock.recvfrom(2048)
            except socket.timeout:
                continue
            except OSError:
                break
            pkt = parse_packet(data)
            if pkt:
                self._handle_packet(pkt)

    def _handle_packet(self, pkt):
        with self.lock:
            ptype = pkt["type"]
            if ptype == TYPE_CLAIM:
                self._handle_claim(pkt)
            elif ptype == TYPE_AUDIO:
                self._handle_audio(pkt)
            elif ptype == TYPE_END:
                self._handle_end(pkt)
            elif ptype == TYPE_BUSY:
                # A peer told us it is busy; if we were claiming, back off.
                if self.state == STATE_CLAIMING:
                    self._claim_cancel.set()
                    self._set_state(STATE_IDLE)

    def _remote_wins(self, pkt):
        """Deterministic contention winner: lowest (session_id, sender_id)."""
        local = (self.tx_session, NODE_ID)
        remote = (pkt["session_id"], pkt["sender_id"])
        return remote < local

    def _handle_claim(self, pkt):
        if self.state in (STATE_CLAIMING, STATE_TALKING):
            # Contention: does the remote claim beat ours?
            if self._remote_wins(pkt):
                self._claim_cancel.set()
                self._begin_receiving(pkt)
            else:
                # We win; tell them we own the floor.
                self._send(TYPE_BUSY, self.tx_session, 0)
            return
        if self.state == STATE_IDLE:
            self._begin_receiving(pkt)
        elif self.state == STATE_RECEIVING and pkt["session_id"] != self.rx_session:
            # Another session is already playing; tell the newcomer we're busy.
            self._send(TYPE_BUSY, self.rx_session, 0)

    def _begin_receiving(self, pkt):
        self.rx_sender = pkt["sender_id"]
        self.rx_session = pkt["session_id"]
        self.rx_last_ms = self._now_ms()
        self.jitter.reset()
        self.rx_pcm_log = []
        self._set_state(STATE_RECEIVING)

    def _handle_audio(self, pkt):
        if self.state in (STATE_CLAIMING, STATE_TALKING):
            if self._remote_wins(pkt):
                self._claim_cancel.set()
                self._begin_receiving(pkt)
            else:
                return
        if self.state == STATE_IDLE:
            self._begin_receiving(pkt)
        if self.state != STATE_RECEIVING or pkt["session_id"] != self.rx_session:
            return
        self.rx_last_ms = self._now_ms()
        pcm = decode_frame(pkt["payload"])
        if pcm is not None:
            self.jitter.push(pkt["sequence"], pcm)

    def _handle_end(self, pkt):
        if self.state == STATE_RECEIVING and pkt["session_id"] == self.rx_session:
            self._finalize_rx()

    def _finalize_rx(self):
        # Drain whatever the jitter buffer can still produce, then save WAV.
        for _ in range(JITTER_PREBUFFER + REORDER_WINDOW):
            pcm, real = self.jitter.pop()
            if pcm is None:
                break
            if real:
                self.rx_pcm_log.append(pcm)
        self._write_wav()
        self.jitter.reset()
        self._set_state(STATE_IDLE)

    def _write_wav(self):
        if not self.rx_pcm_log:
            return
        ts = datetime.now().strftime("%Y%m%d-%H%M%S")
        path = os.path.join(
            RECORDINGS_DIR,
            f"{ts}-{self.rx_sender:08x}-{self.rx_session:08x}.wav")
        audio = np.concatenate(self.rx_pcm_log)
        try:
            with wave.open(path, "wb") as wf:
                wf.setnchannels(1)
                wf.setsampwidth(2)
                wf.setframerate(SAMPLE_RATE)
                wf.writeframes(audio.tobytes())
            print(f"[wav] saved {path} ({len(audio)/SAMPLE_RATE:.1f}s)")
        except OSError as exc:
            print(f"[wav] failed to save {path}: {exc}")

    # -- playout ------------------------------------------------------------
    def playout_frame(self):
        """Return 320 int16 samples for the output stream (silence when idle)."""
        with self.lock:
            receiving = self.state == STATE_RECEIVING
        if not receiving:
            return np.zeros(FRAME_SAMPLES, dtype=np.int16)
        pcm, real = self.jitter.pop()
        if pcm is None:
            return np.zeros(FRAME_SAMPLES, dtype=np.int16)
        if real:
            self.rx_pcm_log.append(pcm)
        return pcm

    def _timeout_loop(self):
        while self.running:
            time.sleep(0.05)
            with self.lock:
                if self.state == STATE_RECEIVING:
                    age = (self._now_ms() - self.rx_last_ms) & 0xFFFFFFFF
                    if age > RX_TIMEOUT_MS:
                        self._finalize_rx()


# ---------------------------------------------------------------------------
# Audio engine: sounddevice capture (16 kHz mono) and non-blocking playback.
# ---------------------------------------------------------------------------
class AudioEngine:
    def __init__(self, node, input_device=None, output_device=None):
        self.node = node
        self.input_device = input_device
        self.output_device = output_device
        self.in_stream = None
        self.out_stream = None

    def start(self):
        if sd is None:
            raise RuntimeError(
                f"sounddevice not available: {_SD_IMPORT_ERROR}")

        self.in_stream = sd.InputStream(
            samplerate=SAMPLE_RATE, blocksize=FRAME_SAMPLES, channels=1,
            dtype="int16", device=self.input_device, callback=self._on_input)
        self.out_stream = sd.OutputStream(
            samplerate=SAMPLE_RATE, blocksize=FRAME_SAMPLES, channels=1,
            dtype="int16", device=self.output_device, callback=self._on_output)
        self.in_stream.start()
        self.out_stream.start()

    def stop(self):
        for s in (self.in_stream, self.out_stream):
            if s is not None:
                try:
                    s.stop()
                    s.close()
                except Exception:
                    pass

    def _on_input(self, indata, frames, time_info, status):
        # indata is (320, 1) int16; hand a flat copy to the node.
        self.node.capture_frame(indata[:, 0].copy())

    def _on_output(self, outdata, frames, time_info, status):
        pcm = self.node.playout_frame()
        outdata[:, 0] = pcm


# ---------------------------------------------------------------------------
# Tkinter user interface.
# ---------------------------------------------------------------------------
class IntercomUI:
    def __init__(self, root):
        self.root = root
        root.title("Wi-Fi PTT Intercom")
        root.geometry("380x300")

        self.node = IntercomNode(on_state_change=self._on_state_change)

        in_dev, out_dev = self._pick_devices()

        header = tk.Label(root, text=f"Node {NODE_ID:08x}",
                          font=("Segoe UI", 12, "bold"))
        header.pack(pady=(10, 0))
        tk.Label(root, text=f"Peer: {PEER_IP}:{UDP_PORT}").pack()
        tk.Label(root, text=f"In:  {in_dev}",
                 font=("Segoe UI", 8)).pack()
        tk.Label(root, text=f"Out: {out_dev}",
                 font=("Segoe UI", 8)).pack()

        self.status_var = tk.StringVar(value=STATE_IDLE)
        self.status = tk.Label(root, textvariable=self.status_var,
                               font=("Segoe UI", 20, "bold"), fg="gray")
        self.status.pack(pady=8)

        self.ptt = tk.Button(root, text="HOLD TO TALK",
                             font=("Segoe UI", 16, "bold"),
                             bg="#2e7d32", fg="white", activebackground="#1b5e20",
                             height=2, width=18)
        self.ptt.pack(pady=6)
        self.ptt.bind("<ButtonPress-1>", lambda e: self._press())
        self.ptt.bind("<ButtonRelease-1>", lambda e: self._release())

        tk.Label(root, text="(or hold SPACE while focused)",
                 font=("Segoe UI", 8), fg="gray").pack()

        # Space bar as PTT.  key-repeat fires many presses, so guard with a flag.
        self._space_down = False
        root.bind("<KeyPress-space>", self._on_space_press)
        root.bind("<KeyRelease-space>", self._on_space_release)

        self.node.start()
        try:
            self.audio = AudioEngine(self.node, in_dev if isinstance(in_dev, int)
                                     else None,
                                     out_dev if isinstance(out_dev, int)
                                     else None)
            self.audio.start()
        except Exception as exc:
            self.audio = None
            self.status_var.set("No audio")
            print(f"[audio] {exc}")

        root.protocol("WM_DELETE_WINDOW", self._on_close)

    def _pick_devices(self):
        if sd is None:
            return "unavailable", "unavailable"
        try:
            in_idx = sd.default.device[0]
            out_idx = sd.default.device[1]
            devs = sd.query_devices()
            in_name = devs[in_idx]["name"] if in_idx is not None and in_idx >= 0 \
                else "default"
            out_name = devs[out_idx]["name"] if out_idx is not None and out_idx >= 0 \
                else "default"
            return in_name, out_name
        except Exception:
            return "default", "default"

    def _press(self):
        self.node.ptt_press()

    def _release(self):
        self.node.ptt_release()

    def _on_space_press(self, _event):
        if not self._space_down:
            self._space_down = True
            self._press()

    def _on_space_release(self, _event):
        if self._space_down:
            self._space_down = False
            self._release()

    def _on_state_change(self, state):
        colors = {STATE_IDLE: "gray", STATE_CLAIMING: "#f9a825",
                  STATE_TALKING: "#c62828", STATE_RECEIVING: "#1565c0"}
        # UI updates must happen on the Tk thread.
        self.root.after(0, lambda: self._apply_state(state, colors.get(state,
                                                                       "gray")))

    def _apply_state(self, state, color):
        self.status_var.set(state)
        self.status.config(fg=color)

    def _on_close(self):
        if self.audio:
            self.audio.stop()
        self.node.close()
        self.root.destroy()


def main():
    if sd is None:
        print("WARNING: sounddevice is not installed; audio will not work.")
        print(f"         ({_SD_IMPORT_ERROR})")
    root = tk.Tk()
    IntercomUI(root)
    root.mainloop()


if __name__ == "__main__":
    main()
