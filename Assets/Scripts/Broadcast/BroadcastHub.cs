using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VGuns.Broadcast
{
    /// <summary>
    /// Boots automatically (no scene setup needed). Persists across scenes, runs the
    /// web server, and starts a SessionController whenever a gameplay scene loads.
    ///
    /// Keyboard (Editor / PC):  1 FPV · 2 TPV · 3 CINE · 0 AUTO · = good cut · - bad cut · F1 panel
    /// </summary>
    public class BroadcastHub : MonoBehaviour
    {
        public static BroadcastHub Instance { get; private set; }

        public BroadcastServer Server { get; private set; }
        public SessionController Session { get; set; }
        public string LanIp { get; private set; }
        public string LanUrl { get; private set; }
        public string LocalUrl { get; private set; }
        public readonly List<GameEvent> RecentEvents = new List<GameEvent>();

#if UNITY_EDITOR || UNITY_STANDALONE
        static bool browserOpened;
#endif
        float nextState;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            if (!BroadcastConfig.Enabled || Instance != null) return;
            var go = new GameObject("VGuns Broadcast Hub");
            DontDestroyOnLoad(go);
            go.AddComponent<BroadcastHub>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            LanIp = FindLanIp();
            Server = new BroadcastServer();
            var html = Resources.Load<TextAsset>("BroadcastWeb/index");
            if (html != null) Server.IndexHtml = html.text;
            else Debug.LogWarning("[Broadcast] Resources/BroadcastWeb/index.html missing");

            if (Server.Start(BroadcastConfig.HttpPort, TelemetryPaths.Root))
            {
                LocalUrl = "http://localhost:" + Server.Port + "/";
                LanUrl = "http://" + LanIp + ":" + Server.Port + "/";
                Debug.Log("[Broadcast] Viewer running: " + LocalUrl + "  (LAN: " + LanUrl + ")");
            }
            else
            {
                LocalUrl = LanUrl = "(server failed: " + Server.LastError + ")";
            }

            GameEventBus.OnEvent += OnGameEvent;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            GameEventBus.OnEvent -= OnGameEvent;
            if (Server != null) Server.Stop();
            Instance = null;
        }

        void OnApplicationQuit()
        {
            if (Session != null) Session.End("application quit");
            if (Server != null) Server.Stop();
        }

        void OnGameEvent(GameEvent e)
        {
            if (e.Type == GameEventType.PlayerShot || e.Type == GameEventType.EnemyAttack) return;
            RecentEvents.Add(e);
            if (RecentEvents.Count > 40) RecentEvents.RemoveAt(0);
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (Session != null) return;
            var player = FindObjectOfType<XRTarget>();
            if (player == null) return;

            // Only gameplay scenes (the menu scene also contains the XR rig).
            bool gameplay = scene.buildIndex > 0 || FindObjectOfType<EnemyAi>() != null || FindObjectOfType<wave>() != null;
            if (!gameplay) return;

            RecentEvents.Clear();
            var go = new GameObject("AI Director Session");
            SceneManager.MoveGameObjectToScene(go, scene);
            Session = go.AddComponent<SessionController>();
            Session.Begin(player, scene.name, Server);

#if UNITY_EDITOR || UNITY_STANDALONE
            if (BroadcastConfig.OpenBrowserOnGameStart && !browserOpened && Server.Running)
            {
                browserOpened = true;
                Application.OpenURL(LocalUrl);
            }
#endif
        }

        void Update()
        {
            ServerCommand c;
            while (Server != null && Server.Commands.TryDequeue(out c))
            {
                if (Session != null) Session.HandleCommand(c);
            }

            HandleKeyboard();

            if (Time.unscaledTime >= nextState && Server != null)
            {
                nextState = Time.unscaledTime + 0.2f;
                try
                {
                    Server.StateJson = Session != null
                        ? Session.BuildStateJson(this)
                        : "{\"live\":false,\"lanUrl\":" + Json.Str(LanUrl) + ",\"telemetryRoot\":" + Json.Str(TelemetryPaths.Root) + "}";
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Broadcast] state json failed: " + ex.Message);
                }
            }
        }

        void HandleKeyboard()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null || Session == null) return;
            if (kb.digit1Key.wasPressedThisFrame) Session.HandleCommand(new ServerCommand { Kind = "camera", Arg = "fpv" });
            if (kb.digit2Key.wasPressedThisFrame) Session.HandleCommand(new ServerCommand { Kind = "camera", Arg = "tpv" });
            if (kb.digit3Key.wasPressedThisFrame) Session.HandleCommand(new ServerCommand { Kind = "camera", Arg = "cine" });
            if (kb.digit0Key.wasPressedThisFrame) Session.HandleCommand(new ServerCommand { Kind = "camera", Arg = "auto" });
            if (kb.equalsKey.wasPressedThisFrame) Session.HandleCommand(new ServerCommand { Kind = "rate", Arg = "1" });
            if (kb.minusKey.wasPressedThisFrame) Session.HandleCommand(new ServerCommand { Kind = "rate", Arg = "-1" });
        }

        static string FindLanIp()
        {
            try
            {
                // No packets are sent: connecting a UDP socket just selects the outgoing interface.
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("10.255.255.255", 1);
                    var ep = s.LocalEndPoint as IPEndPoint;
                    if (ep != null && !IPAddress.IsLoopback(ep.Address)) return ep.Address.ToString();
                }
            }
            catch { }
            try
            {
                foreach (var a in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)) return a.ToString();
            }
            catch { }
            return "localhost";
        }
    }
}
