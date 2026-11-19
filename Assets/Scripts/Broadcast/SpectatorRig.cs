using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace VGuns.Broadcast
{
    /// <summary>
    /// The broadcast (spectator) camera. It is separate from the headset camera,
    /// renders into a RenderTexture, frames the shot the director picks, and
    /// hands JPEG frames to the web stream.
    /// Also spawns a simple stand-in body for the player that only the spectator
    /// camera can see (so third-person and cinematic shots show where the player is).
    /// </summary>
    public class SpectatorRig : MonoBehaviour
    {
        public Camera Cam { get; private set; }
        public RenderTexture Target { get; private set; }
        public ShotKind Shot { get; private set; }
        public CameraAngle Angle { get { return CameraTaxonomy.AngleOf(Shot); } }
        public float ShotStartTime { get; private set; }
        public float MeasuredStreamFps { get; private set; }

        /// <summary>Render even with no stream viewers (e.g. the in-headset panel preview is visible).</summary>
        public bool ForceRender;

        GameStateTracker state;
        BroadcastServer server;
        int spectatorOnlyLayer, hudLayer;
        int envMask;

        GameObject mannequin;
        Transform body, headBall;

        // Shot context
        float orbitAngle;
        Vector3 deathStartPos;
        Quaternion deathStartRot;
        Vector3 velocity;
        bool snapNext;

        // Capture
        float nextRender;
        bool readbackPending;
        readonly Stack<byte[]> bufferPool = new Stack<byte[]>();
        readonly object poolLock = new object();
        int encodedFrames;
        float fpsWindowStart;

        public void Init(GameStateTracker tracker, BroadcastServer srv)
        {
            state = tracker;
            server = srv;

            spectatorOnlyLayer = BroadcastConfig.LayerOrDefault(BroadcastConfig.SpectatorOnlyLayerName, 14);
            hudLayer = BroadcastConfig.LayerOrDefault(BroadcastConfig.PlayerHudLayerName, 15);

            // Collision mask for camera placement: world geometry only.
            int ignore = (1 << spectatorOnlyLayer) | (1 << hudLayer);
            foreach (var n in new[] { "Ignore Raycast", "UI", "Interactable", "XR Rig", "Enemy", "Player", "Bullets", "Menu" })
            {
                int l = LayerMask.NameToLayer(n);
                if (l >= 0) ignore |= 1 << l;
            }
            envMask = ~ignore;

            // Headset camera must not see the stand-in body.
            if (state.HeadCamera != null) state.HeadCamera.cullingMask &= ~(1 << spectatorOnlyLayer);

            Target = new RenderTexture(BroadcastConfig.StreamWidth, BroadcastConfig.StreamHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Target.name = "SpectatorRT";
            Target.antiAliasing = BroadcastConfig.StreamAntiAliasing;
            Target.Create();

            var camGo = new GameObject("Spectator Camera (AI Director)");
            camGo.transform.SetParent(transform, false);
            Cam = camGo.AddComponent<Camera>();
            Cam.enabled = false;                         // rendered manually at stream fps
            Cam.stereoTargetEye = StereoTargetEyeMask.None;
            Cam.targetTexture = Target;
            Cam.nearClipPlane = 0.05f;
            Cam.farClipPlane = 300f;
            Cam.depth = -50;
            if (state.HeadCamera != null)
            {
                Cam.clearFlags = state.HeadCamera.clearFlags;
                Cam.backgroundColor = state.HeadCamera.backgroundColor;
                Cam.cullingMask = state.HeadCamera.cullingMask | (1 << spectatorOnlyLayer);
            }
            Cam.cullingMask &= ~(1 << hudLayer);

            BuildMannequin();

            if (state.Head != null)
            {
                Cam.transform.SetPositionAndRotation(state.Head.position, state.Head.rotation);
            }
            SetShot(ShotKind.FPV_Stabilized);
            fpsWindowStart = Time.realtimeSinceStartup;
        }

        void BuildMannequin()
        {
            mannequin = new GameObject("Player Stand-in (spectator only)");
            mannequin.transform.SetParent(transform, false);

            var color = new Color(0.1f, 0.75f, 0.85f);

            var b = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            b.name = "Body";
            Destroy(b.GetComponent<Collider>());
            b.transform.SetParent(mannequin.transform, false);
            b.transform.localScale = new Vector3(0.42f, 0.55f, 0.32f);
            body = b.transform;

            var hball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hball.name = "Head";
            Destroy(hball.GetComponent<Collider>());
            hball.transform.SetParent(mannequin.transform, false);
            hball.transform.localScale = Vector3.one * 0.24f;
            headBall = hball.transform;

            var visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visor.name = "Visor";
            Destroy(visor.GetComponent<Collider>());
            visor.transform.SetParent(headBall, false);
            visor.transform.localPosition = new Vector3(0f, 0.05f, 0.42f);
            visor.transform.localScale = new Vector3(0.85f, 0.35f, 0.3f);

            foreach (var r in mannequin.GetComponentsInChildren<Renderer>())
            {
                r.material.color = r.gameObject.name == "Visor" ? new Color(0.05f, 0.05f, 0.08f) : color;
                r.shadowCastingMode = ShadowCastingMode.On;
            }
            SetLayerRecursive(mannequin, spectatorOnlyLayer);
        }

        static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform t in go.transform) SetLayerRecursive(t.gameObject, layer);
        }

        // ------------------------------------------------------------------
        // Shot control
        // ------------------------------------------------------------------
        public void SetShot(ShotKind shot)
        {
            Shot = shot;
            ShotStartTime = GameEventBus.Now;
            snapNext = true;
            velocity = Vector3.zero;
            var spec = CameraTaxonomy.Get(shot);
            if (Cam != null)
            {
                Cam.fieldOfView = spec.FieldOfView;
                deathStartPos = Cam.transform.position;
                deathStartRot = Cam.transform.rotation;
            }
            if (state != null && state.Head != null)
            {
                Vector3 toCam = Cam != null ? Cam.transform.position - state.Head.position : -state.FlatForward;
                orbitAngle = Mathf.Atan2(toCam.x, toCam.z) * Mathf.Rad2Deg;
                if (shot == ShotKind.CINE_DeathCam)
                {
                    deathStartPos = state.Head.position;
                    deathStartRot = state.Head.rotation;
                }
            }
        }

        void LateUpdate()
        {
            if (state == null || state.Head == null || Cam == null) return;

            UpdateMannequin();
            UpdateShot(Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f));

            bool viewers = server != null && server.StreamClients > 0;
            if (!viewers && !ForceRender) return;

            float now = Time.realtimeSinceStartup;
            if (now < nextRender) return;
            nextRender = now + 1f / Mathf.Max(1, BroadcastConfig.StreamFps);

            Cam.Render();
            if (viewers) Capture();
        }

        void UpdateMannequin()
        {
            bool show = Angle != CameraAngle.FirstPerson;
            if (mannequin.activeSelf != show) mannequin.SetActive(show);
            if (!show) return;

            Vector3 hp = state.Head.position;
            Vector3 fwd = state.FlatForward;
            Quaternion yaw = Quaternion.LookRotation(fwd, Vector3.up);
            headBall.SetPositionAndRotation(hp, state.Head.rotation);
            float bodyTop = hp.y - 0.2f;
            body.SetPositionAndRotation(new Vector3(hp.x, bodyTop - 0.55f, hp.z) - fwd * 0.05f, yaw);
            if (state.IsDead)
            {
                // fall over
                float t = Mathf.Clamp01((GameEventBus.Now - state.DeathTime) / 0.8f);
                Vector3 pivot = new Vector3(hp.x, hp.y - 1.6f, hp.z);
                body.RotateAround(pivot, yaw * Vector3.right, 80f * t);
                headBall.RotateAround(pivot, yaw * Vector3.right, 80f * t);
            }
        }

        void UpdateShot(float dt)
        {
            Transform cam = Cam.transform;
            Vector3 head = state.IsDead ? state.DeathPosition : state.Head.position;
            Vector3 fwd = state.FlatForward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 desiredPos;
            Quaternion desiredRot;
            float posSmooth = 0.15f;
            float rotSharpness = 10f;

            switch (Shot)
            {
                case ShotKind.FPV_Stabilized:
                {
                    desiredPos = state.Head.position;
                    Vector3 look = state.Head.forward;
                    desiredRot = Quaternion.LookRotation(look, Vector3.up);   // removes head roll
                    posSmooth = 0.02f;
                    rotSharpness = 12f;
                    break;
                }
                case ShotKind.TPV_OverShoulder:
                {
                    Vector3 pivot = head + Vector3.up * 0.15f;
                    Vector3 want = pivot - fwd * 2.4f + Vector3.up * 0.45f + right * 0.45f;
                    desiredPos = Resolve(pivot, want);
                    Vector3 lookAt = head + fwd * 4f - Vector3.up * 0.2f;
                    desiredRot = Quaternion.LookRotation(lookAt - desiredPos, Vector3.up);
                    break;
                }
                case ShotKind.TPV_ThreatFocus:
                {
                    Vector3 pivot = head + Vector3.up * 0.15f;
                    Vector3 dir = fwd;
                    Vector3 enemy = head + fwd * 6f;
                    if (state.NearestEnemy != null)
                    {
                        enemy = state.NearestEnemy.transform.position + Vector3.up * 0.8f;
                        Vector3 d = enemy - head; d.y = 0f;
                        if (d.sqrMagnitude > 0.01f) dir = d.normalized;
                    }
                    Vector3 side = Vector3.Cross(Vector3.up, dir).normalized;
                    Vector3 want = pivot - dir * 2.6f + Vector3.up * 0.7f + side * 0.7f;
                    desiredPos = Resolve(pivot, want);
                    Vector3 lookAt = Vector3.Lerp(head, enemy, 0.45f);
                    desiredRot = Quaternion.LookRotation(lookAt - desiredPos, Vector3.up);
                    rotSharpness = 6f;
                    break;
                }
                case ShotKind.CINE_Orbit:
                {
                    orbitAngle += 14f * dt;
                    Vector3 off = Quaternion.Euler(0f, orbitAngle, 0f) * Vector3.forward * 3.6f;
                    Vector3 pivot = head - Vector3.up * 0.2f;
                    desiredPos = Resolve(pivot, pivot + off + Vector3.up * 1.1f);
                    desiredRot = Quaternion.LookRotation(pivot - desiredPos, Vector3.up);
                    posSmooth = 0.3f;
                    rotSharpness = 5f;
                    break;
                }
                case ShotKind.CINE_WideEstablishing:
                {
                    Vector3 c = head; int n = 1; float spread = 0f;
                    foreach (var e in state.Enemies)
                    {
                        if (e == null) continue;
                        Vector3 ep = e.transform.position;
                        if ((ep - head).sqrMagnitude > 25f * 25f) continue;
                        c += ep; n++;
                    }
                    c /= n;
                    foreach (var e in state.Enemies)
                        if (e != null) spread = Mathf.Max(spread, Vector3.Distance(c, e.transform.position));
                    spread = Mathf.Max(spread, Vector3.Distance(c, head));
                    float t = GameEventBus.Now - ShotStartTime;
                    float dist = Mathf.Max(5f, spread * 1.5f) * (1f - Mathf.Min(0.25f, t * 0.03f));   // slow push-in
                    Vector3 want = c - fwd * dist * 0.75f + Vector3.up * dist * 0.65f;
                    desiredPos = Resolve(c + Vector3.up * 1f, want);
                    desiredRot = Quaternion.LookRotation(c - desiredPos, Vector3.up);
                    posSmooth = 0.4f;
                    rotSharpness = 4f;
                    break;
                }
                case ShotKind.CINE_KillCam:
                {
                    Vector3 victim = state.LastKillPosition + Vector3.up * 0.9f;
                    Vector3 d = victim - head; d.y = 0f;
                    float len = Mathf.Max(1f, d.magnitude);
                    Vector3 dir = d.sqrMagnitude > 0.01f ? d.normalized : fwd;
                    Vector3 side = Vector3.Cross(Vector3.up, dir).normalized;
                    Vector3 mid = Vector3.Lerp(head, victim, 0.55f);
                    float t = GameEventBus.Now - ShotStartTime;
                    float sideDist = Mathf.Max(2.2f, len * 0.6f) * (1f - Mathf.Min(0.3f, t * 0.12f));  // dolly in
                    Vector3 want = mid + side * sideDist - Vector3.up * 0.5f;
                    desiredPos = Resolve(mid, want);
                    desiredRot = Quaternion.LookRotation(Vector3.Lerp(head, victim, 0.6f) - desiredPos, Vector3.up);
                    posSmooth = 0.25f;
                    rotSharpness = 6f;
                    break;
                }
                case ShotKind.CINE_DeathCam:
                {
                    float t = Mathf.Clamp01((GameEventBus.Now - ShotStartTime) / 3.5f);
                    float e = t * t * (3f - 2f * t);
                    Vector3 ground = head - Vector3.up * 1.3f;
                    orbitAngle += 10f * dt;
                    Vector3 orbit = Quaternion.Euler(0f, orbitAngle, 0f) * Vector3.forward * 2.8f;
                    Vector3 end = Resolve(ground + Vector3.up * 0.5f, ground + orbit + Vector3.up * 4.2f);
                    desiredPos = Vector3.Lerp(deathStartPos, end, e);
                    Quaternion endRot = Quaternion.LookRotation(ground - end, Vector3.up);
                    desiredRot = Quaternion.Slerp(deathStartRot, endRot, e);
                    snapNext = true;      // fully scripted move
                    break;
                }
                default: // CINE_LowHero
                {
                    Vector3 floorish = new Vector3(head.x, head.y - 1.35f, head.z);
                    Vector3 want = floorish + fwd * 2.3f + right * 0.7f + Vector3.up * 0.25f;
                    desiredPos = Resolve(head - Vector3.up * 0.5f, want);
                    desiredRot = Quaternion.LookRotation(head - desiredPos, Vector3.up);
                    posSmooth = 0.2f;
                    rotSharpness = 7f;
                    break;
                }
            }

            if (snapNext)
            {
                cam.SetPositionAndRotation(desiredPos, desiredRot);
                snapNext = Shot == ShotKind.CINE_DeathCam;
                velocity = Vector3.zero;
                return;
            }
            cam.position = Vector3.SmoothDamp(cam.position, desiredPos, ref velocity, posSmooth, Mathf.Infinity, Mathf.Max(dt, 1e-4f));
            cam.rotation = Quaternion.Slerp(cam.rotation, desiredRot, 1f - Mathf.Exp(-rotSharpness * dt));
        }

        /// <summary>Pull the camera in front of walls between the subject and the desired spot.</summary>
        Vector3 Resolve(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 1e-3f) return to;
            RaycastHit hit;
            if (Physics.SphereCast(from, 0.2f, d / len, out hit, len, envMask, QueryTriggerInteraction.Ignore))
                return from + d / len * Mathf.Max(0.3f, hit.distance - 0.05f);
            return to;
        }

        // ------------------------------------------------------------------
        // Frame capture -> JPEG -> web stream
        // ------------------------------------------------------------------
        void Capture()
        {
            if (readbackPending) return;
            if (BroadcastConfig.UseAsyncReadback && SystemInfo.supportsAsyncGPUReadback)
            {
                readbackPending = true;
                AsyncGPUReadback.Request(Target, 0, TextureFormat.RGB24, OnReadback);
            }
            else
            {
                CaptureSync();
            }
        }

        Texture2D syncTex;
        void CaptureSync()
        {
            if (syncTex == null) syncTex = new Texture2D(Target.width, Target.height, TextureFormat.RGB24, false);
            var prev = RenderTexture.active;
            RenderTexture.active = Target;
            syncTex.ReadPixels(new Rect(0, 0, Target.width, Target.height), 0, 0, false);
            syncTex.Apply(false);
            RenderTexture.active = prev;
            byte[] jpg = syncTex.EncodeToJPG(BroadcastConfig.JpegQuality);
            server.PublishFrame(jpg);
            CountFrame();
        }

        void OnReadback(AsyncGPUReadbackRequest req)
        {
            readbackPending = false;
            if (req.hasError || server == null) return;
            NativeArray<byte> data = req.GetData<byte>();
            int w = req.width, h = req.height;
            byte[] buf = RentBuffer(data.Length);
            data.CopyTo(buf);
            bool flip = BroadcastConfig.FlipStreamVertically;
            int quality = BroadcastConfig.JpegQuality;
            var srv = server;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (flip) FlipRows(buf, w * 3, h);
                    byte[] jpg = ImageConversion.EncodeArrayToJPG(buf, GraphicsFormat.R8G8B8_SRGB, (uint)w, (uint)h, 0, quality);
                    srv.PublishFrame(jpg);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Spectator] JPEG encode failed: " + ex.Message);
                }
                finally
                {
                    ReturnBuffer(buf);
                }
            });
            CountFrame();
        }

        void CountFrame()
        {
            encodedFrames++;
            float now = Time.realtimeSinceStartup;
            if (now - fpsWindowStart >= 1f)
            {
                MeasuredStreamFps = encodedFrames / (now - fpsWindowStart);
                encodedFrames = 0;
                fpsWindowStart = now;
            }
        }

        static void FlipRows(byte[] buf, int stride, int h)
        {
            var tmp = new byte[stride];
            for (int y = 0; y < h / 2; y++)
            {
                int a = y * stride, b = (h - 1 - y) * stride;
                Buffer.BlockCopy(buf, a, tmp, 0, stride);
                Buffer.BlockCopy(buf, b, buf, a, stride);
                Buffer.BlockCopy(tmp, 0, buf, b, stride);
            }
        }

        byte[] RentBuffer(int len)
        {
            lock (poolLock)
            {
                while (bufferPool.Count > 0)
                {
                    var b = bufferPool.Pop();
                    if (b.Length == len) return b;
                }
            }
            return new byte[len];
        }

        void ReturnBuffer(byte[] b)
        {
            lock (poolLock) { if (bufferPool.Count < 4) bufferPool.Push(b); }
        }

        void OnDestroy()
        {
            if (Target != null) { Target.Release(); Destroy(Target); }
            if (syncTex != null) Destroy(syncTex);
        }
    }
}
