#!/usr/bin/env python3
"""
VGuns AI Camera Director - training pipeline
=============================================

Trains the decision model that the in-game DirectorAI uses to pick between
the three camera angles of the taxonomy:

    0 = FPV   (first-person, stabilised head camera)
    1 = TPV   (third-person, over-the-shoulder / threat focus)
    2 = CINE  (cinematic: orbit, wide, kill-cam, death-cam, low hero)

Model: small MLP  (20 features -> 24 tanh -> 3 softmax), pure numpy.
Output: Assets/Resources/DirectorAI/director_policy.json (loaded by Unity).

Training data comes from two sources:

  1. Synthetic "expert" data. A rule-based expert director (teacher()) scores
     each angle for a game situation. We sample thousands of situations
     (idle, combat, under fire, kills, death, wave start, bullet time) and the
     network learns to imitate the expert's soft decisions. This gives a
     working model before any real play data exists.

  2. Real sessions (optional, --sessions <TelemetryRoot>). Every play session
     writes director_ticks.csv. Rows where a human viewer overrode the camera
     from the web page are used as labelled demonstrations (imitation
     learning, weighted x4). Rows in the 3 s before a thumbs-up rating are
     reinforced with the AI's own choice (x2); rows in the 3 s before a
     thumbs-down are dropped.

Usage
-----
    pip install numpy
    python train_director.py                          # synthetic only
    python train_director.py --sessions ../../Telemetry
    python train_director.py --sessions ../../Telemetry --synthetic 20000 --epochs 60

Keep FEATURES in the same order as DirectorFeatures.cs in Unity.
"""

import argparse
import csv
import datetime as dt
import glob
import json
import os
import sys

import numpy as np

FEATURES = [
    "health_frac",          # 0  current health / max health
    "low_health",           # 1  1 if health < 35 %
    "enemies_alive",        # 2  alive enemies / 8
    "nearest_enemy_dist",   # 3  metres / 25 (1 = none / far)
    "enemies_in_range",     # 4  enemies within their attack range / 4
    "shots_2s",             # 5  player shots in last 2 s / 6
    "hits_taken_3s",        # 6  hits taken in last 3 s / 3
    "kills_3s",             # 7  kills in last 3 s / 3
    "head_ang_speed",       # 8  head angular speed deg/s / 180
    "player_speed",         # 9  head horizontal speed m/s / 3
    "time_in_shot",         # 10 seconds on current shot / 12 (max 1.5)
    "cur_fpv",              # 11 one-hot current angle
    "cur_tpv",              # 12
    "cur_cine",             # 13
    "dead",                 # 14 player is dead
    "kill_recent",          # 15 enemy killed in last 1.5 s
    "hit_recent",           # 16 player hit in last 1.0 s
    "wave_recent",          # 17 wave started in last 3 s
    "reloading",            # 18 player reloading
    "bullet_time",          # 19 slow-motion active
]
CLASSES = ["FPV", "TPV", "CINE"]
F = len(FEATURES)
C = len(CLASSES)

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_OUT = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "Resources", "DirectorAI", "director_policy.json"))


# --------------------------------------------------------------------------
# Expert teacher. Mirrored 1:1 in HeuristicDirector.cs (used as the fallback
# and as the "rule baseline" the AI panel compares against).
# --------------------------------------------------------------------------
def teacher(f):
    health, low, enem, near, inrange, shots, hits, kills, angs, speed, tin, cf, ct, cc, dead, killr, hitr, wave, reload, bt = f

    if dead > 0.5:
        return np.array([0.0, 0.25, 0.75])

    s = np.array([1.2, 0.7, 0.4])
    if killr > 0.5:
        s[2] += 3.0
        s[1] += 0.4
    if wave > 0.5:
        s[2] += 2.2
    if bt > 0.5:
        s[2] += 2.4
        s[1] += 0.3
    if hitr > 0.5:
        s[1] += 1.4
    if low > 0.5:
        s[1] += 0.8
    s[1] += 1.6 * inrange
    s[0] += 1.5 * shots * (1.0 - min(angs, 1.0))
    s[1] += 2.0 * max(0.0, angs - 0.35)
    if shots > 0.01:
        s[0] += 0.8 * (1.0 - near)
    if enem < 0.01 and shots < 0.01:
        s[2] += 1.8
    if reload > 0.5:
        s[1] += 0.6
    s[2] += 0.5 * speed * (1.0 if enem < 0.01 else 0.0)

    cur = int(np.argmax([cf, ct, cc]))
    if tin < 0.25:
        s[cur] += 0.5                       # stickiness right after a cut
    s[cur] -= 4.0 * max(0.0, tin - 0.5)     # shot fatigue after ~6 s

    z = s * 1.6
    z = z - z.max()
    p = np.exp(z)
    return p / p.sum()


