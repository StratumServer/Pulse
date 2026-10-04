# Changelog

All notable changes to Pulse are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow
[SemVer](https://semver.org). Releases are tag-driven: a tag with a hyphenated suffix
(`v0.1.0-indev.1`) is a prerelease, and every stable version gets at least one prerelease
first.

## [Unreleased]

## [0.2.1] - 2026-10-04

Everything from v0.2.1-indev.1, unchanged: each mod's id in its log lines, an OTLP `instance`
label that survives restarts, and per-mod attribution that credits the game's own entity
behaviours to the mod that ships them.

### Changed

- Both mods now write to the server log through the logger the game gives each mod, so every entry
  they write carries the mod's id in square brackets right after the severity: `[pulse]` for the
  base mod and `[pulseotlp]` for the OTLP mod. `Logs/server-main.log` used to hold
  `[Notification] Pulse serving metrics on http://127.0.0.1:9464/metrics` and now holds
  `[Notification] [pulse] Pulse serving metrics on http://127.0.0.1:9464/metrics`. An entry that
  runs over several lines, such as an exception with its stack trace, has the severity and the id on
  its first line only. The messages themselves are word for word what they were, so a search for the
  words of one still finds it; a pattern, in a log shipper or an alert rule, that expects the
  severity to be followed straight by Pulse's own words needs the mod's tag between the two.
  `pulse_log_entries_total` and `pulse_engine_warnings_total` are unchanged: they count what the
  server's own logger receives, and every mod's logger passes its entries on to it.
- **The `instance` label of a Pulse OTLP server's series now stays the same across restarts, and
  changes once, when the server first starts on this version.** 0.2.0 exported a
  `service.instance.id` that the OpenTelemetry SDK generated at random on every start (unless
  `OTEL_SERVICE_NAME` was set), which Prometheus, Mimir and Grafana Cloud show as the `instance`
  label: every restart began a new set of series next to the old one, and a dashboard or an alert
  keyed on `instance` saw a different server each time. The id now lives in a new
  `ServiceInstanceId` key of `pulse-otlp.json`. Left blank, or missing from a file an older version
  wrote, it is filled with a generated GUID at startup and written into the file, and every start
  after that finds it there. That GUID is not the one the last 0.2.0 start exported, which is the
  one change of label; after it, the label stays. Any other value is used as written, trimmed, so a
  readable id such as `survival-eu-1` can go in the key instead, and clearing the key asks for a
  new generated one. For a server coming from 0.2.0 that first start is also the first rewrite of
  its complete `pulse-otlp.json`, which drops any comments in it without a word and any key Pulse
  does not know with a warning. The line that reports the write, `Pulse OTLP wrote these keys into
  pulse-otlp.json: ServiceInstanceId. Everything else in the file was kept as it was.`, reads that
  way in both mods now: it said `added these keys to <file> with their defaults`, which was not
  true of a key the file held empty or of a generated value.

  Pulse OTLP cannot save the id it generated when `ModConfig` is mounted read-only, or when the
  file is one it cannot rewrite (written with single quotes, say). It then logs a warning that
  names the id and says the next start will export a different one. On a read-only `ModConfig`, put
  `ServiceInstanceId` in the file yourself, or set `OTEL_RESOURCE_ATTRIBUTES=service.instance.id=<id>`
  in the server's environment. A folder that does not survive a restart loses the id the same way,
  with no warning, since the write itself worked. Each server needs an id of its own: a
  `pulse-otlp.json` copied to a second server, or a template that several servers' `ModConfig`
  folders are built from, carries one id to all of them, and two servers with the same
  `ServiceName` and the same id are one server to a backend. Clear the key in the copy, or give each
  server a different `ServiceInstanceId` or `OTEL_RESOURCE_ATTRIBUTES=service.instance.id=<id>`.

  The environment keeps the last word, as it already did for the name. A `service.instance.id` in
  `OTEL_RESOURCE_ATTRIBUTES` takes precedence over the key, and survives: 0.2.0 replaced it with a
  random one on every start unless `OTEL_SERVICE_NAME` was set too. `OTEL_SERVICE_NAME`, when set,
  still leaves the whole identity to the environment, so neither `ServiceName` nor
  `ServiceInstanceId` is used and the resource carries an instance id only if
  `OTEL_RESOURCE_ATTRIBUTES` has one; pairing the two variables, as the 0.2.0 notes advise, still
  works and is no longer needed. `service.name` is unchanged: without `OTEL_SERVICE_NAME`,
  `ServiceName` still wins over a `service.name` in `OTEL_RESOURCE_ATTRIBUTES`. The startup line
  `Pulse OTLP exporting ... as service 'vintagestory'` now ends with `, instance '<id>'` when the
  export carries one, naming what the backend will see.

### Fixed

- Per-mod attribution no longer reports the time of many of the game's own entity behaviours as
  `unattributed`. The engine marks a behaviour with the name its `PropertyName()` returns, and Pulse
  looked that name up by the code its class was registered under, which differs for 18 of the 59
  behaviour classes the game's own mods register in 1.22.7: `despawn` marks as `timeddespawn`,
  `nametag` as `displayname`, `entitystatetags` as `entityStateTags`. Twelve names found no class,
  so their time was filed under `unattributed`, which the README defines as work that no loaded mod
  claims, when it belonged to `game` (the game's own Essentials mod), to `survival` or, for the
  passive physics, to `engine`. Pulse now credits each name to the mod that ships the class
  declaring it, learned from the loaded entities: a behaviour from a third-party mod is billed to
  that mod whatever its name, and a subclass that inherits its parent's name to the parent's mod. On
  a test server with 1,691 entities loaded, `unattributed` went from about 2.5% of the sampled tick
  time to under 0.1%, and the difference went to `game`. What the read costs is in the README's
  cost section.

## [0.2.0] - 2026-09-29

The first stable release of the 0.2 line, carrying everything from v0.2.0-indev.1 through
indev.5 plus the release-review fixes and mutation-testing hardening that followed them:
tick attribution, the runtime metric rename below, the socket-based metrics server, and
export-failure logging in Pulse OTLP.

### Added

- Export failures no longer pass silently. Pulse OTLP now listens to the OpenTelemetry SDK's own
  diagnostic event source, filtered to Pulse's own configured endpoint (exact match; two exporters
  that happen to send to the very same collector URL are inherently indistinguishable from here, a
  known and accepted limit) so another mod's exporter sharing the same process-wide source is not
  logged as Pulse's, and turns the first failure of each kind (a rejected push, a refused or
  unreachable collector, a timeout, and so on) into one line in the server log, repeated at most
  every ten minutes: `Pulse OTLP export to <endpoint> failed: <why>. The backend answered: <clipped
  to 200 characters>. Metrics are not reaching the backend; check Endpoint and Headers in
  pulse-otlp.json. This is logged again at most every 10 minutes.` A matching line reports the
  first successful export after a failure, or the first export after each server start, at
  Notification rather than Warning. Every failure line is logged at Warning, never
  Error, so a struggling backend cannot push a server toward `DieAboveErrorCount`. Neither line is
  meant to carry a header value: each configured value of at least 6 characters (shorter than that
  reads as an ordinary id, not a credential), the credential half of it when the value has a
  "scheme credential" shape, and the JSON-escaped form of both, are matched case-insensitively and
  redacted out of the backend's own response body and gRPC status detail before a line is queued,
  longest value first so a short one can never land inside a longer one's own match. Anything else
  shaped like a bearer or basic credential of at least 8 characters is redacted too, whether or not
  it matches a configured value; that length floor spares short words after either scheme word,
  though a longer one ("Basic authentication required") can still come out masked. None of this
  is exhaustive, so the log itself is still worth treating as sensitive. The listener is read off
  the export thread only to classify and queue; a five second tick listener drains it into the
  game logger on the main thread. Up to 32 distinct failure kinds
  are tracked at once; once the cap is reached, every kind whose own ten-minute window has already
  passed is evicted first, so a server old enough to have once seen that many, all since resolved,
  never loses a genuinely new one to it.
