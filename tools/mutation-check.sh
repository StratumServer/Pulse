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
    local filter="${1:-}"
    dotnet test "$TEST_PROJECT" -c Release --no-restore --nologo -v q ${filter:+--filter "$filter"} >/dev/null 2>&1
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

MUTATED="Pulse/PrometheusText.cs Pulse/MetricsAggregator.cs Pulse/LogClassifier.cs Pulse/MetricsHttpServer.cs Pulse/TickBookkeeper.cs Pulse/EngineSample.cs Pulse/PingSummary.cs Pulse/EntityBreakdown.cs Pulse/SuspendBookkeeper.cs Pulse/DutyCycle.cs Pulse/TickAttribution.cs Pulse/ModOwners.cs Pulse/ConfigUpgrade.cs Pulse/ConfigLoad.cs Pulse/PulseCommands.cs Pulse/AttributionMetrics.cs Pulse.Otlp/OtlpOptions.cs Pulse.Otlp/ExportFailureLog.cs Pulse.Otlp/PulseOtlpModSystem.cs"

if ! git diff --quiet -- $MUTATED; then
    echo "One of $MUTATED has uncommitted changes; refusing to mutate over them."
    exit 2
fi

# The files the Stratum timings block mutates, guarded the same way. A list of their own keeps that
# block from sharing a line with the one above.
STRATUM_MUTATED="Pulse/StratumTimingsSource.cs Pulse/StratumKeyParser.cs Pulse/StratumFold.cs"
if ! git diff --quiet -- $STRATUM_MUTATED; then
    echo "One of $STRATUM_MUTATED has uncommitted changes; refusing to mutate over them."
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

# The index finds a series through a dictionary, which only asks whether two keys are equal once
# their hashes already are. A part of the key left out of the equality alone changes nothing the
# aggregator can show, so the first two mutations below leave it out of the hash as well.
mutate Pulse/MetricsAggregator.cs \
    's/ && SameLabels\(Labels, other\.Labels\)//;s/return hash\.ToHashCode\(\);/return RuntimeHelpers.GetHashCode(Instrument);/' \
    "aggregator: series lookup ignores the tag set"

mutate Pulse/MetricsAggregator.cs \
    's/ReferenceEquals\(Instrument, other\.Instrument\) && //;s/hash\.Add\(RuntimeHelpers\.GetHashCode\(Instrument\)\);//' \
    "aggregator: series lookup ignores the instrument, so two instruments recording the same tags share a series"

mutate Pulse/MetricsAggregator.cs \
    's/hash\.Add\(label\.Key\);/hash.Add(Labels);/' \
    "aggregator: the lookup hashes the tag array's identity, not its content, so a tag set seen again opens a second series"

mutate Pulse/MetricsAggregator.cs \
    's/hash\.Add\(label\.Value\);//' \
    "aggregator: the lookup hash ignores the tag values, so a family's tag sets all share one bucket and the index is a linear scan again"

mutate Pulse/MetricsAggregator.cs \
    's/hash\.Add\(RuntimeHelpers\.GetHashCode\(Instrument\)\);//' \
    "aggregator: the lookup hash ignores the instrument, so every untagged series shares one bucket and the per-tick records scan them"

mutate Pulse/MetricsAggregator.cs \
    's/index\.Remove\(s\.Key\);//' \
    "aggregator: a retired series stays in the index, so its tag set comes back into a series nobody serves"

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
    's/bool retired = s\.Instrument\.IsObservable && s\.Generation != generation;/bool retired = false;/' \
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
    's/if \(mark\.Key == SleepMark\)/if (false)/' \
    "attribution: the throttle sleep is attributed as if it were work"

mutate Pulse/TickAttribution.cs \
    's/total \+= Walk\(child, owner\);/total += 0;/' \
    "attribution: nested ranges go unwalked, losing every entity behavior to the engine bucket"

