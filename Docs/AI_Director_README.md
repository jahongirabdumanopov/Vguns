# VGuns: Telemetry, AI Camera Director and Live Stream

All of this starts by itself. You don't add anything to a scene: `BroadcastHub` boots on play
(`[RuntimeInitializeOnLoadMethod]`) and starts a session whenever the gameplay level loads.

```
Game scripts ──GameEventBus.Raise()──► GameStateTracker ──features──► DirectorAI (trained MLP + shot grammar)
     │                                        │                               │
     │                                        ▼                               ▼
     └────────────────────────────► TelemetryRecorder (CSV)        SpectatorRig (broadcast camera)
                                                                              │ JPEG frames
                                    AIPanel (in headset) ◄── state ──► BroadcastServer :8080 ──► browser
```

## 1. Telemetry: one new folder per session

Each run of the level writes `session_YYYYMMDD_HHMMSS/`:

| File | Content |
|---|---|
| `session_info.csv` | device, XR runtime, scene, model version, stream URL |
| `events.csv` | every game event: shots, reloads, hits, kills, multi-kills, deaths, waves, bullet time, pauses, camera cuts, viewer overrides/ratings |
| `frames.csv` | 10 Hz samples: head pose, head speed, health, enemies, nearest enemy, kills, intensity, active camera |
| `director_ticks.csv` | 5 Hz: the 20 model features, model probabilities, rule-baseline probabilities, AI choice, final choice, human label |
| `camera_decisions.csv` | every cut: from → to, trigger event, source (AI / Viewer / Rule), confidence, how long the previous shot was held, reason |
| `ratings.csv` | 👍/👎 from viewers |
| `camera_taxonomy.csv` | the shot taxonomy |
| `summary.csv` | written when the session ends: kills, accuracy, cuts/min, average shot length, screen-time split, event→cut latency, agreement and approval |

**Where they are saved**

| Running in | Folder |
|---|---|
| Unity Editor | `VGuns-Unity-VR/Telemetry/` (project root, next to `Assets/`) |
| Windows/Linux build | `Telemetry/` next to the .exe |
| Quest (Android) | `/sdcard/Android/data/<package>/files/Telemetry/` |

The easiest way on any platform: the web viewer has a **Telemetry sessions** panel with download links for every CSV.

## 2. Camera taxonomy

There are 3 angles (FPV, TPV, Cinematic) and 8 shots. See `Docs/CameraTaxonomy.md`.

## 3. Director AI

- **Features (20):** health, low health, enemies alive, nearest enemy distance, enemies in attack range,
  shots / hits taken / kills in a recent window, head angular speed, player speed, time in the current shot,
  the current angle, plus event flags: dead, kill in the last 1.5 s, hit in the last 1 s, wave start in the last 3 s,
  reloading, bullet time.
- **Model:** MLP with 20 inputs, 24 tanh units and a 3-way softmax over FPV/TPV/CINE
  (`Assets/Resources/DirectorAI/director_policy.json`). Inference runs in C#; there's no ML package dependency.
- **Shot grammar:** minimum/maximum hold, hysteresis, event fast-cut, and no FPV after death.
- **Event reactions** (from the shipped model): death → CINE death cam (or TPV), enemy killed → CINE kill cam,
  taking hits with enemies close → TPV threat focus, wave start → CINE wide, steady aiming in combat → FPV.
- **Explanations:** every cut stores a reason such as `EnemyKilled -> CINE (p=0.73): enemy just killed | Kill cam`.

### Training

```bash
cd Tools/DirectorTraining
pip install numpy
python train_director.py                        # synthetic expert data only (what's shipped)
python train_director.py --sessions ../../Telemetry   # + your real sessions
python analyze_sessions.py                      # summary table for the report
```

The shipped model was trained on 24,000 situations labelled by a rule-based expert director
(`teacher()` in the script, mirrored in `HeuristicDirector.cs`). It scores 98% validation accuracy at
imitating that expert. Treat it as a starting point. It improves with real data:

1. Play while someone watches the web page and uses **First person / Third person / Cinematic** whenever they'd pick a different angle.
   Every 0.2 s tick during an override becomes a human label (weighted ×4).
2. Viewers click 👍/👎. Ticks in the 3 s before a 👍 reinforce the AI's choice (×2), and ticks before a 👎 are dropped.
3. Run `train_director.py --sessions ../../Telemetry`, which overwrites `director_policy.json`.
   To try a model without rebuilding, copy it as `director_policy.json` into the Telemetry root folder; that copy is loaded first.

