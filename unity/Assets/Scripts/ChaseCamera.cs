using UnityEngine;

namespace KickrWorld
{
    /// <summary>
    /// Chase camera that sits behind and above the bike.
    ///
    /// In its own file because Unity only makes a MonoScript for the class
    /// matching the filename -- this one previously shared BikeRider.cs and
    /// silently became a missing script in every built player.
    /// </summary>
    public class ChaseCamera : MonoBehaviour
    {
        public Transform Target;
        public Vector3 Offset = new Vector3(0f, 2.6f, -7.5f);
        public float PositionDamping = 4.5f;
        public float RotationDamping = 5f;
        public float LookAheadHeight = 1.4f;

        void LateUpdate()
        {
            if (Target == null) return;
            float dt = Time.deltaTime;

            // Yaw only: inheriting the bike's lean would roll the horizon and
            // make the whole thing nauseating.
            Vector3 flatFwd = Target.forward;
            flatFwd.y = 0f;
            if (flatFwd.sqrMagnitude < 1e-5f) flatFwd = Vector3.forward;
            flatFwd.Normalize();
            Quaternion yaw = Quaternion.LookRotation(flatFwd, Vector3.up);

            Vector3 desired = Target.position + yaw * Offset;
            transform.position = Vector3.Lerp(transform.position, desired,
                1f - Mathf.Exp(-PositionDamping * dt));

            Quaternion look = Quaternion.LookRotation(
                (Target.position + Vector3.up * LookAheadHeight) - transform.position, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, look,
                1f - Mathf.Exp(-RotationDamping * dt));
        }
    }
}