mutate Pulse/TickAttribution.cs \
    's/foreach \(string modid in seenMods\.Order\(StringComparer\.Ordinal\)\)/foreach (string modid in ticksByMod.Keys.Order(StringComparer.Ordinal))/' \
    "attribution: a mod that goes quiet is dropped, freezing its share gauge at whatever it last read"

# The duty cycle is the schedule every burst of every measurement runs on, so each way it can drift
# is a measurement that quietly runs too often, too long, or on a tick it should have discarded.
mutate Pulse/DutyCycle.cs \
    's/if \(!warm\)/if (false)/' \
    "duty cycle: the tick after a burst starts is taken for a sample, so the stale one is folded instead of discarded"

mutate Pulse/DutyCycle.cs \
    's/if \(!InBurst\)/if (false)/' \
    "duty cycle: the idle phase never runs, so nothing waits out the interval and a burst never starts"

mutate Pulse/DutyCycle.cs \
    's/idleSeconds \+= elapsedSeconds;/idleSeconds++;/' \
    "duty cycle: the interval counts ticks instead of the seconds they took"

mutate Pulse/DutyCycle.cs \
    's/if \(idleSeconds < IntervalSeconds\)/if (idleSeconds <= IntervalSeconds)/' \
    "duty cycle: the interval boundary is inclusive, so a burst due exactly on it waits one more tick"

mutate Pulse/DutyCycle.cs \
    's/if \(\+\+burstTicksElapsed < BurstTicks\)/if (++burstTicksElapsed <= BurstTicks)/' \
    "duty cycle: a burst takes one sample more than its length"

mutate Pulse/DutyCycle.cs \
    '/public DutyStep OnTick/,/^    }/s/Restart\(\);//' \
    "duty cycle: the last sample leaves the burst running, so it never ends and the interval is never waited again"

mutate Pulse/DutyCycle.cs \
    '/public void Apply/,/^    }/s/Restart\(\);//' \
    "duty cycle: applying a cycle leaves the burst in progress running instead of dropping it"

mutate Pulse/DutyCycle.cs \
    '/private void Restart/,/^    }/s/idleSeconds = 0;//' \
    "duty cycle: a reload keeps the idle time the old interval had already counted"

mutate Pulse/DutyCycle.cs \
    '/private void Restart/,/^    }/s/burstTicksElapsed = 0;//' \
    "duty cycle: the sample count carries over between bursts, so every burst after the first ends on its first sample"

mutate Pulse/DutyCycle.cs \
    '/private void Restart/,/^    }/s/warm = false;//' \
    "duty cycle: the next burst skips its warm-up and folds the tree from the tick the profiler came on"

mutate Pulse/DutyCycle.cs \
    's/MaximumBurstTicks = 300;/MaximumBurstTicks = 299;/' \
    "duty cycle: the burst cap the README documents moves"

mutate Pulse/DutyCycle.cs \
    's/Math\.Clamp\(burstTicks, 1, MaximumBurstTicks\)/Math.Max(1, burstTicks)/' \
    "duty cycle: the burst length cap is gone, so a configured burst of any length runs"

mutate Pulse/DutyCycle.cs \
    's/Math\.Clamp\(burstTicks, 1, MaximumBurstTicks\)/Math.Min(burstTicks, MaximumBurstTicks)/' \
    "duty cycle: the burst length floor is gone, so a burst of no ticks is accepted"

mutate Pulse/DutyCycle.cs \
    's/Math\.Max\(MinimumIntervalSeconds, intervalSeconds\)/intervalSeconds/' \
    "duty cycle: the interval floor is gone, so a zero interval starts a burst on every tick"

# What attribution does with each step is its own: reading the warm-up as a sample counts a tree
# that is not this burst's, never closing on the last sample publishes nothing at all, and a cycle
# restarted mid-burst has to take what the burst had folded with it.
mutate Pulse/TickAttribution.cs \
    's/if \(step is not \(DutyStep\.Sample or DutyStep\.LastSample\)\)/if (step == DutyStep.Idle)/' \
    "attribution: a start or a warm-up is read as a sample too, so the stale tree from the tick the profiler came on is folded"

