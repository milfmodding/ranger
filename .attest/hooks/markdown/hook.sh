#!/usr/bin/env bash
# HOOK: every tracked .md file must survive rumdl (markdownlint-compatible
# rule IDs, single prebuilt Rust binary). Replaced markdownlint-cli2 on
# 2026-08-29 - same rules on day one, since rumdl reads markdownlint config
# natively and the pin swap also deleted the npm supply-chain trust exemption
# the cli2 dependency tree carried. Rules and overrides live in rumdl.toml.
#
# Files are enumerated from the index (git ls-files '*.md'), not passed as
# globs: the gate lints exactly what is tracked and walks nothing.
#
# Speaks the attest line protocol (gitlab.com/gaylatea/attest):
#     unit <TAB> pass|fail <TAB> title [<TAB> detail]
set -u
cd "$(dirname "$0")/../../.." || exit 1

command -v rumdl >/dev/null 2>&1 || {
    printf '(markdown)\tfail\trumdl not on PATH\tpinned in mise.toml [tools] -- run through a mise task or mise exec\n'
    exit 1
}
command -v git >/dev/null 2>&1 || {
    printf '(markdown)\tfail\tgit not on PATH\tthe gate enumerates tracked docs only\n'
    exit 1
}

files=()
while IFS= read -r file; do
    files+=("$file")
done < <(git ls-files '*.md')
if [ "${#files[@]}" -eq 0 ]; then
    printf '(markdown)\tpass\tno markdown files tracked\t\n'
    exit 0
fi

out="$(rumdl check --output-format=text "${files[@]}" 2>&1)"
code=$?

fail=0
flagged=()
while IFS= read -r row; do
    # rumdl text format: <file>:<line>:<col>: [<RULE>] <message>
    if [[ $row =~ ^([^ :]+):([0-9]+):[0-9]+:[[:space:]]\[([^]]+)\][[:space:]](.*)$ ]]; then
        file="${BASH_REMATCH[1]}"
        lineno="${BASH_REMATCH[2]}"
        rule="${BASH_REMATCH[3]}"
        msg="${BASH_REMATCH[4]}"
        flagged+=("$file")
        printf '%s:%s\tfail\t%s\t%.300s\n' "$file" "$lineno" "$rule" "$msg"
        fail=1
    fi
done <<<"$out"

if [ "$code" -gt 1 ]; then
    printf '(markdown)\tfail\trumdl failed (exit %s)\t%.300s\n' "$code" "$(printf '%s' "$out" | head -c 300 | tr '\n' ' ')"
    exit 1
fi

if [ "$fail" -eq 0 ] && [ "$code" -eq 1 ]; then
    # exited non-zero but nothing parsed as a finding - a run failure or an
    # output-format drift, not a clean result. Don't let it read as a silent
    # pass; the ruff hook learned this lesson the hard way first.
    printf '(markdown)\tfail\trumdl reported errors with no parseable findings\trun: rumdl check --output-format=text\n'
    exit 1
fi

for f in "${files[@]}"; do
    seen=0
    if [ "${#flagged[@]}" -gt 0 ]; then
        for flagged_file in "${flagged[@]}"; do
            [ "$flagged_file" = "$f" ] && seen=1
        done
    fi
    [ "$seen" -eq 1 ] || printf '%s\tpass\tmarkdownlint clean\t\n' "$f"
done

exit "$fail"
