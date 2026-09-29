# Pulse metrics feasibility report

**Historical document.** This is the day-one survey, written on 1 September 2026 before any
mod code existed, and kept as the record of what was known then. Where it disagrees with the
README, the README describes what shipped. The main places it has been overtaken: the join and
leave counters were never built, the suspend window ships as two counters rather than a
histogram, the packaging question closed as one dll plus a separate optional OTLP mod, the
engine's frame profiler turned out to be the per-mod attribution source (it is dismissed below
as a tick-time source, which is still true), and Stratum's `StratumEntityBehaviorTimings` is
not readable from another mod, so it is not the V2 route this survey imagined.

Survey of Vintage Story 1.22.7 server internals, done before writing any mod code. Method: the public API sources at 1.22.7 (GitHub master, which matches the shipped build; the stable branch lags at 1.20.11), the shipped `VintagestoryAPI.xml`, and decompilation of the closed `VintagestoryLib.dll` where the engine hides the interesting parts. Line references below point at the 1.22.7 sources or at decompiled engine types. Raw survey notes live in `.survey/` (not committed).

## Verdict

Go. Every metric group we care about is measurable today, most of it through the documented public API, and the runtime does us a favor: VS 1.22 runs on .NET 10, so `System.Diagnostics.Metrics` ships in the framework and the built-in `System.Runtime` meter hands us GC, heap, CPU, working set and thread pool metrics for free. One compiled dll runs unchanged on vanilla, Stratum and Lithos.

Two findings reshape the plan rather than block it:

1. **We are not first.** A closed-source mod, VS Exporter (SalteK, released May 2026, 37 downloads, one release, game 1.22.0 to 1.22.3), already serves a Prometheus `/metrics` endpoint plus a Zabbix JSON one. No published source, no license, no changelog history, and it lacks GC stats, log-derived error counters, OTLP and callback introspection. Our differentiator shifts from "the Prometheus option" to "the open, database-agnostic one built on the standard Meter API, with OTLP". Temporalog remains InfluxDB-push only.
2. **The best tick number is engine-internal.** The engine already measures per-tick busy time (`ServerMain.StatsCollector`), but no public API exposes it. Reaching it takes a cast to a concrete engine type, which works and is what `/stats` itself reads, but it is undocumented surface that can move between game versions. The design below treats it as an optional bonus with a clean degraded mode, never as a foundation.

## The tick model, in one paragraph

`ServerMain.Process()` is a sleep-throttled loop: run systems, fire tick listeners, drain main-thread tasks, measure elapsed busy time, then sleep whatever remains of `Config.TickTime` (default 33.333 ms, so 30 TPS nominal; operators can change it with `/serverconfig tickrate`). There is no catch-up mechanism; slow ticks just lower TPS. Busy time per tick is accumulated in whole milliseconds into `StatsCollector`, a public ring of four 2-second buckets, and a tick busier than 500 ms logs "Server overloaded. A tick took {0}ms to complete." During autosave the server suspends: no ticks run and the unpaused clock stops, so tick series legitimately pause while wall time advances.

## Metric by metric

| Metric | Source | Cost per read | Route |
|---|---|---|---|
| Tick rate (TPS) | own counter in a tick listener | negligible | public API |
| Tick period | own stopwatch delta in the same listener | negligible | public API |
| Tick busy time | `ServerMain.StatsCollector` buckets | field reads | engine cast, optional |
| Tick budget | `sapi.Server.Config.TickTime` | O(1) | public API |
| Players online | `sapi.World.AllOnlinePlayers` | O(players) | public API |
| Player ping | `IServerPlayer.Ping` | O(1) | public API |
| Entities loaded | `sapi.World.LoadedEntities` | Count locks briefly; enumeration lock-free | public API |
| Chunks loaded | `AllLoadedChunks.Count` | full dictionary clone under lock | public API, scrape slowly |
| Worldgen queue | `WorldManager.CurrentGeneratingChunkCount` | O(1) | public API |
| Columns generated | `MapChunkGeneration` event | O(1) per column | public API |
| Memory, GC, CPU, thread pool | built-in `System.Runtime` meter | pull-based | .NET, free |
| Network bytes | `sapi.Server.TotalSent/ReceivedBytes` | O(1) | public API, TCP only |
| Network UDP and packets/s | `StatsCollector` buckets | field reads | engine cast, optional |
| Saves and pauses | `GameWorldSave`, `ServerSuspend/Resume` | event | public API |
| Log health counters | `ILogger.EntryAdded` | event | public API |
| Uptime | `sapi.Server.ServerUptimeSeconds` | O(1) | public API |

