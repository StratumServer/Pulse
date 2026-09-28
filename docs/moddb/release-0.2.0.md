# ModDB release entries: 0.2.0

Text for the "what's new in this version" box on each zip's ModDB file entry.
Plain text, no HTML: ModDB's file changelog field does not run the page
sanitizer, but it does not render markup either. Kept under 1500 characters
each. No prior 0.1.0-era entries exist in the repo history to match the shape
of (this file itself is new), so both are written as a plain what's-new list
for a server owner deciding whether to upgrade.

## pulse_0.2.0.zip

What's new in 0.2.0:

- Per-mod tick attribution. Turn it on and see which mod is actually
  spending the tick, on a live graph, not a one-off report. Off by default:
  about 26% of a tick while a burst runs, about 0.9% amortised at the
  shipped default (10 ticks every 10 seconds).
- New server command, /pulse attribution on|off|status, plus /pulse reload,
  so attribution switches on the running server with no restart.
- The metrics endpoint is now a plain socket server: no administrator rights
  or netsh reservation on Windows, no more 404s on Linux when a request's
  Host header does not match Bind, and several scrapes at once instead of
  queued.
- Config files upgrade themselves at startup: a new key is added with its
  default, existing values are left alone. A config file that fails to
  parse now logs an error and keeps the mod running on defaults instead of
  stopping it.
- Nine dotnet_* runtime series are renamed to match the OTLP path's own
  names (dotnet_gc_pause_time_total is now
  dotnet_gc_pause_time_seconds_total, for example). pulse_* families are
  unchanged; full list in the changelog.
- The bundled Grafana dashboard gains an attribution row, and the alert
  rules gain one for a single mod hogging the tick under load.

Upgrading from 0.1.0: drop in the new zip and restart once. Nothing to edit
by hand.

## pulseotlp_0.2.0.zip

What's new in 0.2.0:

- Export failures no longer pass silently. A rejected push, a refused or
  unreachable collector, or a timeout now logs one line, repeated at most
  every 10 minutes, naming what failed and the backend's own (redacted)
  response. Recovery logs one line too.
- A handful of metrics that could stay invisible on a quiet server until
  their first real event (engine warnings, player deaths, suspends, worldgen
  columns, log entries) now reach OTLP from the first export, seeded at
  zero, instead of waiting.
- New ServiceName config key (default "vintagestory") sets the service.name
  resource attribute, so a backend collecting from more than one server can
  tell them apart. OTEL_SERVICE_NAME still overrides it if you set that
  instead.
- Same nine dotnet_* renames as the base mod (they travel over OTLP too),
  plus two pulse_network_*_per_second series that had picked up a doubled
  _per_second suffix over OTLP specifically are now named the same on both
  paths.
- A config file that fails to parse now logs an error and leaves OTLP export
  off for that session instead of stopping the mod or falling back to a
  guessed endpoint.
- Depends on pulse 0.2.0; upgrade both zips together.

New to Grafana Cloud? The getting-started guide in the repo covers the free
tier over OTLP end to end, alongside the local Prometheus and Grafana path.
