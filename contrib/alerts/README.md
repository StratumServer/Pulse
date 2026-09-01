# Alerting rules

A Prometheus alerting rules file for Pulse, `pulse-alerts.yml`. Ten rules across tick health,
engine warnings, log errors, endpoint availability and the worldgen queue, each carrying a
`severity` label (`warning` or `critical`) and an annotation that says what to check, not just
what happened. The thresholds are calibrated against the engine's own numbers: 30 TPS is the
game's nominal tick rate, 500 ms is the engine's own overload cutoff, 90%/100% of
`DieAboveMemoryUsageMb` are the engine's own memory thresholds. The comments in the file say
where each one comes from.

It is not wired in automatically. Add it to `rule_files` in your `prometheus.yml`:

```yaml
rule_files:
  - /etc/pulse/pulse-alerts.yml

scrape_configs:
  - job_name: vintagestory
    static_configs:
      - targets: ["127.0.0.1:9464"]
```

If you're running the container from `contrib/grafana`, mount this directory alongside it and
point `rule_files` at the mounted path:

```sh
docker run -d --rm --name pulse-prom --network host \
  -v "$PWD/contrib/grafana:/etc/pulse" \
  -v "$PWD/contrib/alerts:/etc/pulse-alerts" \
  prom/prometheus --config.file=/etc/pulse/prometheus.yml
```

with `rule_files: [/etc/pulse-alerts/pulse-alerts.yml]` added to `contrib/grafana/prometheus.yml`
(left out of that file by default, so the grafana kit stays alerting-free until you ask for it).

Prometheus only reads `rule_files` at startup or on a reload: send it `SIGHUP`, hit
`/-/reload` if it was started with `--web.enable-lifecycle`, or restart the container.

A firing alert only gets you as far as Prometheus's own `/alerts` page. To have it actually
notify anyone, point the `alerting:` block in `prometheus.yml` at an Alertmanager and configure
routing there; that setup is entirely yours; nothing here assumes a particular chat tool or
paging service.

Two things worth knowing before you rely on these:

- `PulseEndpointDown` matches `up{job="vintagestory"}`, the job name `contrib/grafana`'s own
  `prometheus.yml` uses. If your scrape job is named differently, change that one label.
- `PulseTickSaturationHigh` and `PulseTickOverrunsHigh` read `pulse_server_tick_busy_seconds` and
  `pulse_server_tick_budget_seconds`, two of the families that only exist when Pulse's engine
  probe resolved successfully (see the main README's "Degraded mode" section). If that cast ever
  fails on a game update, those two families disappear from `/metrics` and these two rules simply
  have no data to evaluate; they go quiet, not green. The tick rate and log/worldgen rules are
  unaffected either way.

Validate the file after editing it with the same promtool container used to write it:

```sh
docker run --rm -v "$PWD/contrib/alerts:/a" --entrypoint promtool prom/prometheus check rules /a/pulse-alerts.yml
```
