#!/usr/bin/env bash
# HOOK: structural (AST) rule layer over .ast-grep/rules/ (sgconfig.yml points
# there). The rule set is deliberately EMPTY at adoption (2026-08-29): this
# repo's first structural invariant has not been named yet, and inventing a
# rule to justify the gate would be worse than an honest placeholder. The pass
# row below says so, so the placeholder can never masquerade as coverage. When
# the first rule lands, this hook upgrades automatically: with any rule file
# present it runs findings parsing (json=compact, one protocol row per
# finding) instead of the placeholder.
#
# Speaks the attest line protocol (gitlab.com/gaylatea/attest):
#     unit <TAB> pass|fail <TAB> title [<TAB> detail]
set -u
cd "$(dirname "$0")/../../.." || exit 1

command -v ast-grep >/dev/null 2>&1 || {
    printf '(ast-grep)\tfail\tast-grep not on PATH\tpinned in mise.toml [tools] -- run through a mise task or mise exec\n'
    exit 1
}

out="$(mktemp)"
err="$(mktemp)"
trap 'rm -f "$out" "$err"' EXIT

ast-grep scan --json=compact >"$out" 2>"$err"
code=$?

# 0 = clean, 1 = findings, anything else = the scan itself broke.
if [ "$code" -gt 1 ]; then
    printf '(ast-grep)\tfail\tast-grep scan failed (exit %s)\t%.300s\n' "$code" "$(cat "$err")"
    exit 1
fi

if [ -z "$(ls -A .ast-grep/rules 2>/dev/null)" ] && [ ! -s "$out" ]; then
    printf '(ast-grep)\tpass\tno rules registered yet - placeholder until the first invariant lands\t\n'
    exit 0
fi

fail=0
while IFS=$'\t' read -r file line rule msg; do
    [ -n "$file" ] || continue
    printf '%s:%s\tfail\t%s\t%.300s\n' "$file" "$line" "$rule" "$msg"
    fail=1
done < <(python3 -c "
import json, sys
for m in json.load(open(sys.argv[1])):
    line = m['range']['start']['line']
    msg = m['message'].replace(chr(9), ' ').replace(chr(10), ' ')
    print(f\"{m['file']}\t{line + 1}\t{m['ruleId']}\t{msg}\")
" "$out")

if [ "$fail" -eq 0 ]; then
    printf '(ast-grep)\tpass\tscan clean\t\n'
fi

exit "$fail"
