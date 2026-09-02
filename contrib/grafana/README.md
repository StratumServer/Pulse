# Grafana kit

A ready-to-run Prometheus and Grafana pair for Pulse, the exact setup used to produce the
project's dashboard screenshots. The dashboard covers every metric family the mod serves,
grouped into rows: a glance strip of the numbers you check first, then tick health, players,
world, worldgen, network, pauses and warnings, and a runtime row at the bottom. Panels that
depend on something optional say so in their description, so an empty graph tells you why it
is empty instead of leaving you to guess. The runtime row needs `RuntimeMetrics` left on, and
busy time, the per-second network families and the connection queue all come from the engine
probe, which means they are blank on a server running in degraded mode.

With a Pulse-equipped server running on the same host (default bind, port 9464):

```sh
docker run -d --rm --name pulse-prom --network host \
  -v "$PWD/contrib/grafana:/etc/pulse" \
  prom/prometheus --config.file=/etc/pulse/prometheus.yml

docker run -d --rm --name pulse-graf --network host \
  -e GF_AUTH_ANONYMOUS_ENABLED=true \
  -e GF_AUTH_ANONYMOUS_ORG_ROLE=Admin \
  -e GF_AUTH_DISABLE_LOGIN_FORM=true \
  -v "$PWD/contrib/grafana/provisioning:/etc/grafana/provisioning" \
  grafana/grafana
```

Then open http://localhost:3000/d/pulse-overview. The datasource and the dashboard are
provisioned from the files here; there is nothing to click together. Stop it all with
`docker stop pulse-graf pulse-prom`.

The anonymous-admin settings are for a local look, not for anything reachable from outside;
run Grafana properly if you keep it.

Prometheus scrapes every 2 seconds here, which is pleasant for watching a test server live
and far denser than a production setup needs; 15 seconds is plenty for a real host. The panels
ask for `$__rate_interval` rather than a fixed window, so they follow whatever scrape interval
you settle on instead of going ragged at 15 seconds and lying at 60.

## Importing it into a Grafana you already run

Use `pulse-overview-shared.json`. In Grafana, go to Dashboards, then Import, upload that file,
and pick your Prometheus datasource when it asks for one. That prompt is the entire difference
between the two dashboard files: the provisioned copy points at the datasource uid `pulse-prom`,
which exists only on a Grafana provisioned from this directory, so importing that one anywhere
else gets you a dashboard wired to nothing.

You still need the scrape target from `prometheus.yml`.

## The files

- `provisioning/` is what the Grafana container reads: the datasource, the dashboard provider,
  and the dashboard itself at `provisioning/dashboards/json/pulse.json`, uid `pulse-overview`.
  This is the copy to edit.
- `pulse-overview-shared.json` is generated from that one, not maintained beside it. Edit the
  provisioned dashboard and regenerate.
- `make-shared.py` does the generating: it swaps the datasource for the `DS_PROMETHEUS` import
  prompt and adds the `__inputs` and `__requires` blocks Grafana's import dialog reads.
- `check-dashboard.py` looks for the mistakes Grafana will not report. Overlapping panels, a
  panel wider than the 24 column grid and duplicate panel ids all get drawn wrong or dropped
  silently, which is a miserable thing to debug by eye.

So after editing the dashboard, run both:

```sh
python3 contrib/grafana/make-shared.py
python3 contrib/grafana/check-dashboard.py \
  contrib/grafana/provisioning/dashboards/json/pulse.json \
  contrib/grafana/pulse-overview-shared.json
```

Neither script needs anything beyond the Python standard library.
