#!/usr/bin/env bash
# HOOK: Vale prose gate over every tracked Markdown and C# file. The vendored
# House/SignsOfAi styles (.attest/hooks/vale/vale-styles/, SignsOfAi under
# CC BY-SA 4.0 - see its NOTICE and LICENSE) are copied into Vale's gitignored
# runtime cache on every run; the packaged style packages (proselint, alex,
# write-good) sync once into the same cache on first run, offline after that.
# Ported from Framesaver 2026-08-29, where both registers measured clean
# against this exact style set - here the styles are the drift alarm too.
#
# Speaks the attest line protocol (gitlab.com/gaylatea/attest):
#     unit <TAB> pass|fail <TAB> title [<TAB> detail]
set -u
cd "$(dirname "$0")/../../.." || exit 1

command -v vale >/dev/null 2>&1 || {
    printf '(vale)\tfail\tvale not on PATH\tpinned in mise.toml [tools] -- run through a mise task or mise exec\n'
    exit 1
}
command -v git >/dev/null 2>&1 || {
    printf '(vale)\tfail\tgit not on PATH\tthe gate enumerates tracked docs only\n'
    exit 1
}

if [ ! -d .attest/hooks/vale/vale-styles/SignsOfAi ]; then
    printf '(vale)\tfail\tvendored styles missing\t.attest/hooks/vale/vale-styles has not been copied in yet\n'
    exit 1
fi

if [ ! -d bin/vale-styles ] || [ -z "$(ls -A bin/vale-styles 2>/dev/null)" ]; then
    vale sync --config=.vale.ini >&2 || {
        printf '(vale)\tfail\tvale sync failed\tcould not populate bin/vale-styles\n'
        exit 1
    }
fi
mkdir -p bin/vale-styles/House bin/vale-styles/SignsOfAi
cp .attest/hooks/vale/vale-styles/House/*.yml bin/vale-styles/House/
cp .attest/hooks/vale/vale-styles/SignsOfAi/*.yml bin/vale-styles/SignsOfAi/

files=()
while IFS= read -r file; do
    files+=("$file")
done < <(git ls-files '*.md' '*.cs')
if [ "${#files[@]}" -eq 0 ]; then
    printf '(vale)\tpass\tno markdown or C# files tracked\t\n'
    exit 0
fi

out="$(vale --config=.vale.ini --output=line "${files[@]}" 2>&1)"
vale_code=$?
# vale exits non-zero when it flags anything - but also when it fails to run
# (broken styles, unreadable config). Parsed fail rows mean findings; a
# non-zero exit with NO parsed rows is a broken run and must not read green.
fail=0
flagged=()
while IFS= read -r line; do
    if [[ $line =~ ^([^:]+):([0-9]+):([0-9]+):([^:]+):(.*)$ ]]; then
        file="${BASH_REMATCH[1]}"
        lineno="${BASH_REMATCH[2]}"
        rule="${BASH_REMATCH[4]}"
        msg="${BASH_REMATCH[5]}"
        flagged+=("$file")
        printf '%s:%s\tfail\t%s\t%.300s\n' "$file" "$lineno" "$rule" "$msg"
        fail=1
    fi
done <<<"$out"

if [ "$fail" -eq 0 ] && [ "$vale_code" -ne 0 ]; then
    printf '(vale)\tfail\tvale exited %s with no parseable findings\tbroken styles or config - run: vale --config=.vale.ini --output=line\n' "$vale_code"
    exit 1
fi

for f in "${files[@]}"; do
    seen=0
    if [ "${#flagged[@]}" -gt 0 ]; then
        for flagged_file in "${flagged[@]}"; do
            [ "$flagged_file" = "$f" ] && seen=1
        done
    fi
    [ "$seen" -eq 1 ] || printf '%s\tpass\tvale clean\t\n' "$f"
done

exit "$fail"