- `docs/getting-started.md`, a walkthrough for a server owner who has never used Prometheus or
  Grafana, routed by how their server is hosted: installing the base mod, then either a local
  Prometheus and Grafana pair or Grafana Cloud's free tier over OTLP, ending at the shared
  dashboard either way. `contrib/grafana` gains `docker-compose.yml` for a Linux machine (host
  networking, one `docker compose up -d` instead of the two `docker run` commands the README also
  shows) and `docker-compose.desktop.yml` plus `prometheus.desktop.yml` for Windows and macOS. The
  Windows and macOS file reaches Pulse through `host.docker.internal` directly. Grafana shares
  Prometheus's network namespace so the provisioned datasource needs no change between the two
  files. All three, plus the README's own `docker run` commands in both `contrib/grafana` and
  `contrib/alerts`, bind Grafana and Prometheus to 127.0.0.1: with host networking an unbound,
  unauthenticated Grafana admin account and an unbound Prometheus are reachable from anywhere that
  can reach the machine, not just from it, so the guides cover an SSH tunnel for viewing a remote
  server instead. An earlier draft of the guide listed nine Grafana Cloud dashboard panels that did
  not yet render over OTLP, tracked in [issue #77](https://github.com/StratumServer/Pulse/issues/77);
  the units and runtime naming fix below closed that gap before this release shipped, so the guide
  no longer carries the caveat.
- A dashboard row, `Attribution, only when turned on`, placed right after tick health: a stacked
  time series of `pulse_mod_tick_share` by mod, a bar gauge for the current share, attributed tick
  time per mod using the main README's own seconds-per-profiled-tick recipe, and a small panel for
  the profiled ticks rate and dropped samples. The new `contrib/alerts/pulse-alerts.yml` (below)
  includes a matching `PulseModHoggingTick` alert, which only fires when a single mod other than
  `engine` or `unattributed` holds more than 50% of the profiled tick for 10 minutes while
  `pulse_server_tick_busy_seconds` is also over 80% of budget, the same load threshold
  `PulseTickSaturationHigh` already uses, so a mod that is merely heavy on an idle server does not
  page anyone.
- `/pulse`, a server command behind the `controlserver` privilege, so per-mod tick attribution no
  longer needs a restart to switch. `/pulse attribution on` and `off` drive the duty cycle on the
  running server without writing `pulse.json`, `/pulse attribution status` reports the cycle in use
  and the ticks it has profiled, and `/pulse reload` re-reads the config file and applies the
  `Attribution` block live, naming any other key whose value in the file has drifted from what the
  server is running. A file that fails to parse leaves everything as it was and the reply carries
  the parse error. This is the shape a profiler wants: the moment you need attribution is while the
  server is struggling, and a restart erases what you wanted to look at. The four families and the
  frame profiler priming are now registered whether or not `Attribution.Enabled` is set, which is
  what makes a later switch-on safe rather than merely likely to work; an instrument nothing has
  recorded into is not a series, so an idle server serves the exposition it always did.
- Config files are brought up to date at startup instead of only on first boot. Each mod compares
  the file on disk against the keys it knows and writes the missing ones back with their defaults,
  keeping every value already in the file, so a server upgrading from 0.1.0 gets the `Attribution`
  block in `pulse.json` and `ServiceName` in `pulse-otlp.json` without anyone editing them by hand.
  Keys neither mod recognises are named in a warning, since the rewrite drops them. A file that
  already holds every key is left alone, modification time included.
- Per-mod tick attribution, behind a new `Attribution` block in `pulse.json` and off by default.
  `pulse_mod_tick_share{modid}` is the fraction of profiled main-thread busy time one mod took over
  the last burst, `pulse_mod_tick_seconds_total{modid}` the sampled seconds behind it,
  `pulse_attribution_ticks_total` the ticks those seconds were measured over, and
  `pulse_attribution_dropped_samples_total` the readings discarded because the engine's 32 bit
  marker counter had wrapped. It drives the engine's own frame profiler in short bursts (10 ticks
  every 10 seconds by default) rather than leaving it on, which costs about 0.9% of the tick
  budget amortised against roughly 26% while a burst runs, measured on a 4000-entity server. The
  README section lists what it cannot see: broadcast event handlers carry no markers, and
  thread-safe physics is measured for the main thread only.
