using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace VGuns.Broadcast
{
    public static class Csv
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        public static string F(float v, string fmt = "0.###")
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "";
            return v.ToString(fmt, Inv);
        }
    }

    public static class TelemetryPaths
    {
        /// <summary>
        /// Editor:            &lt;project&gt;/Telemetry
        /// Windows/Linux exe: next to the .exe, in Telemetry/
        /// Quest / Android:   /sdcard/Android/data/&lt;package&gt;/files/Telemetry (also downloadable from the web page)
        /// </summary>
        public static string Root
        {
            get
            {
#if UNITY_EDITOR
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Telemetry"));
#elif UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Telemetry"));
#else
                return Path.Combine(Application.persistentDataPath, "Telemetry");
#endif
            }
        }
    }

    /// <summary>
    /// Writes one folder of CSV files per play session:
    ///   session_info.csv, events.csv, frames.csv, director_ticks.csv,
    ///   camera_decisions.csv, ratings.csv, camera_taxonomy.csv, summary.csv
    /// </summary>
    public class TelemetryRecorder : MonoBehaviour
    {
        public string SessionId { get; private set; }
        public string SessionFolder { get; private set; }
        public int EventCount { get; private set; }
        public bool Ok { get; private set; }

        GameStateTracker state;
        SpectatorRig rig;
        DirectorMetrics metrics;     // plain C# refs captured at Init so Close() works during scene teardown
        string modelName = "";

        /// <summary>Called once if this component is destroyed before Close() (scene teardown order).</summary>
        public System.Action BeforeTeardownClose;

        StreamWriter events, frames, ticks, decisions, ratings;
        float nextFrame, nextFlush;
        readonly StringBuilder sb = new StringBuilder(512);
        bool closed;

        public const float FrameRateHz = 10f;

        public void Init(GameStateTracker s, DirectorAI d, SpectatorRig r, string sceneName)
        {
            state = s; rig = r;
            metrics = d.Metrics;
            modelName = d.Policy.Name;
            SessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            try
            {
                string root = TelemetryPaths.Root;
                string folder = Path.Combine(root, "session_" + SessionId);
                int n = 1;
                while (Directory.Exists(folder)) folder = Path.Combine(root, "session_" + SessionId + "_" + (++n));
                Directory.CreateDirectory(folder);
                SessionFolder = folder;

                events = Open("events.csv", "t,frame,type,actor,target,value,x,y,z,camera_angle,camera_shot,detail");
                frames = Open("frames.csv", "t,frame,timescale,fps,head_x,head_y,head_z,head_yaw,head_pitch,head_ang_speed,player_speed,health,enemies_alive,nearest_enemy_m,enemies_in_range,kills,shots,hits_taken,intensity,camera_angle,camera_shot,cam_x,cam_y,cam_z,viewers");
                var th = new StringBuilder("t");
                foreach (var name in DirectorFeatures.Names) th.Append(',').Append(name);
                th.Append(",p_fpv,p_tpv,p_cine,rule_fpv,rule_tpv,rule_cine,ai_choice,final_choice,human_label,override_active,trigger");
                ticks = Open("director_ticks.csv", th.ToString());
                decisions = Open("camera_decisions.csv", "t,from_angle,from_shot,to_angle,to_shot,source,trigger,confidence,p_fpv,p_tpv,p_cine,held_s,reason");
                ratings = Open("ratings.csv", "t,rating,source,camera_angle,camera_shot,seconds_since_cut,note");

                CameraTaxonomy.WriteCsv(Path.Combine(folder, "camera_taxonomy.csv"));
                WriteInfo(sceneName);
                Ok = true;
                Debug.Log("[Telemetry] Recording session to " + folder);
            }
            catch (Exception ex)
            {
                Ok = false;
                Debug.LogError("[Telemetry] Could not create session folder: " + ex);
            }
            GameEventBus.OnEvent += OnGameEvent;
        }

        StreamWriter Open(string file, string header)
        {
            var w = new StreamWriter(Path.Combine(SessionFolder, file), false, new UTF8Encoding(false));
            w.WriteLine(header);
            return w;
        }

        void WriteInfo(string sceneName)
        {
            var lines = new List<string>
            {
                "key,value",
                "session_id," + SessionId,
                "started_local," + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                "scene," + Csv.Escape(sceneName),
                "product," + Csv.Escape(Application.productName),
                "app_version," + Csv.Escape(Application.version),
                "unity_version," + Application.unityVersion,
                "platform," + Application.platform,
                "device_model," + Csv.Escape(SystemInfo.deviceModel),
                "xr_device," + Csv.Escape(UnityEngine.XR.XRSettings.loadedDeviceName),
                "initial_health," + Csv.F(state.MaxHealth),
                "director_model," + Csv.Escape(modelName),
                "stream_url," + Csv.Escape(BroadcastHub.Instance != null ? BroadcastHub.Instance.LanUrl : ""),
                "frame_rate_hz," + Csv.F(FrameRateHz),
            };
            File.WriteAllLines(Path.Combine(SessionFolder, "session_info.csv"), lines.ToArray());
        }

        void OnGameEvent(GameEvent e)
        {
            if (!Ok || closed) return;
            EventCount++;
            sb.Length = 0;
            sb.Append(Csv.F(e.SessionTime)).Append(',')
              .Append(e.Frame).Append(',')
              .Append(e.Type).Append(',')
              .Append(Csv.Escape(e.Actor)).Append(',')
              .Append(Csv.Escape(e.Target)).Append(',')
              .Append(Csv.F(e.Value)).Append(',')
              .Append(Csv.F(e.Position.x)).Append(',')
              .Append(Csv.F(e.Position.y)).Append(',')
              .Append(Csv.F(e.Position.z)).Append(',')
              .Append(!ReferenceEquals(rig, null) ? CameraTaxonomy.Code(rig.Angle) : "").Append(',')
              .Append(!ReferenceEquals(rig, null) ? rig.Shot.ToString() : "").Append(',')
              .Append(Csv.Escape(e.Detail));
            events.WriteLine(sb.ToString());
        }

        public void LogTick(float t, float[] f, float[] p, float[] rule, CameraAngle ai, CameraAngle final, string humanLabel, bool overrideActive, string trigger)
        {
            if (!Ok || closed) return;
            sb.Length = 0;
            sb.Append(Csv.F(t));
            for (int i = 0; i < f.Length; i++) sb.Append(',').Append(Csv.F(f[i], "0.####"));
            sb.Append(',').Append(Csv.F(p[0], "0.####")).Append(',').Append(Csv.F(p[1], "0.####")).Append(',').Append(Csv.F(p[2], "0.####"));
            sb.Append(',').Append(Csv.F(rule[0], "0.####")).Append(',').Append(Csv.F(rule[1], "0.####")).Append(',').Append(Csv.F(rule[2], "0.####"));
            sb.Append(',').Append(CameraTaxonomy.Code(ai))
              .Append(',').Append(CameraTaxonomy.Code(final))
              .Append(',').Append(humanLabel ?? "")
              .Append(',').Append(overrideActive ? 1 : 0)
              .Append(',').Append(trigger ?? "");
            ticks.WriteLine(sb.ToString());
        }

        public void LogDecision(DirectorDecision d)
        {
            if (!Ok || closed) return;
            sb.Length = 0;
            sb.Append(Csv.F(d.Time)).Append(',')
              .Append(CameraTaxonomy.Code(CameraTaxonomy.AngleOf(d.FromShot))).Append(',')
              .Append(d.FromShot).Append(',')
              .Append(CameraTaxonomy.Code(CameraTaxonomy.AngleOf(d.ToShot))).Append(',')
              .Append(d.ToShot).Append(',')
              .Append(d.Source).Append(',')
              .Append(d.Trigger).Append(',')
              .Append(Csv.F(d.Confidence)).Append(',')
              .Append(Csv.F(d.Probs[0])).Append(',').Append(Csv.F(d.Probs[1])).Append(',').Append(Csv.F(d.Probs[2])).Append(',')
              .Append(Csv.F(d.HeldSeconds)).Append(',')
              .Append(Csv.Escape(d.Reason));
            decisions.WriteLine(sb.ToString());
        }

        public void LogRating(float t, int rating, string source, float sinceCut, string note)
        {
            if (!Ok || closed) return;
            ratings.WriteLine(Csv.F(t) + "," + rating + "," + Csv.Escape(source) + "," +
                              CameraTaxonomy.Code(rig.Angle) + "," + rig.Shot + "," + Csv.F(sinceCut) + "," + Csv.Escape(note));
        }

        void Update()
        {
            if (!Ok || closed || state == null || state.Head == null) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextFrame)
            {
                nextFrame = now + 1f / FrameRateHz;
                WriteFrame();
            }
            if (now >= nextFlush)
            {
                nextFlush = now + 1f;
                Flush();
            }
        }

        void WriteFrame()
        {
            Transform h = state.Head;
            Vector3 e = h.rotation.eulerAngles;
            float pitch = e.x > 180f ? e.x - 360f : e.x;
            Vector3 cp = rig != null && rig.Cam != null ? rig.Cam.transform.position : Vector3.zero;
            sb.Length = 0;
            sb.Append(Csv.F(GameEventBus.Now)).Append(',')
              .Append(Time.frameCount).Append(',')
              .Append(Csv.F(Time.timeScale)).Append(',')
              .Append(Csv.F(1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), "0.#")).Append(',')
              .Append(Csv.F(h.position.x)).Append(',').Append(Csv.F(h.position.y)).Append(',').Append(Csv.F(h.position.z)).Append(',')
              .Append(Csv.F(e.y, "0.#")).Append(',').Append(Csv.F(pitch, "0.#")).Append(',')
              .Append(Csv.F(state.HeadAngularSpeed, "0.#")).Append(',')
              .Append(Csv.F(state.PlayerSpeed)).Append(',')
              .Append(Csv.F(state.Health)).Append(',')
              .Append(state.Enemies.Count).Append(',')
              .Append(state.NearestEnemy != null ? Csv.F(state.NearestEnemyDist, "0.##") : "").Append(',')
              .Append(state.EnemiesInRange).Append(',')
              .Append(state.TotalKills).Append(',')
              .Append(state.TotalShots).Append(',')
              .Append(state.TotalHitsTaken).Append(',')
              .Append(Csv.F(state.Intensity)).Append(',')
              .Append(rig != null ? CameraTaxonomy.Code(rig.Angle) : "").Append(',')
              .Append(rig != null ? rig.Shot.ToString() : "").Append(',')
              .Append(Csv.F(cp.x)).Append(',').Append(Csv.F(cp.y)).Append(',').Append(Csv.F(cp.z)).Append(',')
              .Append(BroadcastHub.Instance != null && BroadcastHub.Instance.Server != null ? BroadcastHub.Instance.Server.StreamClients : 0);
            frames.WriteLine(sb.ToString());
        }

        public void Flush()
        {
            if (!Ok || closed) return;
            try
            {
                events.Flush(); frames.Flush(); ticks.Flush(); decisions.Flush(); ratings.Flush();
            }
            catch (Exception ex) { Debug.LogWarning("[Telemetry] flush failed: " + ex.Message); }
        }

        void OnDestroy()
        {
            if (closed) return;
            var hook = BeforeTeardownClose;
            BeforeTeardownClose = null;
            if (hook != null) hook();          // lets the session log SessionEnd first
            Close("scene unloaded");
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) Flush();   // Quest: headset taken off / app backgrounded
        }

        /// <summary>Writes summary.csv and closes all files. Safe to call twice.</summary>
        public void Close(string endReason)
        {
            if (closed) return;
            GameEventBus.OnEvent -= OnGameEvent;
            if (!Ok) { closed = true; return; }

            try
            {
                var m = metrics;
                var lines = new List<string>
                {
                    "metric,value",
                    "session_id," + SessionId,
                    "end_reason," + Csv.Escape(endReason),
                    "ended_local," + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    "duration_s," + Csv.F(GameEventBus.Now, "0.##"),
                    "events_logged," + EventCount,
                    "player_died," + (state.IsDead ? 1 : 0),
                    "final_health," + Csv.F(state.LastKnownHealth),
                    "kills," + state.TotalKills,
                    "shots," + state.TotalShots,
                    "enemy_hits," + state.TotalEnemyHits,
                    "accuracy," + Csv.F(state.Accuracy),
                    "hits_taken," + state.TotalHitsTaken,
                    "damage_taken," + Csv.F(state.TotalDamageTaken),
                    "max_multikill," + state.MaxMultiKill,
                    "wave_reached," + state.WaveIndex,
                };
                if (m != null)
                {
                    lines.Add("director_model," + Csv.Escape(modelName));
                    lines.Add("director_ticks," + m.Ticks);
                    lines.Add("camera_cuts," + m.Cuts);
                    lines.Add("cuts_per_minute," + Csv.F(m.CutsPerMinute(GameEventBus.Now), "0.##"));
                    lines.Add("avg_shot_length_s," + Csv.F(m.AvgShotLength, "0.##"));
                    lines.Add("time_fpv_pct," + Csv.F(m.SharePct(CameraAngle.FirstPerson), "0.#"));
                    lines.Add("time_tpv_pct," + Csv.F(m.SharePct(CameraAngle.ThirdPerson), "0.#"));
                    lines.Add("time_cine_pct," + Csv.F(m.SharePct(CameraAngle.Cinematic), "0.#"));
                    lines.Add("avg_confidence," + Csv.F(m.AvgConfidence));
                    lines.Add("event_cuts," + m.EventCuts);
                    lines.Add("avg_event_to_cut_latency_s," + Csv.F(m.AvgEventLatency));
                    lines.Add("rule_baseline_agreement," + Csv.F(m.RuleAgreement));
                    lines.Add("human_override_ticks," + m.HumanTicks);
                    lines.Add("human_agreement," + (m.HumanTicks > 0 ? Csv.F(m.HumanAgreement) : ""));
                    lines.Add("ratings_up," + m.RatingsUp);
                    lines.Add("ratings_down," + m.RatingsDown);
                    lines.Add("viewer_approval," + (m.RatingsUp + m.RatingsDown > 0 ? Csv.F(m.Approval) : ""));
                }
                File.WriteAllLines(Path.Combine(SessionFolder, "summary.csv"), lines.ToArray());
            }
            catch (Exception ex) { Debug.LogWarning("[Telemetry] summary failed: " + ex.Message); }

            foreach (var w in new[] { events, frames, ticks, decisions, ratings })
            {
                try { if (w != null) w.Dispose(); } catch { }
            }
            closed = true;
            Debug.Log("[Telemetry] Session saved: " + SessionFolder);
        }
    }
}
