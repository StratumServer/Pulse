# Changelog

All notable changes to Pulse are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow
[SemVer](https://semver.org). Releases are tag-driven: a tag with a hyphenated suffix
(`v0.1.0-indev.1`) is a prerelease, and every stable version gets at least one prerelease
first.

## [Unreleased]

### Added

- Prometheus scrape endpoint on a dedicated thread, loopback by default, port 9464,
  configurable through `ModConfig/pulse.json`.
- Five metric families: `pulse_server_ticks_total`, `pulse_server_tick_seconds` (histogram of
  the wall-clock tick period), `pulse_players_online`, `pulse_entities_loaded`,
  `pulse_server_tick_budget_seconds`.
- Instrumentation through `System.Diagnostics.Metrics` with a hand-rolled aggregator and text
  writer, so the mod ships a single dll with no bundled dependencies.
- A bind failure logs an error and leaves the game server running without an endpoint.
- Labelled series: measurements are keyed by instrument and tag set, and the writer renders
  `name{key="value"} value` with the exposition format's three escapes. Unlabelled families are
  written exactly as before.
- Worldgen metrics: `pulse_worldgen_queue_columns` (gauge) and
  `pulse_worldgen_columns_generated_total`, counted from `MapChunkGeneration` on the worldgen
  thread.
- `pulse_chunks_loaded` (gauge), on its own listener at `ChunksRefreshSeconds` because reading
  the loaded-chunk count clones the whole dictionary under the chunk lock.
- Log-derived counters from `Logger.EntryAdded`: `pulse_log_entries_total{level}` by severity,
  and `pulse_engine_warnings_total{kind}` matching four of the engine's own warning strings at
  1.22.7 (tick overload, memory ceiling, suspend timeout, autosave disk contention).
- The runtime's built-in `System.Runtime` meter is served as `dotnet_*` families behind the
  `RuntimeMetrics` config flag, on by default: GC, heap, working set, CPU time, JIT, thread
  pool, exceptions.
- Two config keys: `RuntimeMetrics` (bool, true) and `ChunksRefreshSeconds` (int, 30).
