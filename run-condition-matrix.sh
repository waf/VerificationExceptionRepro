#!/usr/bin/env bash
# All three harnesses, one condition changed per row, two proxy types per module.
#   CastleHarness  - DynamicProxy from a console application
#   FullEmitter    - tpflueger's System.Reflection.Emit emitter, unreduced
#   MinimalEmitter - the same emitter reduced to what still fails
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG="$ROOT/build.log"
ITER=${ITER:-600}
VARIANTS=${VARIANTS:-"BASELINE SCALAR NO_CONSTRAINT NO_SIBLING OWN_GENERATOR NO_PROCEED ONE_CLASS ONE_TYPE"}

# The generic parameter the runtime names in the exception, which is the interesting part.
named() {
    grep -oE "type argument '[^']+'|GenericArguments\[0\], '[^']+'" \
        | head -1 | grep -oE "'[^']+'" | tr -d "'" | sed 's/.*\.//'
}

failures() { grep '^RESULT' | grep -oE 'failures=[0-9]+/[0-9]+' | sed 's/failures=//'; }

build() {
    ( cd "$ROOT/src/$1" && dotnet build -clp:ErrorsOnly -nologo -p:Variant="$2" -p:Variant2= ) > "$LOG" 2>&1
    ! grep -q "error" "$LOG"
}

run() {
    local project=$1 variant=$2
    build "$project" "$variant" || { echo "BUILD_FAILED"; return; }
    "$ROOT/src/$project/bin/Debug/net10.0/$project.exe" "$ITER" 1 2>&1
}

printf "%-16s | %-10s %-5s | %-10s %-5s | %-10s %-5s\n" \
    "condition" "castle" "names" "full" "names" "minimal" "names"
printf "%s\n" "-----------------+------------------+------------------+-----------------"

for variant in $VARIANTS; do
    [ "$variant" = "BASELINE" ] && variant=""
    label="${variant:-BASELINE}"

    castle=$(run CastleHarness "$variant")
    full=$(run FullEmitter "$variant")
    minimal=$(run MinimalEmitter "$variant")

    printf "%-16s | %-10s %-5s | %-10s %-5s | %-10s %-5s\n" "$label" \
        "$(echo "$castle" | failures)" "$(echo "$castle" | named || true)" \
        "$(echo "$full" | failures)" "$(echo "$full" | named || true)" \
        "$(echo "$minimal" | failures)" "$(echo "$minimal" | named || true)"
done
