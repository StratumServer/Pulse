# Getting started: from install to your first dashboard

This guide is for a server owner who has never used Prometheus or Grafana and just wants to see
graphs of their Vintage Story server. What else you need depends on how your server is hosted;
the list right below sorts that out.

Pulse itself does not draw any graphs. It only serves a page of current numbers, `/metrics`, and
waits for something to come and read it. **Prometheus** is that something: a program that visits
`/metrics` on a timer and keeps a history of every number it finds. **Grafana** is the program
that turns Prometheus's history into the graphs you actually look at, arranged on a web page
called a dashboard. You need one of each: a ready-made setup you run yourself (path A), or
Grafana's own free hosted service (path B).

## Which path is yours

- **A Linux server you rent or administer.** Install the mod (step 1), then path A on the server
  itself. View the dashboard from your own computer through an SSH tunnel, covered in path A.
- **A home PC running the game server, on Windows, macOS or Linux.** Install the mod, then path
  A: the Docker Desktop variant if it is Windows or macOS, and on Linux, whichever compose file
  matches what you installed: the Linux one for plain Docker Engine (the usual choice), the
  Docker Desktop one if you specifically installed Docker Desktop for Linux instead.
- **A Windows Server host, or a panel-only host with no shell access** (many rented game panels
  are like this). Skip straight to path B, Grafana Cloud: it needs nothing installed besides the
  mod itself.
- **The game server itself runs inside a container** (a Pterodactyl panel and similar). Path A
  cannot reach it: `127.0.0.1` inside that container is the container, not the machine
  underneath it. Use path B instead.

## Step 1: install the mod on the server

This part is the same whichever path you pick, and it happens on the game server only, not on
your own computer. Pulse needs Vintage Story 1.22 or newer.