mutate Pulse/TickAttribution.cs \
    's/return step == DutyStep\.LastSample \? Take\(\) : null;/return null;/' \
    "attribution: the last sample never closes the burst, so nothing is ever published"

mutate Pulse/TickAttribution.cs \
    '/public void Apply/,/^    }/s/ClearBurst\(\);//' \
    "attribution: a reload mid-burst keeps the half-folded sample, so the next burst publishes the ticks that were dropped too"

mutate Pulse/TickAttribution.cs \
    '/private AttributionBurst Take/,/^    }/s/ClearBurst\(\);//' \
    "attribution: a published burst is not cleared, so the next one adds its ticks to the last one's"

mutate Pulse/ModOwners.cs \
    's/byName\[name\] = resolved;//' \
    "mod owners: a class registry miss is asked again on every profiled tick"

# A behavior's mark carries its property name, not the code its class was registered under, so a
# live instance is the only thing that can say whose a renamed behavior is. Each of these is a way
# the walk that reads them can quietly go wrong: a name that never matches a mark, a class that is
# no longer read once, a name credited to the class of the instance instead of the class that
# declares it, a name that changes hands between two bursts, an engine behavior left to report as
# nobody's, and an entity that is read again every burst.
mutate Pulse/ModOwners.cs \
    's/profilerName\[TickAttribution\.BehaviorPrefix\.Length\.\.\]/profilerName/' \
    "mod owners: a learned behavior name keeps its prefix, so no mark ever finds it"

mutate Pulse/ModOwners.cs \
    's/if \(!seenBehaviorClasses\.Add\(behavior\)\)/if (false)/' \
    "mod owners: every instance of a class is read again, so the walk costs the instance count"

mutate Pulse/ModOwners.cs \
    's/OwnerOfClass\(NameDeclarer\(behavior\)\)/OwnerOfClass(behavior)/' \
    "mod owners: a subclass that inherits its parent's name is credited to its own mod, so the owner of the name depends on which entity the walk meets first"

mutate Pulse/ModOwners.cs \
    's/learnedBehaviors\.Add\(name\) \|\| \(byName\[name\] == null && modid != null\)/learnedBehaviors.Add(name) || modid != null/' \
    "mod owners: a later class that declares the same name takes it from the first, so it flaps between bursts"

mutate Pulse/ModOwners.cs \
    's/learnedBehaviors\.Add\(name\) \|\| \(byName\[name\] == null && modid != null\)/learnedBehaviors.Add(name)/' \
    "mod owners: a shared name stays with the first class even when that one has no owner"

mutate Pulse/ModOwners.cs \
    's/ \?\? \(behavior\.Assembly == EngineApi \? TickAttribution\.Engine : null\)//' \
    "mod owners: a behavior the game's API assembly declares reports as unattributed instead of engine"

mutate Pulse/ModOwners.cs \
    's/for \(int i = 0; i < behaviors\?\.Count; i\+\+\)/for (int i = 0; i < 1 \&\& i < behaviors?.Count; i++)/' \
    "mod owners: the walk reads the first behavior of each entity and no other"

mutate Pulse/ModOwners.cs \
    's/if \(readEntities\.Contains\(entry\.Key\)\)/if (false)/' \
    "mod owners: every entity is read again on every burst, so the walk costs the loaded entity count times their behaviors"

mutate Pulse/ModOwners.cs \
    's/^( +)stillLoaded\.Add\(entry\.Key\);$/\1if (!readEntities.Contains(entry.Key)) { stillLoaded.Add(entry.Key); }/' \
    "mod owners: an entity already read is left out of the set the next walk keeps, so it is read again every other burst"

mutate Pulse/ModOwners.cs \
    's/\(readEntities, stillLoaded\) = \(stillLoaded, readEntities\);//' \
    "mod owners: the walk never remembers what it has read, so each burst reads every entity again"

