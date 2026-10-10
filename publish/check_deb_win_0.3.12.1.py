#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Проверка .deb 0.3.12.1: ар-члены, версия в control, соответствие md5sums файлам data.tar.gz."""
import gzip
import hashlib
import io
import os
import tarfile

DEB = os.path.join("package", "linux", "deb", "out", "configuration-management_0.3.12.1_amd64.deb")
EXPECTED_VERSION = "0.3.12.1"


def parse_ar(data: bytes) -> dict:
    assert data[:8] == b"!<arch>\n", "неверный ar-магик"
    i = 8
    members = {}
    while i + 60 <= len(data):
        name = data[i:i + 16].decode("ascii").strip()
        size = int(data[i + 48:i + 58].decode("ascii"))
        body = data[i + 60:i + 60 + size]
        members[name] = body
        i += 60 + size + (size % 2)
    return members


def main() -> None:
    with open(DEB, "rb") as fh:
        data = fh.read()
    members = parse_ar(data)
    print("ar-members:", list(members))

    # control.tar.gz -> control
    with tarfile.open(fileobj=io.BytesIO(gzip.decompress(members["control.tar.gz"]))) as tar:
        names = sorted(tar.getnames())
        print("control.tar.gz:", names)
        control = tar.extractfile("./control").read().decode("utf-8")
        version_line = next((ln for ln in control.splitlines() if ln.startswith("Version:")), None)
        print("control Version:", version_line)
        assert version_line == f"Version: {EXPECTED_VERSION}", "версия не совпадает!"
        md5sums = tar.extractfile("./md5sums").read().decode("utf-8")

    # data.tar.gz -> md5 по содержимому
    with tarfile.open(fileobj=io.BytesIO(gzip.decompress(members["data.tar.gz"]))) as tar:
        data_names = sorted(tar.getnames())
        print("data.tar.gz:", data_names)
        expected = {}
        for m in tar.getmembers():
            if not m.isfile():
                continue
            f = tar.extractfile(m)
            if f is None:
                continue
            path = m.name[2:] if m.name.startswith("./") else m.name
            payload = f.read()
            expected[path] = hashlib.md5(payload).hexdigest()
            if path == "usr/bin/ConfigurationManagement":
                print("binary mode in tar:", oct(m.mode))
                print("binary size:", len(payload))
                print("binary ELF magic:", payload[:4].hex(" "))
                assert payload[:4] == b"\x7fELF", "бинарь в .deb не ELF!"

    # md5sums-строки: "<md5>  <path>"
    actual = {}
    for line in md5sums.strip().splitlines():
        digest, path = line.split("  ", 1)
        actual[path] = digest
    print("md5sums entries:", len(actual), "data files:", len(expected))
    assert actual == expected, "md5sums не совпадают с содержимым data.tar.gz!"
    print("OK: контрольная сумма и состав data.tar.gz совпадают с DEBIAN/md5sums")
    print("OK: структура .deb корректна")


if __name__ == "__main__":
    main()
