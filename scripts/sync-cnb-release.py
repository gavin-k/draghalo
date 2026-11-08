#!/usr/bin/env python3
"""Mirror original GitHub assets; a CNB job's short-lived token never leaves the API host."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request

REPOSITORY = "shhield-ai/drophalo"
GITHUB = "gavin-k/draghalo"
API = f"https://api.cnb.cool/{REPOSITORY}/-/releases"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def open_url(url, *, method="GET", data=None, authenticated=False, headers=None):
    opener = urllib.request.build_opener(NoRedirect)
    for _ in range(6):
        parsed = urllib.parse.urlparse(url)
        if parsed.scheme != "https" or parsed.username or parsed.password or parsed.port not in (None, 443):
            raise ValueError("Untrusted transport address")
        request_headers = {"User-Agent": "DropHalo-release-mirror", **(headers or {})}
        if authenticated:
            if parsed.netloc != "api.cnb.cool":
                raise ValueError("Refusing to send credentials outside the CNB API")
            request_headers["Authorization"] = "Bearer " + os.environ["CNB_TOKEN"]
        try:
            return opener.open(urllib.request.Request(url, data=data, method=method, headers=request_headers), timeout=180)
        except urllib.error.HTTPError as error:
            if error.code not in (301, 302, 303, 307, 308) or method != "GET":
                raise
            url = urllib.parse.urljoin(url, error.headers["Location"])
            if urllib.parse.urlparse(url).hostname not in ("github.com", "api.github.com", "release-assets.githubusercontent.com", "cnb.cool", "asset.cnb.cool"):
                raise ValueError("Untrusted download redirect") from None
            authenticated = False
    raise ValueError("Too many redirects")


def api(method, url, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    with open_url(url, method=method, data=data, authenticated=True, headers={"Content-Type": "application/json", "Accept": "application/vnd.cnb.api+json"}) as response:
        body = response.read(4 * 1024 * 1024)
        return json.loads(body) if body else None


def download(url, path, *, authenticated=False, expected_size=None):
    digest = hashlib.sha256()
    total = 0
    with open_url(url, authenticated=authenticated) as response, path.open("wb") as output:
        while chunk := response.read(1024 * 1024):
            total += len(chunk)
            if total > (expected_size if expected_size is not None else 1024 * 1024 * 1024):
                raise ValueError("Download exceeds expected size")
            output.write(chunk)
            digest.update(chunk)
    if expected_size is not None and total != expected_size:
        raise ValueError("Download is truncated")
    return digest.hexdigest()


def assets_for(release):
    tag = release["tag_name"]
    if not re.fullmatch(r"v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", tag) or release.get("draft") or release["prerelease"]:
        raise ValueError("Only stable releases may be mirrored")
    version = tag[1:]
    names = {f"DropHalo-{version}-{arch}.{kind}" for arch in ("x64", "arm64") for kind in ("msi", "msix")}
    names.update((f"DropHalo-{version}.msixbundle", "SHA256SUMS.txt"))
    assets = {a["name"]: a for a in release["assets"] if a["name"] in names}
    if set(assets) != names or any(a["size"] <= 0 or a["size"] > 1024 * 1024 * 1024 for a in assets.values()):
        raise ValueError("Incomplete or oversized release")
    for name, asset in assets.items():
        expected = f"https://github.com/{GITHUB}/releases/download/{tag}/{name}"
        if asset["browser_download_url"] != expected:
            raise ValueError("Unexpected GitHub asset URL")
    return tag, assets


def mirror():
    if os.environ.get("CNB_REPO_SLUG") != REPOSITORY or not os.environ.get("CNB_TOKEN"):
        raise ValueError("Run this only in the official CNB repository's trusted pipeline")
    with open_url(f"https://api.github.com/repos/{GITHUB}/releases/latest") as response:
        upstream = json.load(response)
    tag, assets = assets_for(upstream)
    manifest = {"tag_name": tag, "prerelease": False, "draft": False,
                "assets": [{"name": name, "size": asset["size"]} for name, asset in sorted(assets.items())]}
    pointer = Path("updates/latest.json")
    if pointer.exists() and json.loads(pointer.read_text()) == manifest:
        # Re-check the published attachment identities, without transferring unchanged installers every hour.
        current = api("GET", f"{API}/tags/{tag}")
        if not current["draft"] and not current["prerelease"] and {a["name"]: a["size"] for a in current["assets"]} == {n: a["size"] for n, a in assets.items()}:
            print(f"{tag}: mirror already complete")
            return
    try:
        release = api("GET", f"{API}/tags/{tag}")
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
        release = api("POST", API, {"tag_name": tag, "target_commitish": tag, "name": upstream["name"], "body": upstream["body"], "draft": True, "prerelease": False, "make_latest": "false"})
    with tempfile.TemporaryDirectory(prefix="drophalo-mirror-") as temporary:
        directory = Path(temporary)
        sums = directory / "SHA256SUMS.txt"
        sums_hash = download(assets[sums.name]["browser_download_url"], sums, expected_size=assets[sums.name]["size"])
        checksums = {}
        for line in sums.read_text().splitlines():
            match = re.fullmatch(r"([a-fA-F0-9]{64})  ([^/\\]+)", line)
            if not match or match[2] in checksums:
                raise ValueError("Invalid or duplicate checksum entry")
            checksums[match[2]] = match[1].lower()
        if set(checksums) != set(assets) - {sums.name}:
            raise ValueError("Checksums do not describe all installers")
        checksums[sums.name] = sums_hash
        for name, asset in sorted(assets.items()):
            path = directory / name
            digest = sums_hash if name == sums.name else download(asset["browser_download_url"], path, expected_size=asset["size"])
            if digest != checksums[name] or asset.get("digest") not in (None, "sha256:" + digest):
                raise ValueError(f"GitHub checksum mismatch: {name}")
            existing = next((a for a in release["assets"] if a["name"] == name), None)
            if existing is None:
                upload = api("POST", f"{API}/{release['id']}/asset-upload-url", {"asset_name": name, "size": asset["size"], "ttl": 0, "overwrite": False})
                with path.open("rb") as body, open_url(upload["upload_url"], method="PUT", data=body, headers={"Content-Length": str(asset["size"]), "Content-Type": "application/octet-stream"}):
                    pass
                api("POST", upload["verify_url"])
            elif existing["size"] != asset["size"]:
                raise ValueError(f"Refusing to overwrite a different published asset: {name}")
            verified = download(f"{API}/download/{tag}/{name}", directory / "verify.tmp", authenticated=True, expected_size=asset["size"])
            if verified != digest:
                raise ValueError(f"CNB readback mismatch: {name}")
            print(f"Verified {name}: {digest}")
            path.unlink()
        api("PATCH", f"{API}/{release['id']}", {"draft": False, "prerelease": False, "make_latest": "true"})
    pointer.parent.mkdir(exist_ok=True)
    pointer.write_text(json.dumps(manifest, indent=2) + "\n")
    subprocess.run(["git", "add", str(pointer)], check=True)
    if subprocess.run(["git", "diff", "--cached", "--quiet"]).returncode:
        subprocess.run(["git", "commit", "-m", f"chore: publish CNB update feed for {tag}"], check=True)
    print(f"{tag}: all original signed assets verified; update feed ready to push")


def self_test():
    names = [f"DropHalo-0.1.6-{a}.{k}" for a in ("x64", "arm64") for k in ("msi", "msix")] + ["DropHalo-0.1.6.msixbundle", "SHA256SUMS.txt"]
    fixture = {"tag_name": "v0.1.6", "prerelease": False, "assets": [{"name": n, "size": 100, "browser_download_url": f"https://github.com/{GITHUB}/releases/download/v0.1.6/{n}"} for n in names]}
    assert len(assets_for(fixture)[1]) == 6
    for invalid in ({**fixture, "tag_name": "v0.1.6-beta"}, {**fixture, "prerelease": True}, {**fixture, "assets": fixture["assets"][:-1]}):
        try:
            assets_for(invalid)
        except ValueError:
            pass
        else:
            raise AssertionError("Accepted an invalid release")
    print("Release mirror validation self-test passed")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    else:
        mirror()
