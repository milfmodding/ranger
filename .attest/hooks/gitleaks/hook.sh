#!/usr/bin/env bash
# HOOK: no secret in the working tree, checked with gitleaks' built-in
# detectors (private keys, provider API key shapes, auth headers, ...).
#
# Working tree only, not history - history does not change commit to commit,
# so re-scanning it every run would pay a cost with no new signal. Run a
# one-time `gitleaks git .` from the repo root after landing notable new
# material instead.
#
# Findings get hand-checked before anything is suppressed: a real leak is
# fixed, a structural false positive goes to .gitleaks.toml or
# .gitleaksignore with the specific reason it is safe - never a blanket
# suppression.
#
# Speaks the attest line protocol (gitlab.com/gaylatea/attest):
#     unit <TAB> pass|fail <TAB> title [<TAB> detail]
set -u
cd "$(dirname "$0")/../../.." || exit 1

command -v gitleaks >/dev/null 2>&1 || {
    printf '(gitleaks)\tfail\tgitleaks not on PATH\tpinned in mise.toml [tools] -- run through a mise task or mise exec\n'
    exit 1
}

report="$(mktemp)"
trap 'rm -f "$report"' EXIT

if gitleaks dir . --no-banner --report-format json --report-path "$report" >/dev/null 2>&1; then
    printf '(gitleaks)\tpass\tno secrets found\t\n'
    exit 0
fi

fail=0
while IFS=$'\t' read -r file line rule; do
    [ -n "$file" ] || continue
    printf '%s:%s\tfail\t%s\t%s\n' "$file" "$line" "$rule" "possible secret -- verify, then fix or extend .gitleaksignore with the reason"
    fail=1
done < <(python3 -c "
import json, sys
for leak in json.load(open(sys.argv[1])):
    print(f\"{leak['File']}\t{leak.get('StartLine','')}\t{leak['RuleID']}\")
" "$report")

if [ "$fail" -eq 0 ]; then
    # gitleaks exited non-zero but the report parsed to nothing - a run
    # failure, not a finding; don't let it read as a silent pass.
    printf '(gitleaks)\tfail\tgitleaks exited non-zero with no parseable findings\trun: gitleaks dir . --no-banner\n'
    fail=1
fi

exit "$fail"
