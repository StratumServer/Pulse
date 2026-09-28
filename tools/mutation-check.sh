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

# One restore up front; every test run after it skips the restore, which is most of the idle
# time in a loop that rebuilds the same projects dozens of times.
dotnet restore Pulse.slnx --nologo -v q >/dev/null 2>&1

run_tests() { # [test filter]
    dotnet test "$TEST_PROJECT" -c Release --no-restore --nologo -v q ${1:+--filter "$1"} >/dev/null 2>&1
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
    # The mutated file's own test class runs first: it kills almost every mutant in a second or
    # two. Only a mutant it misses pays for the whole suite, so SURVIVED still means the full
    # suite passed with the mutation in place. Running the full suite for every mutant cost CI
    # about a quarter of an hour once the socket server's timing tests joined it.
    if ! run_tests "FullyQualifiedName~$(basename "$file" .cs)Tests" || ! run_tests; then
        echo "killed:   $label"
    else
        echo "SURVIVED: $label"
        FAILS=$((FAILS + 1))
    fi
    git checkout -- "$file"
}

MUTATED="Pulse/PrometheusText.cs Pulse/MetricsAggregator.cs Pulse/LogClassifier.cs Pulse/MetricsHttpServer.cs Pulse/TickBookkeeper.cs Pulse/EngineSample.cs Pulse/PingSummary.cs Pulse/EntityBreakdown.cs Pulse/SuspendBookkeeper.cs Pulse/TickAttribution.cs Pulse/ModOwners.cs Pulse/ConfigUpgrade.cs Pulse/ConfigLoad.cs Pulse/PulseCommands.cs Pulse/AttributionMetrics.cs Pulse.Otlp/OtlpOptions.cs Pulse.Otlp/ExportFailureLog.cs"

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

mutate Pulse/MetricsAggregator.cs \
    's/s\.Instrument\.IsObservable && s\.Generation != generation/s.Generation != generation/' \
    "aggregator: a synchronous series is retired between scrapes as if it were observable"

mutate Pulse/MetricsAggregator.cs \
    's/series\.RemoveAll\(s => s\.Instrument\.IsObservable && s\.Generation != generation\);//' \
    "aggregator: an observable series is never retired, freezing it at whatever it last measured"

mutate Pulse/LogClassifier.cs \
    's/\("Server suspend requested, but reached max wait time", "suspend_timeout"\)/("Server suspend requested and reached max wait time", "suspend_timeout")/' \
    "classifier: an engine warning prefix drifts from the engine string"

mutate Pulse/MetricsHttpServer.cs \
    's/now - lastErrorLogMs < ErrorLogIntervalMs/false/' \
    "http server: error log rate limit never suppresses a repeat failure"

mutate Pulse/MetricsHttpServer.cs \
    's/request\.Path != "\/metrics"/request.Path == "\/metrics"/' \
    "http server: the metrics path match inverts, so the one real path 404s and every other path would serve it"

mutate Pulse/MetricsHttpServer.cs \
    's/MaxHeadBytes = 8192;/MaxHeadBytes = 65536;/' \
    "http server: the request head size limit widened, no longer rejecting a header the tests expect it to reject"

mutate Pulse/MetricsHttpServer.cs \
    's/stream\.ReadTimeout = ClampToDeadline\(deadline\);/stream.ReadTimeout = IoTimeoutMs;/' \
    "http server: a Read already blocked when the deadline passes waits out the full backstop timeout instead"

mutate Pulse/MetricsHttpServer.cs \
    's/MaxConcurrentConnections = 16;/MaxConcurrentConnections = 1000;/' \
    "http server: the concurrency cap is effectively removed, so a scrape is never actually queued behind it"

mutate Pulse/MetricsHttpServer.cs \
    's/Thread\.Sleep\(acceptBackoff\.NextMs\(\)\);/Thread.Sleep(0);/' \
    "http server: the accept-failure backoff stops sleeping, spinning the loop instead of throttling it"

mutate Pulse/MetricsHttpServer.cs \
    's/nextMs = Math\.Min\(nextMs \* 2, MaxMs\);/nextMs = Math.Min(nextMs, MaxMs);/' \
    "http server: the accept backoff stops doubling, retrying at a constant rate forever"

mutate Pulse/MetricsHttpServer.cs \
    's/Math\.Min\(nextMs \* 2, MaxMs\)/nextMs * 2/' \
    "http server: the accept backoff is no longer capped, growing without bound"

mutate Pulse/MetricsHttpServer.cs \
    's/internal void Reset\(\) => nextMs = InitialMs;/internal void Reset() { }/' \
    "http server: the accept backoff never resets after a successful accept"

mutate Pulse/MetricsHttpServer.cs \
    's/stream\.WriteTimeout = ClampToDeadline\(deadline\);/stream.WriteTimeout = IoTimeoutMs;/' \
    "http server: the write timeout ignores the deadline and always uses the full backstop"

mutate Pulse/MetricsHttpServer.cs \
    's/acceptBackoff\.Reset\(\);/ /' \
    "http server: the real accept loop stops resetting the backoff after a successful accept"

