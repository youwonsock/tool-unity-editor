#!/usr/bin/env python3
"""Prints one line per probe scenario from a FlowFieldPlayerProbe CSV."""
import csv
import sys
from collections import OrderedDict

scenarios = OrderedDict()
with open(sys.argv[1], newline="") as source:
    for row in csv.DictReader(source):
        scenarios.setdefault(row["scenario"], OrderedDict())[row["metric"]] = row["value"]

for scenario, metrics in scenarios.items():
    values = " ".join(f"{metric}={value}" for metric, value in metrics.items())
    print(f"{scenario:42}{values}")