mutate Pulse/ModOwners.cs \
    's/stillLoaded\.Clear\(\);//' \
    "mod owners: an entity that has unloaded is never forgotten, so the ids pile up for the life of the server"

mutate Pulse/ModOwners.cs \
    's/behavior == null \? null : OwnerOfClass\(behavior\)/behavior == null ? null : OfAssembly(behavior.Assembly)/' \
    "mod owners: a class the game's API declares, reached through the class registry, reports as unattributed instead of engine"

mutate Pulse/AttributionMetrics.cs \
    's/walkBehaviors\(owners!\);//' \
    "attribution: the behavior walk never runs, so a renamed behavior reports as unattributed again"

mutate Pulse/AttributionMetrics.cs \
    's/behaviorWalkFailed = true;//' \
    "attribution: a behavior walk that throws is tried again every burst, one warning each"

# The config upgrade decides whether a live server rewrites a file an admin owns, so the two ways
# it can be wrong are both here: not writing what it should, and writing over what it cannot read.
mutate Pulse/ConfigUpgrade.cs \
    's/Walk\(nestedFile, nested, prefix \+ entry\.Key \+ "\.", /Walk(nestedFile, nested, prefix, /' \
    "config upgrade: a key inside a block is reported without the block it lives in"

mutate Pulse/ConfigUpgrade.cs \
    's/byKey\.Where\(g => !known\.Contains\(g\.Key\)\)/byKey.Where(g => false)/' \
    "config upgrade: a key the config does not know goes unreported and is dropped in silence"

mutate Pulse/ConfigUpgrade.cs \
    's/return null;/return new JsonObject();/' \
    "config upgrade: a file that does not parse is treated as an empty one and rewritten over"

mutate Pulse/ConfigUpgrade.cs \
    's/merged\[property\.Key\] = property\.Value\?\.DeepClone\(\);/{ }/' \
    "config upgrade: a block duplicated under two spellings merges to nothing, so a field only one spelling set is reported missing and rewritten over"

mutate Pulse/ConfigUpgrade.cs \
    's/configValue is JsonObject \|\| configByKey\[key\]\.Count\(\) > 1/false/' \
    "config upgrade: a duplicated block or a dictionary's colliding keys claims a winner that does not exist"

mutate Pulse/ConfigUpgrade.cs \
    's/if \(IsBlank\(matches\[\^1\]\.Value\) && IsFilled\(entry\.Value\)\)/if (false)/' \
    "config upgrade: a key the file carries blank, which a mod has since filled in, goes unreported, so the value it generated is never written back and changes on every restart"

mutate Pulse/ConfigUpgrade.cs \
    's/IsBlank\(matches\[\^1\]\.Value\)/IsBlank(matches[0].Value)/' \
    "config upgrade: the first spelling of a duplicated key decides whether it is blank, where Newtonsoft keeps the last"

# Loading a config file has the same two ways to be wrong as upgrading one: an unreadable file is
# the one this whole fix exists for, so mistaking it for a loaded or an absent one is exactly the
# regression that would bring back the original bug (an admin's broken file getting overwritten).
mutate Pulse/ConfigLoad.cs \
    's/existing != null$/existing == null/' \
    "config load: a file that loaded fine is treated as though it were absent"

mutate Pulse/ConfigLoad.cs \
    's/ConfigLoadStatus\.Unreadable, e\.Message\)/ConfigLoadStatus.Absent, e.Message)/' \
    "config load: an unreadable file is treated as absent, so the caller would overwrite it"

# Stratum's entity timings reach Pulse through three pure pieces, and each one fails silently when it
# is wrong: a binder that accepts a shape it should refuse calls into a Stratum it does not
# understand, a parser that guesses serves a series under a name nobody asked for, and a fold that
# miscounts serves a counter that is wrong or runs backwards, which no scrape would ever say.
#
# The binder: every check that stands between a Stratum Pulse does not know and a call into it.
mutate Pulse/StratumTimingsSource.cs \
    's/if \(type == null\)/if (type == null \&\& (reason = "absent") != null)/' \
    "stratum binder: a server with no Stratum is reported as a failure, so vanilla would log a warning at every boot"

