"""Exports a Windows build for playtesters and zips it. Run from the repo root:

    python tools/export_build.py [--godot <path to the Godot .NET console exe>]

Needs the Godot 4.7.2 .NET export templates (Editor > Manage Export Templates; Windows x86_64 is enough).
Builds the C# first and stops if it fails (Godot would quietly export the last good build), exports the
"Windows Desktop" preset (godot/export_presets.cfg) to build/windows, puts the game data beside the
executable (an exported build reads data/ from there: SimHost.LoadData), adds a short how-to, and zips it
all to build/TriplePoint-windows-<commit>.zip. The .NET runtime is bundled: testers install nothing.
"""
import argparse
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GODOT = "C:/Program Files/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
OUT = ROOT / "build" / "windows"

HOW_TO = """Triple Point, playtest build ({commit})

Start TriplePoint.exe. Nothing to install. Windows may warn that the app is unrecognized
(it isn't signed): More info, then Run anyway.

Skirmish: pick your side and the opponent (Easy AI, Normal AI, or Hotseat: two players at one
computer, F2 hands over). Controls in the main menu lists every key and the main rules.

Multiplayer (two players, each on their own computer, both with this same zip): one clicks
Multiplayer, picks a side and Host; the other types the host's address (shown on the host's screen)
and Join; the host clicks Start. Windows asks the host whether TriplePoint may use the network: allow
it. On the same network that's all. Over the internet the host forwards UDP port 7777 on the router
to their computer, or you both install Tailscale (tailscale.com) and use the host's Tailscale address.

After each match a report is written to
    %APPDATA%\\Godot\\app_userdata\\Triple Point\\matches
Paste that path into Explorer's address bar and send the newest file back with your notes.
If a networked match says "Out of sync", please send the folder it names (under ...\\desyncs): that's
a bug, and the folder says where.
"""


def run(args, what):
    print(f"> {what}")
    result = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    output = result.stdout + result.stderr
    errors = [line for line in output.splitlines() if "error" in line.lower() and "0 error" not in line.lower()]
    if result.returncode != 0 or errors:
        print(output[-4000:])
        sys.exit(f"{what} failed")
    return output


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--godot", default=GODOT)
    godot = parser.parse_args().godot

    commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip() or "local"
    dirty = subprocess.run(["git", "status", "--porcelain"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
    if dirty:
        commit += "-modified"

    run([godot, "--headless", "--path", "godot", "--build-solutions", "--quit"], "build the C#")
    if OUT.exists():
        shutil.rmtree(OUT)
    OUT.mkdir(parents=True)
    run([godot, "--headless", "--path", "godot", "--export-release", "Windows Desktop", str(OUT / "TriplePoint.exe")], "export")
    if not (OUT / "TriplePoint.exe").exists():
        sys.exit("export wrote no TriplePoint.exe (are the export templates installed?)")

    (OUT / "data").mkdir()
    for f in (ROOT / "data").glob("*.json"):
        shutil.copy2(f, OUT / "data" / f.name)
    (OUT / "HOW-TO-PLAY.txt").write_text(HOW_TO.format(commit=commit), encoding="utf-8")

    archive = shutil.make_archive(str(ROOT / "build" / f"TriplePoint-windows-{commit}"), "zip", OUT.parent, OUT.name)
    print(f"{archive} ({Path(archive).stat().st_size / 1e6:.0f} MB)")


if __name__ == "__main__":
    main()
