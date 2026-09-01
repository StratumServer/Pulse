# Pulse

Pulse is a server-side Vintage Story mod that serves the server's own health numbers on a
Prometheus scrape endpoint. It runs on the dedicated server only, ships as a single dll with no
bundled dependencies, and does not talk to anything on its own: something has to come and read
`/metrics`. A separate optional mod pushes the same metrics over OTLP, described further down.

The metric families it serves:

- `pulse_server_ticks_total` (counter): server ticks processed since startup. Prometheus
  `rate()` over it is your TPS.
- `pulse_server_tick_seconds` (histogram): wall-clock seconds between consecutive ticks,
  measured with the mod's own stopwatch, in buckets from 25 ms to 1 s.
- `pulse_players_online` (gauge): players currently connected.
- `pulse_entities_loaded` (gauge): entities loaded in the world.
- `pulse_server_tick_budget_seconds` (gauge): the configured tick budget, so
  `tick_seconds / tick_budget_seconds` reads as saturation.
- `pulse_worldgen_queue_columns` (gauge): chunk columns waiting in the generation queue.
- `pulse_worldgen_columns_generated_total` (counter): chunk columns generated since startup.
- `pulse_chunks_loaded` (gauge): chunks loaded in the world, on a slow cadence of its own.
- `pulse_log_entries_total{level}` (counter): log entries by severity, one series each for
  `warning`, `error` and `fatal`.
- `pulse_engine_warnings_total{kind}` (counter): the engine's own health warnings, recognised
  by the text it logs. `overload` is a tick past 500 ms, `memory` is crossing 90% of
  `DieAboveMemoryUsageMb`, `suspend_timeout` is a server suspend that gave up waiting for a
  thread, and `autosave_io` is an autosave arriving while the previous one is still writing.
- `pulse_server_uptime_seconds` (gauge): seconds the server has been ticking. This is the
  engine's own unpaused clock, so it stops during a save and is not process uptime.
- `pulse_entities_by_code{code}` (gauge): loaded entities by entity code, the ten most numerous
  plus an `other` bucket for the rest. Refreshed on the same slow cadence as the chunk count.
- `pulse_player_ping_seconds{stat}` (gauge): round trip time to the players online, as `avg` and
  `max`. Both read 0 with nobody connected.
- `pulse_player_deaths_total` (counter): player deaths since startup.
- `pulse_server_suspends_total` and `pulse_server_suspend_seconds_total` (counters): how often
  the server suspended ticking and how long it spent suspended. Every autosave is one of these,
  and the seconds are the pause players actually feel.
- `pulse_network_sent_bytes_total` and `pulse_network_received_bytes_total` (counters): bytes
  over the main TCP channel. UDP is not in these two; the public server API does not report it.

Six more come from the engine's own accounting, which no public API exposes. See the note on
degraded mode below for what happens when they are unavailable.

- `pulse_server_tick_busy_seconds` (gauge): the average time one tick spent working, sleep
  excluded, over the engine's last completed two-second window. This is the number `/stats`
  prints, and the only view of headroom below the tick budget that exists at all. The engine
  measures in whole milliseconds, so an idle server legitimately averages zero.
- `pulse_network_packets_per_second{channel}` and `pulse_network_bytes_per_second{channel}`
  (gauges): traffic rates over that same window, split `tcp` and `udp`. Gauges rather
  than counters because the engine zeroes its window rather than accumulating it; a window cut short by a suspend reads low for one sample.
- `pulse_connection_queue_clients` (gauge): clients waiting because the server is full.
- `pulse_network_udp_sent_bytes_total` and `pulse_network_udp_received_bytes_total` (counters):
  the UDP totals missing from the two public byte counters above.

The tick period is measured rather than taken from the value the engine hands tick listeners,
because that one is rounded to whole milliseconds. Overruns still land exactly: once a tick's
work exceeds the budget the engine's throttle sleep is zero, and the period is the busy time.
Most gauges come from a snapshot the tick listener refreshes about once a second on the
server's main thread, so a scrape never touches live world state.

