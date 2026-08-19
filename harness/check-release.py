"""Release gate: refuse a forge push until Sophia has written the prose and read the code.

SOPHIA'S TWO RULES, 2026-07-30, verbatim in substance:

  1. Public-facing prose is HERS. READMEs, the forge mod description, any public docs. Agents
     may review what she has written and suggest changes; agents may not write it.
  2. Before a forge push she MUST review the code herself and demonstrate she understands what
     it does and how to update it on her own.

Internal coordination and analysis prose is exempt - she said so explicitly - and this gate does
not look at it.

WHY THIS IS A SCRIPT AND NOT A NOTE IN A CHECKLIST. Twice this week an agent shipped a prose
guarantee with nothing implementing it: a promise that mixed-build strata would be treated as
absent, and a drift gate written as a comment. A rule that lives only in a document is a rule
that gets skipped on the evening it matters. So this refuses, with an exit code.

WHAT IT CANNOT DO, said first because the gap is the whole risk. **It cannot verify
understanding.** No script can. What it can verify is that a CLAIM of understanding exists, that
the claim names the commit it was made against, and that the file has not changed since - so a
stale claim is caught even though a false one is not. Gate C is the only mechanical proxy for
"can update it on my own" and it is a proxy, not a proof.

THE CLASSIFICATION IS HERS, NOT MINE. This reads `harness/release-manifest.json`, which she owns.
If it is absent the gate REFUSES rather than passes - an undecided question is not an all-clear,
and I do not get to decide on her behalf which files are public-facing.

EXIT 0 every gate passed, 1 a gate FAILED, 2 REFUSED (cannot tell).
"""
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
AI_TRAILER = "Co-Authored-By: Claude"

# `--manifest <path>` exists so a DRAFT can be checked without committing it, and so this
# file's own failure paths can be exercised without leaving a test manifest in the repo where
# it would later be mistaken for Sophia's. The first attempt at that test copied the script to
# a scratchpad instead, which changed how REPO is derived - so it read the wrong repository,
# found no source files, and two gates never ran. A test harness that relocates the thing under
# test shares an assumption with it.
_mf = None
for _i, _a in enumerate(sys.argv):
    if _a == "--manifest" and _i + 1 < len(sys.argv):
        _mf = sys.argv[_i + 1]
MANIFEST = _mf or os.path.join(HERE, "release-manifest.json")

fails, refusals, notes = [], [], []


def git(*args):
    r = subprocess.run(["git", "-C", REPO] + list(args), capture_output=True, text=True)
    return r.stdout if r.returncode == 0 else ""


def commits_touching(path):
    """(sha, had_ai_trailer) for every commit touching this path, newest first."""
    out = git("log", "--format=%H%x01%B%x02", "--", path)
    rows = []
    for chunk in out.split("\x02"):
        chunk = chunk.strip()
        if not chunk or "\x01" not in chunk:
            continue
        sha, body = chunk.split("\x01", 1)
        rows.append((sha.strip(), AI_TRAILER in body))
    return rows


def changed_since(path, sha):
    """True when `path` differs between `sha` and HEAD."""
    return bool(git("diff", "--name-only", sha, "HEAD", "--", path).strip())


