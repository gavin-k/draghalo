#!/usr/bin/env python3
"""Mirror original GitHub assets; a CNB job's short-lived token never leaves the API host."""
import hashlib
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

REPOSITORY = "shhield-ai/drophalo"
GITHUB = "gavin-k/draghalo"
API = f"https://api.cnb.cool/{REPOSITORY}/-/releases"
GITHUB_API = f"https://api.github.com/repos/{GITHUB}/releases"
RAW = f"https://cnb.cool/{REPOSITORY}/-/git/raw/main/updates"
MIN_VERSION = (0, 1, 6)  # Earlier releases predate the automatic updater; their existing mirrors stay untouched.
STABLE_TAG = r"v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def open_url(url, *, method="GET", data=None, authenticated=False, headers=None):
    opener = urllib.request.build_opener(NoRedirect)
    for _ in range(6):
        parsed = urllib.parse.urlparse(url)
        if parsed.scheme != "https" or parsed.username or parsed.password or parsed.port not in (None, 443):
            raise ValueError("Untrusted transport address")
        request_headers = {"User-Agent": "DropHalo-release-mirror", "Cache-Control": "no-cache", **(headers or {})}
        if authenticated:
            token = {"api.cnb.cool": "CNB_TOKEN", "api.github.com": "GH_TOKEN"}.get(parsed.netloc)
            if token is None:
                raise ValueError("Refusing to send credentials outside the API host")
            request_headers["Authorization"] = "Bearer " + os.environ[token]
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


def assets_for(release, *, stable=True):
    tag = release["tag_name"]
    pattern = STABLE_TAG + (r"(?:-[A-Za-z0-9.-]+)?" if not stable else "")
    if not re.fullmatch(pattern, tag) or release.get("draft") or stable and release["prerelease"]:
        raise ValueError("Only stable releases may be mirrored")
    version = tag[1:]
    names = {f"DropHalo-{version}-{arch}.{kind}" for arch in ("x64", "arm64") for kind in ("msi", "msix")}
    names.update((f"DropHalo-{version}.msixbundle", "SHA256SUMS.txt"))
    assets = {a["name"]: a for a in release["assets"]}
    if len(release["assets"]) != len(names) or set(assets) != names or any(a["size"] <= 0 or a["size"] > 1024 * 1024 * 1024 for a in assets.values()):
        raise ValueError("Incomplete or oversized release")
    for name, asset in assets.items():
        expected = f"https://github.com/{GITHUB}/releases/download/{tag}/{name}"
        if asset["browser_download_url"] != expected:
            raise ValueError("Unexpected GitHub asset URL")
    return tag, assets


def github(path):
    with open_url(GITHUB_API + path, authenticated=bool(os.environ.get("GH_TOKEN"))) as response:
        return json.load(response)


def checksums(path, names):
    result = {}
    for line in path.read_text().splitlines():
        match = re.fullmatch(r"([a-fA-F0-9]{64})  ([^/\\]+)", line)
        if not match or match[2] in result:
            raise ValueError("Invalid or duplicate checksum entry")
        result[match[2]] = match[1].lower()
    if set(result) != set(names) - {"SHA256SUMS.txt"}:
        raise ValueError("Checksums do not describe all installers")
    return result


def identity(release, directory, *, stable=True):
    tag, assets = assets_for(release, stable=stable)
    digests = {name: a.get("digest", "") or "" for name, a in assets.items()}
    if all(re.fullmatch(r"sha256:[a-fA-F0-9]{64}", value) for value in digests.values()):
        hashes = {name: value[7:].lower() for name, value in digests.items()}
    else:
        sums = directory / "SHA256SUMS.txt"
        sums_hash = download(assets[sums.name]["browser_download_url"], sums, expected_size=assets[sums.name]["size"])
        hashes = checksums(sums, assets)
        hashes[sums.name] = sums_hash
        for name, digest in digests.items():
            if digest and digest.lower() != "sha256:" + hashes[name]:
                raise ValueError(f"GitHub digest disagrees with checksums: {name}")
    return {"tag_name": tag, "draft": False, "prerelease": release["prerelease"], "github_release_id": release["id"],
            "assets": [{"name": name, "size": a["size"], "sha256": hashes[name], "github_asset_id": a["id"]} for name, a in sorted(assets.items())]}