- `contrib/alerts/pulse-alerts.yml`, a Prometheus alerting rules file covering tick rate, tick
  saturation, sustained tick overruns, engine warnings, log errors, endpoint availability and a
  stuck worldgen queue, calibrated against the engine's own thresholds. `contrib/alerts/README.md`
  explains how to load it.
- `.github/workflows/game-watch.yml`, a weekly scheduled workflow that builds and runs both unit
  and scenario suites against the newest stable Vintage Story server version and fails if either
  doesn't hold up, catching an engine-side break before a user's server update does.
- `ServiceName` config key in `pulse-otlp.json` (default `vintagestory`), setting the
  `service.name` resource attribute. Set it per server so a backend receiving metrics from several
  of them can tell them apart, since the default is the same everywhere. `OTEL_SERVICE_NAME`, the
  ecosystem's standard override, takes precedence when set.
- A wire-level test for the grpc protocol: a scenario boots the server against a fake gRPC
  collector and reads the export off the socket, so both protocols `pulse-otlp.json` accepts are
  now proven end to end, not just http/protobuf.

### Changed

- Every 0.2.0 start now logs the engine's own warning, `Over 400ms tick. Skipping N physics
  ticks.`, once at startup, attribution on or off: Pulse primes the engine's frame profiler for the first
  tick after every start, and the engine only prints that line while its profiler is on. 0.1.0
  never touched the profiler, so it never logged this. It adds 1 to
  `pulse_log_entries_total{level="warning"}` and is harmless: it is not one of the four kinds
  `pulse_engine_warnings_total` counts, so none of the bundled alerts fire over it. With
  attribution on, the same line also appears during a profiled burst whenever physics falls
  behind.
- **Breaking for anything scraping the runtime series directly:** nine `dotnet_*` families on
  `/metrics` are renamed to the name Prometheus's otlptranslator derives from the same instrument,
  the library Prometheus's own OTLP receiver, Mimir and Grafana Cloud use to turn an OTLP
  instrument into a Prometheus name. The writer now derives a name from the instrument's unit the
  same way that translation does, instead of only mapping dots to underscores and appending
  `_total`, which is what left these nine out of step with every other OTLP consumer in the first
  place.

  | Old name | New name |
  | --- | --- |
  | `dotnet_process_memory_working_set` | `dotnet_process_memory_working_set_bytes` |
  | `dotnet_gc_heap_total_allocated_total` | `dotnet_gc_heap_allocated_bytes_total` |
  | `dotnet_gc_last_collection_memory_committed_size` | `dotnet_gc_last_collection_memory_committed_size_bytes` |
  | `dotnet_gc_last_collection_heap_size` | `dotnet_gc_last_collection_heap_size_bytes` |
  | `dotnet_gc_last_collection_heap_fragmentation_size` | `dotnet_gc_last_collection_heap_fragmentation_size_bytes` |
  | `dotnet_gc_pause_time_total` | `dotnet_gc_pause_time_seconds_total` |
  | `dotnet_jit_compiled_il_size_total` | `dotnet_jit_compiled_il_size_bytes_total` |
  | `dotnet_jit_compilation_time_total` | `dotnet_jit_compilation_time_seconds_total` |
  | `dotnet_process_cpu_time_total` | `dotnet_process_cpu_time_seconds_total` |

  Five panels in `contrib/grafana`'s dashboard, across seven queries, query `new or old` for this
  release, so a dashboard stays populated whether the Pulse it points at has been upgraded yet or
  not; the fallback side can be dropped once every server it watches is on 0.2 or later. `pulse_*`
  families are unaffected.
- Corrected the declared unit of three `pulse_*` instruments
  (`pulse_network_packets_per_second`, `pulse_network_bytes_per_second`,
  `pulse_mod_tick_share`), which over OTLP was adding a spurious `_per_second` or `_ratio` suffix
  on top of a name that already spelled the rate or the ratio out in words. `/metrics` is
  unaffected: no name served locally changes. The two network families shipped with the OTLP mod
  back in 0.1.0, so a Grafana Cloud stack that has been receiving OTLP since then stored them under
  the doubled name; old to new on that side only:

  | Old OTLP name | New OTLP name |
  | --- | --- |
  | `pulse_network_packets_per_second_per_second` | `pulse_network_packets_per_second` |
  | `pulse_network_bytes_per_second_per_second` | `pulse_network_bytes_per_second` |

  `pulse_mod_tick_share_ratio` never reached a stable release: attribution, and the unit behind it,
  only existed in 0.2 prereleases, so there is no 0.1.0-era OTLP data under that name to migrate.
- Bumped `OpenTelemetry` and `OpenTelemetry.Exporter.OpenTelemetryProtocol` from 1.18.0 to 1.19.1
  in the OTLP mod. Nothing Pulse depends on in the endpoint, header or service name handling
  changed between the two releases, but the resources the SDK builds by default now carry a schema
  URL, `https://opentelemetry.io/schemas/1.44.0`, where 1.18.0 sent none; OTLP exports gain that
  field on the wire. The collision precedence the `ServiceName` guard depends on is unaffected.