mutate Pulse/StratumTimingsSource.cs \
    's/declared is not \[FieldInfo \{ IsLiteral: true \} field\]/declared is not [FieldInfo field]/' \
    "stratum binder: a static field is read like a literal, which runs Stratum's type initializer before anything is known about it"

mutate Pulse/StratumTimingsSource.cs \
    's/if \(declared\.Length == 0\)/if (declared.Length == 0 || declared is not [FieldInfo { IsLiteral: true }])/' \
    "stratum binder: a ContractVersion that is not an integer literal is called a Stratum that predates the contract, so an admin is told to upgrade a Stratum that is current"

mutate Pulse/StratumTimingsSource.cs \
    's/if \(version != ContractVersion\)/if (version < ContractVersion)/' \
    "stratum binder: a contract newer than this Pulse reads is bound as if it were version 1"

mutate Pulse/StratumTimingsSource.cs \
    's/request\?\.ReturnType != typeof\(IDisposable\)/request == null/' \
    "stratum binder: a request that returns something other than a lease is bound anyway"

mutate Pulse/StratumTimingsSource.cs \
    's/\|\| snapshot\.GetParameters\(\)\[0\]\.ParameterType != SnapshotInto\)/)/' \
    "stratum binder: a snapshot that takes a base of the list is bound, so Pulse would pass its list to a contract it was not promised"

mutate Pulse/StratumTimingsSource.cs \
    's/public IDisposable Request\(\) => request\(\);/public IDisposable Request() { request(); return request(); }/' \
    "stratum binder: a request takes two leases and releases one, so Stratum records for the rest of the run"

mutate Pulse/StratumTimingsSource.cs \
    's/=> snapshot\(into\);/=> snapshot([]);/' \
    "stratum binder: a snapshot is read into a list nobody sees"

mutate Pulse/StratumTimingsSource.cs \
    's/StratumEntityBehaviorTimings";/StratumEntityBehaviorTiming";/' \
    "stratum binder: the type name drifts from the one Stratum declares, so no Stratum is ever found"

mutate Pulse/StratumTimingsSource.cs \
    's/ContractVersion = 1;/ContractVersion = 2;/' \
    "stratum binder: Pulse reads a contract version no Stratum has"

# The parser: which keys are read, and which name and thread each one gives.
mutate Pulse/StratumKeyParser.cs \
    's/memo\[key\] = parsed = Read\(key\);/parsed = Read(key);/' \
    "stratum parser: a key is read again at every burst, allocating its label strings again"

mutate Pulse/StratumKeyParser.cs \
    's/if \(key\.StartsWith\(ThreadSafeBehavior, StringComparison\.Ordinal\)\)/if (false)/' \
    "stratum parser: a thread-safe key is read as a main-thread one, with threadsafe as its category"

mutate Pulse/StratumKeyParser.cs \
    's/threadSafe: true\)/threadSafe: false)/' \
    "stratum parser: a thread-safe behavior is reported as running on the main thread"

mutate Pulse/StratumKeyParser.cs \
    's/threadSafe \&\& name\.StartsWith\(TickAttribution\.BehaviorPrefix, StringComparison\.Ordinal\)/false/' \
    "stratum parser: a thread-safe behavior keeps the engine's done-behavior prefix, so it never meets its main-thread namesake"

mutate Pulse/StratumKeyParser.cs \
    's/threadSafe \&\& name\.StartsWith/name.StartsWith/' \
    "stratum parser: the prefix is stripped from a main-thread name too, where it is part of the name"

mutate Pulse/StratumKeyParser.cs \
    's/name\[TickAttribution\.BehaviorPrefix\.Length\.\.\]/name[(TickAttribution.BehaviorPrefix.Length - 1)..]/' \
    "stratum parser: the prefix strip leaves its last dash on the name"

