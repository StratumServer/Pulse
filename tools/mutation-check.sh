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

# Which suite has to fail. Set once per block of mutations, so a mutation to the OTLP mod is
# judged by the OTLP tests rather than by a suite that cannot see it.
TEST_PROJECT="Pulse.Tests/Pulse.Tests.csproj"

run_tests() {
    dotnet test "$TEST_PROJECT" -c Release --nologo -v q >/dev/null 2>&1
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

MUTATED="Pulse/PrometheusText.cs Pulse/MetricsAggregator.cs Pulse/LogClassifier.cs Pulse/MetricsHttpServer.cs Pulse/TickBookkeeper.cs Pulse/EngineSample.cs Pulse/PingSummary.cs Pulse/EntityBreakdown.cs Pulse/SuspendBookkeeper.cs Pulse.Otlp/OtlpOptions.cs"

if ! git diff --quiet -- $MUTATED; then
    echo "One of $MUTATED has uncommitted changes; refusing to mutate over them."
    exit 2
fi

for TEST_PROJECT in Pulse.Tests/Pulse.Tests.csproj Pulse.Otlp.Tests/Pulse.Otlp.Tests.csproj; do
    if ! run_tests; then
        echo "$TEST_PROJECT is red before any mutation; fix that first."
        exit 2
    fi
done
TEST_PROJECT="Pulse.Tests/Pulse.Tests.csproj"

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

mutate Pulse/LogClassifier.cs \
    's/\("Server suspend requested, but reached max wait time", "suspend_timeout"\)/("Server suspend requested and reached max wait time", "suspend_timeout")/' \
    "classifier: an engine warning prefix drifts from the engine string"

mutate Pulse/MetricsHttpServer.cs \
    's/now - lastErrorLogMs < ErrorLogIntervalMs/false/' \
    "http server: error log rate limit never suppresses a repeat failure"

mutate Pulse/TickBookkeeper.cs \
    's/sinceSnapshotSeconds < snapshotIntervalSeconds/sinceSnapshotSeconds <= snapshotIntervalSeconds/' \
    "tick bookkeeper: snapshot cadence boundary made inclusive, delaying the due tick that lands exactly on it"

mutate Pulse/PrometheusText.cs \
    's/samples\.GroupBy\(sample => MetricName\(sample\.Name, sample\.Kind\)\)/samples.GroupBy(sample => MetricName(sample.Name, sample.Kind) + sample.Labels.Length)/' \
    "writer: a family is split by tag set, repeating its HELP and TYPE lines"

mutate Pulse/EngineSample.cs \
    's/ticksTotal > 0 \?/ticksTotal >= 0 ?/' \
    "engine sample: busy average divides by a bucket that counted no ticks"

mutate Pulse/EngineSample.cs \
    's|tickTimeTotalMs / \(double\)ticksTotal / 1000\.0|tickTimeTotalMs / (double)ticksTotal * 1000.0|' \
    "engine sample: engine milliseconds published as if they were seconds"

mutate Pulse/PingSummary.cs \
    's/if \(!float\.IsFinite\(ping\)\)/if (float.IsFinite(ping))/' \
    "ping summary: the NaN skip keeps the NaNs and drops the real pings"

mutate Pulse/PingSummary.cs \
    's|new PingSummary\(total / counted, max\)|new PingSummary(total, max)|' \
    "ping summary: the average stops dividing by the number of players"

mutate Pulse/PingSummary.cs \
    's/max = Math\.Max\(max, ping\);/max = ping;/' \
    "ping summary: the maximum becomes whichever player was read last"

mutate Pulse/EntityBreakdown.cs \
    's/if \(!current\.Contains\(code\)\)/if (current.Contains(code))/' \
    "entity breakdown: a code that left the top ten is never zeroed and its series freezes"

mutate Pulse/EntityBreakdown.cs \
    's/published = \[\.\. current\];/published = [];/' \
    "entity breakdown: nothing is remembered as published, so nothing is ever retired"

mutate Pulse/EntityBreakdown.cs \
    's/\.ThenBy\(entry => entry\.Key, StringComparer\.Ordinal\)//' \
    "entity breakdown: tied codes are ordered by whatever the dictionary hands back"

mutate Pulse/EntityBreakdown.cs \
    's/\(OtherCode, total - top\)/(OtherCode, total)/' \
    "entity breakdown: the other bucket counts the codes it already published"

mutate Pulse/SuspendBookkeeper.cs \
    '/public void Open/,/^    }/s/if \(startSeconds < 0\)/if (true)/' \
    "suspend bookkeeper: every poll of the suspend handler restarts the window"

mutate Pulse/SuspendBookkeeper.cs \
    '/Close/,/^    }/s/startSeconds = -1;/startSeconds = 0;/' \
    "suspend bookkeeper: the window is never marked closed, so a second resume counts again"

# The OTLP mod is thin wiring apart from this one file, where every line is something that fails
# silently when it is wrong: a wrong endpoint path 404s on every export and a header encoded the
# wrong way is rejected by the backend, neither of which the game server would ever notice.
TEST_PROJECT="Pulse.Otlp.Tests/Pulse.Otlp.Tests.csproj"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/Math\.Max\(MinimumIntervalSeconds, intervalSeconds\)/Math.Min(MinimumIntervalSeconds, intervalSeconds)/' \
    "otlp: the interval floor becomes a ceiling"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/if \(protocol == OtlpExportProtocol\.Grpc\)/if (protocol != OtlpExportProtocol.Grpc)/' \
    "otlp: the signal path goes to grpc and not to http/protobuf"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/text\.EndsWith\(MetricsPath, StringComparison\.OrdinalIgnoreCase\)/text.StartsWith(MetricsPath, StringComparison.OrdinalIgnoreCase)/' \
    "otlp: an endpoint already carrying /v1/metrics gets a second one"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/Uri\.EscapeDataString\(h\.Value \?\? string\.Empty\)/(h.Value ?? string.Empty)/' \
    "otlp: header values go out unencoded"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/\.Where\(h => !string\.IsNullOrWhiteSpace\(h\.Key\)\)//' \
    "otlp: a header with no name is rendered anyway"

echo
echo "$((TOTAL - FAILS))/$TOTAL mutations killed"
if [[ "$FAILS" -ne 0 ]]; then
    echo "Mutation check FAILED: a mutation survived or went inert."
    exit 1
fi
echo "Mutation check passed."
