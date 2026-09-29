# ModDB release entries: 0.2.0

Text for the "what's new in this version" box on each zip's ModDB file entry.
Plain text, no HTML: ModDB's file changelog field does not run the page
sanitizer, but it does not render markup either. Kept under 1500 characters
each. This is the canonical text: paste each box below onto ModDB as is.

## pulse_0.2.0.zip

What's new in 0.2.0:

- Security note: an earlier contrib/grafana or contrib/alerts README's
  docker run commands exposed Grafana (anonymous admin) and Prometheus on
  every interface on Linux; see the release notes before upgrading.
- Per-mod tick attribution. See which mod is actually spending the tick,
  on a live graph, not a one-off report. Off by default.
- New server command, /pulse attribution on|off|status, plus /pulse
  reload, so attribution switches on the running server with no restart.
- The metrics endpoint is now a plain socket server: no administrator
  rights needed on Windows, no more 404s on Linux, several scrapes served
  at once instead of queued.
- Config files upgrade themselves at startup, keeping existing values. A
  config file that fails to parse now logs an error and keeps the mod
  running on defaults instead of stopping it.
- Breaking: nine dotnet_* runtime series on /metrics are renamed to the
  names an OTLP backend derives. pulse_* families are unchanged; full
  list in the changelog.
- New: contrib/alerts, an eleven-rule Prometheus alerting pack (tick
  health, engine warnings, log errors, endpoint availability), including
  PulseModHoggingTick. The Grafana dashboard gains a matching attribution
  row.

Upgrading from 0.1.0: replace the old zip and restart once, no config
edits needed. Rolling back leaves the config alone (nothing lost), but
attribution tuning goes inert until you upgrade again.

## pulseotlp_0.2.0.zip

What's new in 0.2.0:

- Export failures no longer pass silently. A rejected push, a refused or
  unreachable collector, or a timeout now logs one line, repeated at most
  every 10 minutes, naming what failed and the backend's own (redacted)
  response. Recovery logs one line too.
- Counters that could stay invisible on a quiet server until their first
  real event (engine warnings, player deaths, suspends, suspend seconds,
  worldgen columns, log entries) now reach OTLP from the first export,
  seeded at zero.
- New ServiceName config key (default "vintagestory") replaces 0.1.0's
  unknown_service: fallback. Prometheus, Mimir and Grafana Cloud derive
  job from service.name, so every series' job changes on upgrade; an old
  job filter breaks quietly. Keep it with OTEL_SERVICE_NAME instead: only
  the env var also skips a new, restart-churning instance label.
- The bundled dashboard reads the same over OTLP as over /metrics now: the
  two pulse_network_*_per_second series lose a doubled suffix, and the
  renamed dotnet_* series match your OTLP backend's own names.
- Odd config values no longer crash startup: a bad Endpoint, a comma in a
  header value, or an unparseable file now log one error and leave
  export off.
- Requires the base pulse mod; upgrade both zips together.

New to Grafana Cloud? The getting-started guide covers the free tier
over OTLP end to end, alongside the local Prometheus path.
