"""Local WebSocket bridge: trainer <-> 3D world.

Outbound (bridge -> Unity), ~30 Hz, one JSON object per message:

    {"type":"telemetry","t":12.34,"power_w":248,"cadence_rpm":91.0,
     "speed_mps":8.94,"speed_kph":32.2,"distance_m":1234.5,
     "elevation_gain_m":45.2,"grade":0.052,"heart_rate_bpm":148,
     "trainer_power_kph":31.8,"connected":true}

Inbound (Unity -> bridge):

    {"type":"grade","grade":0.052}          -- terrain slope under the bike
    {"type":"erg","watts":220}              -- switch to fixed-wattage mode
    {"type":"sim"}                          -- back to grade-following mode
    {"type":"rider","rider_kg":78,"cda":0.30}

Run with --demo to produce plausible telemetry with no trainer present, so the
Unity side can be built and tested without hardware.
"""

from __future__ import annotations

import argparse
import asyncio
import contextlib
import json
import logging
import math
import time

from websockets.asyncio.server import serve

from . import ftms, physics, procwatch
from .trainer import Trainer, TrainerNotFound

log = logging.getLogger(__name__)

PHYSICS_HZ = 60.0
BROADCAST_HZ = 30.0


class Bridge:
    def __init__(self, rider: physics.Rider, demo: bool = False):
        self.model = physics.RideModel(rider)
        self.demo = demo
        self.trainer: Trainer | None = None
        self.clients: set = set()

        self.power_w = 0.0
        self.cadence_rpm = 0.0
        self.heart_rate: int | None = None
        self.trainer_speed_kph: float | None = None
        self.grade = 0.0
        self.mode = "sim"
        self.started = time.monotonic()
        self._last_packet_at = 0.0
        self.stop = asyncio.Event()
        # Short machine-readable state, plus a human-readable reason. The reason
        # is the whole point: "not found" alone is useless, "not found, saw
        # nothing at all" vs "not found, saw 6 other devices" point at completely
        # different problems (no Bluetooth permission vs trainer asleep).
        self.trainer_status = "starting"
        self.trainer_detail = ""
        self.scan_seen: list[str] = []

    # --- telemetry in -------------------------------------------------------

    def _on_bike_data(self, data: ftms.BikeData):
        if data.power_w is not None:
            self.power_w = float(data.power_w)
        if data.cadence_rpm is not None:
            self.cadence_rpm = data.cadence_rpm
        if data.heart_rate_bpm is not None:
            self.heart_rate = data.heart_rate_bpm
        # The trainer's own speed figure assumes a fixed virtual wheel and
        # ignores our terrain, so it is reported for reference only -- our
        # physics model is what actually moves the bike.
        if data.speed_kph is not None:
            self.trainer_speed_kph = data.speed_kph
        self._last_packet_at = time.monotonic()

    def _demo_power(self, t: float) -> float:
        """A plausible rider: steady tempo with breathing surges, plus a shove
        of extra effort when the road tilts up."""
        base = 205.0 + 35.0 * math.sin(t / 11.0) + 12.0 * math.sin(t / 2.7)
        return max(0.0, base + 900.0 * max(0.0, self.grade))

    # --- loops --------------------------------------------------------------

    async def physics_loop(self):
        dt = 1.0 / PHYSICS_HZ
        last = time.monotonic()
        while True:
            await asyncio.sleep(dt)
            now = time.monotonic()
            step_dt = min(now - last, 0.25)  # clamp after a stall
            last = now

            if self.demo:
                t = now - self.started
                self.power_w = self._demo_power(t)
                self.cadence_rpm = 88.0 + 6.0 * math.sin(t / 3.1)
            elif now - self._last_packet_at > 3.0:
                # Trainer went quiet -- coast rather than freewheeling forever
                # on a stale wattage figure.
                self.power_w = 0.0
                self.cadence_rpm = 0.0

            self.model.step(self.power_w, self.grade, step_dt)

    async def grade_loop(self):
        """Push grade to the trainer. Separate from the physics loop because
        the BLE write is rate limited and must not stall the integrator."""
        while True:
            await asyncio.sleep(1.0 / 10.0)
            if self.trainer and self.mode == "sim":
                try:
                    await self.trainer.set_grade(self.grade)
                except Exception as exc:  # noqa: BLE001
                    log.warning("Grade write failed: %s", exc)

    def snapshot(self) -> dict:
        s = self.model.state
        return {
            "type": "telemetry",
            "t": round(time.monotonic() - self.started, 3),
            "power_w": round(self.power_w, 1),
            "cadence_rpm": round(self.cadence_rpm, 1),
            "speed_mps": round(s.speed_mps, 4),
            "speed_kph": round(s.speed_kph, 2),
            "distance_m": round(s.distance_m, 2),
            "elevation_gain_m": round(s.elevation_gain_m, 2),
            "grade": round(self.grade, 5),
            "heart_rate_bpm": self.heart_rate,
            "trainer_speed_kph": self.trainer_speed_kph,
            "mode": self.mode,
            "connected": bool(self.trainer) or self.demo,
            "demo": self.demo,
            "trainer_status": self.trainer_status,
            "trainer_detail": self.trainer_detail,
            "scan_seen": ", ".join(self.scan_seen[:6]),
            "scan_count": len(self.scan_seen),
        }

    async def broadcast_loop(self):
        while True:
            await asyncio.sleep(1.0 / BROADCAST_HZ)
            if not self.clients:
                continue
            payload = json.dumps(self.snapshot())
            dead = []
            for ws in self.clients:
                try:
                    await ws.send(payload)
                except Exception:  # noqa: BLE001
                    dead.append(ws)
            for ws in dead:
                self.clients.discard(ws)

    # --- client handling ----------------------------------------------------

    async def handle_client(self, ws):
        self.clients.add(ws)
        log.info("Client connected (%d total)", len(self.clients))
        try:
            async for raw in ws:
                try:
                    msg = json.loads(raw)
                except json.JSONDecodeError:
                    log.warning("Ignoring non-JSON message: %r", raw[:120])
                    continue
                await self._on_message(msg)
        except Exception as exc:  # noqa: BLE001
            log.debug("Client loop ended: %s", exc)
        finally:
            self.clients.discard(ws)
            log.info("Client disconnected (%d left)", len(self.clients))

    async def _on_message(self, msg: dict):
        kind = msg.get("type")
        if kind == "grade":
            try:
                grade = float(msg.get("grade", 0.0))
            except (TypeError, ValueError):
                return
            if math.isfinite(grade):
                self.grade = max(-0.40, min(0.40, grade))
        elif kind == "erg":
            watts = int(msg.get("watts", 200))
            self.mode = "erg"
            if self.trainer:
                with contextlib.suppress(Exception):
                    await self.trainer.set_target_power(watts)
        elif kind == "sim":
            self.mode = "sim"
            if self.trainer:
                with contextlib.suppress(Exception):
                    await self.trainer.set_grade(self.grade, force=True)
        elif kind == "rider":
            rider = self.model.rider
            for key in ("rider_kg", "bike_kg", "cda", "crr"):
                if key in msg:
                    setattr(rider, key, float(msg[key]))
            log.info("Rider updated: %.1f kg total, CdA %.3f", rider.total_mass, rider.cda)
        elif kind == "reset":
            self.model.state = physics.RideState()
        elif kind == "shutdown":
            # The normal quit path when the app launched us. Getting this lets us
            # hand the trainer back to a neutral state instead of being killed
            # mid-ride with the resistance still set to whatever hill you were on.
            log.info("Shutdown requested by client.")
            self.stop.set()