The C# inference was checked against numpy on 300 random situations. Probabilities matched within 2e-7.

## 4. Live stream website

When the level starts, the game opens **http://localhost:8080** in your browser (Editor/PC).

- Live video of the AI-directed spectator camera (MJPEG, 1280×720 at 30 fps on PC, 960×540 at 20 fps on Quest).
- LIVE badge, current angle (AI / MANUAL), a lower-third with the shot name and the AI's reason, and a flash on every cut.
- Buttons: **AUTO**, **First person**, **Third person**, **Cinematic**, **👍 Good cut**, **👎 Bad cut**, **Flip**, **Fullscreen**.
  Keys: `0` auto, `1` `2` `3` angles, `=` / `-` rate.
- AI decision bars (model) with white ticks (rule baseline), assessment metrics, game stats, the shot timeline for the last 2 minutes, the decision log, the event feed and the CSV downloads.

**Playing on Quest (build to headset):**
- Same Wi-Fi: open the URL shown on the in-headset panel, for example `http://192.168.0.23:8080`.
- Over USB, which is also "localhost": `adb forward tcp:8080 tcp:8080`, then open http://localhost:8080 on the Mac.
  Use this when campus Wi-Fi blocks device-to-device traffic.

## 5. In-headset AI assessment panel

The panel floats at the lower-left of the player's view. It shows a live preview of what viewers see, the current
angle and shot, the probability bars with rule-baseline ticks, the last decision and its reason, the metrics
(cuts/min, average shot length, confidence, event→cut latency, rule and human agreement, viewer approval,
screen-time split), the event feed, the stream URL and the telemetry folder.
**Toggle:** left thumbstick click on Quest, or **F1**. Spectators never see it.

## Files

**New**
- `Assets/Scripts/Broadcast/` contains `GameEvents`, `GameStateTracker`, `CameraTaxonomy`, `SpectatorRig`, `DirectorPolicy`,
  `DirectorAI`, `TelemetryRecorder`, `BroadcastServer`, `BroadcastHub`, `SessionController`, `AIPanel` and `BroadcastConfig` (all settings).
- `Assets/Resources/BroadcastWeb/index.html` is the viewer page.
- `Assets/Resources/DirectorAI/director_policy.json` and `training_report.txt` are the trained model.
- `Tools/DirectorTraining/train_director.py` and `analyze_sessions.py`.
- `Docs/` contains this file and `CameraTaxonomy.md`.

**Changed** (one-line `GameEventBus.Raise(...)` hooks, marked `// telemetry / AI director`)
- `Gun.cs` (shot, reload), `EnemyAi.cs` (spawn, attack, hit, kill), `wave.cs` (wave start/complete), `Target.cs`,
  `BulletTime.cs`, `SpeedUpTime.cs`, `PauseMenu.cs` (pause/resume).
- `XRTarget.cs`: logs player hits and death. Death now runs once and **waits 5 s (`deathCamSeconds`) before returning to the menu**,
  so the death camera can play. The old code started a 5 s wait but loaded the menu immediately.
- `ProjectSettings/TagManager.asset`: layer 14 `SpectatorOnly`, layer 15 `PlayerHUD`.
- `ProjectSettings/ProjectSettings.asset`: Android *Internet Access = Require*, which the server needs on Quest.

## Troubleshooting

- **Stream is upside down:** click **Flip** on the page (remembered), or set `BroadcastConfig.FlipStreamVertically = true`.
- **Black or no video:** check the Console for `[Broadcast]`. As a fallback set `BroadcastConfig.UseAsyncReadback = false`.
- **Port busy:** the server tries ports 8080 to 8089. The actual URL is printed in the Console and shown on the panel.
- **Quest FPS drops:** lower `StreamFps`, `StreamWidth` and `StreamHeight` in `BroadcastConfig`. The spectator camera only renders while someone is watching or the panel is open.
- **Layers missing:** if Unity was open while these files were added, it may have rewritten `TagManager.asset`.
  Add layers `SpectatorOnly` (14) and `PlayerHUD` (15) in *Project Settings → Tags and Layers*. The code falls back to 14 and 15 anyway.
- **Turn everything off:** `BroadcastConfig.Enabled = false`.
