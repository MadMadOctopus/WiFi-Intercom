#!/usr/bin/env python3
"""
Wi-Fi half-duplex push-to-talk intercom - PC peer node.

This is a companion node for any number of Seeed XIAO ESP32-C3 devices on one
local network. Peers discover one another through a link-local UDP multicast
group; no static IP address list is required. Both nodes speak the exact same
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
import json
from datetime import datetime
from tkinter import messagebox, simpledialog

import numpy as np

try:
    import sounddevice as sd
except Exception as exc:  # pragma: no cover - only hit without the dependency
    sd = None
    _SD_IMPORT_ERROR = exc

# ---------------------------------------------------------------------------
# Group transport. Keep the protocol values in sync with firmware/main/config.h.
# ---------------------------------------------------------------------------
MESH_ID = 0x4D455348          # "MESH" - must match firmware MESH_ID
NODE_ID = random.SystemRandom().randrange(1, 0xFFFFFFFF)
UDP_PORT = 45678              # single UDP port used for all packets
BIND_IP = "0.0.0.0"           # listen on all interfaces
MULTICAST_GROUP = "239.255.42.99"
MULTICAST_TTL = 1
PC_ALIAS = socket.gethostname()[:32] or "Companion"
PEER_HELLO_MS = 3000
PEER_EXPIRE_MS = 10000

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
TYPE_HELLO = 5
TYPE_HEARTBEAT = 6
TYPE_CONFIG_GET = 7
TYPE_CONFIG_SET = 8
TYPE_CONFIG_REPLY = 9

FLAG_DIRECTED = 0x01


def pack_packet(ptype, session_id, sequence, timestamp_ms, payload=b"", flags=0):
    """Serialise one packet (header + optional payload)."""
    header = struct.pack(
        HEADER_FMT,
        MAGIC, ptype & 0xFF, flags & 0xFF, HEADER_LEN,
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
    def __init__(self, on_state_change=None, on_peers_change=None,
                 on_config=None):
        self.on_state_change = on_state_change
        self.on_peers_change = on_peers_change
        self.on_config = on_config
        self.lock = threading.RLock()
        self.state = STATE_IDLE
        self.state_events = queue.Queue()

        # Transmit session bookkeeping.
        self.tx_session = 0
        self.tx_sequence = 0
        self.encoder = AdpcmEncoder()
        self._claim_cancel = threading.Event()
        self.tx_directed = False
        self.tx_destination = None

        # Receive session bookkeeping.
        self.rx_sender = 0
        self.rx_session = 0
        self.rx_last_ms = 0
        self.jitter = JitterBuffer()
        self.rx_pcm_log = []          # accumulated played frames for the WAV
        self.last_talker_id = None
        self.peers = {}                # node id -> {address, alias, seen_ms}
        self.peers_dirty = True
        self.config_events = queue.Queue()

        self.running = True
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind((BIND_IP, UDP_PORT))
        self.sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL,
                             MULTICAST_TTL)
        self.sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_LOOP, 1)
        self.sock.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP,
                             struct.pack("=4s4s", socket.inet_aton(MULTICAST_GROUP),
                                         socket.inet_aton("0.0.0.0")))
        self.sock.settimeout(0.2)

        os.makedirs(RECORDINGS_DIR, exist_ok=True)

        self._threads = [
            threading.Thread(target=self._rx_loop, daemon=True),
            threading.Thread(target=self._timeout_loop, daemon=True),
            threading.Thread(target=self._hello_loop, daemon=True),
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
            self.state_events.put(state)

    def _send(self, ptype, session, sequence, payload=b"", destination=None,
              flags=0):
        pkt = pack_packet(ptype, session, sequence, self._now_ms(), payload, flags)
        if destination is not None:
            destinations = [destination]
        elif ptype == TYPE_HELLO:
            # Multicast is discovery only. All floor and audio traffic uses
            # normal Wi-Fi unicast learned from these beacons.
            destinations = [(MULTICAST_GROUP, UDP_PORT)]
        else:
            now = self._now_ms()
            with self.lock:
                destinations = [peer["address"] for peer in self.peers.values()
                                if (now - peer["seen_ms"]) & 0xFFFFFFFF <= PEER_EXPIRE_MS]
        for endpoint in destinations:
            try:
                self.sock.sendto(pkt, endpoint)
            except OSError:
                pass

    def _announce_peer(self, sender_id, address, alias=None):
        if sender_id == NODE_ID:
            return
        with self.lock:
            previous = self.peers.get(sender_id, {})
            self.peers[sender_id] = {
                "id": sender_id, "address": (address[0], UDP_PORT),
                "alias": alias or previous.get("alias") or f"Device {sender_id:08x}",
                "seen_ms": self._now_ms(),
            }
            self.peers_dirty = True

    def active_peers(self):
        with self.lock:
            return sorted(self.peers.values(), key=lambda peer: peer["alias"].lower())

    def consume_peers_dirty(self):
        with self.lock:
            dirty = self.peers_dirty
            self.peers_dirty = False
            return dirty

    def drain_config_events(self):
        events = []
        while True:
            try:
                events.append(self.config_events.get_nowait())
            except queue.Empty:
                return events

    def drain_state_events(self):
        events = []
        while True:
            try:
                events.append(self.state_events.get_nowait())
            except queue.Empty:
                return events

    def request_config(self, peer):
        self._send(TYPE_CONFIG_GET, random.getrandbits(32), 0,
                   b'{"cmd":"get"}', peer["address"])

    def set_config(self, peer, config):
        payload = dict(config)
        payload["cmd"] = "set"
        self._send(TYPE_CONFIG_SET, random.getrandbits(32), 0,
                   json.dumps(payload, separators=(",", ":")).encode(), peer["address"])

    def _remote_active(self):
        return self.state == STATE_RECEIVING

    # -- transmit (PTT) -----------------------------------------------------
    def ptt_press(self, destination=None):
        with self.lock:
            if self.state != STATE_IDLE:
                return                       # busy: receiving or already talking
            self.tx_session = random.getrandbits(32)
            self.tx_sequence = 0
            self.encoder.reset()
            self._claim_cancel.clear()
            self.tx_directed = destination is not None
            self.tx_destination = destination
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
            self._send(TYPE_CLAIM, session, 0,
                       flags=FLAG_DIRECTED if self.tx_directed else 0)
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
            self._send(TYPE_END, session, seq,
                       flags=FLAG_DIRECTED if self.tx_directed else 0)
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
        self._send(TYPE_AUDIO, session, seq, payload,
                   destination=self.tx_destination if self.tx_directed else None,
                   flags=FLAG_DIRECTED if self.tx_directed else 0)

    # -- receive ------------------------------------------------------------
    def _rx_loop(self):
        while self.running:
            try:
                data, addr = self.sock.recvfrom(2048)
            except socket.timeout:
                continue
            except OSError:
                break
            pkt = parse_packet(data)
            if pkt:
                self._announce_peer(pkt["sender_id"], addr)
                self._handle_packet(pkt, addr)

    def _handle_packet(self, pkt, address):
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
            elif ptype == TYPE_HELLO:
                try:
                    alias = pkt["payload"].decode("utf-8").strip()[:32]
                except UnicodeDecodeError:
                    alias = None
                self._announce_peer(pkt["sender_id"], address, alias)
            elif ptype == TYPE_CONFIG_REPLY:
                try:
                    config = json.loads(pkt["payload"].decode("utf-8"))
                except (UnicodeDecodeError, json.JSONDecodeError):
                    return
                self.config_events.put((pkt["sender_id"], config))

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
        self.last_talker_id = pkt["sender_id"]
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

                now = self._now_ms()
                expired = [node_id for node_id, peer in self.peers.items()
                           if (now - peer["seen_ms"]) & 0xFFFFFFFF > PEER_EXPIRE_MS]
                for node_id in expired:
                    del self.peers[node_id]
                if expired:
                    self.peers_dirty = True

    def _hello_loop(self):
        while self.running:
            self._send(TYPE_HELLO, 0, 0, PC_ALIAS.encode("utf-8")[:32])
            for _ in range(PEER_HELLO_MS // 100):
                if not self.running:
                    return
                time.sleep(0.1)


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
        root.title("Wi-Fi Intercom Companion")
        root.geometry("620x500")
        self.peer_rows = []
        self.node = IntercomNode()

        in_dev, out_dev = self._pick_devices()

        header = tk.Label(root, text=f"Companion {NODE_ID:08x}",
                          font=("Segoe UI", 12, "bold"))
        header.pack(pady=(10, 0))
        tk.Label(root, text=f"Mesh {MESH_ID:08x}  •  multicast {MULTICAST_GROUP}:{UDP_PORT}").pack()
        tk.Label(root, text=f"In:  {in_dev}",
                 font=("Segoe UI", 8)).pack()
        tk.Label(root, text=f"Out: {out_dev}",
                 font=("Segoe UI", 8)).pack()

        self.status_var = tk.StringVar(value=STATE_IDLE)
        self.status = tk.Label(root, textvariable=self.status_var,
                               font=("Segoe UI", 20, "bold"), fg="gray")
        self.status.pack(pady=8)

        ptt_frame = tk.Frame(root)
        ptt_frame.pack(pady=4)
        self.broadcast_ptt = tk.Button(ptt_frame, text="HOLD TO BROADCAST",
            font=("Segoe UI", 16, "bold"), bg="#2e7d32", fg="white",
            activebackground="#1b5e20", height=2, width=20)
        self.broadcast_ptt.pack(side=tk.LEFT, padx=4)
        self.broadcast_ptt.bind("<ButtonPress-1>", lambda e: self._press_broadcast())
        self.broadcast_ptt.bind("<ButtonRelease-1>", lambda e: self._release())
        self.reply_ptt = tk.Button(ptt_frame, text="HOLD TO REPLY",
            font=("Segoe UI", 12, "bold"), bg="#1565c0", fg="white",
            activebackground="#0d47a1", height=2)
        self.reply_ptt.pack(side=tk.LEFT, padx=4)
        self.reply_ptt.bind("<ButtonPress-1>", lambda e: self._press_reply())
        self.reply_ptt.bind("<ButtonRelease-1>", lambda e: self._release())

        tk.Label(root, text="Hold Space to broadcast. Select a device to direct-message or configure it.",
                 font=("Segoe UI", 8), fg="gray").pack()

        body = tk.Frame(root)
        body.pack(fill=tk.BOTH, expand=True, padx=12, pady=8)
        left = tk.LabelFrame(body, text="Active devices")
        left.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(0, 6))
        self.devices = tk.Listbox(left, height=10, exportselection=False)
        self.devices.pack(fill=tk.BOTH, expand=True, padx=6, pady=6)
        self.devices.bind("<<ListboxSelect>>", lambda _e: self._request_selected_config())
        actions = tk.Frame(left)
        actions.pack(fill=tk.X, padx=6, pady=(0, 6))
        tk.Button(actions, text="Get config", command=self._request_selected_config).pack(side=tk.LEFT)
        direct = tk.Button(actions, text="Hold to selected", bg="#6a1b9a", fg="white")
        direct.pack(side=tk.RIGHT)
        direct.bind("<ButtonPress-1>", lambda e: self._press_selected())
        direct.bind("<ButtonRelease-1>", lambda e: self._release())

        right = tk.LabelFrame(body, text="Selected device configuration")
        right.pack(side=tk.LEFT, fill=tk.Y)
        self.alias_var = tk.StringVar()
        self.volume_var = tk.StringVar(value="512")
        self.brightness_var = tk.StringVar(value="48")
        self.button_swap_var = tk.BooleanVar(value=False)
        self.orientation_var = tk.StringVar(value="0")
        self._field(right, "Alias", self.alias_var)
        self._field(right, "Volume (64–1024)", self.volume_var)
        self._field(right, "LED brightness (0–255)", self.brightness_var)
        tk.Checkbutton(right, text="Physical buttons swapped", variable=self.button_swap_var).pack(anchor=tk.W, padx=8, pady=2)
        orient = tk.Frame(right)
        orient.pack(fill=tk.X, padx=8, pady=2)
        tk.Label(orient, text="Ring orientation").pack(side=tk.LEFT)
        tk.OptionMenu(orient, self.orientation_var, "0", "180").pack(side=tk.RIGHT)
        tk.Button(right, text="Apply configuration", command=self._apply_config).pack(padx=8, pady=8, fill=tk.X)

        # Space bar as PTT.  key-repeat fires many presses, so guard with a flag.
        self._space_down = False
        root.bind("<KeyPress-space>", self._on_space_press)
        root.bind("<KeyRelease-space>", self._on_space_release)

        self.node.start()
        self.root.after(250, self._poll_node_events)
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

    @staticmethod
    def _field(parent, label, variable):
        frame = tk.Frame(parent)
        frame.pack(fill=tk.X, padx=8, pady=2)
        tk.Label(frame, text=label).pack(anchor=tk.W)
        tk.Entry(frame, textvariable=variable, width=24).pack(fill=tk.X)

    def _selected_peer(self):
        selection = self.devices.curselection()
        return self.peer_rows[selection[0]] if selection and selection[0] < len(self.peer_rows) else None

    def _press_broadcast(self):
        self.node.ptt_press()

    def _press_selected(self):
        peer = self._selected_peer()
        if not peer:
            messagebox.showinfo("Choose device", "Select an active device first.")
            return
        self.node.ptt_press(peer["address"])

    def _press_reply(self):
        peer = self.node.peers.get(self.node.last_talker_id)
        if not peer:
            messagebox.showinfo("Reply unavailable", "No active device has spoken to this companion yet.")
            return
        self.node.ptt_press(peer["address"])

    def _release(self):
        self.node.ptt_release()

    def _on_space_press(self, _event):
        if not self._space_down:
            self._space_down = True
            self._press_broadcast()

    def _on_space_release(self, _event):
        if self._space_down:
            self._space_down = False
            self._release()

    def _apply_state(self, state, color):
        self.status_var.set(state)
        self.status.config(fg=color)

    def _poll_node_events(self):
        if self.node.consume_peers_dirty():
            self._refresh_peers()
        colors = {STATE_IDLE: "gray", STATE_CLAIMING: "#f9a825",
                  STATE_TALKING: "#c62828", STATE_RECEIVING: "#1565c0"}
        for state in self.node.drain_state_events():
            self._apply_state(state, colors.get(state, "gray"))
        for sender_id, config in self.node.drain_config_events():
            self._apply_config_response(sender_id, config)
        if self.node.running:
            self.root.after(250, self._poll_node_events)

    def _refresh_peers(self):
        selected = self._selected_peer()
        selected_id = selected["id"] if selected else None
        self.peer_rows = self.node.active_peers()
        self.devices.delete(0, tk.END)
        for index, peer in enumerate(self.peer_rows):
            self.devices.insert(tk.END, f"{peer['alias']}  ({peer['id']:08x})  {peer['address'][0]}")
            if peer["id"] == selected_id:
                self.devices.selection_set(index)

    def _request_selected_config(self):
        peer = self._selected_peer()
        if peer:
            self.node.request_config(peer)

    def _apply_config_response(self, sender_id, config):
        peer = self._selected_peer()
        if not peer or peer["id"] != sender_id:
            return
        self.alias_var.set(str(config.get("alias", "")))
        self.volume_var.set(str(config.get("speaker_volume", 512)))
        self.brightness_var.set(str(config.get("led_brightness", 48)))
        self.button_swap_var.set(bool(config.get("buttons_swapped", False)))
        self.orientation_var.set(str(config.get("ring_orientation", 0)))

    def _apply_config(self):
        peer = self._selected_peer()
        if not peer:
            messagebox.showinfo("Choose device", "Select an active device first.")
            return
        try:
            config = {"alias": self.alias_var.get().strip()[:32],
                      "speaker_volume": int(self.volume_var.get()),
                      "led_brightness": int(self.brightness_var.get()),
                      "buttons_swapped": self.button_swap_var.get(),
                      "ring_orientation": int(self.orientation_var.get())}
        except ValueError:
            messagebox.showerror("Invalid value", "Volume, brightness and orientation must be numbers.")
            return
        self.node.set_config(peer, config)

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