Loaded chunks are the exception. The engine offers no cheap count, and the one accessor that
exists clones the entire loaded-chunk dictionary under the chunk lock, so that gauge gets its
own listener at `ChunksRefreshSeconds` and reads 0 until the first refresh. The entity breakdown
rides that same slow listener. The event-driven counters do not ride the tick listener either:
columns generated is incremented from `MapChunkGeneration`, which fires on the worldgen thread,
and the log counters from `Logger.EntryAdded`, which fires on whichever thread wrote the line.
Both handlers classify and increment, and nothing else.

## Degraded mode

Six of those families come from a place the modding API does not reach. Tick busy time, the
per-window packet and byte counts, the connection queue depth and the UDP byte totals are all
measured by the engine on concrete types inside `VintagestoryLib.dll`, which the game's authors
change freely between versions and make no promises about. Pulse casts the server world to
`Vintagestory.Server.ServerMain` once, at startup, in a try/catch, and every read of those types
lives in one small class.

If that cast ever stops working, Pulse logs one warning naming what went wrong and carries on.
The six families are simply absent from `/metrics` rather than present and lying, and every other
metric on this page keeps being served exactly as before, including the tick period histogram,
which still catches every overrun: once a tick's work exceeds the budget the engine's throttle
sleep is zero and the period is the busy time. What you lose is the view of headroom on a healthy
server. One warning, no retry loop, no log spam, and nothing that can stop a game server.

The compile-time reference to `VintagestoryLib.dll` is deliberate for the same reason. A game
version that renames or moves `ServerMain` breaks the Pulse build, loudly, before a release goes
out, instead of shipping a mod that quietly serves six families fewer.

## Runtime metrics

With `RuntimeMetrics` left on, the .NET runtime's own `System.Runtime` meter is served
alongside Pulse's, as `dotnet_*` families: GC collections and pause time, heap size and
fragmentation by generation, working set, CPU time by mode, JIT, thread pool, lock contention,
loaded assemblies. None of it is instrumented here. The runtime publishes the meter, Pulse
subscribes to it, and the writer renames the instruments for the exposition format: dots become
underscores, and a monotonic counter gains the `_total` suffix if it lacks one, so
`dotnet.gc.collections` is served as `dotnet_gc_collections_total`.

Pulse renders the shape each instrument declares, including where that is arguable.
`dotnet_thread_pool_thread_count_total` is typed as a counter because the runtime publishes it
as an ObservableCounter, even though the number goes down as often as up. Second-guessing the
framework here would only make the series harder to correlate with any other .NET exporter.

## Install

Drop `pulse_0.1.0.zip` into your server's `Mods/` folder and start the server. Add
`pulseotlp_0.1.0.zip` beside it if you want OTLP push as well; the base mod works on its own and
the OTLP one does not. On first boot Pulse writes `ModConfig/pulse.json` with its defaults:

```json
{
  "Enabled": true,
  "Bind": "127.0.0.1",
  "Port": 9464,
  "RuntimeMetrics": true,
  "ChunksRefreshSeconds": 30
}
```

Set `Enabled` to false and the mod loads but registers nothing at all: no tick listener, no
socket, no meter. `RuntimeMetrics` false drops the `dotnet_*` families and keeps the rest, which
is what you want if something else already collects them on that host. `ChunksRefreshSeconds`
is how often the loaded-chunk gauge is refreshed, and 30 is already fast for what that read
costs; lower it only if you know why. Every one of these takes a server restart.

## Scraping it

```yaml
scrape_configs:
  - job_name: vintagestory
    static_configs:
      - targets: ["127.0.0.1:9464"]
```

`GET /metrics` returns the exposition text; every other path returns 404.

A ready-to-run Prometheus and Grafana pair lives in `contrib/grafana`; Prometheus alerting rules
calibrated to these thresholds live in `contrib/alerts`.

### For panel authors

