# Wie scripts/test-linux-run.py run, aber der Seed der Datenplatte bekommt eine Fülldatei von FILLER_MIB MiB (Standard 478 bei 504 MiB),
# damit die Platte voll ist, bevor das Kontingent je Wurzel (64 MiB) greift. Für src/v7b/v7bfull; Argumente wie "run".
import importlib.util
import os
import sys
from pathlib import Path

repo = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("tlr", repo / "scripts/test-linux-run.py")
tlr = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tlr)
tools = tlr.probe().tools()
original = tools.create_disk


def create_with_filler(seed, image):
    with open(seed / "filler.bin", "wb") as f:
        f.truncate(int(os.environ.get("FILLER_MIB", "478")) << 20)
    original(seed, image)


tools.create_disk = create_with_filler
sys.exit(tlr.main(sys.argv[1:]))
