#!/usr/bin/env python3
"""Records the registry responses TPD's registry fallback needs for a fixture.

For every package in the SPDX documents, by purl type:

pkg:nuget  For both the registration5-semver1 and registration5-gz-semver2
           hives, the registration leaf and the catalog entry it points to
           (trimmed to license-related fields). For packages that only declare
           a licenseUrl, the first 64 KB of that URL, which is what
           LicenseTextDetector reads.
pkg:npm    The registry's version document (trimmed to the fields the lookup
           reads) and the version's tarball, cut down to its license files.
pkg:pypi   The JSON API document for the version (trimmed) and the artifact the
           lookup picks (pure wheel, else smallest wheel, else sdist), cut down
           to its license files.

Archives are rebuilt deterministically (fixed timestamps, owners and order)
and stored base64-encoded, so regenerating an unchanged fixture gives an
identical file.

Usage: record-registry.py <registry-responses.json> <spdx.json> [<spdx.json>...]
"""

import base64
import gzip
import io
import json
import re
import sys
import tarfile
import urllib.error
import urllib.request
import zipfile
from urllib.parse import quote, urlparse

HIVES = ("registration5-semver1", "registration5-gz-semver2")
CATALOG_FIELDS = ("id", "version", "licenseExpression", "licenseUrl", "licenseFile", "authors", "copyright")
DEPRECATED_LICENSE_URL = "https://aka.ms/deprecateLicenseUrl"
MAX_LICENSE_BYTES = 64 * 1024
MAX_ARCHIVE_BYTES = 50 * 1024 * 1024

NPM_FIELDS = ("name", "version", "license", "licenses", "author", "repository", "homepage")
PYPI_INFO_FIELDS = ("name", "version", "license", "license_expression", "author", "author_email", "home_page", "project_urls")
PYPI_FILE_FIELDS = ("filename", "url", "packagetype", "size")

# Mirrors PackageArchive.IsLicenseFileName; recording a few extra files is harmless.
LICENSE_PREFIXES = ("LICENSE", "LICENCE", "COPYING", "NOTICE", "UNLICENSE")


def get(url, max_bytes=None, binary=False):
    req = urllib.request.Request(url, headers={"User-Agent": "ThirdPartyDetective-fixture-recorder", "Accept-Encoding": "gzip"})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            raw = resp.read()
            if resp.headers.get("Content-Encoding") == "gzip":
                raw = gzip.decompress(raw)
            if max_bytes is not None:
                raw = raw[:max_bytes]
            if binary:
                return resp.status, resp.headers.get_content_type(), raw
            return resp.status, resp.headers.get_content_type(), raw.decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, "text/plain", b"" if binary else ""
    except (urllib.error.URLError, TimeoutError) as e:
        return 0, "text/plain", b"" if binary else f"unreachable: {e}"


def escape(segment):
    # Matches C# Uri.EscapeDataString for the characters package names and versions use.
    return quote(segment, safe="-._~")


def is_license_file_name(name):
    upper = name.upper()
    return upper.startswith(LICENSE_PREFIXES) or re.search(r"[-_]LICEN[CS]E(\.[^.]*)?$", upper) is not None