mutate Pulse/StratumKeyParser.cs \
    's/!key\.AsSpan\(dot \+ 1\)\.StartsWith\(AiTask, StringComparison\.Ordinal\) \|\| //' \
    "stratum parser: the AI phase keys that nest inside each other are read as tasks"

mutate Pulse/StratumKeyParser.cs \
    's/ \|\| nameStart >= key\.Length\)/)/' \
    "stratum parser: a task key with no code is read"

mutate Pulse/StratumKeyParser.cs \
    's/key\.StartsWith\(EntityType, StringComparison\.Ordinal\) \? ReadEntity\(key\) : null/ReadEntity(key)/' \
    "stratum parser: a key of a shape nobody knows is guessed to be an entity"

# The fold: what a burst added, and which series it is told to.
mutate Pulse/StratumFold.cs \
    's#ticks / \(double\)Stopwatch\.Frequency#ticks * (double)Stopwatch.Frequency#' \
    "stratum fold: stopwatch ticks are multiplied by the frequency instead of divided"

mutate Pulse/StratumFold.cs \
    's/new Total\(after\.Ticks - before\.Ticks, after\.Calls - before\.Calls\)/new Total(after.Ticks, after.Calls)/' \
    "stratum fold: a burst reports the totals the accumulator ended on instead of what it added"

mutate Pulse/StratumFold.cs \
    's/after\.Ticks >= before\.Ticks \&\& after\.Calls >= before\.Calls/true/' \
    "stratum fold: a total that went down is not read as a reset, so the series is told to go backwards"

mutate Pulse/StratumFold.cs \
    's/after\.Ticks >= before\.Ticks \&\&/after.Ticks > before.Ticks \&\&/' \
    "stratum fold: a total that did not move is read as a reset, so the burst adds all of it again"

mutate Pulse/StratumFold.cs \
    's/Math\.Max\(0, after\.Ticks\)/after.Ticks/' \
    "stratum fold: a negative total runs a counter backwards"

mutate Pulse/StratumFold.cs \
    's/ \&\& after\.Calls >= before\.Calls//' \
    "stratum fold: a key is reset when its ticks went down and not when its calls did, so a count that went down is paired with a delta"

mutate Pulse/StratumFold.cs \
    's/startTotals\.Clear\(\);//' \
    "stratum fold: the first snapshot of the last burst is subtracted from this one's"

mutate Pulse/StratumFold.cs \
    's/startTotals\.TryGetValue\(key, out Total before\);/if (!startTotals.TryGetValue(key, out Total before)) { continue; }/' \
    "stratum fold: a key that appeared during the burst is dropped, so a type that did not run in the warm-up tick never gets a series"

mutate Pulse/StratumFold.cs \
    's/deltas\[series\] = deltas\.GetValueOrDefault\(series\) \+ added;/deltas[series] = added;/' \
    "stratum fold: the codes that share a type replace each other instead of adding up"

mutate Pulse/StratumFold.cs \
    's/if \(added != default\)/if (true)/' \
    "stratum fold: a key the burst added nothing to takes a place under the cap"

mutate Pulse/StratumFold.cs \
    's/if \(room\.Count < cap\)/if (room.Count <= cap)/' \
    "stratum fold: a family keeps one series more than its cap"

mutate Pulse/StratumFold.cs \
    's/List<KeyValuePair<StratumKey, Total>> kept = \[\];/List<KeyValuePair<StratumKey, Total>> kept = []; foreach (HashSet<StratumKey> places in admitted.Values) { places.Clear(); }/' \
    "stratum fold: the places are handed out again every burst, so a heavy newcomer takes one from a series that had it"

mutate Pulse/StratumFold.cs \
    's/if \(admitted\[entry\.Key\.Family\]\.Contains\(entry\.Key\)\)/if (false)/' \
    "stratum fold: a series that has its place is counted against the cap again, and spills once the family is full"

