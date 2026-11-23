from pathlib import Path
import hashlib
import hmac
import time
from urllib.parse import quote, urlsplit
import oss2
from .config import Settings


class Storage:
    def __init__(self, settings: Settings):
        self.settings = settings
        self.bucket = None
        if settings.storage == "oss":
            if not settings.oss_bucket or not settings.oss_endpoint:
                raise ValueError("OSS storage configuration is required")
            endpoint_region = (urlsplit(settings.oss_endpoint).hostname or "").split(".")[0]
            region = settings.oss_region or endpoint_region.removeprefix("oss-").removesuffix("-internal")
            if not region or not (settings.oss_region or endpoint_region.startswith("oss-")):
                raise ValueError("OSS region is required for V4 signatures")
            self.bucket = oss2.Bucket(
                oss2.AuthV4(settings.oss_access_key_id, settings.oss_access_key_secret),
                settings.oss_endpoint, settings.oss_bucket, region=region)
        elif settings.storage != "local":
            raise ValueError("Unsupported storage backend")

    def local_path(self, key: str) -> Path:
        root = Path(self.settings.object_directory).resolve()
        path = (root / key).resolve()
        if not path.is_relative_to(root):
            raise ValueError("Invalid object path")
        return path

    def signature(self, key: str, method: str, expires: int, size: int = 0) -> str:
        return hmac.new(self.settings.signing_key.encode(), f"{method}\n{key}\n{expires}\n{size}".encode(), hashlib.sha256).hexdigest()

    def url(self, key: str, method: str, size: int = 0) -> tuple[str, dict]:
        if self.bucket:
            headers = {"Content-Type": "application/octet-stream", "Content-Length": str(size),
                       "x-oss-server-side-encryption": "AES256", "x-oss-forbid-overwrite": "true"} if method == "PUT" else {}
            additional = {"content-length"} if method == "PUT" else None
            return self.bucket.sign_url(method, key, 600, headers=headers, slash_safe=True, additional_headers=additional), headers
        expires = int(time.time()) + 600
        signature = self.signature(key, method, expires, size)
        return f"{self.settings.public_url}/v1/objects/{quote(key)}?expires={expires}&size={size}&signature={signature}", {}

    def verify(self, key: str, expected_size: int, expected_hash: str) -> bool:
        digest = hashlib.sha256()
        size = 0
        if self.bucket:
            metadata = self.bucket.head_object(key)
            if metadata.content_length != expected_size:
                return False
            stream = self.bucket.get_object(key)
        else:
            path = self.local_path(key)
            if not path.exists() or path.stat().st_size != expected_size:
                return False
            stream = path.open("rb")
        try:
            while data := stream.read(1024 * 1024):
                size += len(data)
                digest.update(data)
                if size > expected_size:
                    return False
        finally:
            stream.close()
        return size == expected_size and hmac.compare_digest(digest.hexdigest(), expected_hash)
