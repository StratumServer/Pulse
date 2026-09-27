# Alerting rules

A Prometheus alerting rules file for Pulse, `pulse-alerts.yml`. Eleven rules across tick health,
engine warnings, log errors, endpoint availability, the worldgen queue and per-mod attribution,
each carrying a `severity` label (`warning` or `critical`) and an annotation that says what to
check, not just what happened. The thresholds are calibrated against the engine's own numbers: 30
TPS is the game's nominal tick rate, 500 ms is the engine's own overload cutoff, 90%/100% of
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

Three things worth knowing before you rely on these:

- `PulseEndpointDown` matches `up{job="vintagestory"}`, the job name `contrib/grafana`'s own
  `prometheus.yml` uses. If your scrape job is named differently, change that one label.
- `PulseTickSaturationHigh` reads `pulse_server_tick_busy_seconds`, one of the families that only
  exists when Pulse's engine probe resolved successfully (see the main README's "Degraded mode"
  section). If that cast ever fails on a game update, the family disappears from `/metrics` and
  this rule simply has no data to evaluate; it goes quiet, not green. `PulseTickOverrunsHigh` looks
  like it belongs in the same boat but does not: it only reads the tick histogram and
  `pulse_server_tick_budget_seconds`, both public API metrics, so it keeps working in degraded mode
  the same as the tick rate and log/worldgen rules.
- `PulseModHoggingTick` reads `pulse_server_tick_busy_seconds` too, so it is quiet in degraded mode
  for the same reason. It also needs attribution switched on (see the main README's "Attribution"
  section) for `pulse_mod_tick_share`. That family is an observable gauge tied to the same duty
  cycle, so it disappears from `/metrics` the moment attribution stops, whether that is
  `/pulse attribution off`, a reload with `Enabled` false, or the duty cycle giving up on its own;
  it no longer sits there at the last burst's values looking like a real number. The rule keeps its
  own guard regardless, `increase(pulse_attribution_ticks_total[5m]) > 0`: that counter only moves
  when a burst actually completes, a more direct signal that attribution is doing real work than
  the family merely being present, and one that still protects the rule if a future change ever
  lets the gauge report something without a completed burst behind it.

Validate the file after editing it with the same promtool container used to write it:

```sh
docker run --rm -v "$PWD/contrib/alerts:/a" --entrypoint promtool prom/prometheus check rules /a/pulse-alerts.yml
```

That only parses the PromQL; it does not run it, so a rule can pass `check rules` and still never
fire, for instance an `and` or an arithmetic operator whose two sides carry different labels and
so never match anything. `pulse-alerts.test.yml` catches that class of mistake by running every
rule against synthetic data, one case where it should fire and one where it should stay quiet:

```sh
docker run --rm -v "$PWD/contrib/alerts:/a" --entrypoint promtool prom/prometheus test rules /a/pulse-alerts.test.yml
```

Run both after touching this file. Adding a rule without adding its two cases here is how the
next silent one gets through.
