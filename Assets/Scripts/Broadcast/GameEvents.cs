using System;
using UnityEngine;

namespace VGuns.Broadcast
{
    /// <summary>Every gameplay event the telemetry logs and the AI director can react to.</summary>
    public enum GameEventType
    {
        SessionStart,
        SessionEnd,
        PlayerShot,
        PlayerReload,
        PlayerHit,
        PlayerLowHealth,
        PlayerDeath,
        EnemySpawned,
        EnemyAttack,
        EnemyHit,
        EnemyKilled,
        MultiKill,
        TargetHit,
        WaveStarted,
        WavesCompleted,
        BulletTimeStarted,
        SpeedUpStarted,
        GamePaused,
        GameResumed,
        CameraCut,
        ViewerOverride,
        ViewerRating
    }

    public struct GameEvent
    {
        public GameEventType Type;
        /// <summary>Seconds since the session started (real time, unaffected by slow-mo/pause).</summary>
        public float SessionTime;
        public int Frame;
        public Vector3 Position;
        public string Actor;
        public string Target;
        public float Value;
        public string Detail;
    }

    /// <summary>
    /// Static event bus. Game scripts call GameEventBus.Raise(...) (one line each);
    /// telemetry, the director AI and the web stream subscribe to OnEvent.
    /// Safe to call when no session is running (events are simply dropped).
    /// </summary>
    public static class GameEventBus
    {
        public static event Action<GameEvent> OnEvent;

        /// <summary>Realtime timestamp the current session started at.</summary>
        public static float SessionStartRealtime;

        public static float Now
        {
            get { return Time.realtimeSinceStartup - SessionStartRealtime; }
        }

        public static void Raise(GameEventType type, Vector3 position, string actor = "", string target = "", float value = 0f, string detail = "")
        {
            var handlers = OnEvent;
            if (handlers == null) return;

            var e = new GameEvent
            {
                Type = type,
                SessionTime = Now,
                Frame = Time.frameCount,
                Position = position,
                Actor = actor ?? "",
                Target = target ?? "",
                Value = value,
                Detail = detail ?? ""
            };

            // Never let a broken subscriber break gameplay code that raised the event.
            foreach (Action<GameEvent> h in handlers.GetInvocationList())
            {
                try { h(e); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        public static void Raise(GameEventType type, string detail = "", float value = 0f)
        {
            Raise(type, Vector3.zero, "", "", value, detail);
        }
    }
}
