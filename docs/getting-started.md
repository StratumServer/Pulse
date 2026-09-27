# Getting started: from install to your first dashboard

This guide is for a server owner who has never used Prometheus or Grafana and just wants to
see graphs of their Vintage Story server. It assumes nothing beyond running a server already.

Pulse itself does not draw any graphs. It only serves a page of current numbers, `/metrics`,
and waits for something to come and read it. **Prometheus** is that something: a program that
visits `/metrics` on a timer and keeps a history of every number it finds. **Grafana** is the
program that turns Prometheus's history into the graphs you actually look at, arranged on a web
page called a dashboard. You need one of each, and this guide covers two ways to get them
without configuring either by hand: a ready-made setup you run yourself (path A), or Grafana's
own free hosted service (path B).

## Step 1: install the mod on the server

This part is the same whichever path you pick further down, and it happens on the game server
only, not on your own computer.

1. Download `pulse_x.x.x.zip` (replace `x.x.x` with the version you downloaded) from the
   [ModDB page](https://mods.vintagestory.at/pulse) or from
   [GitHub releases](https://github.com/StratumServer/Pulse/releases).
2. Drop the zip, unopened, into the server's `Mods/` folder, the same place every other mod
   goes.
3. Start (or restart) the server.
4. Watch the server's log for this line, which is Pulse confirming it is up:

   ```
   Pulse serving metrics on http://127.0.0.1:9464/metrics
   ```

5. Check the page it just mentioned actually answers, from the server itself (the address is
   `127.0.0.1`, meaning "this machine only", so this check has to run on the server, not on your
   own computer). Either open `http://127.0.0.1:9464/metrics` in a browser running on the
   server, or, from a terminal on the server, run:

   ```sh
   curl http://127.0.0.1:9464/metrics
   ```

   Either way you should get back plain text, a long list of lines like
   `pulse_players_online 0`. That text is what Prometheus will be reading in path A, and what
   the OTLP mod pushes onward in path B.

Pulse also wrote its own config file at this point, at `ModConfig/pulse.json` next to the
`Mods/` folder. You do not need to touch it for either path below.

## Path A: graphs on your own machine or on the server

Use this path if you are happy running two small programs yourself, on your own computer or on
the server, as long as that machine is Linux (see the note below if it is Windows or macOS).
Nothing here is sent to the internet.

1. You need Docker installed on that machine (installing Docker itself is outside this guide).
2. Get the Pulse repository, which carries the ready-made setup in `contrib/grafana`:

   ```sh
   git clone https://github.com/StratumServer/Pulse.git
   cd Pulse/contrib/grafana
   ```

3. Start Prometheus and Grafana together:

   ```sh
   docker compose up -d
   ```

4. Give it a few seconds, then open `http://localhost:3000/d/pulse-overview` in a browser. That
   is Grafana's "Pulse server overview" dashboard, already wired to Prometheus. Nobody has to
   sign in: this setup turns on anonymous access, deliberately limited to this local look
   (more on that below), the same way the plain `docker run` version of this kit always has.
5. When you are done, stop both programs from the same folder:

   ```sh
   docker compose down
   ```

This assumes Pulse is running on the same machine as this Docker setup, using the default
`127.0.0.1:9464` from step 1. That works because the compose file uses "host networking": the
containers share the machine's own network instead of getting an isolated one, so `127.0.0.1`
inside a container reaches the machine, same as outside it. Host networking like this only
exists on Linux. **On Docker Desktop for Windows or macOS**, it cannot reach
the host machine at all, so `contrib/grafana/docker-compose.yml` carries a comment at the top
with the two changes that path needs: dropping host networking for published ports, and
pointing Prometheus at `host.docker.internal` instead of `127.0.0.1` in `prometheus.yml`. Follow
that comment rather than guessing.

Anonymous access in this setup is meant for a local look at your own graphs, nothing more.
Do not point it at a network anyone else can reach; run Grafana with real accounts if you want
to keep it running long term.

## Path B: Grafana Cloud, if you would rather host nothing

Use this path if you do not want to run Prometheus or Grafana yourself at all. Grafana Cloud is
Grafana's own hosted service, and its free tier is enough for one server's worth of graphs (see
[Grafana Cloud's free tier page](https://grafana.com/products/cloud/free-tier/) for the current
limits). This path needs a second, optional mod, because nobody is coming to read `/metrics` for
you; instead, the server has to push its numbers out over the internet, using a protocol called
OTLP.

Create your Grafana Cloud account yourself at [grafana.com](https://grafana.com/); this guide
does not do that step for you, and does not need any password or token from you either, since
everything below is quoted from Grafana's own documentation.

1. In your Grafana Cloud stack, open the OpenTelemetry tile and follow it to generate an access
   policy token (Grafana Cloud's name for an API key), as described in Grafana's
   [Send data to the Grafana Cloud OTLP endpoint](https://grafana.com/docs/grafana-cloud/send-data/otlp/send-data-otlp/)
   page. That page is also where your OTLP endpoint address comes from: it looks like
   `https://otlp-gateway-<region>.grafana.net/otlp`, with `<region>` filled in for your stack.
   Grafana Cloud authenticates OTLP with HTTP basic auth, where the username is your numeric
   instance ID and the password is the access policy token you just created; written as one
   header value that is `Basic <instance ID and token, joined with a colon and base64-encoded>`.
2. Download `pulseotlp_x.x.x.zip` next to `pulse_x.x.x.zip`, from the same
   [ModDB page](https://mods.vintagestory.at/pulse) or
   [GitHub releases](https://github.com/StratumServer/Pulse/releases), and drop it into `Mods/`
   as well.
3. Start the server once so the mod writes its own config file, then stop the server again.
4. Edit `ModConfig/pulse-otlp.json`. Three keys change:

   ```json
   {
     "Endpoint": "https://otlp-gateway-<region>.grafana.net/otlp",
     "Protocol": "http/protobuf",
     "Headers": {
       "Authorization": "Basic <the base64 value from step 1>"
     }
   }
   ```

   Leave every other key as the mod wrote it. `Endpoint` is the base address only, Pulse adds
   the rest of the path itself.
5. Start the server again. Look for a log line starting with `Pulse OTLP exporting`, which
   confirms it is pushing on a timer; by default that timer is 60 seconds, so give it a minute
   before you go looking for data in Grafana Cloud.

That `Authorization` value is exactly the case this mod exists to get right: a base64 token
carries `=` padding and sometimes `+` and `/`, all characters a URL would otherwise mangle, and
the mod has to smuggle it through unharmed. `RenderHeaders_RoundTrips_ThroughTheExportersOwnParser`
in
[`Pulse.Otlp.Tests/OtlpOptionsTests.cs`](../Pulse.Otlp.Tests/OtlpOptionsTests.cs)
proves it for exactly this shape of value (`Basic MTIzNDU2OnRva2Vu==`, padding included) by
encoding it the way Pulse does and decoding it back the way the OpenTelemetry exporter does.

Once the numbers are flowing, bring in the dashboard:

1. In Grafana Cloud, go to **Dashboards**, then **Import**.
2. Upload
   [`contrib/grafana/pulse-overview-shared.json`](../contrib/grafana/pulse-overview-shared.json)
   from the Pulse repository.
3. It asks for a Prometheus data source (Grafana's name for a saved connection to somewhere it
   can pull numbers from). Pick the one your Grafana Cloud stack already created for you: Grafana
   Cloud stores OTLP metrics in its own Prometheus-compatible store, so this is the same data
   source your other Grafana Cloud graphs use.
4. Open the imported dashboard. It is the same "Pulse server overview" dashboard as path A.

## Troubleshooting

- **"port is already allocated" (or Grafana/Prometheus fails to start) when running
  `docker compose up -d`.** Something else on the machine is already using port 3000 (Grafana)
  or 9090 (Prometheus). Simplest fix: stop that other program. On Windows or macOS, where you
  have already added `ports:` mappings per the compose file's own top comment, you can instead
  change the port on the left of the mapping (for example `"3001:3000"`) and use that new port
  in the URL you open. If instead it is Pulse's own port, 9464, that is already taken, that shows
  up as an error in the server's own log, not here: change `Port` in `ModConfig/pulse.json` on
  the server and restart it.
- **Grafana opens, but the dashboard has no data.** In Prometheus, open
  `http://localhost:9090/targets`. If the `vintagestory` target is not `UP`, Prometheus cannot
  reach Pulse: check that the server is running, that the address in `prometheus.yml` matches
  where Pulse is actually listening, and, on Windows or macOS, that you made the
  `host.docker.internal` change from path A above.
- **Nothing outside the server can reach `/metrics` at all.** That is by design, not a bug: the
  endpoint has no login of its own, so Pulse only listens on the server itself
  (`127.0.0.1`) unless you deliberately widen it. See the main README's
  [bind address section](../README.md#a-word-on-the-bind-address) before changing that.