def upstream_identity(receipt):
    return {key: value for key, value in receipt.items() if key != "cnb_assets"}


def cnb_identity(release):
    return sorted(({key: a.get(key) for key in ("name", "size", "id", "hash_algo", "hash_value")} for a in (release.get("assets") or [])), key=lambda a: a["name"])


def reusable(receipt, expected, release):
    fingerprints = cnb_identity(release)
    # This caches an earlier full SHA-256 readback, rather than treating equal lengths as equal files.
    complete = len(fingerprints) == 6 and all(a["id"] and str(a["hash_algo"]).lower() in ("sha256", "sha1", "md5")
        and re.fullmatch({"sha256": r"[a-fA-F0-9]{64}", "sha1": r"[a-fA-F0-9]{40}", "md5": r"[a-fA-F0-9]{32}"}[str(a["hash_algo"]).lower()], a["hash_value"] or "") for a in fingerprints)
    return complete and not release["draft"] and not release["prerelease"] and upstream_identity(receipt) == expected and receipt.get("cnb_assets") == fingerprints


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)


def mirror_release(upstream, directory):
    expected = identity(upstream, directory)
    tag, assets = assets_for(upstream)
    pointer = Path(f"updates/releases/{tag}.json")
    try:
        release = api("GET", f"{API}/tags/{tag}")
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
        release = api("POST", API, {"tag_name": tag, "target_commitish": tag, "name": upstream["name"], "body": upstream["body"], "draft": True, "prerelease": False, "make_latest": "false"})
    receipt = json.loads(pointer.read_text()) if pointer.exists() else {}
    if not reusable(receipt, expected, release):
        sums = directory / "SHA256SUMS.txt"
        sums_hash = download(assets[sums.name]["browser_download_url"], sums, expected_size=assets[sums.name]["size"])
        hashes = checksums(sums, assets)
        hashes[sums.name] = sums_hash
        if hashes != {a["name"]: a["sha256"] for a in expected["assets"]}:
            raise ValueError("GitHub checksum manifest changed")
        for name, asset in sorted(assets.items()):
            path = directory / name
            digest = sums_hash if name == sums.name else download(asset["browser_download_url"], path, expected_size=asset["size"])
            if digest != hashes[name]:
                raise ValueError(f"GitHub checksum mismatch: {name}")
            existing = next((a for a in (release.get("assets") or []) if a["name"] == name), None)
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
        if identity(github(f"/tags/{tag}"), directory) != expected:
            raise ValueError("GitHub release changed during mirroring")
        release = api("GET", f"{API}/tags/{tag}")
        if {a["name"] for a in (release.get("assets") or [])} != set(assets) or len(release.get("assets") or []) != len(assets):
            raise ValueError("Unexpected CNB release attachment set")
        write_json(pointer, {**expected, "cnb_assets": cnb_identity(release)})
    metadata = {"name": upstream["name"], "body": upstream["body"], "draft": False, "prerelease": False}
    if release["draft"]:
        metadata["make_latest"] = "false"
    if release["draft"] or any(release.get(k) != metadata[k] for k in ("name", "body")):
        api("PATCH", f"{API}/{release['id']}", metadata)
    return expected


def mirror():
    if os.environ.get("CNB_REPO_SLUG") != REPOSITORY or not os.environ.get("CNB_TOKEN"):
        raise ValueError("Run this only in the official CNB repository's trusted pipeline")
    releases = {}
    page = 1
    while True:
        items = github(f"?per_page=100&page={page}")
        for release in items:
            tag = release["tag_name"]
            if not release["draft"] and not release["prerelease"] and re.fullmatch(STABLE_TAG, tag) and tuple(map(int, tag[1:].split('.'))) >= MIN_VERSION:
                releases[tag] = release
        if len(items) < 100:
            break
        page += 1
    latest = github("/latest")
    releases[latest["tag_name"]] = latest
    with tempfile.TemporaryDirectory(prefix="drophalo-mirror-") as temporary:
        directory = Path(temporary)
        verified = {tag: mirror_release(release, directory) for tag, release in sorted(releases.items(), key=lambda pair: tuple(map(int, pair[0][1:].split('.'))))}
        latest = github("/latest")
        expected = identity(latest, directory)
        tag = latest["tag_name"]
        if verified.get(tag) == expected:
            current = api("GET", f"{API}/tags/{tag}")
            if not current.get("is_latest"):
                api("PATCH", f"{API}/{current['id']}", {"make_latest": "true"})
            write_json(Path("updates/latest.json"), expected)
        else:
            print("Latest changed during synchronization; the next run will finish promotion")
    subprocess.run(["git", "add", "updates"], check=True)
    if subprocess.run(["git", "diff", "--cached", "--quiet"]).returncode:
        subprocess.run(["git", "commit", "-m", f"chore: synchronize verified CNB releases through {tag}"], check=True)
    print(f"{len(verified)} stable releases verified; receipts and update feed ready to push")


