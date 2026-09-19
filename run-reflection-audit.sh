#!/usr/bin/env bash
# Reflection audit of the emitted generic parameters, rather than the exception.
#
# Every emitted generic parameter's correct name is known from the member it belongs to, so after
# the concurrent window reflection can simply be asked for each one. The serial row is the control:
# same emit, same audit, threads released one at a time.
#
# The prewarmed row binds every parameter single-threaded before the concurrent window. It shows
# that the mismatches and the invocation failures are not the same event.
#
# A mismatch here is a stale metadata rid. Every type load in a dynamic module runs a metadata save
# pass that sorts the GenericParam table and renumbers its rids, and the runtime caches rids that
# nothing invalidates. See "Root cause" in CORECLR-INVESTIGATION.md.
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ITER=${ITER:-150}
COPIES=${COPIES:-2}

EXE="$ROOT/src/ReflectionAudit/bin/Debug/net10.0/ReflectionAudit.exe"
( cd "$ROOT/src/ReflectionAudit" && dotnet build -clp:ErrorsOnly -nologo -p:Variant= -p:Variant2= ) > "$ROOT/build.log" 2>&1 || { cat "$ROOT/build.log"; exit 1; }

field() { grep '^RESULT' | grep -oE "$1=[0-9]+/[0-9]+" | sed "s/$1=//"; }

printf "%-24s %-14s %s\n" "mode" "failures" "mismatches"
printf "%s\n" "-------------------------+---------------+------------"

run() {
    local label=$1; shift
    local out
    out=$("$EXE" "$ITER" "$COPIES" auditall "$@" 2>&1)
    printf "%-24s %-14s %s\n" "$label" \
        "$(echo "$out" | field failures)" "$(echo "$out" | field mismatches)"
}

run "serial (control)" serial
run "concurrent"
run "concurrent, prewarmed" prewarm

echo
echo "GenericParam owner column, sampled on throwaway modules:"
"$EXE" 1 "$COPIES" auditall dumptable 2>&1 | grep -E "owner column monotonic"
