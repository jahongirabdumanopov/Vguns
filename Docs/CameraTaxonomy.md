# VGuns Camera Taxonomy

The AI director makes one decision: **which camera angle** the spectators should see.
The spectator rig then picks the **shot** (concrete framing) inside that angle from the
current situation. Code: `Assets/Scripts/Broadcast/CameraTaxonomy.cs`. The same table is
written into every telemetry session as `camera_taxonomy.csv`.

```
CameraAngle (AI decision)          ShotKind (framing chosen by context)
─────────────────────────          ────────────────────────────────────
FPV  First person          ──►     FPV_Stabilized
TPV  Third person          ──►     TPV_OverShoulder | TPV_ThreatFocus
CINE Cinematic             ──►     CINE_Orbit | CINE_WideEstablishing | CINE_KillCam | CINE_DeathCam | CINE_LowHero
```

## Shots

| Shot | Angle | Size | Height | Movement | Subject | FOV | Hold (min–max s) | Typical triggers | Purpose |
|---|---|---|---|---|---|---|---|---|---|
| FPV_Stabilized | FPV | Close-up | Eye level | Head-locked | Player view | 80 | 2–12 | Steady aiming, active shooting, enemy close in front | Player's view with head jitter and roll removed so viewers don't get motion sick |
| TPV_OverShoulder | TPV | Medium | Eye level | Follow | Player stand-in | 65 | 2–12 | Fast head movement, reloading, low health | Over-the-shoulder follow camera |
| TPV_ThreatFocus | TPV | Medium-wide | Eye level | Follow | Player + nearest enemy | 62 | 2–10 | Player hit, enemies within attack range | Behind the player, aimed at the threat |
| CINE_Orbit | CINE | Medium-wide | High | Orbit | Player stand-in | 60 | 3–10 | Downtime, no enemies | Slow orbit in quiet moments |
| CINE_WideEstablishing | CINE | Wide | High | Crane | Player + all enemies | 70 | 2.5–8 | Wave started, many enemies | Establishes arena and enemy positions |
| CINE_KillCam | CINE | Medium | Low | Dolly | Killed enemy + shooter | 55 | 1.5–4 | Enemy killed, multi-kill | Side-on framing of the kill |
| CINE_DeathCam | CINE | Wide | Overhead | Crane | Player death spot | 60 | 3–∞ | Player death | Crane up and away when the player dies |
| CINE_LowHero | CINE | Medium | Low | Follow | Player (front) | 55 | 2–6 | Bullet time, intense moments | Dramatic low angle looking up at the player |

## Shot selection inside an angle

- **FPV** → always `FPV_Stabilized`.
- **TPV** → `TPV_ThreatFocus` if an enemy is within 12 m or the player was just hit, otherwise `TPV_OverShoulder`.
- **CINE** → by trigger: death → `DeathCam`; kill / multi-kill → `KillCam` (5 s cooldown); wave start → `Wide`;
  bullet time → `LowHero`; otherwise 3+ enemies → `Wide`, no enemies → `Orbit`, else alternate `Orbit` / `LowHero`.

## Shot grammar (applies to every decision)

- **Minimum hold** 2.2 s (or the shot's own minimum), so the picture never flickers.
- **Event hold** 0.6 s: an important event (death, kill, wave start, bullet time, player hit) can cut sooner.
- **Hysteresis**: switch only if the new angle beats the current one by more than 12 probability points (or an event fired).
- **Maximum hold**: when a shot exceeds its max hold, cut to the best alternative angle.
- **Death**: FPV is not allowed after death; the chosen death shot is held until the level ends.

## Player stand-in

VR has no visible player body, so third-person and cinematic shots would show floating hands.
The rig spawns a simple capsule body + head with a visor on the `SpectatorOnly` layer. Only the
spectator camera renders it; the headset camera ignores that layer. The in-headset AI panel is on
the `PlayerHUD` layer, which the spectator camera ignores.