- **Breaking for an OTLP-fed dashboard or alert filtering on `job`:** Pulse OTLP now sets
  `service.name`, which it never did before. 0.1.0 called `Sdk.CreateMeterProviderBuilder()` with
  no `ConfigureResource` at all, so the OpenTelemetry SDK's own default resource stood: `job`
  arrived at Prometheus's OTLP receiver, Mimir or Grafana Cloud as `unknown_service:dotnet` (the
  server launched as `dotnet VintagestoryServer.dll`, `server.sh` included) or
  `unknown_service:VintagestoryServer` (launched through the native apphost binary directly),
  unless `OTEL_SERVICE_NAME` or `OTEL_RESOURCE_ATTRIBUTES=service.name=...` was already set, both
  of which that default resource already read, and with no `instance` label at all. Unless
  `OTEL_SERVICE_NAME` is set, 0.2.0 calls `AddService` with the new `ServiceName` config key
  instead, defaulting to `vintagestory`, and two things follow. Left at its default, `ServiceName`
  changes every existing series' `job` to `vintagestory`, breaking anything that filters on the
  old value. Whatever `ServiceName` holds, every series also gains an `instance` label, from a
  `service.instance.id` that `AddService` regenerates at random on every restart, which 0.1.0
  never exported. Setting `ServiceName` to the old value brings the old `job` back but not the
  old identity: the new `instance` label still appears. To keep 0.1.0's `job` and `instance`
  labels exactly, set `OTEL_SERVICE_NAME` to the old value instead: `ConfigureResource` then skips `AddService`
  entirely, so neither `job` nor `instance` changes, and a `service.instance.id` already set
  through `OTEL_RESOURCE_ATTRIBUTES` survives untouched (`OTEL_SERVICE_NAME` itself takes
  precedence over a `service.name` there). Without `OTEL_SERVICE_NAME` set,
  `OTEL_RESOURCE_ATTRIBUTES`'s `service.name` and `service.instance.id` are both silently
  overridden by `AddService`. Setting `OTEL_SERVICE_NAME` together with
  `OTEL_RESOURCE_ATTRIBUTES=service.instance.id=<id>` is therefore the way to get a stable
  `instance` label across restarts: that variable alone is not enough, since `AddService`'s own
  randomly generated id silently overrides it on every start.

