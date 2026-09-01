# Changelog

All notable changes to Pulse are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow
[SemVer](https://semver.org). Releases are tag-driven: a tag with a hyphenated suffix
(`v0.1.0-indev.1`) is a prerelease, and every stable version gets at least one prerelease
first.

## [Unreleased]

### Added

- `contrib/alerts/pulse-alerts.yml`, a Prometheus alerting rules file covering tick rate, tick
  saturation, sustained tick overruns, engine warnings, log errors, endpoint availability and a
  stuck worldgen queue, calibrated against the engine's own thresholds. `contrib/alerts/README.md`
  explains how to load it.
- `.github/workflows/game-watch.yml`, a weekly scheduled workflow that builds and runs both unit
  and scenario suites against the newest stable Vintage Story server version and fails if either
  doesn't hold up, catching an engine-side break before a user's server update does.
- `ServiceName` config key in `pulse-otlp.json` (default `vintagestory`), setting the
  `service.name` resource attribute so a backend receiving metrics from several servers can tell
  them apart. `OTEL_SERVICE_NAME`, the ecosystem's standard override, takes precedence when set.

## [0.1.0] - 2026-09-01

The first stable release, identical in content to v0.1.0-indev.5. Field-tested on a hosting
provider's server, feeding one game panel's live metrics page, and exercised against the
official OpenTelemetry collector.

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
- OTLP export, as a second optional mod (`pulseotlp`) shipped from the same repo and the same
  tag. It carries the OpenTelemetry SDK and its dependencies so the base mod stays a single dll
  with nothing to collide with, and it reaches the base mod through the meter name `Pulse.Server`
  rather than an assembly reference.
- `ModConfig/pulse-otlp.json` with `Enabled`, `Endpoint`, `Protocol` (`http/protobuf` or `grpc`),
  `Headers` for backend authentication, `IntervalSeconds` (60, floored at 5) and
  `IncludeRuntimeMetrics`, which is independent of the base mod's `RuntimeMetrics`.
- An unknown `Protocol` warns and exports over `http/protobuf`; an `Endpoint` that is not an http
  or https URL logs an error and registers nothing. A collector that is unreachable or refusing
  costs the game server nothing, since the SDK exports from its own thread.
- The engine's own accounting, read through a guarded cast to the concrete server type:
  `pulse_server_tick_busy_seconds` (the number `/stats` prints, and the only view of tick
  headroom below the budget that exists), `pulse_network_packets_per_second{channel}` and
  `pulse_network_bytes_per_second{channel}` over the engine's completed two-second window,
  `pulse_connection_queue_clients`, and the UDP byte totals
  `pulse_network_udp_sent_bytes_total` and `pulse_network_udp_received_bytes_total`.
- Degraded mode for those six: the cast is resolved once at startup inside a try/catch and every
  read of a concrete engine type lives in one class. A failure logs one warning and the six
  families are absent rather than wrong; every other metric keeps being served.
- From the public server API, no cast involved: `pulse_server_uptime_seconds`,
  `pulse_network_sent_bytes_total` and `pulse_network_received_bytes_total` (main TCP channel
  only, as the help text says), `pulse_player_deaths_total`, `pulse_player_ping_seconds{stat}`
  as `avg` and `max`, and `pulse_server_suspends_total` with
  `pulse_server_suspend_seconds_total` bracketing every autosave pause.
- `pulse_entities_by_code{code}`, the ten most numerous entity codes plus an `other` bucket,
  refreshed on the `ChunksRefreshSeconds` listener. A code that drops out of the top ten is
  explicitly zeroed once, so its series retires instead of freezing at its last count.

### Fixed

- The exposition writer now groups every series of a metric family together. Series of one
  family are opened whenever a tag set is first measured, so a labelled family could previously
  be split across the body with other families in between.