mutate Pulse/TickBookkeeper.cs \
    's/sinceSnapshotSeconds < snapshotIntervalSeconds/sinceSnapshotSeconds <= snapshotIntervalSeconds/' \
    "tick bookkeeper: snapshot cadence boundary made inclusive, delaying the due tick that lands exactly on it"

mutate Pulse/PrometheusText.cs \
    's/samples\.GroupBy\(sample => MetricName\(sample\.Name, sample\.Kind, sample\.Unit\)\)/samples.GroupBy(sample => MetricName(sample.Name, sample.Kind, sample.Unit) + sample.Labels.Length)/' \
    "writer: a family is split by tag set, repeating its HELP and TYPE lines"

mutate Pulse/PrometheusText.cs \
    's/tokens\.RemoveAll\(t => t == word\);//' \
    "writer: a counter or ratio suffix already present in the name is duplicated instead of moved to the end"

mutate Pulse/PrometheusText.cs \
    's/"per_" \+ per/"per" + per/' \
    "writer: a rate unit is built without its per_ separator"

mutate Pulse/PrometheusText.cs \
    's/\["s"\] = "second", \["m"\] = "minute"/["s"] = "seconds", ["m"] = "minute"/' \
    "writer: the per-unit map pluralises seconds"

mutate Pulse/PrometheusText.cs \
    's/kind == MetricKind\.Gauge && unit == "1"/unit == "1"/' \
    "writer: the _ratio suffix is no longer limited to gauges"

mutate Pulse/PrometheusText.cs \
    's/return joined\.Length > 0 && char\.IsAsciiDigit\(joined\[0\]\) \? "_" \+ joined : joined;/return joined;/' \
    "writer: a name starting with a digit is served invalid"

mutate Pulse/PrometheusText.cs \
    's/string trimmed = part\.Trim\(\);/string trimmed = part;/' \
    "writer: a unit's surrounding whitespace reaches the name"

mutate Pulse/PrometheusText.cs \
    "s/char\.IsAsciiLetterOrDigit\(c\) \|\| c == ':'/char.IsAsciiLetterOrDigit(c)/" \
    "writer: colon is no longer a valid name character"

mutate Pulse/PrometheusText.cs \
    's/tokens\.Add\(perUnit\);//' \
    "writer: the per-unit word is computed and then never appended"

mutate Pulse/PrometheusText.cs \
    "s#unit\.Split\('/', 2\)#unit.Split('/')#" \
    "writer: the unit splits at every slash instead of only the first"

mutate Pulse/PrometheusText.cs \
    's/trimmed\.IndexOfAny\(BraceChars\) < 0/trimmed.IndexOfAny(BraceChars) != 0/' \
    "writer: a brace only disqualifies a unit at the very front"

mutate Pulse/PrometheusText.cs \
    's/\["By"\] = "bytes"/["By"] = "byte"/' \
    "writer: By maps to the singular byte"

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
    's/Where\(code => !current\.Contains\(code\)\)/Where(code => current.Contains(code))/' \
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

mutate Pulse/TickAttribution.cs \
    's/if \(entry\.ElapsedTicks < 0\)/if (entry.ElapsedTicks <= 0)/' \
    "attribution: the wrap clamp fires on a mark that legitimately took no time"

mutate Pulse/TickAttribution.cs \
    's/if \(!warm\)/if (false)/' \
    "attribution: the stale sample from the tick the profiler came on is folded instead of discarded"

mutate Pulse/TickAttribution.cs \
    's/if \(mark\.Key == SleepMark\)/if (false)/' \
    "attribution: the throttle sleep is attributed as if it were work"

mutate Pulse/TickAttribution.cs \
    's/total \+= Walk\(child, owner\);/total += 0;/' \
    "attribution: nested ranges go unwalked, losing every entity behavior to the engine bucket"

mutate Pulse/TickAttribution.cs \
    's/foreach \(string modid in seenMods\.Order\(StringComparer\.Ordinal\)\)/foreach (string modid in ticksByMod.Keys.Order(StringComparer.Ordinal))/' \
    "attribution: a mod that goes quiet is dropped, freezing its share gauge at whatever it last read"

mutate Pulse/ModOwners.cs \
    's/byName\[name\] = resolved;//' \
    "mod owners: a class registry miss is asked again on every profiled tick"

# The config upgrade decides whether a live server rewrites a file an admin owns, so the two ways
# it can be wrong are both here: not writing what it should, and writing over what it cannot read.
mutate Pulse/ConfigUpgrade.cs \
    's/Walk\(nestedFile, nested, prefix \+ entry\.Key \+ "\.", /Walk(nestedFile, nested, prefix, /' \
    "config upgrade: a key inside a block is reported without the block it lives in"

mutate Pulse/ConfigUpgrade.cs \
    's/entry => !config\.ContainsKey\(entry\.Key\)/entry => false/' \
    "config upgrade: a key the config does not know goes unreported and is dropped in silence"

mutate Pulse/ConfigUpgrade.cs \
    's/return null;/return new JsonObject();/' \
    "config upgrade: a file that does not parse is treated as an empty one and rewritten over"