1. Download `pulse_x.x.x.zip` (replace `x.x.x` with the version you downloaded) from the
   [ModDB page](https://mods.vintagestory.at/pulse) or from
   [GitHub releases](https://github.com/StratumServer/Pulse/releases).
2. Drop the zip, unopened, into the server's `Mods/` folder, the same place every other mod
   goes. By default that is:
   - Linux, the official server install script: `/home/vintagestory/data/Mods`.
   - Linux, running the server binary yourself with no `--dataPath`: `~/.config/VintagestoryData/Mods`.
   - Windows: `%AppData%\VintagestoryData\Mods` (not `%AppData%\Vintagestory`, the install
     folder itself, a different place one word shorter).
   - A rented panel: wherever its file manager already shows your other mods; the panel has
     usually worked this out for you already.
3. Start (or restart) the server.
4. Watch the server's log for this line, which is Pulse confirming it is up:

   ```
   Pulse serving metrics on http://127.0.0.1:9464/metrics
   ```

   The log is either the console you started the server in, or `Logs/server-main.log` next to
   `Mods/`. On a panel-only host, use whatever log view the panel gives you.
5. If you have a terminal or browser on the server itself, confirm the page it just mentioned
   actually answers (no shell on a panel-only host: skip this check and go straight to path B
   below). The address is `127.0.0.1`, meaning "this machine only", so this check has to run on
   the server, not on your own computer. Either open `http://127.0.0.1:9464/metrics` in a
   browser running on the server, or, from a terminal on the server, run:

   ```sh
   curl http://127.0.0.1:9464/metrics
   ```

   On Windows PowerShell, type `curl.exe`, not plain `curl`: PowerShell aliases `curl` to its
   own `Invoke-WebRequest`, which takes different options and will not behave like the command
   above.

   Either way you should get back plain text, a long list of lines like
   `pulse_players_online 0`. That text is what Prometheus will be reading in path A, and what
   the OTLP mod pushes onward in path B.

Pulse also wrote its own config file at this point, at `ModConfig/pulse.json` next to `Mods/`.
You do not need to touch it for either path below.

## Path A: run Prometheus and Grafana yourself

Use this path if you are happy running two small programs yourself, on your own computer or on
the server.

### Before you start

- You need Docker installed. On Windows or macOS that always means Docker Desktop; see
  [Docker's own install instructions](https://docs.docker.com/get-started/get-docker/). On
  Linux, that same page also offers Docker Desktop for Linux, a different product aimed at
  desktop use: it needs the Docker Desktop compose file below, not the plain Linux one. For the
  plain Linux one, install Docker Engine instead, from
  [Docker's Engine install guide](https://docs.docker.com/engine/install/) for your distribution.
- Every command below starts with `docker compose` (two words), which needs the current compose
  plugin. Installing from Docker's own repository, as the guides above do, gives you that
  directly. From your distribution's own repository instead, package names vary: Ubuntu calls it
  `docker-compose-v2`, and on Debian 13 (trixie) the plainly-named `docker-compose` package is
  already the current one, not the old, deprecated tool some other distributions still ship
  under that same name. If what you end up with only answers to a hyphenated `docker-compose`,
  the commands below work identically with a hyphen instead of a space.
- If a command below fails with "permission denied" talking to the Docker daemon, either put
  `sudo` in front of it, or add yourself to the `docker` group once, so future commands do not
  need `sudo` at all, following
  [Docker's post-install steps](https://docs.docker.com/engine/install/linux-postinstall/), then
  log out and back in.
- You need the `contrib/grafana` folder from the Pulse repository. This kit has not reached a
  stable release yet, so get it from the `dev` branch specifically:

  ```sh
  git clone -b dev https://github.com/StratumServer/Pulse.git
  cd Pulse/contrib/grafana
  ```

  No `git` on a headless server? Installing it is usually simpler than the alternative:
  `sudo apt install git` (Debian or Ubuntu) or your distribution's equivalent, then the commands
  above. Otherwise, on the repository's GitHub page, switch the branch selector to `dev`, then
  use Code, Download ZIP, or go straight to the
  [dev branch zip](https://github.com/StratumServer/Pulse/archive/refs/heads/dev.zip). Extract
  it and open a terminal in the extracted `contrib/grafana` folder.
- The first `docker compose up -d` downloads the Prometheus and Grafana images, a few hundred
  megabytes together, so it takes minutes, not seconds. Every run after that is fast.

### On a Linux server or PC

1. Start Prometheus and Grafana together:

   ```sh
   docker compose up -d
   ```

   Both containers are set to restart automatically, including after this machine reboots,
   until you stop them in step 3.
2. Open `http://localhost:3000/d/pulse-overview` in a browser on the same machine. That is
   Grafana's "Pulse server overview" dashboard, already wired to Prometheus; nobody has to sign
   in. If you are sitting at that machine, that is it. If this is a rented server, open an SSH
   tunnel from your own computer instead of opening any port to the internet:

   ```sh
   ssh -L 3000:127.0.0.1:3000 -L 9090:127.0.0.1:9090 user@your-server
   ```

   (this also works from PowerShell on Windows 10 or 11), then open
   `http://localhost:3000/d/pulse-overview` on your own computer, as if Grafana were running
   locally.
3. When you are done, stop both programs from the same folder:

   ```sh
   docker compose down
   ```

   This does not keep any history: this setup uses no named storage, so both containers start
   empty again next time. That is fine for a first look; it is not a backup of anything.

This assumes Pulse is running on the same machine as this Docker setup, using the default
`127.0.0.1:9464` from step 1. That works because the compose file uses "host networking": the
containers share the machine's own network instead of getting an isolated one, so `127.0.0.1`
inside a container reaches the machine, same as outside it. Host networking like this is the
normal case on Linux; Docker Desktop added an opt-in host networking mode for Windows and macOS
in version 4.34, but it is off by default and this guide does not rely on it.

Both Grafana and Prometheus are set to listen on `127.0.0.1` only inside the compose file, on
purpose: host networking puts them directly on the machine's own network, and Grafana's
anonymous access has no password of its own, so without that bind, anyone who can reach this
machine at all, not just log into it, could administer Grafana, including pointing its data
sources at addresses of their own choosing. The SSH tunnel above is how you look at it from
elsewhere instead of widening that bind.

### On Windows or macOS (Docker Desktop)

1. Start Prometheus and Grafana together, naming the Windows and macOS compose file:

   ```sh
   docker compose -f docker-compose.desktop.yml up -d
   ```

   Same as above, both containers restart automatically until you stop them in step 3.
2. Open `http://localhost:3000/d/pulse-overview` in a browser on the same machine.
3. When you are done:

   ```sh
   docker compose -f docker-compose.desktop.yml down
   ```

This file reaches Pulse through `host.docker.internal`, the address Docker Desktop provides for
reaching the machine it runs on from inside a container, since Desktop cannot use host networking
the way Linux does. It still asks Pulse for `127.0.0.1` once it gets there (see the comment in
`prometheus.desktop.yml`): on Linux and macOS, Pulse's listener answers 404 to any other address
in the request, `host.docker.internal` included; Windows does not have this problem. Use this
file when Pulse runs directly on this same Windows or macOS machine. Docker Desktop is not
available on Windows Server; use path B there instead. Both ports are published to `127.0.0.1`
only here too, for the same reason as the Linux file above.

## Path B: Grafana Cloud, if you would rather host nothing

Use this path if you do not want to run Prometheus or Grafana yourself, or your host will not
let you. Grafana Cloud is Grafana's own hosted service, with a free tier: see
[Grafana's pricing page](https://grafana.com/pricing/) for the current limits (10,000 metric
series, 14 day retention and 3 users, as of writing). This path needs a second, optional mod,
because nobody is coming to read `/metrics` for you; instead, the server pushes its numbers out
over the internet, using a protocol called OTLP.

Create a Grafana Cloud account yourself at [grafana.com](https://grafana.com/) if you do not
have one already.

1. In the Grafana Cloud portal at grafana.com (not the Grafana app itself), open your stack and
   find its OpenTelemetry tile. Click Configure, then "Generate now" (the exact wording may vary
   a little) to create an access policy token; it prints ready-to-use values, including
   `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_EXPORTER_OTLP_HEADERS`. Use the endpoint as printed,
   something like `https://otlp-gateway-<region>.grafana.net/otlp`.

   From `OTEL_EXPORTER_OTLP_HEADERS`, take everything after `Authorization=`, but change any
   `%20` back into an actual space. That printed value is itself encoded for use as an
   environment variable; Pulse encodes header values its own way before sending them, and the
   exporter decodes them again once they arrive, a round trip that leaves a real space
   untouched, but leaves a pasted `%20` exactly as pasted too, a literal `%20` instead of a
   space, which is not the header Grafana Cloud expects. If you ever need the instance ID by
   itself, read it from this same OpenTelemetry tile, not the separate Prometheus connection
   tile: Grafana Cloud gives each of them their own instance ID.
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
       "Authorization": "<everything after Authorization= from step 1>"
     }
   }
   ```

   Leave every other key as the mod wrote it. `Endpoint` is the base address only, Pulse adds
   the rest of the path itself.
5. Start the server again. Look for a log line starting with `Pulse OTLP exporting`, which
   confirms it is pushing on a timer; by default that timer is 60 seconds, so give it a minute.
6. Confirm it arrived: in Grafana Cloud, open **Explore**, pick your Prometheus data source, and
   query `pulse_players_online`. A value coming back means it worked.

`pulse-otlp.json` now holds a credential in plain text. If you ever paste its contents somewhere
to ask for help, delete the `Authorization` line first.

Once the numbers are flowing, bring in the dashboard:

1. In Grafana Cloud, go to **Dashboards**, then **New**, then **Import dashboard**.
2. You never cloned the repository for this path, so download the dashboard file directly:
   [`pulse-overview-shared.json`](https://github.com/StratumServer/Pulse/blob/dev/contrib/grafana/pulse-overview-shared.json)
   (use that page's download button), then upload it in the Import dialog.
3. It asks for a Prometheus data source (Grafana's name for a saved connection to somewhere it
   can pull numbers from). Pick the one your Grafana Cloud stack already created for you:
   Grafana Cloud stores OTLP metrics in its own Prometheus-compatible store, so this is the same
   data source your other Grafana Cloud graphs use.
4. Open the imported dashboard. It is the same "Pulse server overview" dashboard as path A, with
   one difference, covered next.

Nine panels stay empty on this dashboard when the data arrives over OTLP instead of a direct
scrape, even though the numbers behind them exist. Grafana Cloud's own translation from OTLP
into Prometheus-style names adds a unit suffix to some series on the way in, and the dashboard's
queries do not know the translated names yet: "Bytes per second by channel" and "Packets per
second by channel" in the Network row, "Tick share by mod" and "Current share by mod" in
Attribution, and "GC pause time", "Allocation rate", "Managed heap after last collection",
"Process memory" and "CPU time" in the Runtime row. A decision on the fix, either the dashboard's
queries or how the OTLP mod reports units, is tracked in
[issue #77](https://github.com/StratumServer/Pulse/issues/77); nothing in the dashboard JSON
changes here. Everything else, including tick health, players, world and worldgen, reads
normally.

## Troubleshooting

- **`docker compose up -d` succeeds, but a container keeps restarting.** With host networking, a
  taken port does not show up as "port is already allocated" the way a published port would;
  the container starts, the program inside fails to bind the port, and Docker just restarts it
  in a loop. Run `docker compose ps` to see which container is stuck restarting, then
  `docker compose logs prometheus` or `docker compose logs grafana` to read why. "Address
  already in use" on port 9090 is usually something else already listening there; on Rocky
  Linux, AlmaLinux or RHEL, that is often Cockpit's own web console, worth checking first. On
  the Docker Desktop variant, a taken port does show up as "port is already allocated" at
  startup instead, since those ports are published rather than shared directly. Either way, the
  remedy is the same as any port clash: stop whatever else is using it, or, on the Docker
  Desktop variant only, change the left-hand number in that service's `ports:` entry (for
  example `"127.0.0.1:9091:9090"`) and open that new port instead; the Linux file has no such
  mapping to edit, so there the fix is always to free up the port.
- **Pulse's own port, 9464, will not bind.** That is unrelated to Docker: Pulse itself logs an
  error at startup and runs without the metrics endpoint until you fix it. Either free up port
  9464, or set a different `Port` in `ModConfig/pulse.json`, restart the server, and update
  `prometheus.yml` to match: the target address for the Linux file, or both the `proxy_url` and
  the target address in `prometheus.desktop.yml`, since that one file names the port twice.
  Prometheus only scrapes the address its config file names.
- **`http://localhost:9464/metrics` answers 404, but `127.0.0.1` works.** Use `127.0.0.1`, not
  `localhost`, whenever you address Pulse directly: on Linux and macOS, Pulse's listener
  currently answers 404 to anything but the exact address it was told to bind, `localhost`
  included, even though both names reach the same machine. A fix for this is being looked at
  separately; for now, always use the literal IP from `Bind` in `ModConfig/pulse.json`.
- **Grafana opens, but the dashboard has no data at all.** In Prometheus, open
  `http://localhost:9090/targets` (through your SSH tunnel if this is a remote server). If the
  `vintagestory` target is not `UP`, Prometheus cannot reach Pulse: check that the server is
  running, and that the address in `prometheus.yml` (or `prometheus.desktop.yml`) matches where
  Pulse is actually listening.
- **Some panels are empty, but others show data.** Not every panel needs every setting. The
  Attribution row stays empty until you turn attribution on (see the main README's Attribution
  section), and a handful of engine-level panels (busy time, per-second network rates, the
  connection queue) go blank if Pulse is running in degraded mode, also covered in the main
  README. On path B, see the Grafana Cloud panel list above.
- **Path B: numbers never show up.** An OTLP push that Grafana Cloud rejects costs nothing on the
  game side, but it no longer stays quiet: check the server's log, the console or
  `Logs/server-main.log` from step 4 of installing the mod above, for a line starting `Pulse OTLP
  export to`. A wrong or expired token reads like this:

  ```
  Pulse OTLP export to https://otlp-gateway-<region>.grafana.net/otlp/v1/metrics failed:
  Response status code does not indicate success: 401 (Unauthorized). Metrics are not reaching
  the backend; check Endpoint and Headers in pulse-otlp.json. This is logged again at most every
  10 minutes.
  ```

  `pulse-otlp.json` only holds the already-encoded `Authorization` value, not a separate instance
  ID and token to eyeball, so the quickest fix is to go back to the OpenTelemetry tile, generate a
  fresh value, and paste it in again exactly as in step 1 of path B. No such line at all, this
  soon after starting the server, most likely just means the first push has not happened yet;
  give it one minute, the default `IntervalSeconds`, and check again.
- **Nothing outside the server can reach `/metrics` at all.** That is by design, not a bug: the
  endpoint has no login of its own, so Pulse only listens on the server itself (`127.0.0.1`)
  unless you deliberately widen it. See the main README's
  [bind address section](../README.md#a-word-on-the-bind-address) before changing that.
