using System.Text;
using UnityEngine;

namespace VGuns.Broadcast
{
    /// <summary>
    /// One gameplay session (one run of the level). Lives in the gameplay scene, so it
    /// starts when the level loads and ends when the scene unloads (death -> menu,
    /// restart, quit). Wires tracker, spectator rig, director, telemetry and panel.
    /// </summary>
    public class SessionController : MonoBehaviour
    {
        public GameStateTracker State { get; private set; }
        public SpectatorRig Rig { get; private set; }
        public DirectorAI Director { get; private set; }
        public TelemetryRecorder Telemetry { get; private set; }
        public AIPanel Panel { get; private set; }
        public string SceneName { get; private set; }

        bool ended;

        public void Begin(XRTarget player, string sceneName, BroadcastServer server)
        {
            SceneName = sceneName;
            GameEventBus.SessionStartRealtime = Time.realtimeSinceStartup;

            State = gameObject.AddComponent<GameStateTracker>();
            State.Init(player);

            Rig = gameObject.AddComponent<SpectatorRig>();
            Rig.Init(State, server);

            Telemetry = gameObject.AddComponent<TelemetryRecorder>();
            Director = gameObject.AddComponent<DirectorAI>();
            Director.Init(State, Rig, Telemetry);
            Telemetry.Init(State, Director, Rig, sceneName);
            Telemetry.BeforeTeardownClose = () => End(!ReferenceEquals(State, null) && State.IsDead ? "player died" : "scene unloaded");

            Panel = gameObject.AddComponent<AIPanel>();
            Panel.Init(State, Director, Rig, Telemetry);

            GameEventBus.Raise(GameEventType.SessionStart, State.Head.position, "system", sceneName, State.MaxHealth,
                "telemetry=" + (Telemetry.Ok ? Telemetry.SessionFolder : "off"));
        }

        public void End(string reason)
        {
            if (ended) return;
            ended = true;
            GameEventBus.Raise(GameEventType.SessionEnd, Vector3.zero, "system", SceneName, 0f, reason);
            // ReferenceEquals: during scene teardown the component may already count as "destroyed"
            // but its C# state (open CSV files) is still valid and must be closed.
            if (!ReferenceEquals(Telemetry, null)) Telemetry.Close(reason);
        }

        void OnApplicationQuit()
        {
            End("application quit");
        }

        void OnDestroy()
        {
            End(!ReferenceEquals(State, null) && State.IsDead ? "player died" : "scene unloaded");
            if (BroadcastHub.Instance != null && BroadcastHub.Instance.Session == this)
                BroadcastHub.Instance.Session = null;
        }

        public void HandleCommand(ServerCommand c)
        {
            if (Director == null) return;
            if (c.Kind == "camera")
            {
                CameraAngle a;
                if (CameraTaxonomy.TryParseCode(c.Arg, out a)) Director.SetOverride(a);
                else Director.SetOverride(null);
            }
            else if (c.Kind == "rate")
            {
                int v;
                if (!int.TryParse(c.Arg, out v)) v = 1;
                Director.Rate(v >= 0 ? 1 : -1, "web");
            }
        }

