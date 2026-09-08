> Current protocol/state semantics: [p3 contract](protocol-p3.md). This supersedes
> older delivery-sequence and p1/p2 interoperability notes below.

# Windows companion design

This replaces the experimental Python/Tkinter companion. The firmware and
wire protocol remain unchanged; the desktop app is a native .NET 10 Windows
Forms application using NAudio for Windows capture and playout.

## User-facing behaviour

At launch the app immediately starts LAN discovery and shows a clear network
state. Audio starts independently after the selected Windows input and output
devices are opened. A temporary audio-device failure must never block
discovery, device configuration, or the UI.

The main window has four deliberately separate areas:

1. **Connection/status** shows the companion alias, mesh ID, selected input
   and output devices, and one of `Idle`, `Claiming`, `Talking`, `Receiving`,
   or `Audio unavailable`.
2. **PTT** provides hold-to-broadcast, hold-to-reply, and hold-to-selected
   controls. Space is broadcast PTT. A press begins a claim; release sends
   `END`. A received floor press retains no more than 500 ms, exactly as a
   hardware device does.
3. **Active devices** is a live list of the last ten seconds of HELLOs. Each
   row uses its alias, ID and IP address; there is no stored IP list.
4. **Selected device configuration** reads and writes aliases, volume, ring
   brightness, swapped buttons and orientation through the existing addressed
   CONFIG packets.

The companion keeps a stable random node ID and a user-editable local alias in
`%AppData%\\WiFi-Intercom\\settings.json`. Received recordings are written to
`%AppData%\\WiFi-Intercom\\recordings`, rather than beside the executable.

## Concurrency and audio contract

The UI thread owns controls only. Network receive, discovery and floor control
run on background tasks. They publish immutable view updates to the UI thread
through `BeginInvoke`/a periodic UI timer; no background operation synchronously
waits on the UI.

Received AUDIO is decoded once into 320 signed-16-bit samples and placed in a
four-frame sequence jitter buffer. A dedicated 20 ms playback pump consumes
that buffer and writes PCM to NAudio's `BufferedWaveProvider`. The Windows
audio callback never parses packets, holds a session lock, or touches UI state.
Missing frames use one attenuated replay then silence. The pump is allowed a
small host buffer (roughly 120 ms) to tolerate normal Windows scheduler jitter.

Capture is also frame based: NAudio input is accumulated into exact 320-sample
frames, encoded as independent IMA ADPCM payloads, and handed to the session
sender. It never runs protocol or network work on NAudio's capture callback.

## Session and network contract

* `HELLO` is the only multicast packet, every three seconds on
  `239.255.42.99:45678` with TTL 1.
* Every peer endpoint learned before PTT is snapshotted at press time. The
  same snapshot receives broadcast CLAIM, END and AUDIO for that session.
  Directed control and AUDIO go only to the selected endpoint, with p3
  ACCEPT/BUSY reserving that endpoint. Directed sessions never claim the broadcast floor.
* CLAIM is sent three times, followed by the 100 ms pre-audio delay. Lowest
  `(session_id, sender_id)` wins simultaneous claims. A valid remote session
  expires after 750 ms without AUDIO/HEARTBEAT.
* The fixed 32-byte big-endian header and 164-byte packet-independent IMA
  ADPCM payload remain byte-for-byte compatible with firmware.
* The network receive loop has no audio playback side effects beyond adding
  decoded PCM to the jitter buffer. This is the boundary that the Python app
  failed to maintain consistently.

## Delivery sequence

1. Create a small WinForms solution with protocol/codec tests and a shell that
   can discover peers and read/write configuration.
2. Add the session state machine and receive-to-WAV pipeline; verify the WAV
   against the known-good `device-capture.wav`.
3. Add the NAudio playout pump and test a long device broadcast.
4. Add capture/PTT, peer snapshot delivery and the occupied-floor 500 ms
   behaviour; test device-to-PC, PC-to-device, broadcast, direct and reply.
5. Remove the Python companion and its packaging artefacts, update the README,
   build the Windows executable, then commit the migration to the existing PR.
