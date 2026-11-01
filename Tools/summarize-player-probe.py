#!/usr/bin/env python3
"""Prints one line per probe scenario from a FlowFieldPlayerProbe CSV."""
import csv
import sys
from collections import OrderedDict

scenarios = OrderedDict()
with open(sys.argv[1], newline="") as source:
    for row in csv.DictReader(source):
        key = (row["backend"], row["scenario"])
        scenarios.setdefault(key, OrderedDict())[row["metric"]] = row["value"]

for (backend, scenario), metrics in scenarios.items():
    values = " ".join(f"{metric}={value}" for metric, value in metrics.items())
    print(f"{backend:8}{scenario:42}{values}")