async def trainer_supervisor(bridge: Bridge, match: str, retry_seconds: float = 8.0):
    """Keep a trainer connection up, retrying for as long as we're running.

    Exiting when the trainer isn't found would force you to wake the KICKR before
    launching the app, and would kill the whole bridge if the trainer went to
    sleep mid-session. Instead we serve telemetry regardless and report status,
    so the app can say something useful while the trainer is still waking up.
    """
    while not bridge.stop.is_set():
        trainer = Trainer(match=match, crr=bridge.model.rider.crr)
        trainer.on_data = bridge._on_bike_data
        try:
            bridge.trainer_status = "searching"
            bridge.trainer_detail = f"scanning for a device matching {match!r}"
            await trainer.connect()
            bridge.trainer = trainer
            bridge.trainer_status = "connected"
            feats = trainer.features
            bridge.trainer_detail = (
                "grade control available" if feats and feats.supports_sim_params
                else "connected, but no simulation-parameter support (erg only)"
            )
            log.info("Trainer connected.")

            # Hold here until the link drops or we're asked to stop.
            while not bridge.stop.is_set():
                await asyncio.sleep(1.0)
                client = trainer.client
                if client is None or not client.is_connected:
                    log.warning("Trainer link dropped; will retry.")
                    break
        except TrainerNotFound as exc:
            bridge.trainer_status = "not found"
            bridge.trainer_detail = str(exc)
            bridge.scan_seen = list(trainer.last_seen)
            log.warning("%s", exc)
        except Exception as exc:  # noqa: BLE001
            bridge.trainer_status = "error"
            bridge.trainer_detail = f"{type(exc).__name__}: {exc}"
            log.warning("Trainer connection failed: %s", exc)
        finally:
            bridge.trainer = None
            with contextlib.suppress(Exception):
                await trainer.disconnect()

        if bridge.stop.is_set():
            return
        # Keep the reason from the failed attempt visible while we wait -- the
        # status is far more useful than a bare "retrying".
        bridge.trainer_status = "retrying"
        try:
            await asyncio.wait_for(bridge.stop.wait(), timeout=retry_seconds)
            return  # stop was set while waiting
        except asyncio.TimeoutError:
            pass