### Fixed

- The metrics endpoint now serves from a plain TCP socket instead of `HttpListener`, which fixes
  four different ways it could fail to serve anything. On Windows, binding loopback no longer
  needs an administrator or a `netsh` URL reservation: `HttpListener` went through `http.sys`,
  which refused that to an ordinary user. On Linux and macOS, `http://localhost:9464/metrics` and
  `http://host.docker.internal:9464/metrics` no longer answer 404: the old listener matched the
  Host header against the configured `Bind` address and rejected anything else, and the new
  server does not look at Host at all. `Bind` set to `0.0.0.0` no longer throws at startup on
  Linux, and `::1` binds whether or not it is written with brackets. An invalid `Bind` value, an
  empty string among them, is now caught in the same place a taken port already was, logged as a
  clean bind failure instead of raising an exception that left the rest of the mod running with
  no endpoint and no explanation in the log.
- A `pulse.json` or `pulse-otlp.json` that exists but will not parse (a doubled comma or a
  missing quote is enough; a trailing comma is not, Newtonsoft accepts those) no longer stops the
  mod from starting; before, recovering meant deleting the file by hand. Each mod now logs one
  error naming the file's full path and the parser's own message, and leaves the file untouched
  rather than overwriting it with defaults. Pulse keeps running for that session on its built-in
  defaults; Pulse OTLP keeps exporting off for that session instead of falling back to an
  endpoint nobody configured, and redacts any value Newtonsoft quoted in the parser's message (a
  `Headers` entry of the wrong shape, most often) before it reaches the log. An absent file and a
  valid one are unaffected.
