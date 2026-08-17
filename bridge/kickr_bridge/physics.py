"""Cycling physics: turn rider power + road grade into speed.

The standard road-cycling power balance. At steady state:

    P * eff = v * (F_gravity + F_rolling + F_aero)

but we want acceleration too, so each tick we compute the net force and
integrate. Forces:

    F_gravity = m * g * sin(theta)              -- theta = atan(grade)
    F_rolling = Crr * m * g * cos(theta)
    F_aero    = 0.5 * rho * CdA * v_air^2       -- v_air includes headwind

Sanity checks the model actually produces, at the default 75 kg rider +
8.5 kg bike, CdA 0.32, Crr 0.004:

    200 W flat        ->  33.9 km/h
    250 W flat        ->  36.8 km/h
    250 W up 8%%       ->  12.3 km/h   (~985 m/h VAM, right for 3.3 W/kg)
    100 W down 5%%     ->  54.0 km/h

Those line up with real-world road numbers, so the world should feel
honest under you. Run selftest.py to regenerate the table after tuning.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

G = 9.80665  # m/s^2

# Below this speed the P = F * v relation blows up (dividing by v -> infinite
# force), so we evaluate drive force at a floor. This is the standard dodge and
# it also conveniently models the fact that you can't actually track-stand your
# way to infinite thrust.
V_FLOOR = 0.7  # m/s


@dataclass
class Rider:
    """Everything about the rider and bike that the model needs."""

    rider_kg: float = 75.0
    bike_kg: float = 8.5
    cda: float = 0.32  # m^2; ~0.40 upright, 0.32 hoods, 0.28 drops
    crr: float = 0.004  # good road tyres on asphalt
    drivetrain_efficiency: float = 0.97
    wheel_inertia_kg: float = 1.5  # rotational inertia expressed as extra mass

    @property
    def total_mass(self) -> float:
        return self.rider_kg + self.bike_kg

    @property
    def effective_mass(self) -> float:
        """Mass for acceleration purposes -- wheels must be spun up as well as
        moved forward, which is worth roughly 1.5 kg on a road bike."""
        return self.total_mass + self.wheel_inertia_kg


def air_density(altitude_m: float = 0.0, temperature_c: float = 15.0) -> float:
    """ISA barometric density. Thinner air up high means real free speed on
    descents, which is a nice touch once the terrain has altitude."""
    pressure = 101325.0 * (1.0 - 2.25577e-5 * max(altitude_m, 0.0)) ** 5.25588
    return pressure / (287.058 * (temperature_c + 273.15))


@dataclass
class RideState:
    speed_mps: float = 0.0
    distance_m: float = 0.0
    elevation_gain_m: float = 0.0

    @property
    def speed_kph(self) -> float:
        return self.speed_mps * 3.6


class RideModel:
    """Integrates rider power into speed and distance."""

    def __init__(self, rider: Rider | None = None):
        self.rider = rider or Rider()
        self.state = RideState()

    def step(
        self,
        power_w: float,
        grade: float,
        dt: float,
        headwind_mps: float = 0.0,
        altitude_m: float = 0.0,
    ) -> RideState:
        """Advance one tick.

        `grade` is a ratio (0.08 == 8%%), not a percentage, and not degrees.
        `dt` is seconds. Returns the updated state.
        """
        if dt <= 0:
            return self.state

        rider = self.rider
        mass = rider.total_mass
        v = self.state.speed_mps
        theta = math.atan(grade)

        # Drive force. Evaluated at the speed floor so a standing start produces
        # a large but finite kick rather than a division by zero.
        v_drive = max(v, V_FLOOR)
        f_drive = max(power_w, 0.0) * rider.drivetrain_efficiency / v_drive

        f_gravity = mass * G * math.sin(theta)
        f_rolling = rider.crr * mass * G * math.cos(theta) if v > 0.05 else 0.0

        v_air = v + headwind_mps
        rho = air_density(altitude_m)
        # Signed square so a strong tailwind pushes rather than drags.
        f_aero = 0.5 * rho * rider.cda * v_air * abs(v_air)

        accel = (f_drive - f_gravity - f_rolling - f_aero) / rider.effective_mass
        v_new = v + accel * dt

        # You can roll to a stop, but you don't roll backwards down the hill --
        # in reality you'd put a foot down. Clamp instead of letting it go negative.
        v_new = max(v_new, 0.0)

        # Trapezoidal distance over the tick; more accurate than v_new * dt when
        # accelerating hard from a stop.
        travelled = 0.5 * (v + v_new) * dt
        self.state.speed_mps = v_new
        self.state.distance_m += travelled
        climb = travelled * math.sin(theta)
        if climb > 0:
            self.state.elevation_gain_m += climb

        return self.state

    def steady_state_speed(
        self, power_w: float, grade: float, altitude_m: float = 0.0
    ) -> float:
        """Speed this power would eventually settle at, in m/s.

        Solved by bisection rather than algebraically -- the cubic has an ugly
        closed form and this is only used for sanity checks and tuning.
        """
        rider = self.rider
        mass = rider.total_mass
        theta = math.atan(grade)
        rho = air_density(altitude_m)

        def net_force(v: float) -> float:
            f_drive = power_w * rider.drivetrain_efficiency / max(v, V_FLOOR)
            f_gravity = mass * G * math.sin(theta)
            f_rolling = rider.crr * mass * G * math.cos(theta)
            f_aero = 0.5 * rho * rider.cda * v * v
            return f_drive - f_gravity - f_rolling - f_aero

        lo, hi = 0.0, 40.0  # 144 km/h upper bound covers any descent
        if net_force(hi) > 0:
            return hi
        for _ in range(80):
            mid = 0.5 * (lo + hi)
            if net_force(mid) > 0:
                lo = mid
            else:
                hi = mid
        return 0.5 * (lo + hi)