def main():
    if not os.path.isfile(MANIFEST):
        print("REFUSED: no harness/release-manifest.json.\n")
        print("This gate cannot decide on Sophia's behalf which files are public-facing, and an")
        print("undecided question is not an all-clear. She writes it. Shape:\n")
        print(json.dumps({
            "publicProse": ["README.md", "COMPATIBILITY.md", "LICENSE"],
            "internalExempt": ["COORDINATION.md", "FINDINGS.md", "CLAUDE.md"],
            "configDescriptionsReviewedAt": "<sha, or null>",
            "codeReviewedAt": {"Plugin.cs": "<sha>", "Patches/…": "<sha>"},
            "canUpdateUnaided": {"note": "subsystem -> a human-only commit sha touching it"},
        }, indent=2))
        print("\nEvery `<sha>` means: the commit she read the file AT. A file that has changed")
        print("since is reported as STALE rather than reviewed.")
        return 2

    m = json.load(open(MANIFEST, encoding="utf-8-sig"))

    # ---- GATE A: public prose has at least one human-only commit --------------
    pub = m.get("publicProse") or []
    if not pub:
        refusals.append("manifest lists no publicProse - if that is deliberate, say so with an "
                        "empty list AND a note; an absent key reads as unanswered")
    for rel in pub:
        path = os.path.join(REPO, rel)
        if not os.path.exists(path):
            notes.append("%s listed as public prose but does not exist yet (LICENSE is expected "
                         "to be one of these)" % rel)
            continue
        rows = commits_touching(rel)
        if not rows:
            notes.append("%s exists but is untracked - not yet in history" % rel)
            continue
        human = [s for s, ai in rows if not ai]
        if not human:
            fails.append("%s: ALL %d commits touching it carry the AI co-author trailer. No "
                         "human-only commit has ever touched this file, so its prose has no "
                         "human provenance in the record. Sophia writes it and commits it "
                         "without the trailer." % (rel, len(rows)))
        else:
            notes.append("%s: %d of %d commits are human-only (newest %s)"
                         % (rel, len(human), len(rows), human[0][:7]))

    # ---- GATE A2: the config descriptions, which are public prose in a code file ----
    plugin = os.path.join(REPO, "Plugin.cs")
    if os.path.isfile(plugin):
        src = open(plugin, encoding="utf-8", errors="replace").read()
        # BOTH overloads, because both produce text the user reads. Counting only
        # `new ConfigDescription(` returned 17 against 37 bound entries - the other 20 pass a
        # plain string to Config.Bind and are just as public. A guard that undercounts the
        # thing it guards by more than half is worse than a loud absence.
        n_rich = len(re.findall(r"new ConfigDescription\(", src))
        n_bind = len(re.findall(r"Config\.Bind\(", src))
        n_desc = n_bind if n_bind >= n_rich else n_rich
        at = m.get("configDescriptionsReviewedAt")
        if not at:
            fails.append("Plugin.cs holds %d config descriptions and they are PUBLIC PROSE - "
                         "they appear in the user's cfg file and the F12 overlay. "
                         "configDescriptionsReviewedAt is unset, so nobody has recorded reading "
                         "them as user-facing text." % n_desc)
        elif changed_since("Plugin.cs", at):
            fails.append("config descriptions reviewed at %s but Plugin.cs has changed since - "
                         "STALE. Re-read and update the sha." % at[:7])
        else:
            notes.append("%d config descriptions reviewed at %s, Plugin.cs unchanged since"
                         % (n_desc, at[:7]))

    # ---- GATE B: a current review record per shipped source file --------------
    reviewed = m.get("codeReviewedAt") or {}
    shipped = []
    for root, _dirs, files in os.walk(REPO):
        if any(p in root for p in (".git", "obj", "bin", "tests")):
            continue
        for f in files:
            if f.endswith(".cs"):
                shipped.append(os.path.relpath(os.path.join(root, f), REPO).replace("\\", "/"))
    if not shipped:
        refusals.append("found no .cs files to review - refusing rather than passing")
    missing = [s for s in shipped if s not in reviewed]
    stale = [(s, reviewed[s]) for s in shipped if s in reviewed and changed_since(s, reviewed[s])]
    if missing:
        fails.append("%d of %d shipped source files have NO review record: %s"
                     % (len(missing), len(shipped),
                        ", ".join(sorted(missing)[:6]) + (" ..." if len(missing) > 6 else "")))
    for s, sha in stale:
        fails.append("%s reviewed at %s but has CHANGED since - the review is stale, which is "
                     "not the same as absent and not the same as done" % (s, sha[:7]))
    if shipped and not missing and not stale:
        notes.append("all %d shipped source files carry a current review record" % len(shipped))

    # ---- GATE C: a human-only commit per subsystem, as a PROXY for "can update it" ----
    subs = m.get("canUpdateUnaided") or {}
    declared = {k: v for k, v in subs.items() if k != "note"}
    if not declared:
        fails.append("canUpdateUnaided is empty. This is the only mechanical proxy for "
                     "'demonstrate I can update it on my own': one human-only commit per "
                     "subsystem. It is a PROXY, not proof - but an empty one is not even that.")
    for sub, sha in declared.items():
        body = git("log", "-1", "--format=%B", sha)
        if not body:
            fails.append("canUpdateUnaided[%s] = %s - no such commit" % (sub, str(sha)[:7]))
        elif AI_TRAILER in body:
            fails.append("canUpdateUnaided[%s] = %s carries the AI co-author trailer, so it is "
                         "not an unaided change" % (sub, sha[:7]))
        else:
            notes.append("canUpdateUnaided[%s]: %s is human-only" % (sub, sha[:7]))

    # ---- report ----
    for n in notes:
        print("    ok    %s" % n)
    for f in fails:
        print("    FAIL  %s" % f)
    for r in refusals:
        print("    REFUSED %s" % r)

    print()
    print("WHAT THIS GATE DOES NOT CHECK: whether she actually understands the code. It checks")
    print("that a claim exists, names the commit it was made against, and is not stale. A false")
    print("claim passes. Only she can close that gap, and the vegetables are hers to eat.")
    if refusals:
        print("\nREFUSED - could not tell. This is NOT a pass.")
        return 2
    if fails:
        print("\n%d gate(s) FAILED. Do not push to the forge." % len(fails))
        return 1
    print("\nAll release gates passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
