#!/usr/bin/env python3
# A/B of the ycs side of one or more workloads between two builds of this project (e.g. a commit and its parent,
# each built in a directory of its own), interleaved, with an 8 GiB resident-set cap per child.
#   ab.py <rounds> <runs> <case> [<case>...]
#   env: BASE, NEW (Ycs.Compare binaries), WORK (a suite --work directory: its fixtures are read),
#        STEADY=k (leave each process's first k timed runs out), ONLY=base|new
# Prints per build: the median of all timed runs, min/max, the per-process medians, allocations, held memory, peak RSS.
import json, os, subprocess, sys, time, statistics

BASE = os.environ["BASE"]
NEW = os.environ.get("NEW", os.path.join(os.path.dirname(os.path.abspath(__file__)), "bin/Release/net11.0/Ycs.Compare"))
WORK = os.environ["WORK"]
LIMIT = 8 << 30
only = os.environ.get("ONLY")  # "base" or "new"

def run(binary, case, runs):
    out = WORK + "/ab"
    os.makedirs(out, exist_ok=True)
    p = subprocess.Popen([binary, "child", "ycs", case, WORK + "/fixtures", out, str(runs)], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    peak = 0
    while p.poll() is None:
        try:
            with open(f"/proc/{p.pid}/status") as f:
                for line in f:
                    if line.startswith("VmRSS:"):
                        rss = int(line.split()[1]) * 1024
                        peak = max(peak, rss)
                        if rss > LIMIT:
                            p.kill()
        except FileNotFoundError:
            pass
        time.sleep(0.05)
    o, e = p.communicate()
    if p.returncode != 0:
        return {"failure": f"exit {p.returncode} peak {peak>>20} MiB {e.decode()[-300:]}"}
    j = json.loads(o)
    j["peak"] = peak
    return j

rounds, runs = int(sys.argv[1]), int(sys.argv[2])
for case in sys.argv[3:]:
    res = {"base": [], "new": []}
    for r in range(rounds):
        order = ["base", "new"] if r % 2 == 0 else ["new", "base"]
        for which in order:
            if only and which != only:
                continue
            res[which].append(run(BASE if which == "base" else NEW, case, runs))
    for which in ["base", "new"]:
        rs = [x for x in res[which] if "ms" in x]
        fails = [x["failure"] for x in res[which] if "failure" in x]
        if not rs:
            if fails:
                print(f"{case:24} {which:4} FAILED {fails[0]}")
            continue
        skip = int(os.environ.get("STEADY", "0"))  # leave out each process's first runs (tier-up)
        ms = [m for x in rs for m in x["ms"][skip:]]
        pm = [statistics.median(x["ms"][skip:]) for x in rs]
        al = [a for x in rs for a in x["alloc"]]
        held = statistics.median([x["heldManaged"] for x in rs])
        peak = max(x["peak"] for x in rs)
        print(f"{case:24} {which:4} median {statistics.median(ms):9.1f} ms (min {min(ms):.1f} max {max(ms):.1f}; pass medians {', '.join(f'{v:.1f}' for v in pm)})  alloc {statistics.median(al)/2**20:8.2f} MiB  held {held/2**20:.1f} MiB  peak RSS {peak>>20} MiB  load {rs[-1]['load0']}" + (f"  FAILS {len(fails)}" if fails else ""))
    sys.stdout.flush()