- `pulse_mod_tick_share{modid}` no longer keeps serving the last completed burst's shares after
  attribution stops. It is now an observable gauge tied to the same `Attribution.Enabled` state as
  the duty cycle, so the family disappears from `/metrics` and from OTLP exports the moment
  `/pulse attribution off`, a reload with `Enabled` false, or the duty cycle giving up on its own
  switches it off, instead of freezing at the last burst's values until the server restarts.
  `pulse_mod_tick_seconds_total`, `pulse_attribution_ticks_total` and
  `pulse_attribution_dropped_samples_total` are unaffected: they are cumulative counters and simply
  stop moving.
- The startup `Pulse OTLP exporting ...` log line no longer names the full configured `Endpoint`.
  Userinfo or a query string in it (a backend that authenticates through a signed URL, say) had no
  business there; only scheme, host, port and path are logged now, the same components every
  export failure or success line already named.
- `TryResolveEndpoint` no longer mangles a configured `Endpoint` that already carries a query
  string: `https://host/otlp?key=abc` used to become `https://host/otlp?key=abc/v1/metrics`,
  landing the signal path after the query instead of before it. It now builds the result from the
  endpoint's path alone, through `UriBuilder`, so the query survives in its rightful place:
  `https://host/otlp/v1/metrics?key=abc`.
- The error logged for an `Endpoint` `TryResolveEndpoint` cannot parse used to repeat the configured
  value back whole, the same problem as the two entries above: userinfo or a query string in it
  went straight into the log. The line now names the config key and the file instead of the value:
  `Pulse OTLP's 'Endpoint' in pulse-otlp.json is not an absolute http or https URL.`
