# Wi-Fi Half-Duplex Push-to-Talk Intercom

A compact proof-of-concept intercom with two **equivalent peer nodes** on the
same Wi-Fi network:

1. **`firmware/`** — a Seeed Studio **XIAO ESP32-C3** node (ESP-IDF, C).
2. **`pc_app/`** — a companion **PC application** (Python + Tkinter).

Hold PTT to talk; release to stop. Audio is half-duplex (only one peer holds
the floor at a time). The PC also saves every received message as a timestamped
`.wav`.

Both nodes implement the **same UDP protocol and IMA ADPCM codec independently**
(in C and in Python) — they are byte-for-byte compatible, verified by a
cross-language parity test.

- Internals: **16 kHz, mono, 16-bit PCM**, **20 ms frames = 320 samples**.
- Transport: **UDP**, one port, packet-independent **IMA ADPCM** frames.
- No server, MQTT, TCP, discovery, encryption, or mesh routing.

---

## Hardware wiring (XIAO ESP32-C3)

### Adafruit MAX98357A I²S amplifier

| MAX98357A pin | XIAO ESP32-C3 pin |
|---|---|
| `VIN`  | `5V` |
| `GND`  | `GND` |
| `BCLK` | `D4` / GPIO6 |
| `LRC`  | `D5` / GPIO7 |
| `DIN`  | `D3` / GPIO5 |
| `SD`   | **`3V3`** (see note) |
| `GAIN` | *leave unconnected* |

> **`SD` must be tied to `3V3`.** On the MAX98357A the `SD`/`SD_MODE` pin is
> multi-function: left floating or low, the amp stays in **shutdown** (no LED,
> no sound). Tying it to `3V3` both enables the amp and selects the **Left**
> channel — which is correct here, because the firmware writes the same mono
> sample to the left and right slots. (Some breakouts have an onboard pull that
> enables `(L+R)/2` when floating, but many do not — tying to `3V3` is the
> reliable choice.)

Connect the **4 W, 8 Ω speaker directly across the amp's speaker output
terminals**. Do **not** connect either speaker wire to ground (the MAX98357A
output is a bridge-tied / differential load). The amp runs from **5 V**; its
I²S logic inputs are 3.3 V-compatible.

### Analogue microphone module (VCC / GND / OUT)

| Microphone pin | XIAO ESP32-C3 pin |
|---|---|
| `VCC` | `3V3` |
| `GND` | `GND` |
| `OUT` | `A1` / `D1` / GPIO3 / ADC1_CH3 |

The module runs at **3.3 V**. Its output is biased positive and must never
exceed the ESP32 ADC range (~0–3.3 V with 12 dB attenuation). If audio clips,
reduce `MIC_GAIN` in `config.h` or the module's onboard gain pot.

### PTT button

| Button leg | XIAO ESP32-C3 pin |
|---|---|
| One leg   | `D7` / GPIO20 |
| Other leg | `GND` |

GPIO20 is configured `INPUT_PULLUP`; **pressed = LOW**.

---

## Firmware: build & flash (ESP-IDF)

