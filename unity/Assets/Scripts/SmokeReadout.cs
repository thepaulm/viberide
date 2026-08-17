using UnityEngine;

namespace KickrWorld
{
    /// <summary>Minimal on-screen state for the smoke-test player.</summary>
    public class SmokeReadout : MonoBehaviour
    {
        public TrainerLink Link;

        void OnGUI()
        {
            var style = new GUIStyle(GUI.skin.label) { fontSize = 20 };
            string state = Link == null ? "no link"
                : Link.Connected ? $"connected  {Link.Latest.power_w:F0} W  {Link.Latest.speed_kph:F1} kph"
                : "waiting for bridge...";
            GUI.Label(new Rect(20f, 20f, 900f, 40f), state, style);
        }
    }
}