The exposition text is the contract: game panels can read `/metrics` directly instead of going
through Prometheus, which is how the first panel integration was built. Three things to know.
Each server instance runs its own Pulse on its own port, so a shared machine has one endpoint
per instance. The loopback bind covers a panel running on the same host; scraping from another
machine goes through a reverse proxy or a deliberate `Bind` change, as above. Any polling
cadence works, the endpoint is cheap to hit; existing metric families keep their names and
shapes, and anything breaking would be called out loudly in the changelog first.

### A word on the bind address

The default binds loopback, which means only something running on the same host can scrape it.
That default is deliberate. A Vintage Story server is usually a public host, and the metrics
endpoint has no authentication of any kind, so widening `Bind` to `0.0.0.0` publishes your
player count and tick health to whoever asks. If you need to scrape from elsewhere, put the
endpoint behind a reverse proxy or a firewall rule, or tunnel to it. Changing `Bind` is a choice
you should make on purpose, not a default you inherit.

If the port is already taken, Pulse logs an error and carries on without the endpoint. The game
server keeps running; you get no metrics until you fix the config.

## OTLP export

OTLP is the push counterpart to the scrape endpoint: instead of waiting for Prometheus to come
and read `/metrics`, the server sends its metrics to a collector on a timer, in the wire format
every major observability backend accepts. Grafana Cloud, Honeycomb, Datadog, New Relic and an
`otel-collector` you run yourself all take the same payload.

It ships as a second mod, `pulseotlp_0.1.0.zip`, and both zips go in `Mods/`. The base mod stays
a single dll with no dependencies; the OTLP one carries the OpenTelemetry SDK and its
`Microsoft.Extensions.*` fan-out, eighteen dlls in all. That split is not tidiness. The game's
mod loader puts every root-level dll of every mod into one shared assembly context with no
version arbitration, so a bundled dependency is a collision risk against every other mod on the
server, and a server that does not push metrics should not be paying it.

The two mods share no code. `Pulse.Otlp.dll` has no reference to `Pulse.dll`; it subscribes to
the meter named `Pulse.Server`, which is all `System.Diagnostics.Metrics` needs, and
`modinfo.json` declares the dependency so the loader guarantees the base mod is there first.

On first boot it writes `ModConfig/pulse-otlp.json`:

```json
{
  "Enabled": true,
  "Endpoint": "http://localhost:4318",
  "Protocol": "http/protobuf",
  "Headers": {},
  "IntervalSeconds": 60,
  "IncludeRuntimeMetrics": true
}
```

Those defaults suit a collector running on the same host. `Endpoint` is the base address, without
a signal path: Pulse appends `/v1/metrics` for `http/protobuf` and leaves it alone for `grpc`,
where the exporter appends its own service path. `Protocol` takes the two names the OTLP
specification defines, `http/protobuf` and `grpc`; anything else logs a warning and falls back to
`http/protobuf` rather than leaving you with no export at all. `IncludeRuntimeMetrics` adds the
`System.Runtime` meter to what gets pushed, and it is separate from the base mod's
`RuntimeMetrics` flag, so you can serve the `dotnet_*` families locally and not ship them, or the
other way round.

`IntervalSeconds` is floored at 5. Sixty is the OTLP default and the right answer for almost
everyone: the interval also decides how often every observable gauge is polled, and the
loaded-chunk read behind one of them is not free.

For a local collector, the whole config is the endpoint:

```json
{
  "Endpoint": "http://localhost:4318",
  "Protocol": "http/protobuf"
}
```

A hosted backend wants an auth header. Grafana Cloud's OTLP endpoint takes HTTP basic auth, with
the instance ID as the user and an access policy token as the password:

```json
{
  "Endpoint": "https://otlp-gateway-prod-eu-west-2.grafana.net/otlp",
  "Protocol": "http/protobuf",
  "Headers": {
    "Authorization": "Basic MTIzNDU2OmdsY19leGFtcGxldG9rZW4="
  },
  "IntervalSeconds": 60
}
```