# --------------------------------------------------------------------------
# Synthetic situations
# --------------------------------------------------------------------------
def sample_situation(rng):
    f = np.zeros(F)
    r = rng.random()
    health = rng.uniform(0.05, 1.0)
    enem = near = inrange = shots = hits = kills = 0.0
    angs = rng.uniform(0.0, 0.35)
    speed = rng.uniform(0.0, 0.4)
    dead = killr = hitr = wave = bt = 0.0

    if r < 0.20:                                  # idle / downtime
        enem = rng.choice([0.0, 0.0, 0.125, 0.25])
        near = 1.0 if enem == 0 else rng.uniform(0.6, 1.0)
        speed = rng.uniform(0.0, 0.8)
    elif r < 0.55:                                # active combat
        enem = rng.uniform(0.125, 0.8)
        near = rng.uniform(0.08, 0.7)
        inrange = rng.choice([0.0, 0.0, 0.25, 0.5])
        shots = rng.uniform(0.15, 1.0)
        angs = rng.uniform(0.0, 0.9)
        speed = rng.uniform(0.0, 0.7)
    elif r < 0.70:                                # under fire
        enem = rng.uniform(0.125, 0.8)
        near = rng.uniform(0.05, 0.5)
        inrange = rng.uniform(0.25, 1.0)
        hits = rng.choice([0.33, 0.66, 1.0])
        hitr = 1.0 if rng.random() < 0.7 else 0.0
        shots = rng.uniform(0.0, 0.8)
        angs = rng.uniform(0.1, 1.0)
        health = rng.uniform(0.05, 0.8)
    elif r < 0.82:                                # just killed an enemy
        enem = rng.uniform(0.0, 0.6)
        near = rng.uniform(0.1, 0.8)
        kills = rng.choice([0.33, 0.33, 0.66, 1.0])
        killr = 1.0
        shots = rng.uniform(0.2, 1.0)
        angs = rng.uniform(0.0, 0.6)
    elif r < 0.87:                                # player died
        dead = 1.0
        health = 0.0
        hits = rng.choice([0.33, 0.66, 1.0])
        enem = rng.uniform(0.125, 0.8)
        near = rng.uniform(0.05, 0.5)
    elif r < 0.93:                                # wave start
        wave = 1.0
        enem = rng.uniform(0.25, 1.0)
        near = rng.uniform(0.4, 1.0)
    else:                                         # bullet time
        bt = 1.0
        enem = rng.uniform(0.125, 0.8)
        near = rng.uniform(0.1, 0.7)
        shots = rng.uniform(0.0, 1.0)

    reload = 1.0 if rng.random() < 0.12 else 0.0
    cur = rng.integers(0, 3)
    tin = min(1.5, rng.exponential(0.35))

    f[:] = [health, 1.0 if health < 0.35 else 0.0, enem, near, inrange, shots, hits, kills,
            angs, speed, tin, 1.0 if cur == 0 else 0.0, 1.0 if cur == 1 else 0.0, 1.0 if cur == 2 else 0.0,
            dead, killr, hitr, wave, reload, bt]
    return np.clip(f, 0.0, 1.5)


def synthetic_dataset(n, seed):
    rng = np.random.default_rng(seed)
    X = np.stack([sample_situation(rng) for _ in range(n)])
    Y = np.stack([teacher(x) for x in X])
    W = np.ones(n)
    return X, Y, W


