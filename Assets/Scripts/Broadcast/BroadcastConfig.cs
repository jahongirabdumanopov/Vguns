using UnityEngine;

namespace VGuns.Broadcast
{
    /// <summary>
    /// All settings for telemetry, the AI director stream and the web viewer in one place.
    /// </summary>
    public static class BroadcastConfig
    {
        /// <summary>Turn the whole system (telemetry + director + stream + panel) on/off.</summary>
        public static bool Enabled = true;

        /// <summary>Web viewer port: http://localhost:8080 (or http://&lt;quest-ip&gt;:8080).</summary>
        public static int HttpPort = 8080;

        /// <summary>Open the viewer in the default browser when gameplay starts (Editor / PC builds only).</summary>
        public static bool OpenBrowserOnGameStart = true;

#if UNITY_ANDROID && !UNITY_EDITOR
        public static int StreamWidth = 960;
        public static int StreamHeight = 540;
        public static int StreamFps = 20;
        public static int StreamAntiAliasing = 1;
#else
        public static int StreamWidth = 1280;
        public static int StreamHeight = 720;
        public static int StreamFps = 30;
        public static int StreamAntiAliasing = 2;
#endif
        public static int JpegQuality = 70;

        /// <summary>GPU readback + JPEG encode on a worker thread (fast). False = ReadPixels on main thread.</summary>
        public static bool UseAsyncReadback = true;

        /// <summary>If the web stream shows upside down on your platform, flip this (or use the Flip button on the page).</summary>
        public static bool FlipStreamVertically = false;

        /// <summary>Show the AI assessment panel in the headset when gameplay starts. Toggle: left thumbstick click / F1.</summary>
        public static bool ShowPanelOnStart = true;

        public const string SpectatorOnlyLayerName = "SpectatorOnly";
        public const string PlayerHudLayerName = "PlayerHUD";

        public static int LayerOrDefault(string name, int fallback)
        {
            int l = LayerMask.NameToLayer(name);
            return l >= 0 ? l : fallback;
        }
    }
}