        // ------------------------------------------------------------------
        public string BuildStateJson(BroadcastHub hub)
        {
            var s = State; var d = Director; var r = Rig; var m = d.Metrics;
            float now = GameEventBus.Now;
            var sb = new StringBuilder(2048);
            sb.Append("{\"live\":true");
            sb.Append(",\"session\":").Append(Json.Str(Telemetry.SessionId));
            sb.Append(",\"scene\":").Append(Json.Str(SceneName));
            sb.Append(",\"t\":").Append(Json.Num(now));
            sb.Append(",\"angle\":").Append(Json.Str(CameraTaxonomy.Code(r.Angle)));
            sb.Append(",\"shot\":").Append(Json.Str(r.Shot.ToString()));
            sb.Append(",\"shotName\":").Append(Json.Str(CameraTaxonomy.ShotName(r.Shot)));
            sb.Append(",\"shotLabel\":").Append(Json.Str(CameraTaxonomy.Get(r.Shot).Label));
            sb.Append(",\"held\":").Append(Json.Num(d.TimeInShot));
            sb.Append(",\"aiChoice\":").Append(Json.Str(CameraTaxonomy.Code(d.AiChoice)));
            sb.Append(",\"override\":").Append(d.HasOverride ? Json.Str(CameraTaxonomy.Code(d.OverrideAngle)) : "null");
            sb.Append(",\"probs\":[").Append(Json.Num(d.Probs[0])).Append(',').Append(Json.Num(d.Probs[1])).Append(',').Append(Json.Num(d.Probs[2])).Append(']');
            sb.Append(",\"ruleProbs\":[").Append(Json.Num(d.RuleProbs[0])).Append(',').Append(Json.Num(d.RuleProbs[1])).Append(',').Append(Json.Num(d.RuleProbs[2])).Append(']');
            sb.Append(",\"reason\":").Append(Json.Str(d.LastDecision != null ? d.LastDecision.Reason : ""));
            sb.Append(",\"trigger\":").Append(Json.Str(d.ActiveTrigger));

            sb.Append(",\"game\":{");
            sb.Append("\"health\":").Append(Json.Num(s.Health));
            sb.Append(",\"maxHealth\":").Append(Json.Num(s.MaxHealth));
            sb.Append(",\"dead\":").Append(s.IsDead ? "true" : "false");
            sb.Append(",\"enemies\":").Append(s.Enemies.Count);
            sb.Append(",\"nearest\":").Append(s.NearestEnemy != null ? Json.Num(s.NearestEnemyDist) : "null");
            sb.Append(",\"kills\":").Append(s.TotalKills);
            sb.Append(",\"shots\":").Append(s.TotalShots);
            sb.Append(",\"accuracy\":").Append(Json.Num(s.Accuracy));
            sb.Append(",\"hitsTaken\":").Append(s.TotalHitsTaken);
            sb.Append(",\"wave\":").Append(s.WaveIndex);
            sb.Append(",\"intensity\":").Append(Json.Num(s.Intensity));
            sb.Append(",\"bulletTime\":").Append(s.BulletTime ? "true" : "false");
            sb.Append('}');

            sb.Append(",\"metrics\":{");
            sb.Append("\"ticks\":").Append(m.Ticks);
            sb.Append(",\"cuts\":").Append(m.Cuts);
            sb.Append(",\"cutsPerMin\":").Append(Json.Num(m.CutsPerMinute(now)));
            sb.Append(",\"avgShot\":").Append(Json.Num(m.AvgShotLength));
            sb.Append(",\"avgConfidence\":").Append(Json.Num(m.AvgConfidence));
            sb.Append(",\"eventCuts\":").Append(m.EventCuts);
            sb.Append(",\"avgLatency\":").Append(Json.Num(m.AvgEventLatency));
            sb.Append(",\"ruleAgreement\":").Append(Json.Num(m.RuleAgreement));
            sb.Append(",\"humanTicks\":").Append(m.HumanTicks);
            sb.Append(",\"humanAgreement\":").Append(m.HumanTicks > 0 ? Json.Num(m.HumanAgreement) : "null");
            sb.Append(",\"up\":").Append(m.RatingsUp);
            sb.Append(",\"down\":").Append(m.RatingsDown);
            sb.Append(",\"share\":[").Append(Json.Num(m.SharePct(CameraAngle.FirstPerson))).Append(',')
              .Append(Json.Num(m.SharePct(CameraAngle.ThirdPerson))).Append(',')
              .Append(Json.Num(m.SharePct(CameraAngle.Cinematic))).Append(']');
            sb.Append('}');

            sb.Append(",\"model\":{\"name\":").Append(Json.Str(d.Policy.Name))
              .Append(",\"loaded\":").Append(d.Policy.Loaded ? "true" : "false")
              .Append(",\"valAccuracy\":").Append(Json.Num(d.Policy.ValAccuracy))
              .Append(",\"trainSamples\":").Append(d.Policy.TrainSamples)
              .Append(",\"humanSamples\":").Append(d.Policy.HumanSamples).Append('}');

            sb.Append(",\"decisions\":[");
            for (int i = d.RecentDecisions.Count - 1, n = 0; i >= 0 && n < 10; i--, n++)
            {
                var x = d.RecentDecisions[i];
                if (n > 0) sb.Append(',');
                sb.Append("{\"t\":").Append(Json.Num(x.Time))
                  .Append(",\"from\":").Append(Json.Str(CameraTaxonomy.Code(CameraTaxonomy.AngleOf(x.FromShot))))
                  .Append(",\"to\":").Append(Json.Str(CameraTaxonomy.Code(CameraTaxonomy.AngleOf(x.ToShot))))
                  .Append(",\"shot\":").Append(Json.Str(CameraTaxonomy.ShotName(x.ToShot)))
                  .Append(",\"source\":").Append(Json.Str(x.Source.ToString()))
                  .Append(",\"trigger\":").Append(Json.Str(x.Trigger))
                  .Append(",\"conf\":").Append(Json.Num(x.Confidence))
                  .Append(",\"reason\":").Append(Json.Str(x.Reason)).Append('}');
            }
            sb.Append(']');

            sb.Append(",\"events\":[");
            var ev = hub.RecentEvents;
            for (int i = ev.Count - 1, n = 0; i >= 0 && n < 14; i--, n++)
            {
                var e = ev[i];
                if (n > 0) sb.Append(',');
                sb.Append("{\"t\":").Append(Json.Num(e.SessionTime))
                  .Append(",\"type\":").Append(Json.Str(e.Type.ToString()))
                  .Append(",\"detail\":").Append(Json.Str(e.Detail)).Append('}');
            }
            sb.Append(']');

            sb.Append(",\"stream\":{\"viewers\":").Append(hub.Server.StreamClients)
              .Append(",\"fps\":").Append(Json.Num(r.MeasuredStreamFps))
              .Append(",\"width\":").Append(BroadcastConfig.StreamWidth)
              .Append(",\"height\":").Append(BroadcastConfig.StreamHeight)
              .Append(",\"lanUrl\":").Append(Json.Str(hub.LanUrl)).Append('}');
            sb.Append(",\"telemetry\":{\"recording\":").Append(Telemetry.Ok ? "true" : "false")
              .Append(",\"folder\":").Append(Json.Str(Telemetry.SessionFolder))
              .Append(",\"events\":").Append(Telemetry.EventCount).Append('}');
            sb.Append('}');
            return sb.ToString();
        }
    }
}