- On an OTLP-fed dashboard, five panels could stay empty on an idle server no matter how long it
  ran: `pulse_engine_warnings_total`, `pulse_player_deaths_total`, `pulse_server_suspends_total`,
  `pulse_server_suspend_seconds_total` and `pulse_worldgen_columns_generated_total` reached
  `/metrics` correctly but did not reach OTLP until whatever they measured happened for the first
  time, and `pulse_log_entries_total` was missing the same way until a real log line arrived. What
  never reached OTLP at all, even once the panels did start moving, was the zero every one of them
  is seeded with at startup, plus the handful of log lines the two mods can log between the base
  mod starting and the OTLP mod finishing its own startup. The OTLP mod now starts before the base
  mod, so its exporter is already listening when these counters are seeded.
- Pulse no longer turns the engine's own frame profiler off on every tick. `v0.2.0-indev.1` never
  touched the profiler; `indev.2` and `indev.3` wrote `FrameProfilerUtil.Enabled` every tick while
  `Attribution.Enabled` was true; `indev.4` and `indev.5` wrote it unconditionally every tick
  regardless of `Attribution.Enabled`, which broke `/debug logticks` on a server running either of
  those two prereleases even with attribution off: the report lost its per-system and
  per-listener lines, off-thread reports stopped printing altogether, and any other mod that turns
  the profiler on had its own setting clobbered a tick later. Pulse now tracks whether it is the
  one that last turned the flag on, and writes it only on its own transitions (turning off what it
  primed at startup, every tick a burst is running so another mod cannot fold a stale tick tree
  into it, giving up, and shutting down), never clearing it while `/debug logticks` has asked for
  it.
- A server that already has a `/pulse` chat command from another mod could start Pulse with the
  frame profiler stuck on for the whole run, at close to a quarter of the tick budget, with no
  Pulse metrics to show for it. Registering `/pulse` used to be able to throw partway through
  `AttributionMetrics`'s own startup, after the profiler had already been armed to turn on at the
  next tick but before Pulse's tick listener existed to turn it back off; nothing then ever did. A
  clashing command name is now caught and logged once instead of aborting the mod, and the
  profiler is armed only once Pulse's tick listener is registered and able to manage it.
- Attribution and its frame-profiler priming now degrade the same way the engine probe already
  does: if a future game version reshapes the profiler in a way Pulse does not expect, this logs
  one warning and turns attribution off for the session instead of crashing the server at startup
  or logging an error on every tick for the rest of the run (which, left unaddressed, would have
  driven the server into its own `DieAboveErrorCount` shutdown well within an hour). A reshape of
  `PrintSlowTicks`, the one field the profiler guard itself now reads, still turns the profiler
  fully off through a plain write rather than leaving it running for the rest of the session, and a
  reshape of the profiler type itself costs only attribution instead of the whole mod failing to
  start.
- Config keys are now compared case-insensitively when Pulse rewrites `pulse.json` or
  `pulse-otlp.json` to add new keys, matching how the file is actually loaded. A key written with
  different casing than Pulse's own (`port` for `Port`, say) used to be logged as both a default
  Pulse silently added and a value the rewrite silently dropped, even though the admin's own value
  was kept the whole time. The same key written twice under different casing is now reported as a
  duplicate, naming every spelling involved, never the value written under it: a scalar property
  (`Port`, say) names the spelling actually in effect, and a block or a dictionary entry duplicated
  under two spellings (an OTLP header name typed in two casings, say) says every spelling is read
  rather than claiming one replaces the other, since the config loader merges the former and keeps
  every one of the latter's colliding keys. A block duplicated this way is also no longer misread
  as missing the field only one of its spellings set. This warning is compiled into Pulse OTLP too,
  covering `pulse-otlp.json`'s own duplicate keys, and it never prints the colliding value: an OTLP
  header written under two casings does not put either header's value, a credential in the common
  case, into the server log.
- `ChunksRefreshSeconds` set to an extreme value (above roughly 2.147 million seconds) no longer
  overflows into a negative tick listener period, which made the engine run the loaded-chunk read
  it guards on every single tick instead of never. The value is now clamped to at most a day.