mutate Pulse/StratumFold.cs \
    's/\.OrderByDescending\(candidate => candidate\.Value\.Ticks\)/.OrderBy(candidate => candidate.Value.Ticks)/' \
    "stratum fold: the lightest new series are admitted first, so the cap lumps together the ones worth reading"

mutate Pulse/StratumFold.cs \
    's/spill\[lump\] = spill\.GetValueOrDefault\(lump\) \+ entry\.Value;/spill[lump] = entry.Value;/' \
    "stratum fold: what spills over replaces what spilled before it in the burst instead of adding to it"

mutate Pulse/StratumFold.cs \
    's/entry\.Key with \{ Name = Other \}/entry.Key/' \
    "stratum fold: the overflow series keeps the name of the key that spilled, so the cap bounds nothing"

mutate Pulse/StratumFold.cs \
    's/admitted\[entry\.Key\.Family\]/admitted[StratumFamily.Entity]/' \
    "stratum fold: the families share one set of places, so a full family spills the others"

mutate Pulse/StratumFold.cs \
    's/if \(overflowed\.Add\(lump\.Family\)\)/if (true)/' \
    "stratum fold: a family is reported as spilling for the first time at every burst it spills in"

mutate Pulse/StratumFold.cs \
    's/threadSafe \? "true" : "false"/threadSafe ? "True" : "False"/' \
    "stratum fold: the thread label is spelled the way a bool prints, not the way the families promise"

mutate Pulse/StratumFold.cs \
    's/owner\(key\.Name\) \?\? TickAttribution\.Unattributed/owner(key.Name) ?? TickAttribution.Engine/' \
    "stratum fold: a behavior no mod claims is credited to the engine"

mutate Pulse/StratumFold.cs \
    's/owner\(key\.Name\)/owner(TickAttribution.BehaviorPrefix + key.Name)/' \
    "stratum fold: the owner lookup is asked for the profiler mark, which the table does not know a behavior by"

mutate Pulse/StratumFold.cs \
    's/new EntityDelta\(key\.Name, seconds, total\.Calls\)/new EntityDelta(key.Name, seconds, total.Ticks)/' \
    "stratum fold: the entity ticks run are the stopwatch ticks spent"

# Switching attribution from a command is a promise about a live server: that a server which never
# asked for it is not paying for it, that a reload names only what it could not apply, and that a
# ten minute look does not quietly become permanent. All three fail silently when they are wrong.
mutate Pulse/DutyCycle.cs \
    's/if \(!Enabled\)/if (false)/' \
    "duty cycle: it runs on a server that never switched it on"

mutate Pulse/AttributionMetrics.cs \
    's/\+\+unprimedTicks > UnprimedTickLimit/++unprimedTicks >= UnprimedTickLimit/' \
    "attribution: the unprimed-tick give-up trips one tick before its own documented threshold"

mutate Pulse/AttributionMetrics.cs \
    's/if \(enabled \|\| !profiler\.PrintSlowTicks\)/if (true)/' \
    "attribution: the profiler flag is written on every call again, clobbering /debug logticks"

mutate Pulse/AttributionMetrics.cs \
    's/else if \(profilerEnabledLastWritten\)/else if (false)/' \
    "attribution: switching off mid-burst never turns the profiler back off, the release review's regression"

mutate Pulse/AttributionMetrics.cs \
    's/else if \(profilerEnabledLastWritten\)/else/' \
    "attribution: an idle tick claws back another mod's own Enabled=true, not just logticks's"

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
    's/Math\.Clamp\(intervalSeconds, MinimumIntervalSeconds, MaximumIntervalSeconds\)/Math.Clamp(intervalSeconds, MinimumIntervalSeconds, MinimumIntervalSeconds)/' \
    "otlp: the interval clamp collapses to the floor, so anything above 5 seconds is floored too"

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

