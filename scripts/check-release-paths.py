#!/usr/bin/env python3
"""Prüft Release-Dateien auf lokale Quell- und Benutzerpfade."""

import os
from pathlib import Path
import sys


def check_file(path, patterns):
    data = path.read_bytes()
    # Cosmos enthaelt diesen festen Pfad bereits in vorgebauten Assemblies.
    upstream_root = '/home/runner/work/Cosmos/Cosmos/src/'
    upstream_files = (
        'Cosmos.Kernel.Core/Scheduler/SchedulerManager.cs',
        'Cosmos.Kernel.HAL/DriverKit/Engine/DriverEngine.cs',
        'Cosmos.Kernel.HAL/DriverKit/Engine/InterruptContextGuard.cs',
        'Cosmos.Kernel.HAL/Internal/Runtime/CompilerHelpers/LibraryInitializer.cs',
        'Cosmos.Kernel/Internal/Runtime/CompilerHelpers/LibraryInitializer.cs',
    )
    for name in upstream_files:
        for encoding in ('utf-8', 'utf-16-le', 'utf-16-be'):
            terminator = b'\0' if encoding == 'utf-8' else b'\0\0'
            allowed = (upstream_root + name).encode(encoding) + terminator
            data = data.replace(allowed, b'\0' * len(allowed))
    return not any(value.encode(encoding) in data for value in patterns
                   for encoding in ('utf-8', 'utf-16-le', 'utf-16-be'))


def main(arguments):
    if not arguments:
        print('Keine Release-Artefakte angegeben.', file=sys.stderr)
        return 2
    patterns = ['/home/', '/Users/', '/root/', 'C:\\Users\\']
    patterns.extend(value for key in ('GITHUB_WORKSPACE', 'RUNNER_TEMP')
                    if (value := os.environ.get(key)))
    for argument in arguments:
        path = Path(argument)
        try:
            if not check_file(path, patterns):
                print(f'Lokaler Pfad in {path.name} gefunden.', file=sys.stderr)
                return 1
        except OSError as error:
            print(f'{path.name} kann nicht geprüft werden: {error}', file=sys.stderr)
            return 2
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
