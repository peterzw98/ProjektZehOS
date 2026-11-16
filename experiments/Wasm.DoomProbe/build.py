#!/usr/bin/env python3
"""Baut doomgeneric mit der ZehOS-Plattformschicht als WASIp1-Modul (doom.wasm).

Die doomgeneric-Quellen werden nicht eingecheckt; --source zeigt auf eine Arbeitskopie am
festgelegten Commit. Ohne passenden Commit bricht der Build ab.
"""

import argparse
import subprocess
import sys
from pathlib import Path

COMMIT = "e257a09527062bfb986b6e5e0780839b6cbd5b71"
HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
# Spielquellen laut doomgeneric-Makefile ohne X11-Plattformdatei.
SOURCES = """dummy am_map doomdef doomstat dstrings d_event d_items d_iwad d_loop d_main d_mode d_net f_finale
f_wipe g_game hu_lib hu_stuff info i_cdmus i_endoom i_joystick i_scale i_sound i_system i_timer memio m_argv
m_bbox m_cheat m_config m_controls m_fixed m_menu m_misc m_random p_ceilng p_doors p_enemy p_floor p_inter
p_lights p_map p_maputl p_mobj p_plats p_pspr p_saveg p_setup p_sight p_spec p_switch p_telept p_tick p_user
r_bsp r_data r_draw r_main r_plane r_segs r_sky r_things sha1 sounds statdump st_lib st_stuff s_sound tables
v_video wi_stuff w_checksum w_file w_main w_wad z_zone w_file_stdc i_input i_video doomgeneric""".split()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="doomgeneric-Arbeitskopie")
    parser.add_argument("--wasi-sdk", type=Path, default=ROOT / ".tools" / "wasi-sdk-25.0-x86_64-windows")
    parser.add_argument("--output", type=Path, default=HERE / "bin" / "doom.wasm")
    args = parser.parse_args()

    head = subprocess.run(["git", "-C", str(args.source), "rev-parse", "HEAD"], capture_output=True, text=True)
    if head.returncode != 0 or head.stdout.strip() != COMMIT:
        print(f"doomgeneric muss am Commit {COMMIT} stehen.", file=sys.stderr)
        return 1
    clang = args.wasi_sdk / "bin" / ("clang.exe" if sys.platform == "win32" else "clang")
    sources = [str(args.source / "doomgeneric" / f"{name}.c") for name in SOURCES]
    sources.append(str(HERE / "platform" / "doomgeneric_zehos.c"))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    command = [str(clang), "--target=wasm32-wasip1", f"--sysroot={args.wasi_sdk / 'share' / 'wasi-sysroot'}",
               "-O2", "-D_DEFAULT_SOURCE", "-DDOOMGENERIC_RESX=320", "-DDOOMGENERIC_RESY=200",
               "-D_WASI_EMULATED_SIGNAL", "-D_WASI_EMULATED_PROCESS_CLOCKS",
               "-Wno-everything", "-I", str(args.source / "doomgeneric"),
               # Ohne Abbildung landen absolute Baupfade (Benutzername) ueber __FILE__ im Paket.
               f"-ffile-prefix-map={args.source.resolve()}=/doomgeneric", f"-ffile-prefix-map={HERE.resolve()}=/zehos",
               # 8-Bit-Bild: die Palettenumrechnung im Interpreter kostete fast die Haelfte der Bildzeit.
               "-DCMAP256",
               *sources, "-lwasi-emulated-signal", "-lwasi-emulated-process-clocks",
               "-Wl,-z,stack-size=1048576", "-Wl,--wrap=I_Quit,--wrap=I_Error", "-o", str(args.output)]
    result = subprocess.run(command)
    if result.returncode == 0:
        print(f"{args.output} ({args.output.stat().st_size} Bytes)")
    return result.returncode


if __name__ == "__main__":
    sys.exit(main())
