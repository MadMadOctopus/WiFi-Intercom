# Wi-Fi Intercom user manual

## Before you start

You need one or more powered Wi-Fi Intercom devices, a Windows PC, and a
2.4 GHz Wi-Fi network that all nodes can reach. The system discovers members
of its network group automatically; there is no server and no list of device
IP addresses to enter.

Treat the network as trusted. Intercom traffic and remote configuration are
not authenticated or encrypted. Do not use a public or untrusted Wi-Fi
network.

## First-time setup

1. Connect the device to the PC by USB. It appears as a USB serial/JTAG COM
   port (for example, `COM6` or `COM9`).
2. Start the companion app:

   ```powershell
   dotnet run --project .\companion\IntercomCompanion\IntercomCompanion.csproj
   ```

3. In the **USB port** row, choose the device COM port. Select **Refresh
   ports** first if it is not listed.
4. Select **Read USB config**. This confirms the connected device and fills
   its SSID if it was already configured. The password is never read back.
5. Enter the Wi-Fi SSID and password, then select **Apply Wi-Fi over USB**.
   The device saves the settings, restarts, and joins the Wi-Fi network.
6. After a few seconds the device appears in **Active devices**. Devices
   normally discover each other within one discovery interval; a device that
   has not been heard from for ten seconds disappears from the list.

Use the same Wi-Fi network and network group for every device and companion.
The default group is already correct for normal use; changing `mesh_id` is an
advanced integration setting.

## Talking from a device

| Control | Action | Ring feedback |
| --- | --- | --- |
| Broadcast button | Hold to talk to every active device and companion | Green talking |
| Reply button | Hold to talk to the most recent sender | Blue talking |
| Mute slider on | Do not play received audio | No received sound |

Both buttons are push-to-talk: only audio captured while held is sent. Release
the button to finish the message. If someone else is already talking, keep the
button held briefly. The device retains up to half a second of speech while it
waits for the floor. If the other transmission does not end, the device shows
two short red pulses and does not interrupt it.

The default hardware mapping is D10 for broadcast and D9 for reply. If the
physical buttons were soldered in reverse, set **Buttons swapped** in the
selected-device configuration and apply it.

## Using the companion app

### Audio devices and identity

At the top of the window, set a descriptive **Companion alias** and choose
the Windows **Recording** and **Playback** devices. Select **Apply audio
devices** after making a change. Do this while no local PTT button is held.

The app receives incoming messages as soon as it opens. Its status line shows
receive/packet statistics while a message is playing.

### Sending messages

| App control | Action |
| --- | --- |
| **Hold to broadcast** | Talk to all active peers. `Space` is also a broadcast push-to-talk shortcut. |
| **Hold to reply** | Talk to the most recent sender heard by the companion. |
| **Hold to selected device** | Send a directed message to the selected row in Active devices. |

Select a device row before using targeted PTT. A directed message plays only
on the selected destination. Broadcast calls go to every active device and
companion in the group.

### Device list and configuration

The **Active devices** table shows the alias, device ID, address, firmware /
protocol, and how recently the device was heard. Firmware/protocol text such
as `0.7.8 / p1 / OTA` means the device supports the current protocol and OTA.

To change a device:

1. Select it in **Active devices**.
2. Select **Get configuration** to load its saved settings into the editor.
3. Edit the local form: alias, speaker volume, ring brightness, button
   mapping, or ring orientation.
4. Select **Apply configuration** to send the edit.

The editor is deliberately independent of the refreshing device list: changes
you have typed are not automatically replaced. Settings are neither requested
nor written until you press one of those two buttons.

`Ring orientation` is `0` for the normal logical centre or `180` to rotate it
half a ring. `Ring brightness` ranges from 0 to 255. Keep brightness modest on
the current prototype because the LEDs and amplifier share a supply rail.

## Firmware updates over Wi-Fi

The device needs an OTA-capable firmware already installed by USB before it
can be updated wirelessly. In the device list, an OTA-capable current device
shows `p1 / OTA`.

1. Build a signed `.ota.json` package; maintainers can use
   [`tools/ota/Create-OtaPackage.ps1`](../tools/ota/Create-OtaPackage.ps1).
2. In the companion, select **Choose signed OTA package** and select the
   `.ota.json` manifest, not the `.bin` directly.
3. Read the version and image-size verification message shown beside the
   package field.
4. Select one active compatible device and choose **Update selected device**,
   or use **Update all active devices**. The all-devices action is sequential.
5. Confirm the warning. Keep the companion open and allow the Windows Firewall
   prompt for the temporary local HTTP server on a private network.
6. The app reports acceptance, download/write progress and reboot. The device
   uses an amber progress animation. The operation is complete only when the
   device reappears in the list with the offered version.

If an update fails, the companion displays a modal error and the device
returns to its current firmware. It does not overwrite the running slot;
failed or unhealthy candidate firmware is rolled back automatically.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Device does not appear | Confirm USB-provisioned Wi-Fi credentials, 2.4 GHz reachability, and that the PC and device use the same trusted LAN/group. Wait ten seconds, then restart the companion. |
| One device is missing but devices can talk | Restart the companion to renew discovery; some access points suppress multicast, so discovery also uses fallback probes. Ensure Windows Firewall permits the companion on a private network. |
| Cannot hear received audio | Turn the device mute slider off. In the app, select the intended playback device and choose **Apply audio devices**. |
| A physical button does the wrong thing | Select the device, get its configuration, set **Buttons swapped**, apply it, and wait for the device reboot. |
| Ring centre is physically wrong | Set **Ring orientation** to `180`, apply, and wait for the device reboot. |
| OTA package is rejected | Choose the `.ota.json` manifest and its matching image, use a newer offered version, and make sure it is signed by the key trusted by both the companion and device. |
| OTA stops or times out | Keep the companion open, check the same LAN and Windows Firewall prompt, then retry once the device has returned to the active-device list. |

For builders, pin wiring, USB JSON commands, build/flash instructions and
transport details are in the repository [README](../README.md).
