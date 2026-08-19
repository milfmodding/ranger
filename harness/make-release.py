"""Package a Ranger release zip: build, verify, gate, zip. Nothing clever.

Ported from Framesaver's harness/make-release.py 2026-08-19, same day the
capstone cutover made Ranger a real, independently-shippable mod rather than
a partner artifact copied alongside Framesaver's zip. See that file's own
comments for the full argument; this is a straight port with names swapped,
not a redesign.

WHAT "READY" MEANS HERE, and it is not this script's to decide. Two gates
already exist and this script RUNS them rather than replacing them:

  1. harness/check-release.py - Sophia's release gate. Public prose must be
     hers, review records must exist and be current. It REFUSES today (no
     release-manifest.json is a fresh placeholder, not a completed one) and
     that is correct: an undecided question is not an all-clear. This script
     inherits that refusal.

  2. The csproj's own stamp discipline (RefuseDirtyDeploy, ported into
     Ranger.csproj the same day). A release zip built from a dirty tree
     ships a binary whose commit stamp names a commit whose source is not
     inside it. Deploy is opt-in in the build; packaging a zip is the same
     act with a wider audience, so the same refusal applies.

WHAT THE ZIP CONTAINS: BepInEx/plugins/Ranger.dll at the path a user
extracts over a game root, plus README.md, plus LICENSE if it exists.
The DLL is built by this script (Release, no deploy) - never a stale
bin/Release copy, because "the zip matches the gate" is a statement about
the stamp and the stamp comes from THIS build.

EXIT 0 zip written, 1 refused/failed. --skip-gate produces an INTERNAL
test zip without Sophia's gate - it is loudly named as internal and is
for verifying this script's own machinery, never for a forge push.

Usage:
  python harness/make-release.py
  python harness/make-release.py --skip-gate   (internal test zip only)
"""
import hashlib
import os
import re
import subprocess
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
ARTIFACTS = os.path.join(REPO, "artifacts")


def die(msg, code=1):
    print("REFUSED: " + msg)
    sys.exit(code)


def git(*args):
    r = subprocess.run(["git", "-C", REPO] + list(args),
                       capture_output=True, text=True)
    if r.returncode != 0:
        die("git %s failed: %s" % (args[0], r.stderr.strip()))
    return r.stdout.strip()


def main():
    skip_gate = "--skip-gate" in sys.argv

    # ---- Gate 1: Sophia's release gate ---------------------------------
    if skip_gate:
        print("SKIP-GATE: internal test zip; Sophia's release gate was NOT run.")
    else:
        r = subprocess.run([sys.executable,
                            os.path.join(HERE, "check-release.py")])
        if r.returncode != 0:
            die("check-release.py exited %d. The release is not ready, "
                "so no release zip. Run it directly to see which gate; "
                "--skip-gate builds an internal test zip only." % r.returncode)

    # ---- Gate 2: clean tree (same reasoning as RefuseDirtyDeploy) ------
    dirty = git("status", "--porcelain")
    if dirty:
        die("working tree is dirty, so the build stamp would name a commit "
            "this zip was not built from:\n" + dirty)

    head = git("rev-parse", "HEAD")

    # ---- Build (compile only; deploy is not wanted here) ---------------
    print("Building Release (no deploy)...")
    r = subprocess.run(["dotnet", "build", REPO, "-c", "Release"],
                       capture_output=True, text=True, shell=True)
    # shell=True: dotnet is dotnet.exe; without it CreateProcess fails.
    if r.returncode != 0:
        print(r.stdout[-3000:])
        print(r.stderr[-3000:])
        die("dotnet build failed")

    dll = os.path.join(REPO, "bin", "Release", "Ranger.dll")
    if not os.path.isfile(dll):
        die("build reported success but %s is missing" % dll)

    # ---- Stamp check: the DLL must carry HEAD's sha in its metadata ----
    raw = open(dll, "rb").read()
    if head.encode("ascii") not in raw:
        die("built DLL does not contain HEAD's sha (%s) - it is unstamped. "
            "Anonymous-log binaries are exactly what the csproj refuses to "
            "deploy; a zip is not a lesser act." % head[:7])

    version = "unknown"
    csproj = open(os.path.join(REPO, "Ranger.csproj"),
                  encoding="utf-8", errors="replace").read()
    m = re.search(r"<AssemblyVersion>([^<]+)</AssemblyVersion>", csproj)
    if m:
        version = m.group(1)

    stamp = version + "-" + head[:7]
    dll_md5 = hashlib.md5(raw).hexdigest()

    # ---- Package --------------------------------------------------------
    os.makedirs(ARTIFACTS, exist_ok=True)
    suffix = "-INTERNAL" if skip_gate else ""
    zpath = os.path.join(ARTIFACTS, "Ranger-%s%s.zip" % (stamp, suffix))
    if os.path.exists(zpath):
        die("%s already exists - a re-release of the same commit needs a "
            "conscious deletion first, not a silent overwrite"
            % os.path.basename(zpath))

    members = [("BepInEx/plugins/Ranger.dll", dll)]
    readme = os.path.join(REPO, "README.md")
    if os.path.isfile(readme):
        members.append(("README.md", readme))
    license_ = os.path.join(REPO, "LICENSE")
    if os.path.isfile(license_):
        members.append(("LICENSE", license_))

    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as zf:
        for arcname, src in members:
            zf.write(src, arcname)

    # ---- Report, verified against the file just written -----------------
    names = zipfile.ZipFile(zpath).namelist()
    size = os.path.getsize(zpath)
    print()
    print("WROTE %s (%d bytes)" % (zpath, size))
    for n in names:
        print("  %s" % n)
    print("  Ranger.dll md5 %s" % dll_md5)
    print("  built from %s (clean tree, stamp verified inside the DLL)"
          % head[:7])
    if skip_gate:
        print("  INTERNAL TEST ZIP - do not push to the forge.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
