#!/usr/bin/env python3
"""Keep managed data out of macOS code directories, retaining .NET lookup paths."""
import os
import pathlib
import shutil
import subprocess
import sys


def arrange(app):
    contents = app / "Contents"
    macos = contents / "MacOS"
    for file in sorted(macos.iterdir()):
        if file.name == "MusicFormatConverter.App":
            continue
        if file.name == "tools":
            target = contents / "Resources/tools"
        elif file.suffix == ".dylib":
            target = contents / "Frameworks" / file.name
        elif file.is_file() and "Mach-O" in subprocess.check_output(["file", "-b", str(file)], text=True):
            continue  # e.g. the native createdump helper
        else:
            target = contents / "Resources/managed" / file.name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(file), target)
        file.symlink_to(os.path.relpath(target, file.parent), target_is_directory=target.is_dir())
    # The apphost resolves the managed entry DLL's real path. Its app-local
    # runtime and native assets must therefore also be discoverable beside it.
    managed = contents / "Resources/managed"
    managed.mkdir(parents=True, exist_ok=True)
    for library in (contents / "Frameworks").iterdir():
        (managed / library.name).symlink_to(os.path.relpath(library, managed))
    (managed / "tools").symlink_to("../tools", target_is_directory=True)


if __name__ == "__main__":
    arrange(pathlib.Path(sys.argv[1]).resolve())