def install_signal_handlers(stop: asyncio.Event):
    """Turn SIGINT/SIGTERM into a graceful stop where the platform allows it.

    Windows has no real SIGTERM: Process.Kill() there is TerminateProcess, which
    cannot be caught. That is exactly why the parent-PID watchdog exists -- it
    notices the app is gone and shuts us down cleanly before anyone resorts to
    killing us.
    """
    import signal

    loop = asyncio.get_running_loop()
    for name in ("SIGINT", "SIGTERM"):
        sig = getattr(signal, name, None)
        if sig is None:
            continue
        try:
            loop.add_signal_handler(sig, stop.set)
        except (NotImplementedError, RuntimeError):
            pass  # not supported on this platform


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--match", default="KICKR", help="trainer name/address substring")
    parser.add_argument("--demo", action="store_true", help="fake a rider, no hardware")
    parser.add_argument("--rider-kg", type=float, default=75.0)
    parser.add_argument("--bike-kg", type=float, default=8.5)
    parser.add_argument("--cda", type=float, default=0.32)
    parser.add_argument(
        "--parent-pid",
        type=int,
        default=0,
        help="exit when this process exits; set by the app when it launches us",
    )
    parser.add_argument(
        "--watch-stdin",
        action="store_true",
        help="shut down on a 'shutdown' line or EOF on stdin (used by the app)",
    )
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.INFO, format="%(asctime)s %(levelname)-7s %(name)s: %(message)s"
    )

    rider = physics.Rider(rider_kg=args.rider_kg, bike_kg=args.bike_kg, cda=args.cda)
    bridge = Bridge(rider, demo=args.demo)
    install_signal_handlers(bridge.stop)

    tasks = [
        asyncio.create_task(bridge.physics_loop()),
        asyncio.create_task(bridge.broadcast_loop()),
    ]

    if args.demo:
        log.info("DEMO MODE -- synthetic rider, no trainer connection.")
        bridge.trainer_status = "demo"
    else:
        tasks.append(asyncio.create_task(trainer_supervisor(bridge, args.match)))
        tasks.append(asyncio.create_task(bridge.grade_loop()))

    if args.parent_pid:
        tasks.append(asyncio.create_task(procwatch.watch_parent(args.parent_pid, bridge.stop)))
    if args.watch_stdin:
        tasks.append(asyncio.create_task(procwatch.watch_stdin(bridge.stop)))

    try:
        async with serve(bridge.handle_client, args.host, args.port):
            log.info("Bridge listening on ws://%s:%d", args.host, args.port)
            await bridge.stop.wait()
            log.info("Shutting down.")
    except OSError as exc:
        # Almost always "address already in use" -- another bridge is running.
        log.error("Could not listen on %s:%d: %s", args.host, args.port, exc)
        log.error("Another bridge is probably already running on that port.")
        return 2
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        if bridge.trainer:
            await bridge.trainer.disconnect()
    return 0


if __name__ == "__main__":
    import sys

    code = 0
    with contextlib.suppress(KeyboardInterrupt):
        code = asyncio.run(main()) or 0
    sys.exit(code)