# --------------------------------------------------------------------------
# Real sessions from Unity telemetry
# --------------------------------------------------------------------------
def load_sessions(root):
    Xs, Ys, Ws = [], [], []
    stats = {"sessions": 0, "human_rows": 0, "liked_rows": 0, "dropped_disliked": 0}
    for ticks in sorted(glob.glob(os.path.join(root, "*", "director_ticks.csv"))):
        sdir = os.path.dirname(ticks)
        ratings = []
        rpath = os.path.join(sdir, "ratings.csv")
        if os.path.exists(rpath):
            with open(rpath, newline="") as fh:
                for row in csv.DictReader(fh):
                    try:
                        ratings.append((float(row["t"]), int(float(row["rating"]))))
                    except (KeyError, ValueError):
                        pass
        stats["sessions"] += 1
        with open(ticks, newline="") as fh:
            for row in csv.DictReader(fh):
                try:
                    x = np.array([float(row[k]) for k in FEATURES])
                    t = float(row["t"])
                except (KeyError, ValueError):
                    continue
                human = (row.get("human_label") or "").strip().upper()
                final = (row.get("final_choice") or "").strip().upper()
                if human in CLASSES:
                    y = np.zeros(C); y[CLASSES.index(human)] = 1.0
                    Xs.append(x); Ys.append(y); Ws.append(4.0)
                    stats["human_rows"] += 1
                    continue
                near_r = [v for (rt, v) in ratings if 0.0 <= rt - t <= 3.0]
                if any(v < 0 for v in near_r):
                    stats["dropped_disliked"] += 1
                    continue
                if any(v > 0 for v in near_r) and final in CLASSES:
                    y = np.zeros(C); y[CLASSES.index(final)] = 1.0
                    Xs.append(x); Ys.append(y); Ws.append(2.0)
                    stats["liked_rows"] += 1
    if not Xs:
        return None, None, None, stats
    return np.stack(Xs), np.stack(Ys), np.array(Ws), stats


# --------------------------------------------------------------------------
# MLP
# --------------------------------------------------------------------------
def softmax(z):
    z = z - z.max(axis=1, keepdims=True)
    e = np.exp(z)
    return e / e.sum(axis=1, keepdims=True)


def train(X, Y, W, hidden=24, epochs=40, lr=0.01, batch=256, l2=1e-4, seed=0):
    rng = np.random.default_rng(seed)
    mean = X.mean(axis=0)
    std = X.std(axis=0)
    std[std < 1e-6] = 1.0
    Xn = (X - mean) / std

    idx = rng.permutation(len(X))
    nval = max(1, int(0.15 * len(X)))
    val, tr = idx[:nval], idx[nval:]

    W1 = rng.normal(0, 1.0 / np.sqrt(F), (hidden, F))
    b1 = np.zeros(hidden)
    W2 = rng.normal(0, 1.0 / np.sqrt(hidden), (C, hidden))
    b2 = np.zeros(C)
    params = [W1, b1, W2, b2]
    m = [np.zeros_like(p) for p in params]
    v = [np.zeros_like(p) for p in params]
    b1m, b2m, eps, step = 0.9, 0.999, 1e-8, 0

    def forward(x):
        h = np.tanh(x @ W1.T + b1)
        return h, softmax(h @ W2.T + b2)

    for ep in range(epochs):
        order = rng.permutation(tr)
        for i in range(0, len(order), batch):
            bi = order[i:i + batch]
            x, y, w = Xn[bi], Y[bi], W[bi]
            h, p = forward(x)
            g = (p - y) * w[:, None] / w.sum()
            gW2 = g.T @ h + l2 * W2
            gb2 = g.sum(0)
            gh = (g @ W2) * (1 - h ** 2)
            gW1 = gh.T @ x + l2 * W1
            gb1 = gh.sum(0)
            step += 1
            for k, (p_, gr) in enumerate(zip(params, [gW1, gb1, gW2, gb2])):
                m[k] = b1m * m[k] + (1 - b1m) * gr
                v[k] = b2m * v[k] + (1 - b2m) * gr * gr
                mh = m[k] / (1 - b1m ** step)
                vh = v[k] / (1 - b2m ** step)
                p_ -= lr * mh / (np.sqrt(vh) + eps)
        if (ep + 1) % 10 == 0 or ep == 0:
            _, pv = forward(Xn[val])
            acc = (pv.argmax(1) == Y[val].argmax(1)).mean()
            ce = -(Y[val] * np.log(pv + 1e-9)).sum(1).mean()
            print(f"  epoch {ep + 1:3d}  val_acc={acc:.3f}  val_ce={ce:.3f}")

    _, pv = forward(Xn[val])
    pred, true = pv.argmax(1), Y[val].argmax(1)
    acc = float((pred == true).mean())
    conf = np.zeros((C, C), dtype=int)
    for t_, p_ in zip(true, pred):
        conf[t_, p_] += 1
    return dict(W1=W1, b1=b1, W2=W2, b2=b2, mean=mean, std=std), acc, conf, (X[val], pv)


