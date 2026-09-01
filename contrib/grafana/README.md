# Grafana kit

A ready-to-run Prometheus and Grafana pair for Pulse, the exact setup used to produce the
project's dashboard screenshots. Eight panels: tick rate, tick time against the budget with a
p95, worldgen queue and generated columns, world counts, working set, GC collections by
generation, and the engine warning counters.

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
and far denser than a production setup needs; 15 seconds is plenty for a real host. If you
already run Prometheus and Grafana, the only things you need are the scrape target from
`prometheus.yml` and `provisioning/dashboards/json/pulse.json` to import; on import, point
the panels at your own Prometheus datasource.
