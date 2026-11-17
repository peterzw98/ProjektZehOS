#!/usr/bin/env python3
"""Liest Metadaten der ext2-Familie; keine Konsistenz-/Mountfreigabe."""

import argparse
import json
from pathlib import Path
import struct


def read_ext2_metadata(path: Path) -> dict[str, int]:
    with path.open("rb") as stream:
        stream.seek(1024)
        superblock = stream.read(1024)
    if len(superblock) != 1024:
        raise ValueError("Unvollstaendiger ext2-Superblock.")
    if struct.unpack_from("<H", superblock, 56)[0] != 0xEF53:
        raise ValueError("Keine ext2-Signatur am Partitionsanfang.")
    blocks = struct.unpack_from("<I", superblock, 4)[0]
    log_block_size = struct.unpack_from("<I", superblock, 24)[0]
    if log_block_size > 2:
        raise ValueError("Nicht unterstuetzte ext2-Blockgroesse.")
    if blocks == 0:
        raise ValueError("Ungueltige ext2-Blockanzahl.")
    compat, incompat, ro_compat = struct.unpack_from("<III", superblock, 92)
    return {
        "block_count": blocks,
        "block_size": 1024 << log_block_size,
        "feature_compat": compat,
        "feature_incompat": incompat,
        "feature_ro_compat": ro_compat,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path, help="Rohes Partitionsimage, kein ganzes GPT-/MBR-Image")
    args = parser.parse_args()
    try:
        metadata = read_ext2_metadata(args.image)
    except (OSError, ValueError) as error:
        parser.exit(1, f"Fehler: {error}\n")
    print(json.dumps(metadata, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
