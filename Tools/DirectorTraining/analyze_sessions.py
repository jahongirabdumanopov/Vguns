#!/usr/bin/env python3
"""
Summarise every recorded VGuns session into one table (for the capstone report).

    python analyze_sessions.py                 # uses ../../Telemetry
    python analyze_sessions.py /path/to/Telemetry

Writes <Telemetry>/sessions_overview.csv and prints:
  - per-session summary (duration, kills, cuts/min, avg shot, approval, ...)
  - which events triggered which camera angle (from camera_decisions.csv)
Only uses the Python standard library.
"""
import csv
import glob
import os
import sys
from collections import Counter, defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
root = sys.argv[1] if len(sys.argv) > 1 else os.path.normpath(os.path.join(HERE, "..", "..", "Telemetry"))

COLS = ["session_id", "duration_s", "end_reason", "kills", "accuracy", "hits_taken", "player_died",
        "camera_cuts", "cuts_per_minute", "avg_shot_length_s", "time_fpv_pct", "time_tpv_pct", "time_cine_pct",
        "avg_confidence", "avg_event_to_cut_latency_s", "rule_baseline_agreement", "human_agreement",
        "ratings_up", "ratings_down", "viewer_approval"]

rows = []
trigger_to_angle = defaultdict(Counter)
for sdir in sorted(glob.glob(os.path.join(root, "session_*"))):
    summ = os.path.join(sdir, "summary.csv")
    if os.path.exists(summ):
        with open(summ, newline="") as fh:
            d = {r["metric"]: r["value"] for r in csv.DictReader(fh)}
        rows.append({c: d.get(c, "") for c in COLS})
    else:
        rows.append({"session_id": os.path.basename(sdir).replace("session_", ""), "end_reason": "(no summary - crashed or still running)"})
    dec = os.path.join(sdir, "camera_decisions.csv")
    if os.path.exists(dec):
        with open(dec, newline="") as fh:
            for r in csv.DictReader(fh):
                trigger_to_angle[r.get("trigger", "-")][r.get("to_angle", "?")] += 1

if not rows:
    print(f"No sessions found in {root}")
    sys.exit(0)

out = os.path.join(root, "sessions_overview.csv")
with open(out, "w", newline="") as fh:
    w = csv.DictWriter(fh, fieldnames=COLS)
    w.writeheader()
    for r in rows:
        w.writerow({c: r.get(c, "") for c in COLS})

show = ["session_id", "duration_s", "kills", "camera_cuts", "cuts_per_minute", "avg_shot_length_s", "viewer_approval", "end_reason"]
print(" | ".join(f"{c:>16s}" for c in show))
for r in rows:
    print(" | ".join(f"{str(r.get(c, '')):>16s}" for c in show))

print("\nEvent -> camera angle chosen by the director (all sessions):")
for trig, cnt in sorted(trigger_to_angle.items(), key=lambda kv: -sum(kv[1].values())):
    total = sum(cnt.values())
    parts = ", ".join(f"{a} {100 * n / total:.0f}%" for a, n in cnt.most_common())
    print(f"  {trig:18s} n={total:4d}  {parts}")
print(f"\nSaved {out}")
