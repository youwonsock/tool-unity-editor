#!/usr/bin/env python3
"""Prints min / median / max across TransformPathPlayerProbe CSVs, one line per metric."""
import csv
import statistics
import sys
from collections import OrderedDict

metrics = OrderedDict()
for path in sys.argv[1:]:
    with open(path, newline="") as source:
        for row in csv.DictReader(source):
            metrics.setdefault((row["scenario"], row["metric"]), []).append(float(row["value"]))

print(f"runs={len(sys.argv) - 1}")
for (scenario, metric), values in metrics.items():
    print(f"{scenario:40}{metric:28}{min(values):>11.3f}{statistics.median(values):>11.3f}{max(values):>11.3f}")
