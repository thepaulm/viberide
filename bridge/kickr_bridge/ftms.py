"""Encode/decode for the Bluetooth SIG Fitness Machine Service (FTMS, 0x1826).

Pure functions over bytes -- no BLE, no I/O -- so the wire format can be tested
without a trainer plugged in. Field order and units follow the FTMS spec; the
two that bite people are:

  * Indoor Bike Data flag bit 0 is "More Data", and it is INVERTED: instantaneous
    speed is present when the bit is CLEAR.
  * Cadence is transmitted in half-RPM, so a value of 180 means 90 rpm.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass, field, asdict

# --- UUIDs ------------------------------------------------------------------

FTMS_SERVICE = "00001826-0000-1000-8000-00805f9b34fb"
INDOOR_BIKE_DATA = "00002ad2-0000-1000-8000-00805f9b34fb"
CONTROL_POINT = "00002ad9-0000-1000-8000-00805f9b34fb"
MACHINE_STATUS = "00002ada-0000-1000-8000-00805f9b34fb"
MACHINE_FEATURE = "00002acc-0000-1000-8000-00805f9b34fb"

# --- Control point opcodes --------------------------------------------------

OP_REQUEST_CONTROL = 0x00
OP_RESET = 0x01
OP_SET_TARGET_RESISTANCE = 0x04
OP_SET_TARGET_POWER = 0x05
OP_START_RESUME = 0x07
OP_STOP_PAUSE = 0x08
OP_SET_SIM_PARAMS = 0x11
OP_RESPONSE = 0x80

RESULT_NAMES = {
    0x01: "success",
    0x02: "opcode not supported",
    0x03: "invalid parameter",
    0x04: "operation failed",
    0x05: "control not permitted",
}


class ControlError(RuntimeError):
    """A control point write came back with a non-success result code."""


# --- Indoor Bike Data (0x2AD2) ---------------------------------------------


@dataclass
class BikeData:
    """One decoded Indoor Bike Data notification. Fields are None when absent."""

    speed_kph: float | None = None
    avg_speed_kph: float | None = None
    cadence_rpm: float | None = None
    avg_cadence_rpm: float | None = None
    distance_m: int | None = None
    resistance: int | None = None
    power_w: int | None = None
    avg_power_w: int | None = None
    energy_kcal: int | None = None
    heart_rate_bpm: int | None = None
    elapsed_s: int | None = None
    raw_flags: int = 0

    def to_dict(self) -> dict:
        return {k: v for k, v in asdict(self).items() if v is not None}


class _Cursor:
    """Sequential little-endian reader over the notification payload."""

    def __init__(self, data: bytes, offset: int = 0):
        self.data = data
        self.offset = offset

    def _take(self, n: int) -> bytes:
        if self.offset + n > len(self.data):
            raise ValueError(
                f"Indoor Bike Data truncated: need {n} bytes at offset "
                f"{self.offset}, packet is {len(self.data)} bytes"
            )
        chunk = self.data[self.offset : self.offset + n]
        self.offset += n
        return chunk

    def u8(self) -> int:
        return self._take(1)[0]

    def u16(self) -> int:
        return struct.unpack("<H", self._take(2))[0]

    def s16(self) -> int:
        return struct.unpack("<h", self._take(2))[0]

    def u24(self) -> int:
        return int.from_bytes(self._take(3), "little")


def parse_indoor_bike_data(data: bytes) -> BikeData:
    """Decode a 0x2AD2 notification into a BikeData."""
    if len(data) < 2:
        raise ValueError("Indoor Bike Data packet shorter than its flags field")

    cur = _Cursor(data)
    flags = cur.u16()
    out = BikeData(raw_flags=flags)

    def has(bit: int) -> bool:
        return bool(flags & (1 << bit))

    # Bit 0 is "More Data" and is inverted: speed present when the bit is clear.
    if not has(0):
        out.speed_kph = cur.u16() * 0.01
    if has(1):
        out.avg_speed_kph = cur.u16() * 0.01
    if has(2):
        out.cadence_rpm = cur.u16() * 0.5
    if has(3):
        out.avg_cadence_rpm = cur.u16() * 0.5
    if has(4):
        out.distance_m = cur.u24()
    if has(5):
        out.resistance = cur.s16()
    if has(6):
        out.power_w = cur.s16()
    if has(7):
        out.avg_power_w = cur.s16()
    if has(8):
        out.energy_kcal = cur.u16()  # total energy
        cur.u16()  # energy per hour, unused
        cur.u8()  # energy per minute, unused
    if has(9):
        out.heart_rate_bpm = cur.u8()
    if has(10):
        cur.u8()  # metabolic equivalent, unused
    if has(11):
        out.elapsed_s = cur.u16()
    # Bit 12 (remaining time) is left unread; nothing follows that we need.

    return out


# --- Fitness Machine Feature (0x2ACC) --------------------------------------


@dataclass
class MachineFeatures:
    supports_power_measurement: bool = False
    supports_cadence: bool = False
    supports_resistance: bool = False
    supports_power_target: bool = False
    supports_resistance_target: bool = False
    supports_sim_params: bool = False
    raw: tuple[int, int] = field(default=(0, 0))


def parse_features(data: bytes) -> MachineFeatures:
    """Decode the 8-byte Fitness Machine Feature characteristic.

    The half that matters for us is the second uint32 (target setting features),
    bit 13 -- Indoor Bike Simulation Parameters. Without it there is no grade
    control and we would be limited to erg/resistance mode.
    """
    if len(data) < 8:
        raise ValueError(f"Feature characteristic should be 8 bytes, got {len(data)}")
    machine, target = struct.unpack("<II", data[:8])
    return MachineFeatures(
        supports_power_measurement=bool(machine & (1 << 14)),
        supports_cadence=bool(machine & (1 << 1)),
        supports_resistance=bool(machine & (1 << 7)),
        supports_power_target=bool(target & (1 << 3)),
        supports_resistance_target=bool(target & (1 << 2)),
        supports_sim_params=bool(target & (1 << 13)),
        raw=(machine, target),
    )


# --- Control point encoders -------------------------------------------------


def _clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def encode_request_control() -> bytes:
    """Must be sent -- and acknowledged -- before any other command is accepted."""
    return bytes([OP_REQUEST_CONTROL])


def encode_reset() -> bytes:
    return bytes([OP_RESET])


def encode_start() -> bytes:
    return bytes([OP_START_RESUME])


def encode_stop() -> bytes:
    return bytes([OP_STOP_PAUSE, 0x01])


def encode_target_power(watts: int) -> bytes:
    """Erg mode: hold this wattage regardless of cadence."""
    return struct.pack("<Bh", OP_SET_TARGET_POWER, int(_clamp(watts, -32768, 32767)))


def encode_sim_params(
    grade_pct: float,
    wind_speed_mps: float = 0.0,
    crr: float = 0.004,
    cw: float = 0.51,
) -> bytes:
    """Sim mode: the trainer computes resistance from road grade and drag.

    Resolutions per spec: wind 0.001 m/s (sint16), grade 0.01 %% (sint16),
    rolling resistance 0.0001 (uint8), wind coefficient 0.01 kg/m (uint8).
    The uint8 fields cap crr at 0.0255 and cw at 2.55, which is well outside
    anything physically sensible for a bicycle.
    """
    grade = int(round(_clamp(grade_pct, -40.0, 40.0) * 100))
    wind = int(round(_clamp(wind_speed_mps, -32.0, 32.0) * 1000))
    crr_raw = int(round(_clamp(crr, 0.0, 0.0255) / 0.0001))
    cw_raw = int(round(_clamp(cw, 0.0, 2.55) / 0.01))
    return struct.pack("<BhhBB", OP_SET_SIM_PARAMS, wind, grade, crr_raw, cw_raw)


def parse_control_response(data: bytes) -> tuple[int, int]:
    """Decode an indication from the control point. Returns (request_opcode, result).

    Raises ControlError on any non-success result.
    """
    if len(data) < 3 or data[0] != OP_RESPONSE:
        raise ValueError(f"Not an FTMS control response: {data.hex()}")
    request_op, result = data[1], data[2]
    if result != 0x01:
        reason = RESULT_NAMES.get(result, f"unknown result 0x{result:02x}")
        raise ControlError(f"opcode 0x{request_op:02x} rejected: {reason}")
    return request_op, result