def verify_local(expected, directory):
    if {p.name for p in directory.iterdir() if p.is_file()} != {a["name"] for a in expected["assets"]}:
        raise ValueError("Local release files are incomplete or unexpected")
    for asset in expected["assets"]:
        path = directory / asset["name"]
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if path.stat().st_size != asset["size"] or digest != asset["sha256"]:
            raise ValueError(f"Refusing to replace published bytes: {asset['name']}; publish a new version")


def verify(tag, assets_directory=None, wait_seconds=0, *, existing_only=False):
    if wait_seconds < 0 or not re.fullmatch(STABLE_TAG + (r"(?:-[A-Za-z0-9.-]+)?" if existing_only else ""), tag):
        raise ValueError("Invalid release verification request")
    deadline = time.monotonic() + wait_seconds
    with tempfile.TemporaryDirectory(prefix="drophalo-verify-") as temporary:
        directory = Path(temporary)
        expected = identity(github(f"/tags/{tag}"), directory, stable=not existing_only)
        if expected["tag_name"] != tag:
            raise ValueError("GitHub returned the wrong release")
        if assets_directory:
            verify_local(expected, assets_directory)
        if existing_only:
            print(f"{tag}: existing GitHub release bytes match; no overwrite needed")
            return
        while True:
            try:
                with open_url(f"{RAW}/releases/{tag}.json") as response:
                    receipt = json.load(response)
                if upstream_identity(receipt) != expected:
                    raise ValueError("CNB receipt does not match the frozen GitHub release")
                break
            except (urllib.error.URLError, ValueError) as error:
                if time.monotonic() >= deadline:
                    raise RuntimeError(f"{tag}: CNB mirror not ready; release verification failed") from error
                time.sleep(min(15, deadline - time.monotonic()))
        for asset in expected["assets"]:
            digest = download(f"https://cnb.cool/{REPOSITORY}/-/releases/download/{tag}/{asset['name']}", directory / "verify.tmp", expected_size=asset["size"])
            if digest != asset["sha256"]:
                raise ValueError(f"CNB published bytes differ: {asset['name']}")
            print(f"Verified public CNB {asset['name']}: {digest}")
        if identity(github(f"/tags/{tag}"), directory) != expected:
            raise ValueError("GitHub assets were replaced during release verification")
        while True:
            try:
                canonical = identity(github("/latest"), directory)
                with open_url(f"{RAW}/latest.json") as response:
                    current = json.load(response)
                if current != canonical:
                    raise ValueError("The latest update pointers have not converged")
                break
            except (urllib.error.URLError, ValueError) as error:
                if time.monotonic() >= deadline:
                    raise RuntimeError("Latest update pointers differ; release verification failed") from error
                time.sleep(min(15, deadline - time.monotonic()))
        print(f"{tag}: both update sources have the same six files and SHA-256 hashes")


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
        parser = argparse.ArgumentParser(description=__doc__)
        operation = parser.add_mutually_exclusive_group()
        operation.add_argument("--verify", metavar="TAG")
        operation.add_argument("--check-existing", metavar="TAG")
        parser.add_argument("--assets-dir", type=Path)
        parser.add_argument("--wait", type=int, default=0)
        args = parser.parse_args()
        if args.verify or args.check_existing:
            verify(args.verify or args.check_existing, args.assets_dir, args.wait, existing_only=bool(args.check_existing))
        else:
            mirror()