`Headers` goes out with every export, so anything a backend accepts works the same way:
`x-honeycomb-team` for Honeycomb, `api-key` for New Relic, `x-scope-orgid` for a multi-tenant
Mimir. Values are percent-encoded on the way into the exporter, which the OTLP header format
expects, so a base64 token with `+`, `/` and `=` in it needs no special handling. A literal comma
in a header value is the one thing that cannot survive the trip, because the exporter unescapes
the whole header string before splitting it on commas. No auth scheme in the wild puts a comma in
a token.

**`pulse-otlp.json` holds a credential.** It sits in `ModConfig/` in plain text, with whatever
permissions your server's umask gave it. On a shared or rented host, `chmod 600` it and make sure
it is owned by the account the server runs as. It is also worth keeping out of any config backup
you push somewhere public.

A collector that is down, refusing, or answering 401 costs you nothing on the game side. The
OpenTelemetry SDK exports from its own background thread and swallows the failure into its
internal event source, so the tick loop never sees it. You get no metrics until the collector
comes back, and the server does not notice either way. A malformed `Endpoint` is the one case
Pulse checks itself, because that one would throw while the exporter is being built: it logs an
error and registers nothing.

## Building and testing

You need the .NET 10 SDK and a Vintage Story 1.22.x install, with `VINTAGE_STORY` pointing at
the folder that holds `VintagestoryAPI.dll` and `VintagestoryLib.dll` (the `.pdb` next to the
first is required too, or the engine's logger crashes at boot). Both dlls are compile-time
references only; neither is copied into the mod, which still ships as one file.

```sh
export VINTAGE_STORY=/path/to/vintagestory
dotnet build Pulse.slnx -c Release
dotnet test                      # unit tests, then the Atlas scenarios
dotnet build Pulse/Pulse.csproj -c Release -t:PackageMod            # artifacts/pulse_0.1.0.zip
dotnet build Pulse.Otlp/Pulse.Otlp.csproj -c Release -t:PackageMod  # artifacts/pulseotlp_0.1.0.zip
```

The scenarios in `Pulse.Scenarios` boot a real headless server in-process through
[Atlas](https://github.com/Pixnop/Atlas), load the mod, and scrape it over HTTP for real. The
`atlas` CLI runs the same assembly without VSTest, which is faster to iterate against:

```sh
atlas run Pulse.Scenarios/bin/Release/net10.0/Pulse.Scenarios.dll
atlas run Pulse.Otlp.Scenarios/bin/Release/net10.0/Pulse.Otlp.Scenarios.dll
```

`Pulse.Otlp.Scenarios` is a separate project because it stages both mods, laid out exactly as
their zips are, and the base suite's staging should stay as it is. It stands up an `HttpListener`
as a fake collector, points the mod at it, runs the world, and asserts on the protobuf that
arrives.

Unit tests in `Pulse.Tests` cover the aggregator, the exposition writer, the log classifier and
the small classes behind the wave of engine and world metrics: the busy-time average, the ping
aggregates, the entity top-ten with its series retirement rule, and the suspend window. None of
them needs a server. `Pulse.Otlp.Tests` covers the config translation, which is where the OTLP
mod's only non-obvious logic lives. Mutation verification over those files runs through
`tools/mutation-check.sh`, which applies representative mutations one at a time and requires the
suite to fail on every one; CI runs it on each push. A `stryker-config.json` sits ready for
`dotnet stryker`, which currently finds the tests but runs mutants against the unmutated
assembly on the .NET 10 SDK.

## Where this is going

The survey in `docs/metrics-feasibility.md` lists what is measurable on this engine and what is
not. Save duration is the honest gap: the world-save event fires before any writing happens and
there is no completion signal, so Pulse reports the suspend window instead of inventing a
number. Per-player traffic does not exist anywhere in the engine, not even internally.

Traces and logs over OTLP would reuse most of the exporter mod, but neither has an obvious
consumer on a game server yet, so they stay unbuilt until someone asks.

## License

MIT, see [LICENSE](LICENSE) and [NOTICE](NOTICE).
