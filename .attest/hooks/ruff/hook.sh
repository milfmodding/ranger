#!/usr/bin/env bash
# HOOK: Python lint over harness/ (rules in ruff.toml - the
# default rule set only; widen by measurement, not by default).
#
# --output-format=concise is load-bearing: ruff's default pretty format puts
# the location on a ` --> path:line:col` line that no line-oriented parser
# should have to chase, and this hook's first version parsed exactly that
# badly - ruff's findings parsed to zero rows and the hook printed a false
# pass for a full day (found 2026-08-29 when the harness/ rescan surfaced a
# real F401 the first harvest had "passed"). The no-parse guard below is the
# second half of the same lesson: findings that do not parse are a run
# failure, never a clean result.
#
# Speaks the attest line protocol (gitlab.com/gaylatea/attest):
#     unit <TAB> pass|fail <TAB> title [<TAB> detail]
set -u
cd "$(dirname "$0")/../../.." || exit 1

command -v ruff >/dev/null 2>&1 || {
    printf '(ruff)\tfail\truff not on PATH\tpinned in mise.toml [tools] -- run through a mise task or mise exec\n'
    exit 1
}

out="$(ruff check --output-format=concise harness/ 2>&1)"
code=$?

# 0 = clean, 1 = findings, anything else = the run itself broke.
if [ "$code" -gt 1 ]; then
    printf '(ruff)\tfail\truff failed (exit %s)\t%.300s\n' "$code" "$(printf '%s' "$out" | head -c 300 | tr '\n' ' ')"
    exit 1
fi

fail=0
flagged=()
while IFS= read -r row; do
    # ruff's default formatter: <file>:<line>:<col>: <CODE> <message>
    if [[ $row =~ ^([^ :]+):([0-9]+):[0-9]+:[[:space:]]([^[:space:]]+)[[:space:]](.*)$ ]]; then
        file="${BASH_REMATCH[1]}"
        lineno="${BASH_REMATCH[2]}"
        rule="${BASH_REMATCH[3]}"
        msg="${BASH_REMATCH[4]}"
        flagged+=("$file")
        printf '%s:%s\tfail\t%s\t%.300s\n' "$file" "$lineno" "$rule" "$msg"
        fail=1
    fi
done <<<"$out"

if [ "$fail" -eq 0 ] && [ "$code" -eq 1 ]; then
    # ruff reported findings that parsed to nothing - formatter drift or a
    # broken run. Either way it must not read as a silent pass.
    printf '(ruff)\tfail\truff reported errors with no parseable findings\trun: ruff check --output-format=concise harness/\n'
    exit 1
fi

if [ "$fail" -eq 0 ]; then
    printf '(ruff)\tpass\tharness/ clean\t\n'
fi

exit "$fail"
