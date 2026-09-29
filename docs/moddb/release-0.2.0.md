# ModDB release entries: 0.2.0

HTML for the "what's new in this version" box on each zip's ModDB file entry: paste each block below into the field's HTML source view as is. Plain tags only (paragraphs, lists, bold, code, links), with about 1500 visible characters each. This is the canonical text.

## pulse_0.2.0.zip

<p>What's new in 0.2.0:</p>
<ul>
<li><b>Security note:</b> an earlier contrib/grafana or contrib/alerts README's docker run commands exposed Grafana (anonymous admin) and Prometheus on every interface on Linux; see the <a href="https://github.com/StratumServer/Pulse/releases/tag/v0.2.0">release notes</a> before upgrading.</li>
<li>Per-mod tick attribution. See which mod is actually spending the tick, on a live graph, not a one-off report. Off by default.</li>
<li>New server command, <code>/pulse attribution on|off|status</code>, plus <code>/pulse reload</code>, so attribution switches on the running server with no restart.</li>
<li>The metrics endpoint is now a plain socket server: no administrator rights needed on Windows, no more 404s on Linux, several scrapes served at once instead of queued.</li>
<li>Config files upgrade themselves at startup, keeping existing values. A config file that fails to parse now logs an error and keeps the mod running on defaults instead of stopping it.</li>
<li><b>Breaking:</b> nine <code>dotnet_*</code> runtime series on /metrics are renamed to the names an OTLP backend derives. <code>pulse_*</code> families are unchanged; full list in the changelog.</li>
<li>New: contrib/alerts, an eleven-rule Prometheus alerting pack (tick health, engine warnings, log errors, endpoint availability), including PulseModHoggingTick. The Grafana dashboard gains a matching attribution row.</li>
</ul>
<p><b>Upgrading from 0.1.0:</b> replace the old zip and restart once, no config edits needed, but check <code>Bind</code> first: <code>0.0.0.0</code> now takes effect. Rolling back leaves the config alone (nothing lost), but attribution tuning goes inert until you upgrade again.</p>

## pulseotlp_0.2.0.zip

<p>What's new in 0.2.0:</p>
<ul>
<li>Export failures no longer pass silently. A rejected push, a refused or unreachable collector, or a timeout now logs one line, repeated at most every 10 minutes, naming what failed and the backend's own (redacted) response. Recovery logs one line too.</li>
<li>Counters that could stay invisible on a quiet server until their first real event (engine warnings, player deaths, suspends, suspend seconds, worldgen columns, log entries) now reach OTLP from the first export, seeded at zero.</li>
<li>New <code>ServiceName</code> config key (default "vintagestory") replaces 0.1.0's <code>unknown_service:</code> fallback. Prometheus, Mimir and Grafana Cloud derive job from service.name, so every series' job changes on upgrade; an old job filter breaks quietly. Keep it with <code>OTEL_SERVICE_NAME</code> instead: only the env var also skips a new, restart-churning instance label. Pair with <code>OTEL_RESOURCE_ATTRIBUTES=service.instance.id=YOUR_ID</code> for a stable instance too.</li>
<li>The bundled dashboard reads the same over OTLP as over /metrics now: the two <code>pulse_network_*_per_second</code> series lose a doubled suffix, and the renamed <code>dotnet_*</code> series match your OTLP backend's own names.</li>
<li>Odd config values no longer crash startup: a bad Endpoint, a comma in a header value, or an unparseable file now log one error and leave export off.</li>
<li><b>Requires the base pulse mod</b>; upgrade both zips together.</li>
</ul>
<p>New to Grafana Cloud? The <a href="https://github.com/StratumServer/Pulse/blob/main/docs/getting-started.md">getting-started guide</a> covers the free tier over OTLP end to end, alongside the local Prometheus path.</p>
