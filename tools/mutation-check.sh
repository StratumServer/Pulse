#!/usr/bin/env bash
# Mutation check for the aggregator and the exposition writer: apply one representative
# mutation at a time and require the unit tests to fail on every one of them.
#
# This is the stand-in for Stryker. Stryker 4.16 on the .NET 10 SDK finds the tests but runs
# every mutant against the unmutated assembly, so every mutant "survives" in seconds (see the
# tracking issue). A hand-verified mutation here proved the suite does catch real faults; this
# script keeps that proof repeatable and cheap enough for CI. Retire it the day dotnet stryker
# reports a real score on this solution.
#
# Requires a clean working tree for the mutated files: each mutation is reverted with
# git checkout -- <file>.
set -u
cd "$(dirname "$0")/.."

FAILS=0
TOTAL=0

run_tests() {
    dotnet test Pulse.Tests/Pulse.Tests.csproj -c Release --nologo -v q >/dev/null 2>&1
    return $?
}

mutate() { # <file> <sed -E expression> <label>
    local file="$1" expr="$2" label="$3"
    TOTAL=$((TOTAL + 1))
    sed -i -E "$expr" "$file"
    if git diff --quiet -- "$file"; then
        echo "INERT (pattern no longer matches the source): $label"
        FAILS=$((FAILS + 1))
        return
    fi
    if run_tests; then
        echo "SURVIVED: $label"
        FAILS=$((FAILS + 1))
    else
        echo "killed:   $label"
    fi
    git checkout -- "$file"
}

if ! git diff --quiet -- Pulse/PrometheusText.cs Pulse/MetricsAggregator.cs; then
    echo "Pulse/PrometheusText.cs or Pulse/MetricsAggregator.cs has uncommitted changes; refusing to mutate over them."
    exit 2
fi

if ! run_tests; then
    echo "The unit tests are red before any mutation; fix that first."
    exit 2
fi

mutate Pulse/PrometheusText.cs \
    's/cumulative \+= sample\.Buckets\[i\];/cumulative -= sample.Buckets[i];/' \
    "writer: histogram cumulation flipped to subtraction"

mutate Pulse/PrometheusText.cs \
    's/MetricKind\.Counter => "counter",/MetricKind.Counter => "gauge",/' \
    "writer: counter TYPE line lies"

mutate Pulse/PrometheusText.cs \
    's/return value\.ToString\(CultureInfo\.InvariantCulture\);/return value.ToString(CultureInfo.CurrentCulture);/' \
    "writer: locale-dependent number formatting"

mutate Pulse/MetricsAggregator.cs \
    's/if \(value <= bounds\[i\]\)/if (value < bounds[i])/' \
    "aggregator: bucket bound made exclusive"

mutate Pulse/MetricsAggregator.cs \
    's/s\.Absolute \? value : s\.Value \+ value/value/' \
    "aggregator: counter stops accumulating"

mutate Pulse/MetricsAggregator.cs \
    's/s\.Absolute \? value : s\.Value \+ value/s.Value + value/' \
    "aggregator: an observable counter accumulates the totals it reports"

mutate Pulse/MetricsAggregator.cs \
    's/ && SameLabels\(s\.Labels, labels\)//' \
    "aggregator: series lookup ignores the tag set"

mutate Pulse/PrometheusText.cs \
    's/Escape\(label\.Value\)/label.Value/' \
    "writer: label values written unescaped"

mutate Pulse/MetricsAggregator.cs \
    's/s\.Count\+\+;/s.Count += 2;/' \
    "aggregator: histogram count double-counts"

mutate Pulse/MetricsAggregator.cs \
    's/\(long\[\]\)s\.Buckets\.Clone\(\)/s.Buckets/' \
    "aggregator: scrape hands out the live bucket array"

mutate Pulse/MetricsAggregator.cs \
    's/return bounds\.Length;/return 0;/' \
    "aggregator: overflow values land in the first bucket"

echo
echo "$((TOTAL - FAILS))/$TOTAL mutations killed"
if [[ "$FAILS" -ne 0 ]]; then
    echo "Mutation check FAILED: a mutation survived or went inert."
    exit 1
fi
echo "Mutation check passed."
