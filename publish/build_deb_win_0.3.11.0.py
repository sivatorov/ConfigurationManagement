#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Сборка .deb для configuration-management 0.3.11.0 на Windows (без dpkg-deb).

Копия процедуры build_deb_win_0.3.10.3.py: версия читается из csproj
(InformationalVersion), бинарь берётся из dist/linux-x64/ConfigurationManagement.
Воспроизводит логику package/linux/deb/build-deb.sh:
  - staging-дерево: DEBIAN/control (подстановки @VERSION@, @INSTALLED_SIZE@),
    DEBIAN/md5sums, usr/bin/ConfigurationManagement (0755),
    usr/share/applications/configuration-management.desktop,
    usr/share/icons/hicolor/256x256/apps/configuration-management.png,
    usr/share/doc/configuration-management/copyright;
  - control.tar.gz (файлы с ведущим './'), data.tar.gz (то же);
  - ar-архив: debian-binary (2.0\n) + control.tar.gz + data.tar.gz.

Детерминированность: uid=gid=0, mtime=0, gzip mtime=0.
"""
from __future__ import annotations

import gzip
import hashlib
import io
import os
import shutil
import tarfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
PROJECT_DIR = os.path.join(ROOT, "Configuration Management")
BINARY = os.path.join(PROJECT_DIR, "dist", "linux-x64", "ConfigurationManagement")
PKG_DIR = os.path.join(ROOT, "package", "linux")
DEB_DIR = os.path.join(PKG_DIR, "deb")
CONTROL_SRC = os.path.join(DEB_DIR, "DEBIAN", "control")
COPYRIGHT = os.path.join(DEB_DIR, "copyright")
DESKTOP = os.path.join(PKG_DIR, "configuration-management.desktop")
ICON = os.path.join(PKG_DIR, "app.png")

APP_ID = "configuration-management"
BINARY_NAME = "ConfigurationManagement"
ARCH = "amd64"

STAGING = os.path.join(DEB_DIR, "staging")
OUTDIR = os.path.join(DEB_DIR, "out")


def read_version_from_csproj() -> str:
    """Версия берётся из csproj (InformationalVersion), как в build-deb.sh."""
    csproj = os.path.join(PROJECT_DIR, "Configuration Management.csproj")
    with open(csproj, encoding="utf-8") as fh:
        for raw in fh:
            line = raw.split("<!--")[0]  # отбрасываем комментарии
            if "<InformationalVersion>" in line:
                value = line.split("<InformationalVersion>", 1)[1].split("<", 1)[0].strip()
                if value:
                    return value
    raise SystemExit("Ошибка: не удалось прочитать InformationalVersion из csproj")


def make_tar_gz(files: list) -> bytes:
    """files: (путь внутри архива с './', содержимое, mode)."""
    tar_buf = io.BytesIO()
    with tarfile.open(fileobj=tar_buf, mode="w", format=tarfile.GNU_FORMAT) as tar:
        for name, data, mode in files:
            info = tarfile.TarInfo(name)
            info.size = len(data)
            info.mode = mode
            info.uid = 0
            info.gid = 0
            info.uname = "root"
            info.gname = "root"
            info.mtime = 0
            tar.addfile(info, io.BytesIO(data))
    raw = tar_buf.getvalue()

    out = io.BytesIO()
    with gzip.GzipFile(fileobj=out, mode="wb", mtime=0) as gz:
        gz.write(raw)
    return out.getvalue()


def ar_append(buf: bytearray, name: str, data: bytes) -> None:
    header = (
        name.encode("ascii").ljust(16, b" ")
        + b"0".rjust(12, b" ")   # mtime
        + b"0".rjust(6, b" ")    # uid
        + b"0".rjust(6, b" ")    # gid
        + b"100644".rjust(8, b" ")
        + str(len(data)).encode("ascii").rjust(10, b" ")
        + b"`\n"
    )
    buf += header
    buf += data
    if len(data) % 2 == 1:
        buf += b"\n"


def main() -> None:
    version = read_version_from_csproj()
    out_file = f"{APP_ID}_{version}_{ARCH}.deb"

    if not os.path.exists(BINARY):
        raise SystemExit(f"Ошибка: не найден собранный single-file бинарь: {BINARY}")

    if os.path.isdir(STAGING):
        shutil.rmtree(STAGING)
    if os.path.isdir(OUTDIR):
        shutil.rmtree(OUTDIR)

    os.makedirs(os.path.join(STAGING, "DEBIAN"), exist_ok=True)
    os.makedirs(os.path.join(STAGING, "usr", "bin"), exist_ok=True)
    os.makedirs(os.path.join(STAGING, "usr", "share", "applications"), exist_ok=True)
    os.makedirs(os.path.join(STAGING, "usr", "share", "icons", "hicolor", "256x256", "apps"), exist_ok=True)
    os.makedirs(os.path.join(STAGING, "usr", "share", "doc", APP_ID), exist_ok=True)
    os.makedirs(OUTDIR, exist_ok=True)

    # --- контрольный файл control --------------------------------------------
    with open(CONTROL_SRC, encoding="utf-8") as fh:
        control = fh.read().replace("@VERSION@", version)

    installed_size = 0
    for dirpath, _dirnames, filenames in os.walk(os.path.join(STAGING, "usr")):
        for fn in filenames:
            installed_size += os.path.getsize(os.path.join(dirpath, fn))
    installed_kb = (installed_size + 1023) // 1024  # du -k округляет вверх
    control = control.replace("@INSTALLED_SIZE@", str(installed_kb))
    control_path = os.path.join(STAGING, "DEBIAN", "control")
    with open(control_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(control)

    # --- файлы дерева -----------------------------------------------------------
    shutil.copy2(BINARY, os.path.join(STAGING, "usr", "bin", BINARY_NAME))
    os.chmod(os.path.join(STAGING, "usr", "bin", BINARY_NAME), 0o755)
    shutil.copy2(DESKTOP, os.path.join(STAGING, "usr", "share", "applications", f"{APP_ID}.desktop"))
    shutil.copy2(ICON, os.path.join(STAGING, "usr", "share", "icons", "hicolor", "256x256", "apps", f"{APP_ID}.png"))
    shutil.copy2(COPYRIGHT, os.path.join(STAGING, "usr", "share", "doc", APP_ID, "copyright"))

    # --- md5sums (пути без './', как у dpkg-deb) --------------------------------
    md5_lines = []
    for dirpath, _dirnames, filenames in os.walk(os.path.join(STAGING, "usr")):
        for fn in sorted(filenames):
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, STAGING).replace(os.sep, "/")
            with open(full, "rb") as fh:
                digest = hashlib.md5(fh.read()).hexdigest()
            md5_lines.append(f"{digest}  {rel}")
    md5sums_path = os.path.join(STAGING, "DEBIAN", "md5sums")
    with open(md5sums_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(sorted(md5_lines)) + "\n")

    # --- tar-архивы ---------------------------------------------------------------
    def add_tree(tar_files: list, base: str, prefix: str = "./") -> None:
        for dirpath, _dirnames, filenames in os.walk(base):
            for fn in sorted(filenames):
                full = os.path.join(dirpath, fn)
                rel = os.path.relpath(full, base).replace(os.sep, "/")
                mode = 0o755 if os.access(full, os.X_OK) and os.path.isfile(full) else 0o644
                with open(full, "rb") as fh:
                    tar_files.append((prefix + rel, fh.read(), mode))

    with open(control_path, "rb") as fh:
        control_bytes = fh.read()
    with open(md5sums_path, "rb") as fh:
        md5sums_bytes = fh.read()

    control_tar = make_tar_gz([("./control", control_bytes, 0o644), ("./md5sums", md5sums_bytes, 0o644)])

    data_files: list = []
    add_tree(data_files, os.path.join(STAGING, "usr"), prefix="./usr/")
    data_tar = make_tar_gz(data_files)

    # --- ar-архив ------------------------------------------------------------------
    out = bytearray(b"!<arch>\n")
    ar_append(out, "debian-binary", b"2.0\n")
    ar_append(out, "control.tar.gz", control_tar)
    ar_append(out, "data.tar.gz", data_tar)

    out_path = os.path.join(OUTDIR, out_file)
    with open(out_path, "wb") as fh:
        fh.write(out)

    print(f"==> Бинарь:   {BINARY}")
    print(f"==> Пакет:    configuration-management, версия {version}, архитектура {ARCH}")
    print(f"==> Готово:   {out_path} ({os.path.getsize(out_path)} bytes)")
    print("    Установка: sudo dpkg -i " + out_path.replace("\\", "/"))


if __name__ == "__main__":
    main()
