# ModDB release entries: 0.2.1

HTML for the "what's new in this version" box on each zip's ModDB file entry: paste each block below into the field's HTML source view as is. Plain tags only (paragraphs, lists, bold, code, links), with about 1500 visible characters each. This is the canonical text.

## pulse_0.2.1.zip

<p>What's new in 0.2.1:</p>
<ul>
<li>Log lines now carry the mod's id: <code>[Notification] [pulse] Pulse serving metrics on ...</code>. The messages themselves are unchanged, but a log shipper or alert pattern that expects Pulse's words right after the severity needs the tag in between.</li>
<li>Per-mod attribution no longer files part of the game's own work under <code>unattributed</code>. Despawn timers, name tags, creature physics and the other vanilla entity behaviours whose mark is not their class's code now go to <code>game</code>, <code>survival</code> or <code>engine</code>. On a vanilla test server, <code>unattributed</code> fell from about 2.5% of the sampled tick to under 0.1%. The first burst after attribution starts reads the loaded entities once: a few milliseconds, 23 to 40 ms at 8,000 entities.</li>
<li>The getting-started guide now covers macOS, gives the right data path for the server.sh that ships with the server, and explains how to load the alert rules.</li>
</ul>
<p><b>Upgrading from 0.2.0:</b> replace the zip and restart. No config change is needed.</p>

## pulseotlp_0.2.1.zip

<p>What's new in 0.2.1:</p>
<ul>
<li>The <code>instance</code> label now survives restarts. 0.2.0 exported a random service.instance.id on every start, so each restart began a new set of series. The id now lives in a new <code>ServiceInstanceId</code> key of pulse-otlp.json, generated on the first start and written into the file, or set by you (survival-eu-1, say). That first start changes the label one last time.</li>
<li>Give each server its own id: a pulse-otlp.json copied to another server carries the same one. On a read-only ModConfig the generated id cannot be saved, and a warning says so: set the key yourself, or use <code>OTEL_RESOURCE_ATTRIBUTES=service.instance.id=YOUR_ID</code>.</li>
<li>A service.instance.id in <code>OTEL_RESOURCE_ATTRIBUTES</code> now wins over the key and survives without <code>OTEL_SERVICE_NAME</code>. With <code>OTEL_SERVICE_NAME</code> set, the environment still decides the whole identity.</li>
<li>Log lines carry <code>[pulseotlp]</code>.</li>
<li><b>Requires the base pulse mod</b>; upgrade both zips together.</li>
</ul>
<p>Full notes: <a href="https://github.com/StratumServer/Pulse/releases/tag/v0.2.1">release v0.2.1</a>.</p>
