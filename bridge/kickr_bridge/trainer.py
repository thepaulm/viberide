"""BLE connection management for an FTMS trainer.

Owns the one BLE link the trainer will grant, turns Indoor Bike Data
notifications into callbacks, and serialises control point writes (which are
request/response and must not be issued concurrently).
"""

from __future__ import annotations

import asyncio
import logging
from typing import Callable

from bleak import BleakClient, BleakScanner

from . import ftms

log = logging.getLogger(__name__)

# The trainer reverts to its default state if left alone, and hammering the
# control point at frame rate just fills the BLE queue. A few updates a second
# is plenty -- resistance changes are not perceptible faster than this.
GRADE_MIN_INTERVAL = 0.25  # seconds
GRADE_MIN_DELTA = 0.001  # 0.1% grade
CONTROL_TIMEOUT = 5.0


class TrainerNotFound(RuntimeError):
    pass


class Trainer:
    def __init__(self, match: str = "KICKR", crr: float = 0.004, cw: float = 0.51):
        self.match = match.lower()
        self.crr = crr
        self.cw = cw
        self.client: BleakClient | None = None
        self.features: ftms.MachineFeatures | None = None
        self.has_control = False
        self.on_data: Callable[[ftms.BikeData], None] | None = None
        self.last_seen: list[str] = []

        self._responses: asyncio.Queue[bytes] = asyncio.Queue()
        self._control_lock = asyncio.Lock()
        self._last_grade: float | None = None
        self._last_grade_at = 0.0

    # --- discovery & connection --------------------------------------------

    async def find(self, timeout: float = 15.0):
        log.info("Scanning for a trainer matching %r ...", self.match)
        found: dict = {}

        def on_detect(device, adv):
            found[device.address] = (device, adv)

        scanner = BleakScanner(detection_callback=on_detect)
        await scanner.start()
        try:
            # Poll early so we can stop as soon as the trainer shows up rather
            # than always burning the full timeout.
            for _ in range(int(timeout * 4)):
                await asyncio.sleep(0.25)
                hit = self._pick(found)
                if hit:
                    return hit
        finally:
            await scanner.stop()

        hit = self._pick(found)
        if not hit:
            self.last_seen = [
                (adv.local_name or dev.name or dev.address) for dev, adv in found.values()
            ]
            names = ", ".join(self.last_seen)
            if not self.last_seen:
                # Seeing literally nothing usually means the scan is not permitted
                # rather than that the room is empty -- on macOS a missing
                # Bluetooth grant produces an empty scan rather than an error.
                raise TrainerNotFound(
                    "No BLE devices visible at all. Either Bluetooth is off, or "
                    "this process has no Bluetooth permission. On macOS, grant it "
                    "under System Settings > Privacy & Security > Bluetooth for "
                    "whichever app is running the bridge."
                )
            raise TrainerNotFound(
                f"No device matching {self.match!r}. Saw {len(self.last_seen)}: {names}. "
                "Wake the trainer by spinning the cranks, and make sure no other "
                "app (Zwift, the Wahoo app, a head unit) is holding the connection."
            )
        return hit

    def _pick(self, found: dict):
        for device, adv in found.values():
            name = (adv.local_name or device.name or "").lower()
            uuids = [u.lower() for u in (adv.service_uuids or [])]
            if self.match in name or self.match in device.address.lower():
                return device
            if ftms.FTMS_SERVICE in uuids and "kickr" in name:
                return device
        return None

    async def connect(self, timeout: float = 15.0):
        device = await self.find(timeout)
        log.info("Connecting to %s (%s)", device.name or "unnamed", device.address)
        self.client = BleakClient(device)
        await self.client.connect()
        log.info("Connected.")

        services = {s.uuid.lower() for s in self.client.services}
        if ftms.FTMS_SERVICE not in services:
            raise RuntimeError(
                "Trainer does not expose FTMS (0x1826). Services found: "
                + ", ".join(sorted(services))
                + ". An older KICKR may need a firmware update via the Wahoo app "
                "to gain FTMS, or we fall back to the Wahoo proprietary control point."
            )

        try:
            raw = await self.client.read_gatt_char(ftms.MACHINE_FEATURE)
            self.features = ftms.parse_features(raw)
            log.info(
                "Features: power=%s cadence=%s sim_params=%s power_target=%s",
                self.features.supports_power_measurement,
                self.features.supports_cadence,
                self.features.supports_sim_params,
                self.features.supports_power_target,
            )
            if not self.features.supports_sim_params:
                log.warning(
                    "Trainer reports no simulation-parameter support -- grade "
                    "control will not work; erg mode only."
                )
        except Exception as exc:  # noqa: BLE001
            log.warning("Could not read feature characteristic: %s", exc)

        await self.client.start_notify(ftms.CONTROL_POINT, self._on_control_response)
        await self.client.start_notify(ftms.INDOOR_BIKE_DATA, self._on_bike_data)
        await self.request_control()
        return self

    async def disconnect(self):
        if not self.client:
            return
        try:
            if self.has_control:
                # Hand the trainer back to a neutral state rather than leaving
                # it stuck on whatever grade you stopped at.
                await self._write_control(ftms.encode_sim_params(0.0), expect_response=False)
                await self._write_control(ftms.encode_reset(), expect_response=False)
        except Exception as exc:  # noqa: BLE001
            log.debug("Cleanup write failed (harmless): %s", exc)
        try:
            await self.client.disconnect()
        finally:
            self.client = None
            self.has_control = False

    # --- notifications ------------------------------------------------------

    def _on_bike_data(self, _sender, data: bytearray):
        try:
            parsed = ftms.parse_indoor_bike_data(bytes(data))
        except ValueError as exc:
            log.warning("Bad Indoor Bike Data packet %s: %s", bytes(data).hex(), exc)
            return
        if self.on_data:
            self.on_data(parsed)

    def _on_control_response(self, _sender, data: bytearray):
        self._responses.put_nowait(bytes(data))

    # --- control ------------------------------------------------------------

    async def _write_control(self, payload: bytes, expect_response: bool = True):
        if not self.client:
            raise RuntimeError("Not connected")
        async with self._control_lock:
            while not self._responses.empty():  # drop anything stale
                self._responses.get_nowait()
            await self.client.write_gatt_char(ftms.CONTROL_POINT, payload, response=True)
            if not expect_response:
                return None
            try:
                reply = await asyncio.wait_for(self._responses.get(), CONTROL_TIMEOUT)
            except asyncio.TimeoutError as exc:
                raise TimeoutError(
                    f"No response to control opcode 0x{payload[0]:02x} after "
                    f"{CONTROL_TIMEOUT}s"
                ) from exc
            return ftms.parse_control_response(reply)

    async def request_control(self):
        """Take exclusive control. Every other command is rejected until this
        succeeds, so failure here is fatal rather than a warning."""
        await self._write_control(ftms.encode_request_control())
        self.has_control = True
        log.info("Control granted.")

    async def set_grade(self, grade: float, force: bool = False):
        """Set road grade as a ratio (0.08 == 8%). Rate limited internally."""
        now = asyncio.get_running_loop().time()
        if not force and self._last_grade is not None:
            if (
                abs(grade - self._last_grade) < GRADE_MIN_DELTA
                and now - self._last_grade_at < 1.0
            ):
                return
            if now - self._last_grade_at < GRADE_MIN_INTERVAL:
                return
        payload = ftms.encode_sim_params(grade * 100.0, 0.0, self.crr, self.cw)
        await self._write_control(payload)
        self._last_grade = grade
        self._last_grade_at = now

    async def set_target_power(self, watts: int):
        await self._write_control(ftms.encode_target_power(watts))
