using System;
using System.IO;
using UnityEngine;

namespace VGuns.Broadcast
{
    /// <summary>
    /// Feature vector the director model sees. Order MUST match FEATURES in
    /// Tools/DirectorTraining/train_director.py.
    /// </summary>
    public static class DirectorFeatures
    {
        public static readonly string[] Names =
        {
            "health_frac", "low_health", "enemies_alive", "nearest_enemy_dist", "enemies_in_range",
            "shots_2s", "hits_taken_3s", "kills_3s", "head_ang_speed", "player_speed",
            "time_in_shot", "cur_fpv", "cur_tpv", "cur_cine", "dead",
            "kill_recent", "hit_recent", "wave_recent", "reloading", "bullet_time"
        };

        public const int Count = 20;

        public static void Build(GameStateTracker s, CameraAngle current, float timeInShot, float[] f)
        {
            float now = s.Now;
            f[0] = s.HealthFrac;
            f[1] = s.HealthFrac < 0.35f ? 1f : 0f;
            f[2] = Mathf.Clamp01(s.Enemies.Count / 8f);
            f[3] = s.NearestEnemy != null ? Mathf.Clamp01(s.NearestEnemyDist / 25f) : 1f;
            f[4] = Mathf.Clamp01(s.EnemiesInRange / 4f);
            f[5] = Mathf.Clamp01(s.ShotsInLast(2f) / 6f);
            f[6] = Mathf.Clamp01(s.HitsInLast(3f) / 3f);
            f[7] = Mathf.Clamp01(s.KillsInLast(3f) / 3f);
            f[8] = Mathf.Clamp01(s.HeadAngularSpeed / 180f);
            f[9] = Mathf.Clamp01(s.PlayerSpeed / 3f);
            f[10] = Mathf.Clamp(timeInShot / 12f, 0f, 1.5f);
            f[11] = current == CameraAngle.FirstPerson ? 1f : 0f;
            f[12] = current == CameraAngle.ThirdPerson ? 1f : 0f;
            f[13] = current == CameraAngle.Cinematic ? 1f : 0f;
            f[14] = s.IsDead ? 1f : 0f;
            f[15] = now - s.LastKillTime <= 1.5f ? 1f : 0f;
            f[16] = now - s.LastHitTime <= 1.0f ? 1f : 0f;
            f[17] = now - s.LastWaveTime <= 3.0f ? 1f : 0f;
            f[18] = s.IsReloading ? 1f : 0f;
            f[19] = s.BulletTime ? 1f : 0f;
        }
    }

    [Serializable]
    public class PolicyData
    {
        public string name;
        public string version;
        public string trainedAt;
        public string[] features;
        public string[] classes;
        public int hidden;
        public float[] mean;
        public float[] std;
        public float[] W1;
        public float[] b1;
        public float[] W2;
        public float[] b2;
        public float valAccuracy;
        public int trainSamples;
        public int humanSamples;
    }

    /// <summary>
    /// Trained MLP policy (features -> tanh hidden -> softmax over FPV/TPV/CINE).
    /// Loads Resources/DirectorAI/director_policy.json, or an override file
    /// director_policy.json placed in the Telemetry root folder (lets you drop in a
    /// retrained model without rebuilding).
    /// </summary>
    public class DirectorPolicy
    {
        PolicyData data;
        float[] z = new float[DirectorFeatures.Count];
        float[] h;
        public bool Loaded { get; private set; }
        public string Source { get; private set; }
        public string Name { get { return Loaded ? data.name + " " + data.version : "Rule baseline (no model)"; } }
        public float ValAccuracy { get { return Loaded ? data.valAccuracy : 0f; } }
        public int TrainSamples { get { return Loaded ? data.trainSamples : 0; } }
        public int HumanSamples { get { return Loaded ? data.humanSamples : 0; } }

        public static DirectorPolicy Load(string telemetryRoot)
        {
            var p = new DirectorPolicy();
            try
            {
                string overridePath = string.IsNullOrEmpty(telemetryRoot) ? null : Path.Combine(telemetryRoot, "director_policy.json");
                if (overridePath != null && File.Exists(overridePath))
                {
                    p.TryParse(File.ReadAllText(overridePath), overridePath);
                }
                if (!p.Loaded)
                {
                    var ta = Resources.Load<TextAsset>("DirectorAI/director_policy");
                    if (ta != null) p.TryParse(ta.text, "Resources/DirectorAI/director_policy.json");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DirectorAI] Could not load policy: " + ex.Message);
            }
            if (!p.Loaded) Debug.LogWarning("[DirectorAI] No trained policy found - using rule baseline.");
            else Debug.Log("[DirectorAI] Loaded policy '" + p.Name + "' from " + p.Source);
            return p;
        }

