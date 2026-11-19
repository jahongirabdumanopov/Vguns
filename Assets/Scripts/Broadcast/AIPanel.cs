using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VGuns.Broadcast
{
    /// <summary>
    /// In-headset "AI Director - Assessment" panel. Floats lower-left of the player's view.
    /// Shows the live spectator preview, the AI's current decision and probabilities,
    /// why it decided, evaluation metrics and the event feed.
    /// Toggle: left thumbstick click (Quest) or F1 (keyboard).
    /// Only the headset sees it - it is hidden from the broadcast camera.
    /// </summary>
    public class AIPanel : MonoBehaviour
    {
        GameStateTracker state;
        DirectorAI director;
        SpectatorRig rig;
        TelemetryRecorder telemetry;

        GameObject panelGo;
        RectTransform panelRect;
        TextMeshProUGUI shotText, reasonText, metricsText, eventsText, footerText, modelText;
        readonly RectTransform[] bars = new RectTransform[3];
        readonly TextMeshProUGUI[] barValues = new TextMeshProUGUI[3];
        readonly RectTransform[] ruleTicks = new RectTransform[3];
        Image liveDot;

        const float BarWidth = 190f;
        static readonly Color[] AngleColors =
        {
            new Color(0.30f, 0.75f, 1.00f),   // FPV
            new Color(1.00f, 0.72f, 0.25f),   // TPV
            new Color(0.85f, 0.40f, 1.00f),   // CINE
        };

        public bool Visible { get; private set; }
        float nextRefresh;
        bool lastStickClick;
        bool placedOnce;
        readonly LinkedList<string> feed = new LinkedList<string>();

        public void Init(GameStateTracker s, DirectorAI d, SpectatorRig r, TelemetryRecorder t)
        {
            state = s; director = d; rig = r; telemetry = t;
            Build();
            SetVisible(BroadcastConfig.ShowPanelOnStart);
            GameEventBus.OnEvent += OnGameEvent;
        }

        void OnDestroy()
        {
            GameEventBus.OnEvent -= OnGameEvent;
            if (rig != null) rig.ForceRender = false;
        }

        void OnGameEvent(GameEvent e)
        {
            switch (e.Type)
            {
                case GameEventType.PlayerShot:
                case GameEventType.EnemyAttack:
                case GameEventType.EnemySpawned:
                    return;   // too chatty for the panel
            }
            string line = "<color=#8899aa>" + e.SessionTime.ToString("0.0") + "s</color> " + e.Type;
            if (!string.IsNullOrEmpty(e.Detail)) line += " <color=#8899aa>" + Trunc(e.Detail, 22) + "</color>";
            feed.AddFirst(line);
            while (feed.Count > 7) feed.RemoveLast();
        }

        static string Trunc(string s, int n)
        {
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        public void SetVisible(bool v)
        {
            Visible = v;
            if (panelGo != null) panelGo.SetActive(v);
            if (rig != null) rig.ForceRender = v;
            if (v) placedOnce = false;
        }

        public void Toggle() { SetVisible(!Visible); }

        // ------------------------------------------------------------------
        void Build()
        {
            int hud = BroadcastConfig.LayerOrDefault(BroadcastConfig.PlayerHudLayerName, 15);

            panelGo = new GameObject("AI Director Panel");
            panelGo.transform.SetParent(transform, false);
            var canvas = panelGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = state.HeadCamera;
            canvas.sortingOrder = 50;
            panelRect = panelGo.GetComponent<RectTransform>();
            panelRect.sizeDelta = new Vector2(640, 470);
            panelRect.localScale = Vector3.one * 0.0008f;   // 0.51 m wide

            var bg = MakeImage(panelRect, 0, 0, 640, 470, new Color(0.04f, 0.06f, 0.09f, 0.9f));
            bg.raycastTarget = false;
            MakeImage(panelRect, 0, 0, 640, 4, new Color(0.85f, 0.40f, 1.00f, 1f));

            MakeText(panelRect, 16, 10, 420, 30, 22, "<b>AI DIRECTOR</b>  <color=#8899aa>assessment</color>");
            liveDot = MakeImage(panelRect, 560, 19, 12, 12, new Color(1f, 0.25f, 0.25f));
            MakeText(panelRect, 578, 12, 60, 26, 16, "<b>LIVE</b>");

            // preview of what viewers see
            MakeImage(panelRect, 14, 44, 292, 166, new Color(0.2f, 0.25f, 0.3f));
            var raw = new GameObject("Preview", typeof(RectTransform), typeof(RawImage));
            var rr = Place(raw.GetComponent<RectTransform>(), panelRect, 16, 46, 288, 162);
            raw.GetComponent<RawImage>().texture = rig.Target;
            raw.GetComponent<RawImage>().raycastTarget = false;
            rr.SetAsLastSibling();

            shotText = MakeText(panelRect, 322, 42, 302, 56, 24, "");
            string[] names = { "FPV", "TPV", "CINE" };
            for (int i = 0; i < 3; i++)
            {
                float y = 104 + i * 32;
                MakeText(panelRect, 322, y, 52, 24, 16, "<b>" + names[i] + "</b>");
                MakeImage(panelRect, 374, y + 4, BarWidth, 16, new Color(1f, 1f, 1f, 0.08f));
                bars[i] = MakeImage(panelRect, 374, y + 4, 1, 16, AngleColors[i]).rectTransform;
                ruleTicks[i] = MakeImage(panelRect, 374, y + 1, 2, 22, new Color(1f, 1f, 1f, 0.8f)).rectTransform;
                barValues[i] = MakeText(panelRect, 572, y, 56, 24, 15, "");
            }
            modelText = MakeText(panelRect, 322, 196, 302, 18, 11, "");

            reasonText = MakeText(panelRect, 16, 218, 608, 46, 15, "");
            MakeImage(panelRect, 16, 266, 608, 1, new Color(1f, 1f, 1f, 0.12f));
            metricsText = MakeText(panelRect, 16, 274, 300, 150, 14, "");
            eventsText = MakeText(panelRect, 330, 274, 294, 150, 13, "");
            footerText = MakeText(panelRect, 16, 430, 608, 36, 12, "");

            SetLayerRecursive(panelGo, hud);
        }

        static RectTransform Place(RectTransform rt, RectTransform parent, float x, float y, float w, float h)
        {
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        static Image MakeImage(RectTransform parent, float x, float y, float w, float h, Color c)
        {
            var go = new GameObject("Img", typeof(RectTransform), typeof(Image));
            Place(go.GetComponent<RectTransform>(), parent, x, y, w, h);
            var img = go.GetComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            return img;
        }

        static TextMeshProUGUI MakeText(RectTransform parent, float x, float y, float w, float h, float size, string text)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            Place(go.GetComponent<RectTransform>(), parent, x, y, w, h);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.fontSize = size;
            t.color = new Color(0.92f, 0.95f, 1f);
            t.richText = true;
            t.enableWordWrapping = true;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.alignment = TextAlignmentOptions.TopLeft;
            t.raycastTarget = false;
            t.text = text;
            return t;
        }

        static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform t in go.transform) SetLayerRecursive(t.gameObject, layer);
        }

        // ------------------------------------------------------------------
        void Update()
        {
            if (state == null || state.Head == null) return;

            // Toggle input
            bool click = false;
            var left = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
            if (left.isValid) left.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxisClick, out click);
            if (click && !lastStickClick) Toggle();
            lastStickClick = click;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f1Key.wasPressedThisFrame) Toggle();

            if (!Visible) return;

            Follow();

            liveDot.color = (Mathf.Repeat(Time.unscaledTime, 1f) < 0.5f && BroadcastHub.Instance != null && BroadcastHub.Instance.Server.StreamClients > 0)
                ? new Color(1f, 0.25f, 0.25f) : new Color(0.4f, 0.15f, 0.15f);

            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.25f;
            Refresh();
        }

        void Follow()
        {
            Transform h = state.Head;
            Vector3 fwd = state.FlatForward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 target = h.position + fwd * 0.85f - right * 0.38f - Vector3.up * 0.32f;
            float k = placedOnce ? 1f - Mathf.Exp(-2.5f * Time.unscaledDeltaTime) : 1f;
            placedOnce = true;
            Vector3 pos = Vector3.Lerp(panelRect.position, target, k);
            panelRect.position = pos;
            Vector3 look = pos - h.position;
            if (look.sqrMagnitude > 1e-4f) panelRect.rotation = Quaternion.LookRotation(look, Vector3.up);
        }

        void Refresh()
        {
            int a = (int)rig.Angle;
            string hex = ColorUtility.ToHtmlStringRGB(AngleColors[a]);
            string src = director.HasOverride ? "  <size=14><color=#ffcc66>VIEWER OVERRIDE</color></size>" : "  <size=14><color=#77dd99>AUTO</color></size>";
            shotText.text = "<color=#" + hex + "><b>" + CameraTaxonomy.Code(rig.Angle) + "</b></color> " +
                            CameraTaxonomy.ShotName(rig.Shot) + src +
                            "\n<size=13><color=#8899aa>holding " + director.TimeInShot.ToString("0.0") + "s · AI wants " +
                            CameraTaxonomy.Code(director.AiChoice) + "</color></size>";

            for (int i = 0; i < 3; i++)
            {
                float p = director.Probs[i];
                bars[i].sizeDelta = new Vector2(Mathf.Max(1f, BarWidth * p), 16f);
                ruleTicks[i].anchoredPosition = new Vector2(374f + BarWidth * director.RuleProbs[i] - 1f, ruleTicks[i].anchoredPosition.y);
                barValues[i].text = (p * 100f).ToString("0") + "%";
            }
            modelText.text = "<color=#8899aa>" + Trunc(director.Policy.Name, 40) +
                             (director.Policy.Loaded ? " · val acc " + (director.Policy.ValAccuracy * 100f).ToString("0") + "%" : "") +
                             " · white tick = rule baseline</color>";

            var d = director.LastDecision;
            reasonText.text = d == null
                ? "<color=#8899aa>Waiting for first decision…</color>"
                : "<color=#8899aa>Last cut " + (GameEventBus.Now - d.Time).ToString("0.0") + "s ago (" + d.Source + "):</color> " + d.Reason;

            var m = director.Metrics;
            float t = GameEventBus.Now;
            var sb = new StringBuilder();
            sb.Append("<b>Director</b>\n");
            sb.Append("Cuts ").Append(m.Cuts).Append("  ·  ").Append(m.CutsPerMinute(t).ToString("0.0")).Append("/min\n");
            sb.Append("Avg shot ").Append(m.AvgShotLength.ToString("0.0")).Append("s  ·  conf ").Append((m.AvgConfidence * 100f).ToString("0")).Append("%\n");
            sb.Append("Event→cut ").Append(m.AvgEventLatency.ToString("0.00")).Append("s (").Append(m.EventCuts).Append(")\n");
            sb.Append("Rule agreement ").Append((m.RuleAgreement * 100f).ToString("0")).Append("%\n");
            sb.Append("Human agreement ").Append(m.HumanTicks > 0 ? (m.HumanAgreement * 100f).ToString("0") + "%" : "–").Append('\n');
            sb.Append("Viewer rating <color=#77dd99>+").Append(m.RatingsUp).Append("</color> <color=#ff7777>-").Append(m.RatingsDown).Append("</color>");
            if (m.RatingsUp + m.RatingsDown > 0) sb.Append(" (").Append((m.Approval * 100f).ToString("0")).Append("%)");
            sb.Append("\nFPV ").Append(m.SharePct(CameraAngle.FirstPerson).ToString("0")).Append("% · TPV ")
              .Append(m.SharePct(CameraAngle.ThirdPerson).ToString("0")).Append("% · CINE ")
              .Append(m.SharePct(CameraAngle.Cinematic).ToString("0")).Append('%');
            metricsText.text = sb.ToString();

            sb.Length = 0;
            sb.Append("<b>Game</b>  HP ").Append(state.Health.ToString("0")).Append('/').Append(state.MaxHealth.ToString("0"))
              .Append(" · enemies ").Append(state.Enemies.Count).Append(" · kills ").Append(state.TotalKills).Append('\n');
            foreach (var line in feed) sb.Append(line).Append('\n');
            eventsText.text = sb.ToString();

            var hub = BroadcastHub.Instance;
            footerText.text = "<color=#8899aa>Stream: </color>" + (hub != null ? hub.LanUrl : "-") +
                              "<color=#8899aa>  viewers </color>" + (hub != null ? hub.Server.StreamClients : 0) +
                              "<color=#8899aa>  fps </color>" + rig.MeasuredStreamFps.ToString("0") +
                              "\n<color=#8899aa>Telemetry: </color>" + (telemetry.Ok ? Trunc(telemetry.SessionFolder, 70) : "not recording") +
                              "<color=#8899aa>   F1 / L-stick click: hide</color>";
        }
    }
}
