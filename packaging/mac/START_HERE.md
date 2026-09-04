# VibeRide - macOS

Full documentation, screenshots and source: https://github.com/thepaulm/viberide


## Install

Double-click **Install VibeRide**.

It replaces any previous copy in /Applications, clears the Gatekeeper quarantine
flag, re-signs the app so macOS can attach a Bluetooth permission to it, and
opens it. Run it again any time to upgrade - there is nothing to delete first.

### macOS will block it the first time

You will get *"Apple could not verify Install VibeRide is free of malware"*. That
is Gatekeeper, and every app not signed with a paid Apple Developer ID gets it.
Nothing is wrong with the download.

To allow it:

1. Open **System Settings > Privacy & Security**
2. Scroll down to the message naming **Install VibeRide**
3. Click **Open Anyway**

Once only, and only for the installer - it clears the flag on the app it installs,
so VibeRide itself opens normally afterwards.

Do not bother hunting for right-click > Open. That bypass was removed in macOS 15,
which is why an earlier release shipped a `.command` file that could not be
approved from Finder at all.

The app itself is inside the installer, at `Contents/Resources` - right-click the
installer and choose **Show Package Contents** if you ever want it directly. It
lives there rather than beside the installer because macOS runs a quarantined app
from a randomised temporary copy without its neighbouring files, so an installer
that looks beside itself finds nothing.

### Or skip Gatekeeper entirely

Files downloaded with `curl` are not quarantined, so nothing gets blocked. From
Terminal:

```bash
cd ~/Downloads
curl -fL -O https://github.com/thepaulm/viberide/releases/latest/download/VibeRide-mac-universal.zip
unzip -o VibeRide-mac-universal.zip -d VibeRide
bash VibeRide/install.sh
```

(the exact filename carries the version - copy it from the Releases page)

`bash install.sh` also works on an already-unzipped copy.

**The first launch takes about a minute.** The app builds the Python environment
the trainer bridge needs, into `~/Library/Application Support/VibeRide/bridge`, and shows
the progress in its status panel. After that it starts in a couple of seconds.

It uses a Python you already have. Most Macs have one, and the search runs each
candidate rather than trusting the filename - your own `python3`, then Homebrew,
MacPorts, pyenv and python.org installs, then versioned names like `python3.12`,
and Apple's `/usr/bin/python3` last, only when the command line tools are actually
behind it. Anything older than 3.9 is skipped rather than accepted. Only if none
of that turns up a usable interpreter does it ask you to install one, and it says
so in the status panel.

The environment lives outside the app bundle on purpose. macOS ties the Bluetooth
permission to the app's signature, and writing anything inside the bundle after it
is signed breaks that - which would make the trainer permission quietly stop
sticking.

## Your saved courses survive upgrades

Nothing you have saved lives inside the app, so replacing it cannot lose anything.
The installer removes only `/Applications/VibeRide.app`.

```
~/Library/Application Support/VibeRide/
    VibeRide/courses.json     your saved courses, and the unit setting
    bridge/                   the Python environment, rebuilt if deleted
```

Reinstall as often as you like. The only way to lose courses is to delete that
`VibeRide/courses.json` yourself - so when clearing out the Python environment,
name the `bridge` folder rather than the parent.

## How it fits together

```
   KICKR  --BLE-->  bridge (Python)  --ws://127.0.0.1:47812-->  VibeRide.app
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

**Bridge won't start** - usually no Python 3.9+ on PATH; the status panel names
the reason. To force the environment to be rebuilt from scratch, delete just the
`bridge` folder and open the app again:

```bash
rm -rf ~/Library/Application\ Support/VibeRide/bridge
```

Delete that folder, **not** its parent - your saved courses are the other thing
in there.

**Trainer never found** - macOS identifies BLE devices by CoreBluetooth UUID
rather than MAC address, so match by name (the default, `KICKR`); any address
noted on Windows will be a different string here. To make macOS re-ask for
Bluetooth permission:

```bash
tccutil reset Bluetooth com.viberide.app
```