### Tick timing

The backbone is one `sapi.Event.RegisterGameTickListener(onTick, errorHandler, 0)` registered in `StartServerSide`. Interval 0 fires every tick (strictly: every tick in which at least 1 ms of unpaused time has passed, which at a 33 ms tick is every tick). The callback runs on the server main thread. From it we keep a monotonic tick counter (TPS falls out of `rate()` on the Prometheus side) and record the inter-firing delta from our own `Stopwatch` as a tick period histogram. Not the float the engine passes in: that one is quantised to whole milliseconds. The period histogram catches every overrun exactly, because once busy time exceeds the budget the throttle sleep is zero and period equals busy time.

Two hard rules learned from the decompile. Always pass the `errorHandler` overload: an unhandled exception in a tick listener aborts the remainder of that entire server tick, logs Fatal, and Fatal entries count toward the server's `DieAboveErrorCount` self-shutdown. And read the budget from `sapi.Server.Config.TickTime` instead of hardcoding 33.3, since operators can retune it.

On top of that, a guarded cast gives us the engine's own busy-time accounting, the very numbers `/stats` prints:

```csharp
var sm = sapi.World as Vintagestory.Server.ServerMain;   // null means degraded mode
var sc = sm.StatsCollector[GameMath.Mod(sm.StatsCollectorIndex - 1, 4)];
// average busy ms = sc.tickTimeTotal / (double)sc.ticksTotal, guard ticksTotal > 0
```

`ServerMain` and these fields are public, but they live in `VintagestoryLib.dll`, which the developers change freely between versions and which only a precompiled mod can reference. So: resolve once at startup inside try/catch, sample it from the main-thread listener (the buckets rotate every 2 s, sampling faster is pointless), and if the cast ever breaks, log one warning and keep serving everything else. The only loss in degraded mode is sub-budget busy time, in other words headroom on a healthy server. Overruns still show exactly in the period histogram.

Do not compute TPS as `ticksTotal / 2.0` the way `/stats` does; the 2.0 divisor is hardcoded and silently wrong around suspends. Our own counter is strictly better.

### Players

`sapi.World.AllOnlinePlayers.Length` for the gauge. It is backed by the engine's concurrent client table, so it is the one player collection that is provably safe to touch off the main thread (we will not need that property, but it is good to know which side of the line each member sits on; `sapi.Server.Players` and `AllPlayers` are backed by a plain unsynchronized dictionary). Per-player ping comes from `IServerPlayer.Ping`, in seconds, NaN when offline.

Join and leave counters hook `PlayerNowPlaying` and `PlayerDisconnect`. Trap confirmed in the packet handler: on a graceful quit the engine fires `PlayerLeave` and then also `PlayerDisconnect` for the same disconnection. `PlayerDisconnect` is the exhaustive signal (leave, timeout and kick alike); moving a counter on both events double-counts every voluntary leave.