class Recorder:
    def __init__(self):
        self.responses = {}

    def record(self, url, status, content_type, body):
        self.responses[url] = {"status": status, "contentType": content_type, "body": body}

    def record_binary(self, url, status, content_type, data):
        self.responses[url] = {"status": status, "contentType": content_type, "bodyBase64": base64.b64encode(data).decode("ascii")}

    # --- NuGet -------------------------------------------------------------

    def nuget(self, name, version):
        name, version = name.lower(), version.lower()
        for hive in HIVES:
            leaf_url = f"https://api.nuget.org/v3/{hive}/{escape(name)}/{escape(version)}.json"
            status, ctype, body = get(leaf_url)
            if status != 200:
                self.record(leaf_url, status, ctype, "")
                continue
            catalog_url = json.loads(body)["catalogEntry"]
            self.record(leaf_url, status, ctype, json.dumps({"catalogEntry": catalog_url}))

            if catalog_url in self.responses:
                continue
            c_status, c_type, c_body = get(catalog_url)
            entry = json.loads(c_body) if c_status == 200 else {}
            trimmed = {k: entry[k] for k in CATALOG_FIELDS if k in entry}
            self.record(catalog_url, c_status, c_type, json.dumps(trimmed))

            license_url = trimmed.get("licenseUrl")
            if (not trimmed.get("licenseExpression") and license_url
                    and license_url != DEPRECATED_LICENSE_URL and license_url not in self.responses):
                self.record(license_url, *get(license_url, MAX_LICENSE_BYTES))

    # --- npm ---------------------------------------------------------------

    def npm(self, name, version):
        encoded = "@" + escape(name[1:]) if name.startswith("@") else escape(name)
        url = f"https://registry.npmjs.org/{encoded}/{escape(version)}"
        status, ctype, body = get(url)
        if status != 200:
            self.record(url, status, ctype, "")
            return
        doc = json.loads(body)
        trimmed = {k: doc[k] for k in NPM_FIELDS if k in doc}
        tarball = doc.get("dist", {}).get("tarball")
        if tarball:
            trimmed["dist"] = {"tarball": tarball}
        self.record(url, status, ctype, json.dumps(trimmed, ensure_ascii=False))

        if not tarball or tarball in self.responses:
            return
        see_license_in = None
        license_value = doc.get("license")
        if isinstance(license_value, str) and license_value.upper().startswith("SEE LICENSE IN "):
            see_license_in = license_value[len("SEE LICENSE IN "):].strip().lstrip("./")

        t_status, t_type, data = get(tarball, MAX_ARCHIVE_BYTES, binary=True)
        if t_status != 200:
            self.record(tarball, t_status, t_type, "")
            return

        def keep(path):
            relative = path.split("/", 1)[1] if "/" in path else None
            if relative is None:
                return False
            if see_license_in and relative.lower() == see_license_in.lower():
                return True
            return "/" not in relative and is_license_file_name(relative)

        self.record_binary(tarball, 200, "application/octet-stream", trim_tar_gz(data, keep))

    # --- PyPI --------------------------------------------------------------

    def pypi(self, name, version):
        normalized = re.sub(r"[-_.]+", "-", name).lower()
        url = f"https://pypi.org/pypi/{escape(normalized)}/{escape(version)}/json"
        status, ctype, body = get(url)
        if status != 200:
            self.record(url, status, ctype, "")
            return
        doc = json.loads(body)
        info = doc.get("info", {})
        trimmed_info = {k: info[k] for k in PYPI_INFO_FIELDS if k in info}
        trimmed_info["classifiers"] = [c for c in info.get("classifiers") or [] if c.startswith("License ::")]
        files = [{k: f[k] for k in PYPI_FILE_FIELDS if k in f} for f in doc.get("urls", [])]
        self.record(url, status, ctype, json.dumps({"info": trimmed_info, "urls": files}, ensure_ascii=False))

        artifact = choose_artifact(files)
        if artifact is None or artifact["url"] in self.responses:
            return
        a_status, a_type, data = get(artifact["url"], MAX_ARCHIVE_BYTES, binary=True)
        if a_status != 200:
            self.record(artifact["url"], a_status, a_type, "")
            return

        if artifact["packagetype"] == "bdist_wheel":
            def keep_wheel(path):
                top, _, relative = path.partition("/")
                if not top.endswith(".dist-info") or not relative:
                    return False
                return relative.startswith("licenses/") or ("/" not in relative and is_license_file_name(relative))
            trimmed = trim_zip(data, keep_wheel)
        else:
            def keep_sdist(path):
                _, _, relative = path.partition("/")
                return bool(relative) and "/" not in relative and is_license_file_name(relative)
            trimmed = trim_zip(data, keep_sdist) if artifact["filename"].endswith(".zip") else trim_tar_gz(data, keep_sdist)
        self.record_binary(artifact["url"], 200, "application/octet-stream", trimmed)


# Mirrors PyPiLicenseLookup.ChooseArtifact.
def choose_artifact(files):
    candidates = []
    for f in files:
        if f.get("packagetype") not in ("bdist_wheel", "sdist") or not f.get("url") or not f.get("filename"):
            continue
        host = urlparse(f["url"]).hostname or ""
        if urlparse(f["url"]).scheme != "https" or host.lower() != "files.pythonhosted.org":
            continue
        size = f.get("size") if isinstance(f.get("size"), int) else 2 ** 63 - 1
        if size > MAX_ARCHIVE_BYTES:
            continue
        is_wheel = f["packagetype"] == "bdist_wheel"
        rank = 0 if is_wheel and f["filename"].lower().endswith("-none-any.whl") else 1 if is_wheel else 2
        candidates.append((rank, size, f["filename"], f))
    return min(candidates, key=lambda c: c[:3])[3] if candidates else None


def trim_tar_gz(data, keep):
    out = io.BytesIO()
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as src, \
            gzip.GzipFile(fileobj=out, mode="wb", mtime=0) as gz, \
            tarfile.open(fileobj=gz, mode="w", format=tarfile.PAX_FORMAT) as dst:
        members = sorted((m for m in src.getmembers() if m.isfile() and keep(m.name.lstrip("./"))), key=lambda m: m.name)
        for member in members:
            content = src.extractfile(member).read()
            info = tarfile.TarInfo(member.name)
            info.size, info.mtime, info.mode = len(content), 0, 0o644
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            dst.addfile(info, io.BytesIO(content))
    return out.getvalue()


def trim_zip(data, keep):
    out = io.BytesIO()
    with zipfile.ZipFile(io.BytesIO(data)) as src, zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as dst:
        for name in sorted(n for n in src.namelist() if not n.endswith("/") and keep(n.replace("\\", "/"))):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            dst.writestr(info, src.read(name))
    return out.getvalue()


def main(out_path, spdx_paths):
    recorder = Recorder()
    seen = set()
    for spdx_path in spdx_paths:
        with open(spdx_path, encoding="utf-8") as f:
            doc = json.load(f)
        for pkg in doc.get("packages", []):
            purls = [r.get("referenceLocator", "") for r in pkg.get("externalRefs", [])]
            purl_type = next((p[4:].split("/", 1)[0] for p in purls if p.startswith("pkg:")), None)
            key = (purl_type, pkg.get("name"), pkg.get("versionInfo"))
            if purl_type not in ("nuget", "npm", "pypi") or key in seen:
                continue
            seen.add(key)
            getattr(recorder, purl_type)(pkg["name"], pkg.get("versionInfo", ""))

    with open(out_path, "w", encoding="utf-8") as f:
        json.dump({"responses": dict(sorted(recorder.responses.items()))}, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"Recorded {len(recorder.responses)} responses to {out_path}")


if __name__ == "__main__":
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2:])