- A `pulse.json` that does not exist yet, on a ModConfig folder the server process cannot write to
  (a read-only mount, most commonly), no longer stops Pulse from starting. It now logs one warning
  and runs the session on its built-in defaults, the same as it already does for a file that exists
  but will not parse.
- An `Endpoint` with an empty user name and a password (`http://:token@collector:4318`, say)
  crashed the whole game server at boot instead of merely leaving Pulse OTLP without export.
  `UriBuilder` throws on that shape, for `http/protobuf` while the signal path is resolved and for
  `grpc` a few lines later while the exporter's provider is built, and `UriFormatException` is one
  of the few exception types the mod loader rethrows instead of absorbing. Not a regression,
  0.1.0 crashed on this the same way; endpoint resolution and provider construction are now inside
  one guard that catches any exception there, not only this one, logs a single error naming what
  went wrong and never the endpoint or a header value, and leaves the game server running with
  export off.
- `IntervalSeconds` set several digits too high (`3000000`, say) crashed the OTLP mod on startup
  with a stack trace instead of a clean message: converting it to milliseconds overflowed a
  32-bit integer, and the exporter's own option validation rejected the resulting negative value
  with an unhandled `ArgumentOutOfRangeException`. `IntervalSeconds` is now capped at 86400 (24
  hours) before that conversion, the same way it is already floored at 5.
- A header value containing a comma, or two header names that collide once leading and trailing
  whitespace is trimmed off, crashed the OTLP mod on startup with a stack trace: the exporter's
  own header parser rejects both, the comma because it cannot survive the exporter's own
  unescape-then-split round trip whatever this mod encodes it as going in. Both are now checked
  before a header ever reaches the exporter, and refused with one error naming the offending
  header, never its value, instead of a stack trace with no clear cause.
- A collector answering a failed export with a body that echoes the request target (a reverse
  proxy's own error page, say) could put a signed-URL backend's own secret in the server log:
  `ExportFailureLog`'s redaction only knew about the configured header values, not the query
  parameter values or the userinfo the configured `Endpoint` itself can carry. Both are now in the
  same redaction list, subject to the same 6 character floor and longest-first ordering a header
  value already gets.
- A missing `pulse-otlp.json` on a read-only `ModConfig` directory stopped the OTLP mod from
  starting at all: writing the freshly defaulted file back to disk was not guarded the way
  loading one already is. It now logs one error naming what happened and runs on the in-memory
  defaults for that session, exporting off, instead of never starting.
- `/pulse attribution off`'s reply and its own help text claimed the engine's frame profiler
  always goes back off with it. That stopped being true the moment the fix above landed: the
  profiler now stays on while `/debug logticks` still wants it. Both lines now say so.
- Two of the log lines from the duplicate-key fix above could contradict what the same run of
  `ConfigUpgrade` actually did. Adding a missing key and collapsing a duplicate spelling can
  happen in the same rewrite (a file with both `Port` and `port` set, and a key from a newer
  release still absent, say): the added-keys line still claimed everything already in the file
  was kept as it was, and the duplicate-key line still asked the admin to remove the extra
  spelling by hand, even though the rewrite that added the missing key had already dropped it
  down to one spelling. Each line now names what actually happened for the combination in front
  of it.
- `pulse_server_tick_seconds`'s histogram buckets gain two boundaries, 0.035 and 0.04, just above
  the 33.3 ms default budget. A server ticking only a fraction of a millisecond slow (three
  players, 29.8 TPS) used to land almost every tick in the single 0.0334 to 0.05 bucket, and
  `histogram_quantile` interpolating across that whole span showed the shipped dashboard's p50
  around 40 ms and p99 around 49.8 ms even though the server was healthy. Every existing boundary
  is unchanged, so this only adds new `le` series; nothing that already queries this histogram
  needs to change.

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