No per-player metric labels in the scraped series. Player names are not stable identifiers (the game's own docs warn they can change), UIDs are stable but unbounded over a server's life, and either way the cardinality is wrong for a time series database.

### Entities

`sapi.World.LoadedEntities` is public API and genuinely a `ConcurrentDictionary`. `.Count` briefly takes every internal lock stripe, cheap in absolute terms but a small serialization point against spawn/despawn, fine at scrape cadence. Enumeration is lock-free and weakly consistent, which is the right way to build the optional per-entity-code breakdown (and the active-versus-inactive split `/stats` shows, via `entity.State`). Spawn, despawn and death events exist if we later want counters instead of gauges.

### Chunks

The awkward one. There is no O(1) public count of loaded chunks: `AllLoadedChunks` clones the whole dictionary under the chunk lock on every call, and the interface doc itself warns against calling it often. `LoadedChunkIndices` allocates a full array too. The loaded-chunk gauge therefore samples on its own slow cadence (30 s or more), independent of the scrape.

The cheap and cheerful neighbors: `CurrentGeneratingChunkCount` is a plain index subtraction, the cheapest metric in the whole survey, and a genuinely useful worldgen backlog signal. `ChunkDeletionsInQueue` is its undocumented sibling. For "columns generated since start", the `MapChunkGeneration` event fires exactly once per newly generated column and is the same mechanism the vanilla worldgen hooks through; `ChunkColumnLoaded` is the wrong hook (it conflates disk loads with generation), and anything registered on `ChunkColumnUnloaded` must be allocation-free because shutdown fires it tens of thousands of times.

### Memory, GC, runtime

Free. The shared framework's `System.Runtime` meter (verified present in the shipped .NET 10 `System.Diagnostics.DiagnosticSource.dll`) publishes `dotnet.gc.collections` by generation, heap size and fragmentation, `dotnet.gc.pause.time`, `dotnet.process.memory.working_set`, `dotnet.process.cpu.time`, thread pool queue length and more. Zero instrumentation code on our side; the exporter just subscribes to that meter alongside ours. If we want to mirror the exact numbers `/stats` prints, `GC.GetTotalMemory(false)` and `Process.WorkingSet64` are plain BCL.

### Network

The public API exposes exactly two counters, `sapi.Server.TotalSentBytes` and `TotalReceivedBytes`, both undocumented in the XML doc and covering only the main TCP channel. UDP traffic is tracked internally but not surfaced on the interface, so the public numbers are a lower bound whenever UDP is in play; the per-2s packet and UDP counters sit in the same `StatsCollector` buckets as busy time and ride the same optional cast. Per-player traffic does not exist anywhere, not even internally. We expose the two public counters, add the bucket figures in cast mode, and say plainly in the metric help text that UDP is excluded on the public path.

### Saves and suspends

`GameWorldSave` fires before anything is written, on the main thread, while the server is already suspended, and there is no completion event; on a routine autosave the chunk flush finishes later on another thread with no signal at all. True save duration is therefore not measurable, and we will not pretend otherwise. What we can measure honestly is the suspend window (`ServerSuspend` to `ServerResume`), which is precisely the pause players feel, tagged as save-related when `GameWorldSave` fired inside it. Save counter plus suspend duration histogram covers the operator question ("how often and how long does the world pause") without inventing a number. Our own `ServerSuspend` handler must return `Ready` immediately so we never delay an autosave.

### Log-derived health counters

`ILogger.EntryAdded` is public API and is how the engine's own monitor system watches itself. Counting entries by severity gives error and warning rates; string-matching four engine messages gives high-signal counters that nothing else exposes: the 500 ms overload warning, the two memory warnings near `DieAboveMemoryUsageMb`, the suspend timeout ("possibly deadlocked"), and the autosave disk bottleneck warning. Temporalog has run this exact approach in production for two years.

### Uptime

`sapi.Server.ServerUptimeSeconds`. Not `ServerUptimeMilliseconds`: the engine truncates it through an int cast, so it wraps negative after 24.9 days of unpaused uptime, exactly the horizon a long-running public server crosses. Worth an upstream report once we have a minimal repro written up. Note both count unpaused time and freeze during saves; wall-clock uptime would need the cast (`totalUpTime`) or our own process clock.

## Execution architecture the survey dictates

Nothing here is speculative; each rule traces to a verified engine behavior.

Pulse ships as a **precompiled dll mod**. Source-code mods compile against a fixed Roslyn reference list that omits `System.Diagnostics.DiagnosticSource`, so the Meter API is out of reach for them, and the optional engine cast needs a `VintagestoryLib` reference anyway. Mods are fully trusted (no sandbox), so threads and sockets are ours to use, with care.

Sampling happens **on the main thread only**, inside the tick listener, into an immutable snapshot the HTTP thread reads. Not one world-state member is documented thread-safe for off-thread reads, and entity objects mutate without locks under the physics threads.

The HTTP listener runs on a **dedicated background thread** from `TyronThreadPool.CreateDedicatedThread` (public API, named, visible in `/debug threadpoolstate`). The two tempting alternatives both fail: `AddServerThread` workers freeze during every autosave (a scrape landing mid-save would time out) and are joined for up to 60 s at shutdown, and the engine caps the shared .NET thread pool at 10 workers, so parking a blocking listener on `Task.Run` starves everyone.

Bind the socket in `StartServerSide` so a port conflict fails loudly at startup; flip the endpoint to ready at the `RunGame` phase; tear everything down in `ModSystem.Dispose`, which the shutdown path reaches on SIGTERM/SIGINT as well. Localhost bind by default, port configurable, any wider exposure an explicit config choice. This is a public game server; the metrics endpoint is for the host, not the internet.

## Dependencies and packaging reality

The mod loader extracts a zip and loads **every root-level dll in it** through `Assembly.UnsafeLoadFrom` into the single shared load context. There is no per-mod isolation, no version arbitration and no `deps.json` resolution: two mods shipping the same assembly name either fail hard ("Assembly with same name is already loaded") or silently resolve to whichever copy the probe order finds first. Every dll we bundle is a collision surface against every other mod on the server.

That fact, plus the state of the exporter ecosystem, drives the choice for the default endpoint. OpenTelemetry's Prometheus exporter describes itself as dev-only with no plan to become production ready, so it is out. prometheus-net still ships an `HttpListener`-based `MetricServer` with about eight transitive dlls, but its last release was January 2024. The Prometheus text exposition format itself is a page of spec, and Temporalog proved the pattern by hand-rolling its InfluxDB line-protocol client in ~180 lines rather than shipping any dependency. The recommendation: instrument with the Meter API, aggregate through a `MeterListener`, and write the exposition text ourselves. Zero bundled dlls in the base mod, nothing to collide with anyone.

OTLP is where a real dependency earns its place: `OpenTelemetry` core plus `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.18.0 are stable, actively maintained, and pull neither gRPC nor Google.Protobuf (the exporter carries its own protobuf writer; the game's `protobuf-net` is a different assembly entirely, no clash). The cost is the `Microsoft.Extensions.*` fan-out, 15 to 20 dlls, which the loader would load for every user whether or not OTLP is enabled. Whether that lands as a second optional zip or as inert dlls in the main one is a scaffolding decision, not a feasibility one.

## Forks: Stratum and Lithos

Both forks are patch sets applied over the decompiled 1.22.7 baseline, pinned to the same vsapi commit we surveyed. Neither removes or renames a public API member: Lithos patches two method bodies and nothing else (binary-compatible, full stop), Stratum's vsapi changes are additive or body-only, with comments showing they preserve constructor signatures for compiled mods on purpose. One Pulse.dll built against vanilla `VintagestoryAPI` runs on all three.

The tick loop is untouched where it matters to us. Stratum swaps the throttle sleep for a precision wait (if anything, steadier cadence), and its Folia-style parallel entity ticking is off by default, experimental, and does not touch the main-thread global tick listener path our probe uses. Its default-on simulation-distance gate only affects the `BlockPos`-scoped listener overloads we do not use. Lithos changes nothing tick-related at all.

Two coordination notes rather than technical ones. Stratum already carries internal instrumentation: `/stratum timings`, and a metrics publisher that pushes a JSON snapshot once a second over a local named pipe. Different transport, different consumer, no conflict with an HTTP scrape endpoint, but the team now has overlapping observability efforts (Lithos Probe on the ModDB as of August 31 is the other one) and Pulse should slot in deliberately: Probe and timings for on-demand diagnosis, Pulse for continuous time series. And for a V2, Stratum compiles a public `StratumEntityBehaviorTimings` accumulator into its vsapi, drainable for per-behavior tick histograms; reflection feature-detection with a clean fallback on vanilla is the way in. One anti-pattern to avoid: never key fork detection off `VintagestoryLib`'s assembly version, Stratum pins it to 1.0.0.0.

## Competition summary

| Mod | Model | Source | Gap for hosters |
|---|---|---|---|
| Temporalog (Th3Dilli, 1.8k dl) | push to InfluxDB v2, 10 s cadence | GitLab, no license on own code | InfluxDB required, no scrape, no OTLP |
| VS Exporter (SalteK, 37 dl) | Prometheus scrape + Zabbix JSON | none published, no license | closed box, no GC or log metrics, no OTLP, one release ever |
| Th3ServerStats (Th3Dilli, 2.3k dl) | on-demand JSON for hosting panels | GitLab, MIT | not observability |
| VS Server Stats (JunnA, 57 dl) | gameplay stats JSON API | none | not observability |

Open ground Pulse takes: open source under a real license, standard Meter instrumentation with pluggable exporters instead of hardcoded writers, OTLP (absent everywhere), and Temporalog-depth metrics (GC, log counters) on a scrape model.

## Risks

The engine cast can break on any game update; the design degrades instead of failing, and the dll-as-ground-truth habit from this survey (GitHub sources have lagged shipped builds before) stays part of the release checklist. Engine tick accounting is whole-millisecond, so busy-time histograms below ~5 ms resolution are illusory; the period histogram uses our own stopwatch precisely for that reason. VS Exporter could open its source or grow OTLP and erode our differentiators, which argues for shipping the minimal mod soon rather than polishing. And Lithos is three days old with one release; re-diff both forks before each Pulse release, a five-minute check given they pin their baselines.

## Proposed next step

Scaffolding plus the minimal mod with three real metrics, suggested set: `pulse_server_ticks_total` with the tick period histogram beside it, `pulse_players_online`, `pulse_entities_loaded`. That trio exercises the tick listener, the snapshot handoff and the HTTP thread, which is the whole architecture in miniature, and it is already a usable Grafana panel for a hoster.