        void TryParse(string json, string source)
        {
            var d = JsonUtility.FromJson<PolicyData>(json);
            int F = DirectorFeatures.Count;
            if (d == null || d.hidden <= 0 || d.mean == null || d.mean.Length != F || d.std == null || d.std.Length != F
                || d.W1 == null || d.W1.Length != d.hidden * F || d.b1 == null || d.b1.Length != d.hidden
                || d.W2 == null || d.W2.Length != 3 * d.hidden || d.b2 == null || d.b2.Length != 3)
            {
                Debug.LogWarning("[DirectorAI] Policy file " + source + " has the wrong shape - ignored.");
                return;
            }
            data = d;
            h = new float[d.hidden];
            Loaded = true;
            Source = source;
        }

        /// <summary>Writes P(FPV), P(TPV), P(CINE) into probs.</summary>
        public void Predict(float[] f, float[] probs)
        {
            if (!Loaded)
            {
                HeuristicDirector.Predict(f, probs);
                return;
            }
            int F = DirectorFeatures.Count, H = data.hidden;
            for (int i = 0; i < F; i++) z[i] = (f[i] - data.mean[i]) / data.std[i];
            for (int j = 0; j < H; j++)
            {
                float a = data.b1[j];
                int row = j * F;
                for (int i = 0; i < F; i++) a += data.W1[row + i] * z[i];
                h[j] = (float)Math.Tanh(a);
            }
            for (int c = 0; c < 3; c++)
            {
                float o = data.b2[c];
                int row = c * H;
                for (int j = 0; j < H; j++) o += data.W2[row + j] * h[j];
                probs[c] = o;
            }
            Softmax(probs, 1f);
        }

        public static void Softmax(float[] v, float temperature)
        {
            float m = Mathf.Max(v[0], Mathf.Max(v[1], v[2]));
            float sum = 0f;
            for (int i = 0; i < 3; i++) { v[i] = Mathf.Exp((v[i] - m) * temperature); sum += v[i]; }
            for (int i = 0; i < 3; i++) v[i] /= sum;
        }
    }

    /// <summary>
    /// Rule-based expert director. Mirrors teacher() in train_director.py.
    /// Used (1) to generate the synthetic training data, (2) as fallback when no
    /// model is available, (3) as the baseline the AI panel compares against.
    /// </summary>
    public static class HeuristicDirector
    {
        public static void Predict(float[] f, float[] p)
        {
            float low = f[1], enem = f[2], near = f[3], inrange = f[4], shots = f[5];
            float angs = f[8], speed = f[9], tin = f[10];
            float dead = f[14], killr = f[15], hitr = f[16], wave = f[17], reload = f[18], bt = f[19];

            if (dead > 0.5f) { p[0] = 0f; p[1] = 0.25f; p[2] = 0.75f; return; }

            p[0] = 1.2f; p[1] = 0.7f; p[2] = 0.4f;
            if (killr > 0.5f) { p[2] += 3.0f; p[1] += 0.4f; }
            if (wave > 0.5f) p[2] += 2.2f;
            if (bt > 0.5f) { p[2] += 2.4f; p[1] += 0.3f; }
            if (hitr > 0.5f) p[1] += 1.4f;
            if (low > 0.5f) p[1] += 0.8f;
            p[1] += 1.6f * inrange;
            p[0] += 1.5f * shots * (1f - Mathf.Min(angs, 1f));
            p[1] += 2.0f * Mathf.Max(0f, angs - 0.35f);
            if (shots > 0.01f) p[0] += 0.8f * (1f - near);
            if (enem < 0.01f && shots < 0.01f) p[2] += 1.8f;
            if (reload > 0.5f) p[1] += 0.6f;
            p[2] += 0.5f * speed * (enem < 0.01f ? 1f : 0f);

            int cur = f[11] >= f[12] && f[11] >= f[13] ? 0 : (f[12] >= f[13] ? 1 : 2);
            if (tin < 0.25f) p[cur] += 0.5f;
            p[cur] -= 4.0f * Mathf.Max(0f, tin - 0.5f);

            DirectorPolicy.Softmax(p, 1.6f);
        }
    }
}