Requires **ESP-IDF v5.1+** ([install guide](https://docs.espressif.com/projects/esp-idf/en/latest/esp32c3/get-started/)).

```bash
cd firmware
idf.py set-target esp32c3
idf.py build
idf.py -p <PORT> flash monitor      # e.g. -p COM7 (Windows) or -p /dev/ttyACM0
```

`monitor` shows USB-serial status logs (Wi-Fi connect, got-IP, floor changes).
Exit the monitor with `Ctrl-]`.

On boot the firmware plays a short **1 kHz tone** through the speaker so you can
verify the amp/speaker before any network traffic.

### Configuring Wi-Fi, mesh ID, node IDs, UDP port, peer IP

All static configuration lives in **`firmware/main/config.h`** — edit and
re-flash:

| Setting | Macro in `config.h` | Must match PC (`pc_app/app.py`) |
|---|---|---|
| Wi-Fi SSID / password | `WIFI_SSID`, `WIFI_PASSWORD` | your network |
| Mesh ID  | `MESH_ID` (default `0x4D455348`) | **yes** — `MESH_ID` |
| This node's ID | `NODE_ID` (default `0x00000001`) | must differ from PC's `NODE_ID` |
| Peer (PC) IPv4 | `PEER_IP` | set to the PC's IP |
| UDP port | `UDP_PORT` (default `45678`) | **yes** — `UDP_PORT` |
| Mic gain (capture) | `MIC_GAIN` | — |
| Speaker volume (playback) | `SPK_VOLUME` | `256`=unity, `512`=2×, `1024`=4× |

> The two peers use **unicast** to each other's IP. Set the ESP's `PEER_IP` to
> the PC's IP, and the PC's `PEER_IP` (in `app.py`) to the ESP's IP. Find each
> device's IP from your router or the ESP's serial log (`Got IP: ...`).

### Adjusting ESP speaker volume

There are three independent gain stages, loudest-per-effort first:

1. **`SPK_VOLUME` in `config.h` (software, easiest).** Digital gain on received
   audio, `256` = unity. Default `512` (2×). Raise toward `1024` for more; if
   loud speech starts to distort you've hit the digital ceiling — back off and
   use stage 2 or 3 instead. Re-flash to apply.
2. **MAX98357A `GAIN` pin (analog, cleanest).** Sets the amp's fixed gain.
   Floating = 9 dB (default). For more: `GAIN`→`GND` directly = **12 dB**, or
   `GAIN`→`GND` via a 100 kΩ resistor = **15 dB**. (`GAIN`→`VIN` = 6 dB,
   `GAIN`→`VIN` via 100 kΩ = 3 dB for *less*.)
3. **`MIC_GAIN` on the *sending* node.** Received loudness is ultimately limited
   by how hot the far end captured the audio — a quiet talker plays back quiet no
   matter what. Raise `MIC_GAIN` on the peer that's transmitting.

Prefer the amp `GAIN` pin for headroom; use `SPK_VOLUME` for quick, no-solder
tuning.

---

## PC application: setup & run

Requires **Python 3.9+**. Uses `sounddevice` + `numpy`; everything else is
standard library (`socket`, `wave`, `threading`, `queue`, `tkinter`, `struct`).

```bash
cd pc_app
python -m venv .venv
# Windows:
.venv\Scripts\activate
# macOS/Linux:
source .venv/bin/activate

pip install -r requirements.txt
python app.py
```

> On Linux you may also need the system Tk package (`sudo apt install python3-tk`)
> and PortAudio (`sudo apt install libportaudio2`).

### Configuring the PC node

Edit the constants at the top of **`pc_app/app.py`**:

| Setting | Constant | Notes |
|---|---|---|
| Mesh ID | `MESH_ID` | must match firmware |
| This node's ID | `NODE_ID` (default `0x00000002`) | must differ from ESP's |
| Peer (ESP) IPv4 | `PEER_IP` | set to the ESP's IP |
| UDP port | `UDP_PORT` | must match firmware |

The Tkinter window shows the local node ID, the peer address, the selected
input/output device names, a live status (**Idle / Claiming / Talking /
Receiving**), and a prominent **HOLD TO TALK** button. You can also **hold the
Space bar** while the window is focused.

Received messages are written to
**`pc_app/recordings/YYYYMMDD-HHMMSS-<sender>-<session>.wav`** (mono, 16-bit,
16 kHz). The directory is created automatically.

---

## Basic test sequence

1. **Verify the ESP speaker.** Power/flash the ESP. On boot you should hear the
   short 1 kHz startup tone from the speaker. (No network needed.)
2. **Start the PC app.** `python app.py`. The window shows *Idle* and the peer
   address. Confirm the ESP's serial log shows `Got IP:` and the PC's `PEER_IP`
   points at that address (and vice-versa).
3. **PC → ESP.** Hold the PC's **HOLD TO TALK** (or Space) and speak. Status
   goes *Claiming → Talking*. Audio should come out of the ESP speaker with low
   latency; release to stop.
4. **ESP → PC.** Hold the **physical button** on the ESP and speak into the mic
   module. The PC status shows *Receiving* and you hear audio from the PC's
   speakers/headphones.
5. **Confirm WAV files.** After each received ESP message, a new
   `pc_app/recordings/*.wav` appears. Open it — it should contain the received
   audio.

---

## Protocol summary

One UDP port for all packets. Fixed **32-byte header**, network byte order,
packed explicitly (never a raw C struct on the wire):

```
magic[4]     = "PTT1"
type         uint8    (1=CLAIM 2=BUSY 3=AUDIO 4=END)
flags        uint8
header_len   uint16   (= 32)
mesh_id      uint32
sender_id    uint32
session_id   uint32
sequence     uint32
timestamp_ms uint32   (monotonic local, no clock sync required)
payload_len  uint16
reserved     uint16   (= 0)
```

Audio payload = one **164-byte packet-independent IMA ADPCM frame**:
`int16 predictor · uint8 step_index · uint8 reserved · 160 bytes (320 nibbles)`.
Each frame carries the predictor/step-index it started from, so a single lost
packet never corrupts later frames.

**Half-duplex floor control:** on PTT a node picks a random 32-bit `session_id`,
enters *Claiming*, sends `CLAIM` ×3 at ~30 ms, waits ~100 ms, then streams
`AUDIO` (sequence from 0, acting as a heartbeat). Simultaneous claims are
resolved deterministically by the lowest `(session_id, sender_id)` tuple; the
loser becomes a receiver. If no packet from the active remote session arrives
for 750 ms the floor is released. On release a node sends `END` ×3; on receiving
`END` the receiver drains its jitter buffer, finalises the WAV, and returns to
*Idle*.

**Jitter buffer:** four frames (80 ms). Prebuffer 4 frames before playout,
accept a small reorder window, ignore duplicates/late frames, and conceal a
missing frame by replaying the previous one attenuated (or silence).

---

## Troubleshooting

**No audio from the ESP speaker (no boot tone)**
- The firmware logs `Playing startup tone ...` then `Startup tone done` over
  serial. **If you see those logs but hear nothing, the fault is in hardware,
  not firmware** — check the items below.
- **`SD` must be tied to `3V3`.** Floating/low `SD` = amp in shutdown (this is
  the most common cause of "no LED, no sound"). See the wiring note above.
- Check `VIN`→`5V`, `GND`→`GND`, and a **common ground** between the XIAO, amp,
  and mic. If the amp module has a power LED and it's dark, power isn't reaching
  it.
- I²S pins not swapped: `BCLK`→GPIO6 (D4), `LRC`→GPIO7 (D5), `DIN`→GPIO5 (D3).
- Speaker must be across the two amp output terminals — **not** to ground.

**Wrong I²S pins / distorted or no output**
- Double-check BCLK/LRC/DIN are not swapped. `LRC` (word-select) on GPIO7 and
  `BCLK` on GPIO6 are easy to transpose.

**Microphone: only noise, or too quiet / clipping**
- The firmware logs a once-per-second `MIC diag:` line over serial. Watch it
  while quiet, then while speaking:
  - `raw[... mean=M span=S]` — `mean` should sit near mid-scale (~2048 with 12 dB
    attenuation) and be **stable when quiet**. `span`/`ac_peak` should be small
    when quiet and **clearly rise when you speak** — that means the mic is
    delivering real audio and you may just need more `MIC_GAIN`.
  - If `ac_peak` is large and jumpy even in silence, you're seeing ADC/noise
    floor or a mis-biased module (see below).
  - If `mean` is pinned near 0 or 4095, `OUT` is out of range — check biasing
    and that `VCC` is on `3V3` (not `5V`).
  - A `MIC diag: no ADC results ...` warning means the ADC isn't returning the
    expected channel — a wiring/channel problem.
- **Signal too weak:** raise `MIC_GAIN` in `config.h` (try 12, 24, 48) until
  speech `ac_peak` reaches a few hundred to ~1000. If more gain only makes the
  hiss louder, the useful signal is buried in the ADC noise floor.
- **Wrong module type:** a bare electret-preamp module (MAX4466 / MAX9814) puts
  real biased audio on `OUT` and works here. A "sound sensor" board (KY-038 and
  clones) outputs a threshold/envelope on `OUT`, not audio — sampled as audio it
  sounds like noise. Confirm your module is an audio preamp, not a sound sensor.
- Lower `MIC_GAIN` if speech clips (`ac_peak` railing at 2048+).

**No audio between PC and ESP (packets not arriving)**
- **Windows Firewall:** the first run of `app.py` may prompt — allow Python on
  private networks. Otherwise add an inbound UDP rule for the port (default
  `45678`). Quick test:
  `New-NetFirewallRule -DisplayName "Intercom UDP" -Direction Inbound -Protocol UDP -LocalPort 45678 -Action Allow`
- Verify each side's `PEER_IP` points at the other device's current IP.
- Both devices must be on the **same subnet**; some routers block client-to-
  client traffic ("AP isolation") — disable it.

**ESP won't connect to Wi-Fi**
- The **ESP32-C3 is 2.4 GHz only.** A 5 GHz SSID will never connect. Use the
  2.4 GHz band (or split SSIDs). Check `WIFI_SSID`/`WIFI_PASSWORD` in
  `config.h`; the serial monitor logs connect/reconnect attempts.

**PC app: "No audio" / sounddevice errors**
- Install PortAudio (Linux) and confirm a working default input/output device.
- Close other apps holding the microphone exclusively.
```
