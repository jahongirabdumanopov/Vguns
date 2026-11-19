using System.Collections.Generic;
using UnityEngine;

namespace VGuns.Broadcast
{
    /// <summary>
    /// Keeps a live picture of the match for the director and telemetry:
    /// player/head references, enemies, rolling event windows and session counters.
    /// </summary>
    public class GameStateTracker : MonoBehaviour
    {
        public XRTarget Player { get; private set; }
        public Transform Head { get; private set; }
        public Camera HeadCamera { get; private set; }
        public Transform RigRoot { get; private set; }

        public float MaxHealth { get; private set; }
        public float Health { get { if (Player != null) LastKnownHealth = Player.health; return LastKnownHealth; } }
        public float LastKnownHealth { get; private set; }
        public float HealthFrac { get { return MaxHealth > 0 ? Mathf.Clamp01(Health / MaxHealth) : 0f; } }

        public bool IsDead { get; private set; }
        public float DeathTime { get; private set; }
        public Vector3 DeathPosition { get; private set; }
        public bool IsPaused { get; private set; }
        public int WaveIndex { get; private set; }

        public readonly List<EnemyAi> Enemies = new List<EnemyAi>();
        public EnemyAi NearestEnemy { get; private set; }
        public float NearestEnemyDist { get; private set; }
        public int EnemiesInRange { get; private set; }

        public float HeadAngularSpeed { get; private set; }   // deg/s, smoothed
        public float PlayerSpeed { get; private set; }        // m/s horizontal, smoothed

        public Vector3 LastKillPosition { get; private set; }
        public float LastKillTime { get; private set; }
        public Vector3 LastHitFrom { get; private set; }
        public float LastHitTime { get; private set; }
        public float LastWaveTime { get; private set; }
        public float LastShotTime { get; private set; }
        public float ReloadingUntil { get; private set; }

        // Session counters
        public int TotalShots { get; private set; }
        public int TotalEnemyHits { get; private set; }
        public int TotalKills { get; private set; }
        public int TotalHitsTaken { get; private set; }
        public float TotalDamageTaken { get; private set; }
        public int MaxMultiKill { get; private set; }
        public float Accuracy { get { return TotalShots > 0 ? (float)TotalEnemyHits / TotalShots : 0f; } }

        readonly Queue<float> shots = new Queue<float>();
        readonly Queue<float> hits = new Queue<float>();
        readonly Queue<float> kills = new Queue<float>();

        float nextEnemyRefresh;
        Quaternion lastHeadRot;
        Vector3 lastHeadPos;
        bool haveLastPose;
        bool lowHealthRaised;
        float zeroHealthSince = -1f;

        public float Now { get { return GameEventBus.Now; } }

        public void Init(XRTarget player)
        {
            Player = player;
            RigRoot = player.transform;
            MaxHealth = Mathf.Max(1f, player.health);
            LastKnownHealth = player.health;

            HeadCamera = null;
            foreach (var cam in player.GetComponentsInChildren<Camera>(true))
            {
                if (cam.CompareTag("MainCamera") || HeadCamera == null) HeadCamera = cam;
            }
            if (HeadCamera == null) HeadCamera = Camera.main;
            Head = HeadCamera != null ? HeadCamera.transform : player.transform;

            LastKillTime = LastHitTime = LastWaveTime = LastShotTime = -999f;
            ReloadingUntil = -1f;
            GameEventBus.OnEvent += OnGameEvent;
            RefreshEnemies();
        }

        void OnDestroy()
        {
            GameEventBus.OnEvent -= OnGameEvent;
        }

        void OnGameEvent(GameEvent e)
        {
            float t = e.SessionTime;
            switch (e.Type)
            {
                case GameEventType.PlayerShot:
                    TotalShots++; shots.Enqueue(t); LastShotTime = t;
                    break;
                case GameEventType.PlayerReload:
                    ReloadingUntil = t + Mathf.Max(0.5f, e.Value);
                    break;
                case GameEventType.EnemyHit:
                    TotalEnemyHits++;
                    break;
                case GameEventType.EnemyKilled:
                    TotalKills++; kills.Enqueue(t);
                    LastKillTime = t; LastKillPosition = e.Position;
                    Trim(kills, t, 3f);
                    if (kills.Count >= 2)
                    {
                        MaxMultiKill = Mathf.Max(MaxMultiKill, kills.Count);
                        GameEventBus.Raise(GameEventType.MultiKill, e.Position, "player", e.Target, kills.Count, kills.Count + " kills in 3s");
                    }
                    break;
                case GameEventType.PlayerHit:
                    TotalHitsTaken++; TotalDamageTaken += e.Value; hits.Enqueue(t);
                    LastHitTime = t; LastHitFrom = e.Position;
                    if (!lowHealthRaised && MaxHealth > 0 && Health / MaxHealth < 0.35f && Health > 0)
                    {
                        lowHealthRaised = true;
                        GameEventBus.Raise(GameEventType.PlayerLowHealth, Head.position, "player", "", Health, "health below 35%");
                    }
                    break;
                case GameEventType.PlayerDeath:
                    if (!IsDead)
                    {
                        IsDead = true; DeathTime = t;
                        DeathPosition = Head != null ? Head.position : e.Position;
                    }
                    break;
                case GameEventType.WaveStarted:
                    WaveIndex = Mathf.RoundToInt(e.Value) + 1; LastWaveTime = t;
                    break;
                case GameEventType.GamePaused: IsPaused = true; break;
                case GameEventType.GameResumed: IsPaused = false; break;
            }
        }

