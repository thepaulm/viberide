using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// A trimmed copy of the bridge's physics, used only for keyboard fallback.
    /// The bridge remains authoritative whenever it is connected; this exists so
    /// riding the world isn't gated on hardware.
    ///
    /// Plain class, not a MonoBehaviour, so it could legally share a file -- but
    /// it is kept separate for the same reason as the rest: one type per file
    /// removes any chance of the Unity filename rule biting again.
    /// </summary>
    public class FallbackRideModel
    {
        const float G = 9.80665f;
        const float VFloor = 0.7f;

        public float MassKg = 83.5f;
        public float CdA = 0.32f;
        public float Crr = 0.004f;
        public float Efficiency = 0.97f;
        public float Rho = 1.225f;
        public float Speed;

        public float Step(float watts, float grade, float dt)
        {
            float theta = Mathf.Atan(grade);
            float drive = Mathf.Max(watts, 0f) * Efficiency / Mathf.Max(Speed, VFloor);
            float gravity = MassKg * G * Mathf.Sin(theta);
            float rolling = Speed > 0.05f ? Crr * MassKg * G * Mathf.Cos(theta) : 0f;
            float aero = 0.5f * Rho * CdA * Speed * Speed;
            float accel = (drive - gravity - rolling - aero) / (MassKg + 1.5f);
            Speed = Mathf.Max(0f, Speed + accel * dt);
            return Speed;
        }
    }
}
