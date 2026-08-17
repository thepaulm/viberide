"""Offline checks for the wire format and the physics -- no trainer required."""

import struct
import sys

from kickr_bridge import ftms, physics


def check(label, actual, expected, tol=1e-6):
    ok = abs(actual - expected) <= tol if isinstance(expected, float) else actual == expected
    print(f"  [{'ok' if ok else 'FAIL'}] {label}: {actual!r}" + ("" if ok else f"  expected {expected!r}"))
    return ok


def test_indoor_bike_data():
    print("Indoor Bike Data decode")
    # flags: bit0 clear (speed present), bit2 cadence, bit6 power
    flags = (1 << 2) | (1 << 6)
    packet = struct.pack("<HHHh", flags, 3000, 180, 250)
    d = ftms.parse_indoor_bike_data(packet)
    results = [
        check("speed_kph", d.speed_kph, 30.0),
        check("cadence_rpm", d.cadence_rpm, 90.0),
        check("power_w", d.power_w, 250),
    ]

    # Now with speed absent (More Data bit SET) plus distance and heart rate.
    flags2 = (1 << 0) | (1 << 2) | (1 << 4) | (1 << 6) | (1 << 9)
    packet2 = (
        struct.pack("<HH", flags2, 170)
        + (12345).to_bytes(3, "little")
        + struct.pack("<hB", 312, 148)
    )
    d2 = ftms.parse_indoor_bike_data(packet2)
    results += [
        check("speed absent", d2.speed_kph, None),
        check("cadence_rpm", d2.cadence_rpm, 85.0),
        check("distance_m", d2.distance_m, 12345),
        check("power_w", d2.power_w, 312),
        check("heart_rate_bpm", d2.heart_rate_bpm, 148),
    ]
    return all(results)


def test_control_encoding():
    print("Control point encoding")
    results = []
    sim = ftms.encode_sim_params(grade_pct=6.5, wind_speed_mps=0.0, crr=0.004, cw=0.51)
    op, wind, grade, crr, cw = struct.unpack("<BhhBB", sim)
    results += [
        check("opcode", op, 0x11),
        check("grade raw (0.01%)", grade, 650),
        check("wind raw", wind, 0),
        check("crr raw (0.0001)", crr, 40),
        check("cw raw (0.01)", cw, 51),
        check("length", len(sim), 7),
    ]

    neg = ftms.encode_sim_params(grade_pct=-8.25)
    _, _, ngrade, _, _ = struct.unpack("<BhhBB", neg)
    results.append(check("descent grade raw", ngrade, -825))

    erg = ftms.encode_target_power(285)
    results.append(check("erg encoding", erg.hex(), "051d01"))

    # A rejected write must raise rather than silently no-op.
    try:
        ftms.parse_control_response(bytes([0x80, 0x11, 0x05]))
        results.append(check("rejects control-not-permitted", False, True))
    except ftms.ControlError as exc:
        results.append(check("rejects control-not-permitted", "not permitted" in str(exc), True))

    ok = ftms.parse_control_response(bytes([0x80, 0x00, 0x01]))
    results.append(check("accepts success", ok, (0x00, 0x01)))
    return all(results)


def test_features():
    print("Feature flags decode")
    # Power measurement (machine bit 14) + sim params (target bit 13).
    data = struct.pack("<II", 1 << 14, (1 << 13) | (1 << 3))
    f = ftms.parse_features(data)
    return all(
        [
            check("supports_power_measurement", f.supports_power_measurement, True),
            check("supports_sim_params", f.supports_sim_params, True),
            check("supports_power_target", f.supports_power_target, True),
            check("supports_resistance_target", f.supports_resistance_target, False),
        ]
    )


def test_physics():
    print("Physics sanity")
    model = physics.RideModel(physics.Rider())
    rows = [
        ("200 W, flat", 200, 0.00),
        ("250 W, flat", 250, 0.00),
        ("250 W, 4%", 250, 0.04),
        ("250 W, 8%", 250, 0.08),
        ("300 W, 10%", 300, 0.10),
        ("100 W, -5% (descent)", 100, -0.05),
    ]
    for label, watts, grade in rows:
        kph = model.steady_state_speed(watts, grade) * 3.6
        print(f"  {label:<24} -> {kph:5.1f} km/h")

    # Convergence: integrating from a standstill must reach steady state.
    m = physics.RideModel(physics.Rider())
    target = m.steady_state_speed(250, 0.0)
    for _ in range(60 * 200):  # 200 s at 60 Hz
        m.step(250, 0.0, 1 / 60)
    converged = abs(m.state.speed_mps - target) < 0.05
    print(f"  integrated 200 s @250 W -> {m.state.speed_kph:.1f} km/h "
          f"(steady state {target * 3.6:.1f})")

    # Coasting to a stop on the flat with zero power.
    m2 = physics.RideModel(physics.Rider())
    m2.state.speed_mps = 10.0
    for _ in range(60 * 120):
        m2.step(0, 0.0, 1 / 60)
    stopped = m2.state.speed_mps < 1.0

    # A steep wall at low power must not send you backwards.
    m3 = physics.RideModel(physics.Rider())
    for _ in range(60 * 30):
        m3.step(80, 0.15, 1 / 60)
    forward = m3.state.speed_mps >= 0.0

    return all(
        [
            check("converges to steady state", converged, True),
            check("coasts to a stop", stopped, True),
            check("never rolls backwards", forward, True),
        ]
    )


if __name__ == "__main__":
    passed = all([test_indoor_bike_data(), test_control_encoding(), test_features(), test_physics()])
    print("\nPASS" if passed else "\nFAILURES ABOVE")
    sys.exit(0 if passed else 1)
