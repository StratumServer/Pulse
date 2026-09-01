# Pulse

Pulse is a server-side Vintage Story mod that serves the server's own health numbers on a
Prometheus scrape endpoint. It runs on the dedicated server only, ships as a single dll with no
bundled dependencies, and does not talk to anything on its own: something has to come and read
`/metrics`.

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

The tick period is measured rather than taken from the value the engine hands tick listeners,
because that one is rounded to whole milliseconds. Overruns still land exactly: once a tick's
work exceeds the budget the engine's throttle sleep is zero, and the period is the busy time.
Most gauges come from a snapshot the tick listener refreshes about once a second on the
server's main thread, so a scrape never touches live world state.

Loaded chunks are the exception. The engine offers no cheap count, and the one accessor that
exists clones the entire loaded-chunk dictionary under the chunk lock, so that gauge gets its
own listener at `ChunksRefreshSeconds` and reads 0 until the first refresh. The two event-driven
counters do not ride the tick listener either: columns generated is incremented from
`MapChunkGeneration`, which fires on the worldgen thread, and the log counters from
`Logger.EntryAdded`, which fires on whichever thread wrote the line. Both handlers classify and
increment, and nothing else.

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

Drop `pulse_0.1.0.zip` into your server's `Mods/` folder and start the server. On first boot
Pulse writes `ModConfig/pulse.json` with its defaults:

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

### A word on the bind address

The default binds loopback, which means only something running on the same host can scrape it.
That default is deliberate. A Vintage Story server is usually a public host, and the metrics
endpoint has no authentication of any kind, so widening `Bind` to `0.0.0.0` publishes your
player count and tick health to whoever asks. If you need to scrape from elsewhere, put the
endpoint behind a reverse proxy or a firewall rule, or tunnel to it. Changing `Bind` is a choice
you should make on purpose, not a default you inherit.

If the port is already taken, Pulse logs an error and carries on without the endpoint. The game
server keeps running; you get no metrics until you fix the config.

## Building and testing

You need the .NET 10 SDK and a Vintage Story 1.22.x install, with `VINTAGE_STORY` pointing at
the folder that holds `VintagestoryAPI.dll` (the `.pdb` next to it is required too, or the
engine's logger crashes at boot).

```sh
export VINTAGE_STORY=/path/to/vintagestory
dotnet build -c Release
dotnet test                      # unit tests, then the Atlas scenarios
dotnet build Pulse/Pulse.csproj -c Release -t:PackageMod   # writes artifacts/pulse_0.1.0.zip
```

The scenarios in `Pulse.Scenarios` boot a real headless server in-process through
[Atlas](https://github.com/Pixnop/Atlas), load the mod, and scrape it over HTTP for real. The
`atlas` CLI runs the same assembly without VSTest, which is faster to iterate against:

```sh
atlas run Pulse.Scenarios/bin/Release/net10.0/Pulse.Scenarios.dll
```

Unit tests in `Pulse.Tests` cover the aggregator, the exposition writer and the log classifier
with no server at all. Mutation verification over those three files runs through
`tools/mutation-check.sh`, which applies twelve representative mutations and requires the suite
to fail on every one; CI runs it on each push. A `stryker-config.json` sits ready for
`dotnet stryker`, which currently finds the tests but runs mutants against the unmutated
assembly on the .NET 10 SDK.

## Where this is going

OTLP export is the next thing worth building, since no Vintage Story exporter has it today and
it is the reason to instrument through `System.Diagnostics.Metrics` rather than write to the
Prometheus format directly. It will land when it lands. Beyond that, the survey in
`docs/metrics-feasibility.md` lists what else is measurable on this engine: the global network
byte counters, the suspend window that brackets every autosave, and the engine's own busy-time
accounting, which needs a cast to a concrete engine type and so has to degrade cleanly when a
game update moves it.

## License

MIT, see [LICENSE](LICENSE) and [NOTICE](NOTICE).
