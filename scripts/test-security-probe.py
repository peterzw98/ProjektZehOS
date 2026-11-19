#!/usr/bin/env python3
"""Startet das Kryptografie-Probe-ISO (-p:ZehOSSecurityProbe=true) unter QEMU und wertet SECURITY_PROBE aus.

Python-Fassung von test-security-probe.ps1 fuer Linux-Hosts ohne pwsh; QEMU-Aufruf, Timeout (45 s) und Ablage unter
.tools/security-probe/ wie dort, gemeinsamer Ablauf mit test-thread-probe.py. Exit 0 nur bei der Abschlusszeile
"SECURITY_PROBE PASS ..."; Stufenzeilen wie "SECURITY_PROBE cleanup PASS ..." zaehlen nicht.
"""
import importlib.util
import re
import sys
from pathlib import Path

spec = importlib.util.spec_from_file_location("thread_probe", Path(__file__).resolve().parent / "test-thread-probe.py")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


def classify(log: str) -> str:
    """'PASS', 'FAIL' oder 'RUNNING'; wie in test-thread-probe.py nicht am Zeilenanfang verankert (SMP-Mischausgabe)."""
    if "SECURITY_PROBE FAIL" in log:
        return "FAIL"
    return "PASS" if re.search(r"SECURITY_PROBE PASS ", log) else "RUNNING"


if __name__ == "__main__":
    sys.exit(probe.run_probe("security-probe", classify, ("SECURITY_PROBE",), __doc__))