mutate Pulse.Otlp/OtlpOptions.cs \
    's/string\.IsNullOrWhiteSpace\(configuredId\)/!string.IsNullOrWhiteSpace(configuredId)/' \
    "otlp: a blank ServiceInstanceId exports as-is and a real one is replaced by a generated one"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/: configuredId\.Trim\(\);/: configuredId;/' \
    "otlp: a configured ServiceInstanceId is no longer trimmed"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/Guid\.NewGuid\(\)\.ToString\(\)/Guid.Empty.ToString()/' \
    "otlp: the generated service instance id is the same all-zero GUID on every server"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/: fromEnvironment,/: ResolveServiceInstanceId(configuredInstanceId),/' \
    "otlp: the environment's service.instance.id no longer wins over the config key"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/string\.IsNullOrWhiteSpace\(fromEnvironment\)/fromEnvironment == null/' \
    "otlp: an empty service.instance.id in the environment counts as set, so the resource exports an empty one"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/if \(!string\.IsNullOrWhiteSpace\(Environment\.GetEnvironmentVariable\(ServiceNameVariable\)\)\)/if (false)/' \
    "otlp: OTEL_SERVICE_NAME no longer leaves the whole service identity to the environment"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/^            ResolveServiceName\(configuredName\),$/            configuredName!,/' \
    "otlp: the configured service name goes out unresolved, so a blank one is an empty service.name"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/^                \? ResolveServiceInstanceId\(configuredInstanceId\)$/                ? (string.IsNullOrWhiteSpace(configuredInstanceId) ? null : ResolveServiceInstanceId(configuredInstanceId))/;s/autoGenerateServiceInstanceId: false\);/autoGenerateServiceInstanceId: true);/' \
    "otlp: a blank service instance id is left to the SDK's own automatic one, a single GUID for the whole process"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/if \(!string\.IsNullOrWhiteSpace\(config\.ServiceInstanceId\)\)/if (false)/' \
    "otlp: an id the admin wrote is overwritten by a generated one at every start"

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

mutate Pulse.Otlp/OtlpOptions.cs \
    's/Math\.Clamp\(intervalSeconds, MinimumIntervalSeconds, MaximumIntervalSeconds\)/Math.Max(MinimumIntervalSeconds, intervalSeconds)/' \
    "otlp: the interval ceiling disappears, so a large enough config value overflows on the multiply again"

mutate Pulse.Otlp/OtlpOptions.cs \
    "s/header\.Value\?\.Contains\(','\) \?\? false/false/" \
    "otlp: a header value containing a comma is no longer refused, reopening the exporter's own crash on it"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/!seenNames\.Add\(name\)/false/' \
    "otlp: two header names that collide once trimmed are no longer refused, reopening the exporter's own duplicate-key crash"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/if \(userInfo\.Length > 0\)/if (false)/' \
    "otlp: the endpoint's userinfo is no longer added to the secrets list, so it could leak through an echoing backend"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/query\.Length > 1/false/' \
    "otlp: the endpoint's query values are no longer added to the secrets list, so a signed-URL secret could leak through an echoing backend"

mutate Pulse.Otlp/OtlpOptions.cs \
    's/^        yield return candidate;$//' \
    "otlp: a userinfo or query secret is only added to the secrets list unescaped, so the percent-escaped form a raw echoed request target actually carries leaks through"

mutate Pulse.Otlp/PulseOtlpModSystem.cs \
    's/failureReason = ex\.GetType\(\)\.Name;/failureReason = null;/' \
    "otlp: TryStoreDefaults stops reporting what failed when writing the default config throws"

mutate Pulse.Otlp/PulseOtlpModSystem.cs \
    's/instanceId, StringComparison\.Ordinal\);/instanceId, StringComparison.Ordinal) || true;/' \
    "otlp: a generated service instance id counts as saved whatever the file holds, so the warning that it was not never fires"

mutate Pulse.Otlp/PulseOtlpModSystem.cs \
    's/set ServiceInstanceId in \{0\}/set it in {0}/' \
    "otlp: the warning about an id that could not be saved stops naming the key to set"

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
