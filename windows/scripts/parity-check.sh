#!/usr/bin/env bash
# The parity contract (prd-spit-mac-windows.md rule 22): every test in these Mac files is ported to a C#
# class of the same name, as a method with exactly the same name. Two implementations of one set of rules
# drift apart, and shared test cases are the only defence — so a missing, extra or renamed test fails here.
set -euo pipefail
cd "$(dirname "$0")/../.."

CLASSES=(
  DictationMachineTests TapLatchTests HotkeyInterpreterTests SkipGateTests StitchTests StreamTailTests
  BudgetTests EnergyGateTests OutboxTests RefineServiceTests SyncServiceTests InsightsTests
  TextInjectorRoutingTests RingBufferTests
)

FAILED=0
SWIFT_TOTAL=0
CS_TOTAL=0
for class in "${CLASSES[@]}"; do
  swift="mac/VoiceTests/$class.swift"
  cs="windows/Spit.Core.Tests/$class.cs"
  if [[ ! -f "$cs" ]]; then
    echo "FAIL  $class: $cs is missing"
    FAILED=1
    continue
  fi
  swift_names=$(grep -oE 'func test[A-Za-z0-9_]+' "$swift" | sed 's/^func //' | sort)
  # A ported test is a [Fact] method; take the name from its signature.
  cs_names=$(grep -oE '(void|Task) test[A-Za-z0-9_]+[[:space:]]*\(' "$cs" | sed -E 's/^(void|Task) //; s/[[:space:]]*\($//' | sort)
  facts=$(grep -cE '\[Fact' "$cs" || true)
  swift_count=$(printf '%s\n' "$swift_names" | grep -c . || true)
  cs_count=$(printf '%s\n' "$cs_names" | grep -c . || true)
  SWIFT_TOTAL=$((SWIFT_TOTAL + swift_count))
  CS_TOTAL=$((CS_TOTAL + cs_count))
  if [[ "$swift_names" != "$cs_names" || "$facts" -ne "$cs_count" ]]; then
    echo "FAIL  $class: Swift $swift_count, C# $cs_count, [Fact] $facts"
    diff <(printf '%s\n' "$swift_names") <(printf '%s\n' "$cs_names") | sed 's/^/      /' || true
    FAILED=1
  else
    echo "PASS  $class ($swift_count)"
  fi
done

echo "Swift $SWIFT_TOTAL, C# $CS_TOTAL"
exit $FAILED
