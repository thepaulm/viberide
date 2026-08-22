# VibeRide - macOS

Full documentation, screenshots and source: https://github.com/thepaulm/viberide


## Install

Double-click **Install VibeRide**.

It replaces any previous copy in /Applications, clears the Gatekeeper quarantine
flag, re-signs the app so macOS can attach a Bluetooth permission to it, and
opens it. Run it again any time to upgrade - there is nothing to delete first.

If macOS refuses to open it because it came from the internet, right-click it and
choose **Open**, then confirm. That prompt appears once. The equivalent from a
terminal, if you prefer:

```bash
bash "Install VibeRide.command"
```

**The first launch takes about a minute.** The app builds the Python environment
the trainer bridge needs, into `~/Library/Application Support/VibeRide`, and shows
the progress in its status panel. It needs Python 3.9+ - `brew install python3` if
you don't have it. After that it starts in a couple of seconds.

The environment lives outside the app bundle on purpose. macOS ties the Bluetooth
permission to the app's signature, and writing anything inside the bundle after it
is signed breaks that - which would make the trainer permission quietly stop
sticking.

## How it fits together

```
   KICKR  --BLE-->  bridge (Python)  --ws://127.0.0.1:8765-->  VibeRide.app
          <-grade--                  <-----terrain grade------
```

The app starts the bridge as a child process and stops it on quit. If the app
crashes or is force-quit, the bridge notices its parent is gone and exits by
itself within a couple of seconds - it will not be left holding the trainer.

If you'd rather run the bridge yourself (handy for watching its log), start it
first and the app will use the one already running instead of starting another:

```bash
cd bridge && ./run_bridge.sh
```

## Before riding

Wake the trainer by spinning the cranks, and make sure **nothing else holds it** -
Zwift, the Wahoo phone app, or a head unit will silently take the single BLE
connection a trainer allows.

macOS asks for **Bluetooth permission** on first launch. Allow it, or the trainer
is never found. The grant belongs to whichever app is *responsible* for the
process, so a permission already given to Terminal does not cover the app
launching the bridge itself - they are tracked separately. Check under
System Settings > Privacy & Security > Bluetooth that "VibeRide" is listed and on.

The bridge keeps looking for the trainer while it runs, so you can launch the app
first and wake the trainer afterwards.

## Without a trainer

Launch the app and hold **W** to pedal, **Shift** to surge. Same physics as a
real ride, so the terrain feels the same.

## The status panel

Bottom-left, two lines with a coloured dot each:

```
BRIDGE   * connected
TRAINER  * not found
         No device matching 'KICKR'. Saw 3: AirPods, Samsung TV, ...
```

The detail line is the useful part when something is wrong:

| Detail says | Means |
| --- | --- |
| `No BLE devices visible at all` | Bluetooth permission, not a trainer problem |
| `No device matching 'KICKR'. Saw N: ...` | scanning works; trainer asleep or held by another app |
| `grade control available` | connected, and resistance will follow the terrain |
| `no simulation-parameter support` | connected, but erg only - no grade control |

## Troubleshooting

**App bounces in the Dock and quits** - the bundle was never installed by the
installer, so its permissions are wrong; the launcher
needs its executable bit and the quarantine flag cleared.

**Bridge won't start** - run it by hand to see why: `cd bridge && ./run_bridge.sh`.
Usually no Python 3.9+ on PATH. The status panel names the reason. To force the
environment to be rebuilt from scratch, delete
`~/Library/Application Support/VibeRide` and open the app again.

**Trainer never found** - macOS identifies BLE devices by CoreBluetooth UUID
rather than MAC address, so match by name (the default, `KICKR`); any address
noted on Windows will be a different string here. To make macOS re-ask for
Bluetooth permission:

```bash
tccutil reset Bluetooth com.viberide.app
```
