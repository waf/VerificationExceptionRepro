#!/usr/bin/env bash
# Runs the issue's repro in its original shape: xunit, a shared static ProxyGenerator, one proxy
# built per test class. Use this to check that CastleHarness, which restructures that repro into a
# console loop, still responds to every condition the same way.
#
# The unit here is different: a whole `dotnet test` run either fails or it does not, so this reports
# runs failed out of runs, not failures out of invocations.
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG="$ROOT/build.log"
RUNS=${RUNS:-20}
VARIANTS=${VARIANTS:-"BASELINE SCALAR NO_CONSTRAINT NO_SIBLING OWN_GENERATOR NO_PROCEED ONE_CLASS ONE_TYPE"}

named() {
    grep -oE "type argument '[^']+'|GenericArguments\[0\], '[^']+'" \
        | head -1 | grep -oE "'[^']+'" | tr -d "'" | sed 's/.*\.//'
}

printf "%-16s | %-12s %s\n" "condition" "runs failed" "names"
printf "%s\n" "-----------------+--------------------"

for variant in $VARIANTS; do
    [ "$variant" = "BASELINE" ] && variant=""
    label="${variant:-BASELINE}"

    ( cd "$ROOT/src/CastleXunitHarness" && dotnet build -clp:ErrorsOnly -nologo -p:Variant="$variant" ) > "$LOG" 2>&1
    if grep -q "error" "$LOG"; then
        printf "%-16s | build FAILED\n" "$label"
        continue
    fi

    failed=0
    name=""
    for _ in $(seq 1 "$RUNS"); do
        out=$( cd "$ROOT/src/CastleXunitHarness" && dotnet test --no-build --nologo 2>&1 )
        if echo "$out" | grep -qE "Failed [A-Za-z]+\.Test"; then
            failed=$((failed + 1))
            if [ -z "$name" ]; then name=$(echo "$out" | named); fi
        fi
    done

    printf "%-16s | %-12s %s\n" "$label" "$failed/$RUNS" "${name:--}"
done