def scenario_checks(model):
    """Sanity checks on the events the director must react to."""
    def predict(x):
        z = (x - model["mean"]) / model["std"]
        h = np.tanh(model["W1"] @ z + model["b1"])
        o = model["W2"] @ h + model["b2"]
        e = np.exp(o - o.max())
        return e / e.sum()

    base = np.array([0.8, 0, 0.375, 0.4, 0, 0.5, 0, 0, 0.15, 0.1, 0.1, 1, 0, 0, 0, 0, 0, 0, 0, 0], float)
    cases = {}
    x = base.copy(); cases["combat, steady aim (from FPV)"] = x
    x = base.copy(); x[14] = 1; x[0] = 0; x[1] = 1; x[6] = 0.66; cases["player death"] = x
    x = base.copy(); x[15] = 1; x[7] = 0.33; cases["enemy killed"] = x
    x = base.copy(); x[16] = 1; x[6] = 0.66; x[4] = 0.75; x[8] = 0.6; cases["taking hits, enemies close"] = x
    x = base.copy(); x[17] = 1; x[3] = 0.9; x[5] = 0; cases["wave started"] = x
    x = base.copy(); x[2] = 0; x[3] = 1; x[5] = 0; cases["downtime, no enemies"] = x
    x = base.copy(); x[19] = 1; cases["bullet time"] = x
    x = base.copy(); x[10] = 0.9; cases["FPV held 11 s"] = x
    rows = []
    for name, x in cases.items():
        p = predict(x)
        t = teacher(x)
        rows.append((name, p, t))
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--synthetic", type=int, default=24000, help="number of synthetic expert samples")
    ap.add_argument("--sessions", type=str, default=None, help="Telemetry root folder with session_* subfolders")
    ap.add_argument("--epochs", type=int, default=40)
    ap.add_argument("--hidden", type=int, default=24)
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--out", type=str, default=DEFAULT_OUT)
    args = ap.parse_args()

    print(f"Generating {args.synthetic} synthetic expert samples ...")
    X, Y, W = synthetic_dataset(args.synthetic, args.seed)
    real_stats = None
    if args.sessions:
        Xr, Yr, Wr, real_stats = load_sessions(args.sessions)
        print(f"Real sessions: {real_stats}")
        if Xr is not None:
            X = np.concatenate([X, Xr]); Y = np.concatenate([Y, Yr]); W = np.concatenate([W, Wr])

    print(f"Training MLP {F}->{args.hidden}->{C} on {len(X)} samples ...")
    model, acc, conf, _ = train(X, Y, W, hidden=args.hidden, epochs=args.epochs, seed=args.seed)
    print(f"Validation accuracy (argmax vs label): {acc:.3f}")
    print("Confusion (rows=label, cols=pred):", CLASSES)
    for i, c in enumerate(CLASSES):
        print(f"  {c:5s} {conf[i]}")

    print("\nEvent reaction checks (model vs expert):")
    checks = scenario_checks(model)
    for name, p, t in checks:
        print(f"  {name:34s} model={CLASSES[p.argmax()]:4s} {np.round(p, 2)}   expert={CLASSES[t.argmax()]:4s} {np.round(t, 2)}")

    now = dt.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    out = {
        "name": "VGuns Director MLP",
        "version": dt.datetime.now().strftime("%Y%m%d-%H%M%S"),
        "trainedAt": now,
        "features": FEATURES,
        "classes": CLASSES,
        "hidden": int(args.hidden),
        "mean": [round(float(v), 6) for v in model["mean"]],
        "std": [round(float(v), 6) for v in model["std"]],
        "W1": [round(float(v), 6) for v in model["W1"].ravel()],
        "b1": [round(float(v), 6) for v in model["b1"]],
        "W2": [round(float(v), 6) for v in model["W2"].ravel()],
        "b2": [round(float(v), 6) for v in model["b2"]],
        "valAccuracy": round(acc, 4),
        "trainSamples": int(len(X)),
        "humanSamples": int(real_stats["human_rows"]) if real_stats else 0,
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w") as fh:
        json.dump(out, fh, indent=1)
    print(f"\nSaved policy -> {args.out}")

    report = os.path.join(os.path.dirname(args.out), "training_report.txt")
    with open(report, "w") as fh:
        fh.write(f"VGuns Director MLP - trained {now}\n")
        fh.write(f"samples={len(X)} synthetic={args.synthetic} real={real_stats}\n")
        fh.write(f"val_accuracy={acc:.4f}\nconfusion rows=label cols=pred {CLASSES}\n")
        for i, c in enumerate(CLASSES):
            fh.write(f"  {c:5s} {conf[i].tolist()}\n")
        fh.write("\nEvent reaction checks (model vs expert):\n")
        for name, p, t in checks:
            fh.write(f"  {name:34s} model={CLASSES[p.argmax()]:4s} {np.round(p, 2).tolist()}  expert={CLASSES[t.argmax()]:4s}\n")
    print(f"Saved report -> {report}")


if __name__ == "__main__":
    sys.exit(main())
