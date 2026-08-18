# VibeRide

Ride a Wahoo KICKR through generated 3D terrain.

![Descending Col de Verdon past a sauropod](docs/screenshot.png)

Your trainer drives it. Power and cadence come in over Bluetooth, a physics model
turns them into speed, and the road gradient goes back out as FTMS simulation
parameters — so the flywheel genuinely gets harder on the climbs.

Every world is generated from a seed: terrain, a 25 km course with real designed
gradients, the road, and the scenery scattered across it. **Regenerate** gives you
a completely different ride in about six seconds, and worlds you like can be
saved by name.

## Download

Grab the latest build from the
[Releases page](https://github.com/thepaulm/viberide/releases) — macOS universal
(Apple Silicon + Intel), no Unity required.

Unzip, then once:

```bash
bash setup.sh
```

Then open `VibeRide.app`. It starts the Python trainer bridge itself and shuts it
down when you quit.

Setup restores the executable bit (a zip built on Windows cannot carry it),
clears the Gatekeeper quarantine flag, re-signs the app so macOS can attach a
Bluetooth permission, and builds the Python environment the bridge needs. Full
instructions are in `START_HERE.md` inside the zip.

No trainer? Launch it anyway and hold **W** to pedal, **Shift** to surge.

## How it fits together

```
   KICKR  --BLE/FTMS-->  bridge (Python)  --WebSocket-->  VibeRide (Unity 6)
          <---grade-----                  <--terrain grade--
```

Bluetooth lives in Python rather than in Unity, which is what lets the same app
run unchanged on Windows and macOS.

## More

**[DETAILS.md](DETAILS.md)** covers the architecture, the course generator and the
constraints that keep it rideable, scenery placement, the in-app menu, building
and packaging for macOS, and a list of hard-won gotchas.

- [`packaging/RELEASING.md`](packaging/RELEASING.md) — cutting a release
- [`unity/Assets/Models/CREDITS.md`](unity/Assets/Models/CREDITS.md) — CC0 model sources