        static void Trim(Queue<float> q, float now, float window)
        {
            while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
        }

        public int ShotsInLast(float window) { Trim(shots, Now, 2f); return CountSince(shots, window); }
        public int HitsInLast(float window) { Trim(hits, Now, 3f); return CountSince(hits, window); }
        public int KillsInLast(float window) { Trim(kills, Now, 3f); return CountSince(kills, window); }

        int CountSince(Queue<float> q, float window)
        {
            float now = Now; int n = 0;
            foreach (var t in q) if (now - t <= window) n++;
            return n;
        }

        public bool IsReloading { get { return Now < ReloadingUntil; } }
        public bool BulletTime { get { return !IsPaused && Time.timeScale > 0.001f && Time.timeScale < 0.9f; } }

        /// <summary>0..1 "how much is going on" score for the panel and stream overlay.</summary>
        public float Intensity
        {
            get
            {
                float v = 0.25f * Mathf.Clamp01(Enemies.Count / 6f)
                        + 0.25f * Mathf.Clamp01(ShotsInLast(2f) / 6f)
                        + 0.3f * Mathf.Clamp01(HitsInLast(3f) / 2f)
                        + 0.2f * Mathf.Clamp01(KillsInLast(3f) / 2f);
                if (NearestEnemy != null) v += 0.2f * (1f - Mathf.Clamp01(NearestEnemyDist / 15f));
                return Mathf.Clamp01(v);
            }
        }

        public Vector3 FlatForward
        {
            get
            {
                Vector3 f = Head != null ? Head.forward : Vector3.forward;
                f.y = 0f;
                if (f.sqrMagnitude < 1e-4f) f = RigRoot != null ? RigRoot.forward : Vector3.forward;
                f.y = 0f;
                return f.sqrMagnitude < 1e-4f ? Vector3.forward : f.normalized;
            }
        }

        void Update()
        {
            if (Player == null || Head == null) return;

            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            if (haveLastPose)
            {
                float ang = Quaternion.Angle(lastHeadRot, Head.rotation) / dt;
                Vector3 d = Head.position - lastHeadPos; d.y = 0f;
                float spd = d.magnitude / dt;
                float k = 1f - Mathf.Exp(-6f * dt);
                HeadAngularSpeed = Mathf.Lerp(HeadAngularSpeed, Mathf.Min(ang, 720f), k);
                PlayerSpeed = Mathf.Lerp(PlayerSpeed, Mathf.Min(spd, 10f), k);
            }
            lastHeadRot = Head.rotation; lastHeadPos = Head.position; haveLastPose = true;

            // Fallback death detection in case the XRTarget hook is missing
            // (waits a moment so the normal hook gets to fire first).
            if (!IsDead && Player.health <= 0f)
            {
                if (zeroHealthSince < 0f) zeroHealthSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - zeroHealthSince > 0.5f)
                    GameEventBus.Raise(GameEventType.PlayerDeath, Head.position, "player", "", 0f, "health reached 0 (fallback)");
            }
            else zeroHealthSince = -1f;

            if (Time.unscaledTime >= nextEnemyRefresh)
            {
                nextEnemyRefresh = Time.unscaledTime + 0.5f;
                RefreshEnemies();
            }
            UpdateNearest();
        }

        void RefreshEnemies()
        {
            Enemies.Clear();
            foreach (var e in FindObjectsOfType<EnemyAi>())
            {
                // Note: EnemyAi respawns at its start point without resetting health,
                // so "alive" means active in the scene, not health > 0.
                if (e != null && e.isActiveAndEnabled) Enemies.Add(e);
            }
        }

        void UpdateNearest()
        {
            NearestEnemy = null;
            NearestEnemyDist = float.PositiveInfinity;
            EnemiesInRange = 0;
            Vector3 p = Head.position;
            for (int i = Enemies.Count - 1; i >= 0; i--)
            {
                var e = Enemies[i];
                if (e == null || !e.isActiveAndEnabled) { Enemies.RemoveAt(i); continue; }
                float d = Vector3.Distance(p, e.transform.position);
                if (d < NearestEnemyDist) { NearestEnemyDist = d; NearestEnemy = e; }
                if (d <= Mathf.Max(e.attackRange, 1f) + 0.5f) EnemiesInRange++;
            }
        }
    }
}
