#!/usr/bin/env bash
# Runtime AssemblyBuilder against PersistedAssemblyBuilder, with N proxy types in ONE assembly.
# This is the experiment that separates the builder from the one-proxy-type-per-assembly question.
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ITER=${ITER:-300}
EMITTER=${EMITTER:-MinimalEmitter}

EXE="$ROOT/src/$EMITTER/bin/Debug/net10.0/$EMITTER.exe"
( cd "$ROOT/src/$EMITTER" && dotnet build -clp:ErrorsOnly -nologo -p:Variant= -p:Variant2= ) > "$ROOT/build.log" 2>&1 || { cat "$ROOT/build.log"; exit 1; }

printf "%-28s %-14s %-14s %s\n" "builder" "2 proxy types" "4" "8"
for mode in "" "persisted"; do
    label=$([ -z "$mode" ] && echo "AssemblyBuilder" || echo "PersistedAssemblyBuilder")
    cells=""
    for copies in 1 2 4; do
        out=$("$EXE" "$ITER" "$copies" $mode 2>&1 | grep '^RESULT')
        cells="$cells $(printf '%-14s' "$(echo "$out" | grep -oE 'failures=[0-9]+/[0-9]+' | sed 's/failures=//')")"
    done
    printf "%-28s%s\n" "$label" "$cells"
done
