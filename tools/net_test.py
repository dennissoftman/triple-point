"""Two real game processes in a networked match over localhost ENet, unattended. Run from the repo root:

    python tools/net_test.py [--minutes=3] [--fault-at=<tick>] [--snap=<png>] [--exe=<exported TriplePoint.exe>]

Each process is headless and the commander AI plays its side (--net-ai), its commands going through
lockstep like clicks. Both print their state hash every 10 s of game time and quit at the same tick;
the test passes if they printed the same hashes and reached the end. With --fault-at, the joiner destroys
a unit at that tick on its own (as a CPU difference might): then both must report the desync, and the
host's report must name the unit. --snap runs the host in a window (off-screen) and saves its last frame
there. Uses the editor build (the project in godot/) unless --exe is given.
"""
import argparse
import re
import subprocess
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GODOT = "C:/Program Files/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
PORT = 7790


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--minutes", type=float, default=3)
    parser.add_argument("--fault-at", type=int, default=-1)
    parser.add_argument("--exe")
    parser.add_argument("--snap")
    parser.add_argument("--godot", default=GODOT)
    args = parser.parse_args()

    quit_at = int(args.minutes * 60 * 20)
    base = [str(Path(args.exe).resolve())] if args.exe else [args.godot, "--path", str(ROOT / "godot")]
    common = ["--headless", "--fixed-fps", "60"]
    net = ["--net-ai", "--net-check=200", f"--net-quit-at={quit_at}"]
    host_common = ["--position", "-10000,-10000", "--fixed-fps", "60"] if args.snap else common
    host = base + host_common + ["--", f"--host={PORT}", "--side=blue"] + net + ([f"--net-snap={args.snap}"] if args.snap else [])
    join = base + common + ["--", f"--join=127.0.0.1:{PORT}"] + net + ([f"--net-fault-at={args.fault_at}"] if args.fault_at >= 0 else [])

    outputs = {}

    def run(name, command):
        try:
            result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=300)
            outputs[name] = result.stdout + result.stderr
        except subprocess.TimeoutExpired as e:  # killed: keep what it said
            text = lambda b: b.decode("utf-8", "replace") if isinstance(b, bytes) else (b or "")
            outputs[name] = text(e.stdout) + text(e.stderr) + "\nTIMED OUT"

    threads = [threading.Thread(target=run, args=("host", host))]
    threads[0].start()
    time.sleep(2)  # the host is listening before the joiner knocks
    threads.append(threading.Thread(target=run, args=("join", join)))
    threads[1].start()
    for t in threads:
        t.join()

    checks = {name: re.findall(r"^net-(?:check|quit) (\d+) ([0-9a-f]+)", text, re.M) for name, text in outputs.items()}
    desyncs = {name: re.findall(r"DESYNC at tick (\d+): (.*)", text) for name, text in outputs.items()}
    for name, text in outputs.items():
        for line in text.splitlines():
            if re.search(r"Networked match|DESYNC|Desync report|left the match|net-quit|^  \w+#|Exception|TIMED OUT", line):
                print(f"[{name}] {line}")

    if args.fault_at >= 0:
        ok = all(desyncs[n] for n in outputs) and any("units#" in line for line in outputs["host"].splitlines() if line.startswith("  "))
        print("PASS: both found the desync and the host's report names the unit" if ok else "FAIL: the desync wasn't found and explained on both")
        return 0 if ok else 1

    host_checks, join_checks = dict(checks["host"]), dict(checks["join"])
    common_ticks = sorted(set(host_checks) & set(join_checks), key=int)
    same = all(host_checks[t] == join_checks[t] for t in common_ticks)
    ended = str(quit_at) in host_checks and str(quit_at) in join_checks
    ok = same and ended and not desyncs["host"] and not desyncs["join"] and len(common_ticks) >= quit_at // 200
    print(f"{len(common_ticks)} hashes compared, {'all the same' if same else 'DIFFERENT'}; "
          f"{'both reached' if ended else 'NOT both at'} tick {quit_at}")
    print("PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
