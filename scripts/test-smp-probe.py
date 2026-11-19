#!/usr/bin/env python3
"""Preliminary SMP marker parser; guest protocol and QEMU runner are pending."""
import re

STEPS = ("cpus", "counters", "parallel", "migrate", "locks", "gc", "threads")
MARKER = re.compile(r"SMP_PROBE[ \t]+(?:(\w+)[ \t]+)?(PASS|FAIL|SKIP)(?:[ \t]+[^\r\n]*)?$", re.MULTILINE)


def classify(log):
    seen = set()
    terminal = False
    for match in MARKER.finditer(log):
        step, result = match.groups()
        if result == "FAIL":
            return "FAIL"
        if step is None:
            if terminal or result != "PASS":
                return "FAIL"
            terminal = True
        elif step not in STEPS or step in seen:
            return "FAIL"
        else:
            seen.add(step)
    return "PASS" if terminal and seen == set(STEPS) else "RUNNING"