# Loading a config file has the same two ways to be wrong as upgrading one: an unreadable file is
# the one this whole fix exists for, so mistaking it for a loaded or an absent one is exactly the
# regression that would bring back the original bug (an admin's broken file getting overwritten).
mutate Pulse/ConfigLoad.cs \
    's/existing != null$/existing == null/' \
    "config load: a file that loaded fine is treated as though it were absent"

mutate Pulse/ConfigLoad.cs \
    's/ConfigLoadStatus\.Unreadable, e\.Message\)/ConfigLoadStatus.Absent, e.Message)/' \
    "config load: an unreadable file is treated as absent, so the caller would overwrite it"

# Switching attribution from a command is a promise about a live server: that a server which never
# asked for it is not paying for it, that a reload names only what it could not apply, and that a
# ten minute look does not quietly become permanent. All three fail silently when they are wrong.
mutate Pulse/TickAttribution.cs \
    's/if \(!Enabled\)/if (false)/' \
    "attribution: the duty cycle runs on a server that never switched it on"

mutate Pulse/AttributionMetrics.cs \
    's/\+\+unprimedTicks > UnprimedTickLimit/++unprimedTicks >= UnprimedTickLimit/' \
    "attribution: the unprimed-tick give-up trips one tick before its own documented threshold"

mutate Pulse/PulseCommands.cs \
    's/\.Where\(key => key\.Changed\)/.Where(key => true)/' \
    "commands: a reload names every startup-only key as needing a restart, whether or not it moved"

mutate Pulse/PulseCommands.cs \
    's/"pulse\.json was not changed, so the file decides again after a restart\."/""/' \
    "commands: a switch stops saying it left the config file alone"

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
    's/path\.EndsWith\(MetricsPath, StringComparison\.OrdinalIgnoreCase\)/path.StartsWith(MetricsPath, StringComparison.OrdinalIgnoreCase)/' \
    "otlp: an endpoint already carrying /v1/metrics gets a second one"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/Uri\.EscapeDataString\(h\.Value \?\? string\.Empty\)/(h.Value ?? string.Empty)/' \
    "otlp: header values go out unencoded"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/\.Where\(h => !string\.IsNullOrWhiteSpace\(h\.Key\)\)//' \
    "otlp: a header with no name is rendered anyway"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/string\.IsNullOrWhiteSpace\(configuredName\)/!string.IsNullOrWhiteSpace(configuredName)/' \
    "otlp: a blank ServiceName exports as-is and a real one is replaced by the default"

# The failure log turns a silent export into one rate-limited line; both of its limits exist to
# bound the log itself, and a mutation that erases either one is exactly what would let a stuck
# collector or a churning cause flood it.
mutate Pulse.Otlp/ExportFailureLog.cs \
    's/RepeatMs = 10 \* 60_000;/RepeatMs = 0;/' \
    "otlp failure log: the ten-minute repeat window disappears, so a standing outage floods the log"

mutate Pulse.Otlp/ExportFailureLog.cs \
    's/MaxKinds = 32;/MaxKinds = 320;/' \
    "otlp failure log: the tracked-kinds cap stops bounding memory"

mutate Pulse.Otlp/ExportFailureLog.cs \
    's/now - entry\.Value >= RepeatMs/false/' \
    "otlp failure log: stale kinds are never evicted, so the cap holds a full log hostage forever"

mutate Pulse.Otlp/ExportFailureLog.cs \
    's/candidate == ownEndpoint/true/' \
    "otlp failure log: every exporter in the process is read as Pulse's own"

mutate Pulse.Otlp/ExportFailureLog.cs \
    's/result\.Replace\(target, Redacted, StringComparison\.OrdinalIgnoreCase\)/result/' \
    "otlp failure log: a configured header value survives into the log verbatim"

mutate Pulse.Otlp/ExportFailureLog.cs \
    's/candidate == ownGrpcExportPath/false/' \
    "otlp failure log: a real grpc export of Pulse's own is never recognised as its own"

mutate Pulse.Otlp/ExportFailureLog.cs \
    's/if \(space > 0\)/if (false)/' \
    "otlp failure log: a credential echoed back without its scheme is never redacted"

mutate Pulse.Otlp/ExportFailureLog.cs \
    's/\.OrderByDescending\(target => target\.Length\)//' \
    "otlp failure log: redaction targets are no longer applied longest first, so a short value can gnaw a hole in a longer one"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/headers\?\.Values\.Where/headers.Values.Where/' \
    "otlp: a null Headers block throws instead of exporting with no secrets tracked"

# Every mutation is reverted in the source, but the last one of each block was built before it
# was, so the binaries on disk still carry it. Leave them matching the tree: anything running
# with --no-build after this script would otherwise fail for reasons that are nowhere in the
# source it is looking at.
dotnet build Pulse.slnx -c Release --nologo >/dev/null 2>&1

echo
echo "$((TOTAL - FAILS))/$TOTAL mutations killed"
if [[ "$FAILS" -ne 0 ]]; then
    echo "Mutation check FAILED: a mutation survived or went inert."
    exit 1
fi
echo "Mutation check passed."
