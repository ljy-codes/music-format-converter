#!/usr/bin/env python3
"""Preserve restored NuGet license declarations and all shipped package notices."""
import json
import pathlib
import re
import shutil
import sys
import urllib.request
import xml.etree.ElementTree as ET


def collect(assets_path, destination):
    assets = json.loads(assets_path.read_text())
    destination.mkdir(parents=True)
    packages = []
    for name, library in assets["libraries"].items():
        if library["type"] != "package":
            continue
        package = next((pathlib.Path(root) / library["path"] for root in assets["packageFolders"]
                        if (pathlib.Path(root) / library["path"]).is_dir()), None)
        if package is None:
            raise ValueError(f"Restored package missing: {name}")
        output = destination / library["path"]
        output.mkdir(parents=True)
        nuspec = next(package.glob("*.nuspec"))
        shutil.copy2(nuspec, output)
        metadata = ET.parse(nuspec).getroot()
        fields = {element.tag.rsplit("}", 1)[-1]: element for element in metadata.iter()}
        declaration = fields.get("license")
        expression = declaration.text if declaration is not None else None
        for file in package.rglob("*"):
            if file.is_file() and re.search(r"license|licence|copying|copyright|notice", file.name, re.I):
                target = output / file.relative_to(package)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(file, target)
        if declaration is not None and declaration.get("type") == "file":
            license_file = package / expression
            if not license_file.resolve().is_relative_to(package.resolve()):
                raise ValueError("Invalid package license path")
            target = output / expression
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(license_file, target)
        elif expression:
            for identifier in re.findall(r"[A-Za-z0-9.-]+", expression):
                if identifier in ("AND", "OR", "WITH"):
                    continue
                target = destination / "spdx" / f"{identifier}.txt"
                if not target.exists():
                    target.parent.mkdir(exist_ok=True)
                    url = f"https://raw.githubusercontent.com/spdx/license-list-data/v3.27.0/text/{identifier}.txt"
                    with urllib.request.urlopen(url, timeout=60) as response:
                        target.write_bytes(response.read())
        packages.append({"package": name, "license": expression,
                         "authors": fields.get("authors").text if fields.get("authors") is not None else None,
                         "copyright": fields.get("copyright").text if fields.get("copyright") is not None else None})
    (destination / "packages.json").write_text(json.dumps(packages, indent=2, ensure_ascii=False) + "\n")
    print(f"Preserved license declarations and notices for {len(packages)} restored packages.")


if __name__ == "__main__":
    collect(pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]))
