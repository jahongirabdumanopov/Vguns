using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VGuns.Broadcast
{
    public enum DecisionSource { AI, Viewer, Rule }

    public class DirectorDecision
    {
        public float Time;
        public ShotKind FromShot;
        public ShotKind ToShot;
        public DecisionSource Source;
        public string Trigger;
        public float Confidence;
        public float[] Probs = new float[3];
        public float HeldSeconds;
        public string Reason;
    }

    public class DirectorMetrics
    {
        public int Ticks, Cuts, EventCuts;
        public int RuleAgree;
        public int HumanTicks, HumanAgree;
        public int RatingsUp, RatingsDown;
        public float ConfidenceSum;
        public float LatencySum;
        public readonly float[] AngleTime = new float[3];
        public readonly List<float> ShotLengths = new List<float>();

        public float AvgConfidence { get { return Ticks > 0 ? ConfidenceSum / Ticks : 0f; } }
        public float RuleAgreement { get { return Ticks > 0 ? (float)RuleAgree / Ticks : 0f; } }
        public float HumanAgreement { get { return HumanTicks > 0 ? (float)HumanAgree / HumanTicks : 0f; } }
        public float Approval { get { int n = RatingsUp + RatingsDown; return n > 0 ? (float)RatingsUp / n : 0f; } }
        public float AvgEventLatency { get { return EventCuts > 0 ? LatencySum / EventCuts : 0f; } }

        public float AvgShotLength
        {
            get
            {
                if (ShotLengths.Count == 0) return 0f;
                float s = 0f; foreach (var l in ShotLengths) s += l; return s / ShotLengths.Count;
            }
        }

        public float CutsPerMinute(float sessionSeconds)
        {
            return sessionSeconds > 1f ? Cuts / (sessionSeconds / 60f) : 0f;
        }

        public float SharePct(CameraAngle a)
        {
            float total = AngleTime[0] + AngleTime[1] + AngleTime[2];
            return total > 0f ? 100f * AngleTime[(int)a] / total : 0f;
        }
    }

    /// <summary>
    /// AI camera director. Every tick it builds the feature vector, asks the trained
    /// policy for P(FPV/TPV/CINE), and applies broadcast "shot grammar":
    ///   - minimum hold time (no flicker), shorter when an important event fires
    ///   - hysteresis margin before switching
    ///   - maximum hold time (keep it interesting)
    ///   - player death: FPV is not allowed and the chosen shot is held
    /// Then it picks the concrete shot inside the angle (kill cam, death cam, wide...).
    /// A viewer can override from the web page; the AI keeps predicting and the
    /// human choice is logged as a training label.
    /// </summary>
    public class DirectorAI : MonoBehaviour
    {
        // Tunables
        public float TickInterval = 0.2f;
        public float MinHold = 2.2f;
        public float EventMinHold = 0.6f;
        public float MaxHoldFallback = 12f;
        public float SwitchMargin = 0.12f;
        public float KillCamCooldown = 5f;

        public DirectorPolicy Policy { get; private set; }
        public DirectorMetrics Metrics { get; private set; }
        public CameraAngle CurrentAngle { get { return rig.Angle; } }
        public ShotKind CurrentShot { get { return rig.Shot; } }
        public readonly float[] Probs = new float[3];
        public readonly float[] RuleProbs = new float[3];
        public CameraAngle AiChoice { get; private set; }
        public bool HasOverride { get; private set; }
        public CameraAngle OverrideAngle { get; private set; }
        public DirectorDecision LastDecision { get; private set; }
        public readonly List<DirectorDecision> RecentDecisions = new List<DirectorDecision>();
        public string ActiveTrigger { get { return pendingTrigger.HasValue ? pendingTrigger.Value.ToString() : ""; } }
        public float TimeInShot { get { return GameEventBus.Now - rig.ShotStartTime; } }

        GameStateTracker state;
        SpectatorRig rig;
        TelemetryRecorder telemetry;

        readonly float[] features = new float[DirectorFeatures.Count];
        float nextTick;
        GameEventType? pendingTrigger;
        float pendingTriggerTime;
        float lastKillCamTime = -999f;
        bool deathHandled;
        int cineVariety;

        public void Init(GameStateTracker s, SpectatorRig r, TelemetryRecorder t)
        {
            state = s; rig = r; telemetry = t;
            Metrics = new DirectorMetrics();
            Policy = DirectorPolicy.Load(TelemetryPaths.Root);
            GameEventBus.OnEvent += OnGameEvent;
            AiChoice = CameraAngle.FirstPerson;
        }

        void OnDestroy()
        {
            GameEventBus.OnEvent -= OnGameEvent;
        }

        static int Priority(GameEventType t)
        {
            switch (t)
            {
                case GameEventType.PlayerDeath: return 5;
                case GameEventType.MultiKill: return 4;
                case GameEventType.EnemyKilled: return 4;
                case GameEventType.WaveStarted: return 3;
                case GameEventType.BulletTimeStarted: return 3;
                case GameEventType.PlayerLowHealth: return 2;
                case GameEventType.PlayerHit: return 2;
                default: return 0;
            }
        }

        void OnGameEvent(GameEvent e)
        {
            int p = Priority(e.Type);
            if (p == 0) return;
            if (!pendingTrigger.HasValue || p >= Priority(pendingTrigger.Value))
            {
                pendingTrigger = e.Type;
                pendingTriggerTime = e.SessionTime;
            }
            // React to death immediately rather than waiting for the next tick.
            if (e.Type == GameEventType.PlayerDeath) nextTick = 0f;
        }

        public void SetOverride(CameraAngle? angle)
        {
            HasOverride = angle.HasValue;
            if (angle.HasValue)
            {
                OverrideAngle = angle.Value;
                GameEventBus.Raise(GameEventType.ViewerOverride, state.Head.position, "viewer", "", (int)angle.Value, CameraTaxonomy.Code(angle.Value));
            }
            else
            {
                GameEventBus.Raise(GameEventType.ViewerOverride, state.Head.position, "viewer", "", -1, "AUTO");
            }
            nextTick = 0f;
        }

        public void Rate(int rating, string source)
        {
            if (rating > 0) Metrics.RatingsUp++; else Metrics.RatingsDown++;
            float now = GameEventBus.Now;
            telemetry.LogRating(now, rating > 0 ? 1 : -1, source, TimeInShot, LastDecision != null ? LastDecision.Reason : "");
            GameEventBus.Raise(GameEventType.ViewerRating, state.Head.position, source, rig.Shot.ToString(), rating > 0 ? 1 : -1, "");
        }

        void Update()
        {
            if (state == null || rig == null) return;

            float now = GameEventBus.Now;
            Metrics.AngleTime[(int)rig.Angle] += Time.unscaledDeltaTime;

            if (pendingTrigger.HasValue && now - pendingTriggerTime > 2f) pendingTrigger = null;

            if (state.IsPaused) return;
            if (Time.realtimeSinceStartup < nextTick) return;
            nextTick = Time.realtimeSinceStartup + TickInterval;
            Tick(now);
        }

        void Tick(float now)
        {
            Metrics.Ticks++;
            CameraAngle current = rig.Angle;
            float held = now - rig.ShotStartTime;

            DirectorFeatures.Build(state, current, held, features);
            Policy.Predict(features, Probs);
            HeuristicDirector.Predict(features, RuleProbs);

            if (state.IsDead)
            {
                // FPV of a dead player is meaningless for viewers.
                Probs[0] = 0f;
                float s = Probs[1] + Probs[2];
                if (s > 1e-5f) { Probs[1] /= s; Probs[2] /= s; } else { Probs[2] = 1f; }
            }

            int best = ArgMax(Probs);
            AiChoice = (CameraAngle)best;
            Metrics.ConfidenceSum += Probs[best];
            if (best == ArgMax(RuleProbs)) Metrics.RuleAgree++;

            string trigger = pendingTrigger.HasValue ? pendingTrigger.Value.ToString() : "";
            bool eventTrigger = pendingTrigger.HasValue;
            var spec = CameraTaxonomy.Get(rig.Shot);
            float maxHold = Mathf.Min(spec.MaxHold, MaxHoldFallback);
            if (state.IsDead && deathHandled) maxHold = float.PositiveInfinity;

            string humanLabel = "";
            CameraAngle final = current;

            if (HasOverride)
            {
                humanLabel = CameraTaxonomy.Code(OverrideAngle);
                Metrics.HumanTicks++;
                if (AiChoice == OverrideAngle) Metrics.HumanAgree++;
                final = OverrideAngle;
                if (current != OverrideAngle)
                {
                    Cut(OverrideAngle, trigger, DecisionSource.Viewer, "Viewer override -> " + humanLabel, held, false);
                }
                else if (eventTrigger && OverrideAngle == CameraAngle.Cinematic && held >= EventMinHold)
                {
                    // Stay cinematic but let events pick the cinematic sub-shot.
                    ShotKind wanted = SelectShot(CameraAngle.Cinematic, pendingTrigger);
                    if (wanted != rig.Shot) Cut(CameraAngle.Cinematic, trigger, DecisionSource.Viewer, "Viewer CINE, event " + trigger, held, true);
                }
                pendingTrigger = null;
            }
            else if (state.IsDead && !deathHandled)
            {
                deathHandled = true;
                final = AiChoice;
                Cut(AiChoice, "PlayerDeath", DecisionSource.AI, Explain(AiChoice, "PlayerDeath"), held, true);
            }
            else
            {
                bool canCut = held >= (eventTrigger ? EventMinHold : Mathf.Max(MinHold, spec.MinHold));
                if (state.IsDead) canCut = false;

                if (canCut && best != (int)current && (eventTrigger || Probs[best] - Probs[(int)current] > SwitchMargin))
                {
                    final = AiChoice;
                    Cut(AiChoice, trigger, DecisionSource.AI, Explain(AiChoice, trigger), held, eventTrigger);
                }
                else if (canCut && eventTrigger && best == (int)current && current != CameraAngle.FirstPerson)
                {
                    // Same angle but the event deserves a different framing (e.g. kill cam while orbiting).
                    ShotKind wanted = SelectShot(current, pendingTrigger);
                    if (wanted != rig.Shot)
                        Cut(current, trigger, DecisionSource.AI, Explain(current, trigger), held, true);
                    pendingTrigger = null;
                }
                else if (!state.IsDead && held > maxHold)
                {
                    // Shot fatigue: take the best alternative.
                    int alt = -1;
                    for (int i = 0; i < 3; i++)
                        if (i != (int)current && (alt < 0 || Probs[i] > Probs[alt])) alt = i;
                    final = (CameraAngle)alt;
                    Cut(final, "MaxHold", DecisionSource.Rule, "Held " + held.ToString("0.0") + "s (max " + maxHold.ToString("0") + "s) -> " + CameraTaxonomy.Code(final), held, false);
                }

                if (eventTrigger && canCut) pendingTrigger = null;
            }

            telemetry.LogTick(now, features, Probs, RuleProbs, AiChoice, rig.Angle, humanLabel, HasOverride, trigger);
        }

        void Cut(CameraAngle angle, string trigger, DecisionSource source, string reason, float held, bool fromEvent)
        {
            GameEventType? trig = null;
            if (!string.IsNullOrEmpty(trigger) && System.Enum.IsDefined(typeof(GameEventType), trigger))
                trig = (GameEventType)System.Enum.Parse(typeof(GameEventType), trigger);

            ShotKind from = rig.Shot;
            ShotKind to = SelectShot(angle, trig);
            if (to == ShotKind.CINE_KillCam) lastKillCamTime = GameEventBus.Now;

            var d = new DirectorDecision
            {
                Time = GameEventBus.Now,
                FromShot = from,
                ToShot = to,
                Source = source,
                Trigger = string.IsNullOrEmpty(trigger) ? "-" : trigger,
                Confidence = Probs[(int)angle],
                HeldSeconds = held,
                Reason = reason + " | " + CameraTaxonomy.ShotName(to)
            };
            System.Array.Copy(Probs, d.Probs, 3);

            Metrics.Cuts++;
            Metrics.ShotLengths.Add(held);
            if (fromEvent && trig.HasValue)
            {
                Metrics.EventCuts++;
                Metrics.LatencySum += Mathf.Max(0f, GameEventBus.Now - pendingTriggerTime);
            }

            rig.SetShot(to);
            LastDecision = d;
            RecentDecisions.Add(d);
            if (RecentDecisions.Count > 12) RecentDecisions.RemoveAt(0);
            telemetry.LogDecision(d);
            GameEventBus.Raise(GameEventType.CameraCut, rig.Cam.transform.position, source.ToString(), to.ToString(), d.Confidence,
                CameraTaxonomy.Code(CameraTaxonomy.AngleOf(from)) + "->" + CameraTaxonomy.Code(angle));
        }

        /// <summary>Choose the concrete framing inside an angle.</summary>
        ShotKind SelectShot(CameraAngle angle, GameEventType? trigger)
        {
            switch (angle)
            {
                case CameraAngle.FirstPerson:
                    return ShotKind.FPV_Stabilized;

                case CameraAngle.ThirdPerson:
                    if (state.IsDead) return ShotKind.TPV_OverShoulder;
                    if (state.NearestEnemy != null && (state.NearestEnemyDist < 12f || trigger == GameEventType.PlayerHit))
                        return ShotKind.TPV_ThreatFocus;
                    return ShotKind.TPV_OverShoulder;

                default:
                    if (state.IsDead) return ShotKind.CINE_DeathCam;
                    if ((trigger == GameEventType.EnemyKilled || trigger == GameEventType.MultiKill)
                        && GameEventBus.Now - lastKillCamTime > KillCamCooldown)
                        return ShotKind.CINE_KillCam;
                    if (trigger == GameEventType.WaveStarted) return ShotKind.CINE_WideEstablishing;
                    if (trigger == GameEventType.BulletTimeStarted) return ShotKind.CINE_LowHero;
                    if (state.Enemies.Count >= 3) return ShotKind.CINE_WideEstablishing;
                    if (state.Enemies.Count == 0) return ShotKind.CINE_Orbit;
                    cineVariety++;
                    return cineVariety % 2 == 0 ? ShotKind.CINE_LowHero : ShotKind.CINE_Orbit;
            }
        }

        /// <summary>Human-readable reason built from the situation the model saw.</summary>
        string Explain(CameraAngle chosen, string trigger)
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(trigger) ? "Situation" : trigger)
              .Append(" -> ").Append(CameraTaxonomy.Code(chosen))
              .Append(" (p=").Append(Probs[(int)chosen].ToString("0.00")).Append(")");

            var why = new List<string>();
            if (features[14] > 0.5f) why.Add("player dead");
            if (features[15] > 0.5f) why.Add("enemy just killed");
            if (features[16] > 0.5f) why.Add("player just hit");
            if (features[17] > 0.5f) why.Add("wave started");
            if (features[19] > 0.5f) why.Add("bullet time");
            if (features[1] > 0.5f) why.Add("low health");
            if (features[4] > 0.2f) why.Add(state.EnemiesInRange + " enemy in range");
            if (features[8] > 0.4f) why.Add("fast head motion");
            if (features[5] > 0.3f && features[8] < 0.3f) why.Add("steady aiming");
            if (features[2] < 0.01f && features[5] < 0.01f) why.Add("downtime");
            if (features[18] > 0.5f) why.Add("reloading");
            if (features[10] > 0.5f) why.Add("shot getting long");
            if (why.Count > 0) sb.Append(": ").Append(string.Join(", ", why.ToArray()));
            return sb.ToString();
        }

        static int ArgMax(float[] p)
        {
            int b = 0;
            for (int i = 1; i < p.Length; i++) if (p[i] > p[b]) b = i;
            return b;
        }
    }
}
