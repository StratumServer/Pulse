#!/usr/bin/env python3
"""Turn the provisioned dashboard into one Grafana's import dialog accepts.

A provisioned dashboard points at a datasource by uid, which only means
anything on a Grafana that was provisioned from the files in this repo.
Anyone else importing it gets panels wired to a datasource that does not
exist. Grafana's answer is the export-for-sharing shape: the datasource
becomes an __inputs placeholder, and the import dialog asks for a real one.

    python3 contrib/grafana/make-shared.py

Reads provisioning/dashboards/json/pulse.json, writes
pulse-overview-shared.json beside it. The provisioned file is the source of
truth; this exists so nobody has to keep two copies of every panel in sync.
"""

import json
import pathlib

HERE = pathlib.Path(__file__).parent
SOURCE = HERE / "provisioning" / "dashboards" / "json" / "pulse.json"
TARGET = HERE / "pulse-overview-shared.json"

INPUT_NAME = "DS_PROMETHEUS"

# Panel plugin ids carry no display name in the dashboard, and __requires wants
# one. Anything not listed falls back to the id itself, which is still a usable
# thing to read in an import dialog.
PANEL_NAMES = {
    "row": "Row",
    "stat": "Stat",
    "table": "Table",
    "timeseries": "Time series",
}

# The oldest Grafana that reads schemaVersion 39 and the panel options used
# here. Import onto anything older and it warns rather than silently misdraws.
GRAFANA_VERSION = "10.0.0"


def placeholder(node):
    """Replace every Prometheus datasource reference with the import input."""
    if isinstance(node, dict):
        if node.get("type") == "prometheus" and "uid" in node:
            return {"type": "prometheus", "uid": "${" + INPUT_NAME + "}"}
        return {k: placeholder(v) for k, v in node.items()}
    if isinstance(node, list):
        return [placeholder(v) for v in node]
    return node


def panel_types(dashboard):
    types = set()
    for panel in dashboard.get("panels", []):
        types.add(panel.get("type"))
        for nested in panel.get("panels", []):
            types.add(nested.get("type"))
    return sorted(t for t in types if t)


def main():
    dashboard = placeholder(json.loads(SOURCE.read_text(encoding="utf-8")))

    shared = {
        "__inputs": [
            {
                "name": INPUT_NAME,
                "label": "Prometheus",
                "description": "The Prometheus that scrapes your Pulse endpoint.",
                "type": "datasource",
                "pluginId": "prometheus",
                "pluginName": "Prometheus",
            }
        ],
        "__requires": [
            {"type": "grafana", "id": "grafana", "name": "Grafana", "version": GRAFANA_VERSION},
            {"type": "datasource", "id": "prometheus", "name": "Prometheus", "version": "1.0.0"},
        ]
        + [
            {"type": "panel", "id": t, "name": PANEL_NAMES.get(t, t), "version": ""}
            for t in panel_types(dashboard)
        ],
        # Grafana assigns the imported dashboard a fresh numeric id. Carrying
        # one over from the exporting instance is how an import lands on top of
        # an unrelated dashboard.
        "id": None,
    }
    shared.update(dashboard)

    TARGET.write_text(json.dumps(shared, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {TARGET.relative_to(HERE)} from {SOURCE.relative_to(HERE)}")


if __name__ == "__main__":
    main()
