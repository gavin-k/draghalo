import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import urllib.error

spec = importlib.util.spec_from_file_location("mirror", Path(__file__).resolve().parents[1] / "scripts/sync-cnb-release.py")
mirror = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mirror)


def fixture(tag="v0.1.6"):
    version = tag[1:]
    names = [f"DropHalo-{version}-{arch}.{kind}" for arch in ("x64", "arm64") for kind in ("msi", "msix")] + [f"DropHalo-{version}.msixbundle"]
    files = {name: ("original signed bytes " + name).encode() for name in names}
    files["SHA256SUMS.txt"] = "".join(f"{hashlib.sha256(data).hexdigest()}  {name}\n" for name, data in sorted(files.items())).encode()
    release = {"id": int(version.split('.')[-1]), "tag_name": tag, "draft": False, "prerelease": False, "name": tag, "body": "notes", "assets": [
        {"id": i + 100, "name": name, "size": len(data), "digest": "sha256:" + hashlib.sha256(data).hexdigest(), "browser_download_url": f"https://github.com/{mirror.GITHUB}/releases/download/{tag}/{name}"}
        for i, (name, data) in enumerate(sorted(files.items()))]}
    return release, files


class MirrorTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.previous = Path.cwd()
        os.chdir(self.temporary.name)
        self.work = Path("work")
        self.work.mkdir()
        self.upstream, self.files = fixture()
        self.cnb = {"id": "release-6", "draft": True, "prerelease": False, "is_latest": False, "name": "v0.1.6", "body": "notes", "assets": []}
        self.uploaded = {}
        self.operations = []

    def tearDown(self):
        os.chdir(self.previous)
        self.temporary.cleanup()

    def api(self, method, url, payload=None):
        self.operations.append((method, url, payload))
        if method == "GET":
            return copy.deepcopy(self.cnb)
        if "asset-upload-url" in url:
            return {"upload_url": "https://upload.invalid/" + payload["asset_name"], "verify_url": mirror.API + "/verify"}
        if method == "PATCH":
            self.cnb.update(payload)
            return copy.deepcopy(self.cnb)

    def open_upload(self, url, *, method="GET", data=None, **kwargs):
        self.assertEqual(method, "PUT")
        name = url.rsplit('/', 1)[-1]
        body = data.read()
        self.uploaded[name] = body
        self.cnb["assets"].append({"id": "asset-" + name, "name": name, "size": len(body), "hash_algo": "sha256", "hash_value": hashlib.sha256(body).hexdigest()})
        return io.BytesIO()

    def download(self, url, path, *, authenticated=False, expected_size=None):
        name = url.rsplit('/', 1)[-1]
        body = self.files[name] if "github.com" in url else self.uploaded[name]
        self.assertEqual(len(body), expected_size)
        path.write_bytes(body)
        return hashlib.sha256(body).hexdigest()

    def run_mirror(self):
        with patch.object(mirror, "api", side_effect=self.api), patch.object(mirror, "open_url", side_effect=self.open_upload), patch.object(mirror, "download", side_effect=self.download), patch.object(mirror, "github", return_value=copy.deepcopy(self.upstream)):
            return mirror.mirror_release(self.upstream, self.work)

    def test_receipt_requires_all_six_files_read_back_and_does_not_promote_history(self):
        result = self.run_mirror()
        receipt = json.loads(Path("updates/releases/v0.1.6.json").read_text())
        self.assertEqual(mirror.upstream_identity(receipt), result)
        self.assertEqual(len(receipt["cnb_assets"]), 6)
        self.assertFalse(self.cnb["draft"])
        self.assertNotIn("true", [p.get("make_latest") for m, u, p in self.operations if m == "PATCH"])
        self.assertFalse(Path("updates/latest.json").exists())

    def test_same_size_cnb_replacement_cannot_reuse_or_publish_receipt(self):
        result = self.run_mirror()
        receipt = json.loads(Path("updates/releases/v0.1.6.json").read_text())
        target = next(name for name in self.files if name.endswith(".msi"))
        self.uploaded[target] = b"!" + self.uploaded[target][1:]
        asset = next(a for a in self.cnb["assets"] if a["name"] == target)
        asset["hash_value"] = hashlib.sha256(self.uploaded[target]).hexdigest()
        self.assertFalse(mirror.reusable(receipt, result, self.cnb))
        Path("updates/releases/v0.1.6.json").unlink()
        with self.assertRaisesRegex(ValueError, "CNB readback mismatch"):
            self.run_mirror()
        self.assertFalse(Path("updates/releases/v0.1.6.json").exists())

    def test_unchanged_full_hash_receipt_avoids_large_redownloads(self):
        self.run_mirror()
        with patch.object(mirror, "download", side_effect=AssertionError("unnecessary download")), patch.object(mirror, "api", side_effect=self.api):
            mirror.mirror_release(self.upstream, self.work)

    def test_missing_platform_hash_cannot_reuse_receipt(self):
        result = self.run_mirror()
        receipt = json.loads(Path("updates/releases/v0.1.6.json").read_text())
        for key in ("id", "hash_algo", "hash_value"):
            modified = copy.deepcopy(self.cnb)
            modified["assets"][0].pop(key)
            self.assertFalse(mirror.reusable(receipt, result, modified))

    def test_same_size_github_reupload_changes_identity(self):
        original = mirror.identity(self.upstream, self.work)
        changed = copy.deepcopy(self.upstream)
        changed["assets"][0]["id"] += 10000
        changed["assets"][0]["digest"] = "sha256:" + "0" * 64
        self.assertNotEqual(mirror.identity(changed, self.work), original)

    def test_missing_github_digest_uses_original_checksum_bytes(self):
        expected = mirror.identity(self.upstream, self.work)
        changed = copy.deepcopy(self.upstream)
        changed["assets"][0]["digest"] = None
        with patch.object(mirror, "download", side_effect=self.download):
            self.assertEqual(mirror.identity(changed, self.work), expected)

    def test_github_change_during_copy_cannot_publish(self):
        changed = copy.deepcopy(self.upstream)
        changed["assets"][0]["id"] += 1
        with patch.object(mirror, "api", side_effect=self.api), patch.object(mirror, "open_url", side_effect=self.open_upload), patch.object(mirror, "download", side_effect=self.download), patch.object(mirror, "github", return_value=changed):
            with self.assertRaisesRegex(ValueError, "changed during mirroring"):
                mirror.mirror_release(self.upstream, self.work)
        self.assertTrue(self.cnb["draft"])
        self.assertFalse(Path("updates/releases/v0.1.6.json").exists())

    def test_published_github_bytes_are_immutable_even_at_same_length(self):
        expected = mirror.identity(self.upstream, self.work)
        directory = Path("frozen")
        directory.mkdir()
        for name, data in self.files.items():
            (directory / name).write_bytes(data)
        mirror.verify_local(expected, directory)
        path = next(directory.glob("*.msi"))
        path.write_bytes(b"!" + path.read_bytes()[1:])
        with self.assertRaisesRegex(ValueError, "Refusing to replace published bytes"):
            mirror.verify_local(expected, directory)

    def test_duplicate_or_extra_assets_are_rejected(self):
        for extra in (self.upstream["assets"][0], {**self.upstream["assets"][0], "name": "unexpected.exe"}):
            release = copy.deepcopy(self.upstream)
            release["assets"].append(extra)
            with self.assertRaises(ValueError):
                mirror.assets_for(release)

    def test_multiple_new_releases_are_all_mirrored_and_only_latest_is_activated(self):
        newer, _ = fixture("v0.1.7")
        mirrored = []
        def one(release, directory):
            mirrored.append(release["tag_name"])
            return mirror.identity(release, directory)
        def github(path):
            return [newer, self.upstream] if path.startswith("?") else newer
        with patch.dict(os.environ, {"CNB_REPO_SLUG": mirror.REPOSITORY, "CNB_TOKEN": "fixture"}), patch.object(mirror, "github", side_effect=github), patch.object(mirror, "mirror_release", side_effect=one), patch.object(mirror, "api", side_effect=self.api), patch.object(mirror.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)):
            mirror.mirror()
        self.assertEqual(mirrored, ["v0.1.6", "v0.1.7"])
        self.assertEqual(json.loads(Path("updates/latest.json").read_text())["tag_name"], "v0.1.7")
        self.assertEqual([p["make_latest"] for m, u, p in self.operations if m == "PATCH"], ["true"])

    def test_verification_accepts_target_when_latest_already_advanced(self):
        expected = mirror.identity(self.upstream, self.work)
        latest, _ = fixture("v0.1.7")
        canonical = mirror.identity(latest, self.work)
        def github(path):
            return latest if path == "/latest" else self.upstream
        def opened(url):
            return io.BytesIO(json.dumps(canonical if url.endswith("/latest.json") else expected).encode())
        self.uploaded = dict(self.files)
        with patch.object(mirror, "github", side_effect=github), patch.object(mirror, "open_url", side_effect=opened), patch.object(mirror, "download", side_effect=self.download):
            mirror.verify("v0.1.6")

    def test_verification_refuses_false_receipt_and_corrupt_public_bytes(self):
        expected = mirror.identity(self.upstream, self.work)
        wrong = {**expected, "github_release_id": -1}
        with patch.object(mirror, "github", return_value=self.upstream), patch.object(mirror, "open_url", return_value=io.BytesIO(json.dumps(wrong).encode())):
            with self.assertRaisesRegex(RuntimeError, "not ready"):
                mirror.verify("v0.1.6")
        with patch.object(mirror, "github", return_value=self.upstream), patch.object(mirror, "open_url", return_value=io.BytesIO(json.dumps(expected).encode())), patch.object(mirror, "download", return_value="0" * 64):
            with self.assertRaisesRegex(ValueError, "published bytes differ"):
                mirror.verify("v0.1.6")

    def test_invalid_verification_tag_never_connects(self):
        with patch.object(mirror, "github", side_effect=AssertionError("unexpected network")):
            with self.assertRaises(ValueError):
                mirror.verify("../../other")

    def test_anonymous_tag_quota_can_use_the_same_official_listing(self):
        failure = urllib.error.HTTPError(mirror.GITHUB_API + "/tags/v0.1.6", 403, "rate limit exceeded", {}, None)
        self.addCleanup(failure.close)
        with patch.dict(os.environ, {"GH_TOKEN": ""}), patch.object(mirror, "open_url", side_effect=[failure, io.BytesIO(json.dumps([self.upstream]).encode())]) as opened:
            self.assertEqual(mirror.github("/tags/v0.1.6"), self.upstream)
            self.assertEqual(opened.call_args.args[0], mirror.GITHUB_API + "?per_page=100&page=1")

    def test_quota_fallback_cannot_invent_a_missing_or_authenticated_release(self):
        failure = urllib.error.HTTPError(mirror.GITHUB_API + "/tags/v0.1.6", 403, "rate limit exceeded", {}, None)
        self.addCleanup(failure.close)
        with patch.dict(os.environ, {"GH_TOKEN": ""}), patch.object(mirror, "open_url", side_effect=[failure, io.BytesIO(b"[]")]):
            with self.assertRaises(urllib.error.HTTPError):
                mirror.github("/tags/v0.1.6")
        with patch.dict(os.environ, {"GH_TOKEN": "fixture"}), patch.object(mirror, "open_url", side_effect=failure) as opened:
            with self.assertRaises(urllib.error.HTTPError):
                mirror.github("/tags/v0.1.6")
            self.assertEqual(opened.call_count, 1)


if __name__ == "__main__":
    unittest.main()
