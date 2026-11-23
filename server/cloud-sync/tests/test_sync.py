import hashlib
import os
import secrets
import tempfile
import time
from pathlib import Path
from urllib.parse import urlsplit

root = Path(tempfile.mkdtemp(prefix="inkcloud-test-"))
os.environ.update(INKCLOUD_SIGNING_KEY=secrets.token_urlsafe(64), INKCLOUD_DATABASE_URL=f"sqlite:///{root / 'cloud.db'}",
                  INKCLOUD_STORAGE="local", INKCLOUD_OBJECT_DIRECTORY=str(root / "objects"), INKCLOUD_PUBLIC_URL="https://testserver")
from fastapi.testclient import TestClient
from app.main import app, Session, User, Invitation, passwords, digest


def ticket_path(url):
    parsed = urlsplit(url)
    return parsed.path + "?" + parsed.query


def test_account_file_and_preferences_workflow():
    with TestClient(app, base_url="https://testserver") as client:
        assert client.get("/healthz").json()["protocol"] == 1
        assert client.get("/v1/files").status_code == 401
        invite = secrets.token_urlsafe(32)
        with Session() as session:
            session.add(User(email="admin@example.invalid", password=passwords.hash("admin-test-password-2026"), org="school", admin=True))
            session.add(Invitation(token_hash=digest(invite), org="school", remaining=1, expires=time.time() + 600))
            session.commit()
        credentials = {"email": "teacher@example.invalid", "password": "teacher-test-password-2026", "invitation": invite}
        registered = client.post("/v1/auth/register", json=credentials)
        assert registered.status_code == 200
        tokens = registered.json()
        auth = {"Authorization": "Bearer " + tokens["access_token"]}
        assert client.post("/v1/auth/register", json={**credentials, "email": "other@example.invalid"}).status_code == 403
        assert client.post("/v1/auth/login", json={"email": credentials["email"], "password": "incorrect-password"}).status_code == 401
        assert client.get("/v1/me", headers=auth).json()["email"] == credentials["email"]
        assert client.post("/v1/admin/invitations", json={"count": 2}, headers=auth).status_code == 403
        admin = client.post("/v1/auth/login", json={"email": "admin@example.invalid", "password": "admin-test-password-2026"}).json()
        admin_auth = {"Authorization": "Bearer " + admin["access_token"]}
        assert len(client.post("/v1/admin/invitations", json={"count": 3}, headers=admin_auth).json()["invitations"]) == 3

        data = b"PK\x03\x04whiteboard-archive-sample"
        sha = hashlib.sha256(data).hexdigest()
        request = {"name": "lesson.icink", "sha256": sha, "size": len(data)}
        upload = client.post("/v1/files/uploads", json=request, headers=auth).json()
        assert client.post("/v1/files/uploads", json={**request, "size": len(data) + 1}, headers=auth).status_code == 409
        assert client.put(ticket_path(upload["upload_url"]), content=data + b"extra").status_code == 413
        assert client.put(ticket_path(upload["upload_url"]).replace('size=' + str(len(data)), 'size=' + str(len(data) + 1)), content=data + b"x").status_code == 403
        assert client.put(ticket_path(upload["upload_url"]), content=data).status_code == 204
        assert client.put(ticket_path(upload["upload_url"]), content=data).status_code == 409
        assert client.post(f"/v1/files/{upload['id']}/commit", headers=auth).status_code == 200
        assert client.post("/v1/files/uploads", json=request, headers=auth).json()["complete"] is True
        listed = client.get("/v1/files", headers=auth).json()["files"]
        assert len(listed) == 1 and listed[0]["sha256"] == sha
        assert client.get("/v1/files", headers=admin_auth).json()["files"] == []
        assert client.get(f"/v1/files/{upload['id']}/download", headers=admin_auth).status_code == 404
        download = client.get(f"/v1/files/{upload['id']}/download", headers=auth).json()
        assert client.get(ticket_path(download["download_url"])).content == data
        assert client.get(ticket_path(download["download_url"]) + "tampered").status_code == 403
        bad = client.post("/v1/files/uploads", json={**request, "sha256": "0" * 64}, headers=auth).json()
        client.put(ticket_path(bad["upload_url"]), content=data)
        assert client.post(f"/v1/files/{bad['id']}/commit", headers=auth).status_code == 409
        assert client.post("/v1/files/uploads", json={**request, "name": "../escape.icink"}, headers=auth).status_code == 422
        assert client.post("/v1/files/uploads", json={**request, "size": 536870913}, headers=auth).status_code == 422

        prefs = client.get("/v1/preferences", headers=auth).json()
        assert prefs == {"version": 0, "values": {}}
        values = {"canvas": {"inkWidth": 4}, "appearance": {"theme": 2}}
        saved = client.put("/v1/preferences", json={"version": 0, "values": values}, headers=auth)
        assert saved.status_code == 200 and saved.json()["version"] == 1
        assert client.put("/v1/preferences", json={"version": 0, "values": values}, headers=auth).status_code == 409
        assert client.get("/v1/preferences", headers=auth).json()["values"] == values
        assert client.put("/v1/preferences", json={"version": 1, "values": {"ai": {"api_key": "must-not-sync"}}}, headers=auth).status_code == 422
        assert client.put("/v1/preferences", json={"version": 1, "values": {"canvas": None}}, headers=auth).status_code == 422

        renewed = client.post("/v1/auth/refresh", json={"refresh_token": tokens["refresh_token"]})
        assert renewed.status_code == 200
        assert client.post("/v1/auth/refresh", json={"refresh_token": tokens["refresh_token"]}).status_code == 401
        assert client.get("/v1/me", headers=auth).status_code == 401
        fresh_auth = {"Authorization": "Bearer " + renewed.json()["access_token"]}
        assert client.post("/v1/auth/logout", headers=fresh_auth).status_code == 200
        assert client.get("/v1/me", headers=fresh_auth).status_code == 401
