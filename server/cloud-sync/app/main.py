from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import hmac
import json
import os
import secrets
import time
import uuid
import jwt
import oss2
from fastapi import Depends, FastAPI, Header, HTTPException, Request, Response
from fastapi.responses import FileResponse
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from pydantic import BaseModel, Field, field_validator
from pwdlib import PasswordHash
from pwdlib.hashers.argon2 import Argon2Hasher
from sqlalchemy import BigInteger, Boolean, Column, Float, Integer, String, Text, UniqueConstraint, create_engine, select, update
from sqlalchemy.orm import declarative_base, sessionmaker
from sqlalchemy.exc import IntegrityError
from starlette.concurrency import run_in_threadpool
from .config import settings
from .storage import Storage


config = settings()
engine = create_engine(config.database_url, connect_args={"check_same_thread": False, "timeout": 30} if config.database_url.startswith("sqlite") else {}, pool_pre_ping=True)
Session = sessionmaker(engine, expire_on_commit=False)
Base = declarative_base()
passwords = PasswordHash((Argon2Hasher(memory_cost=19456, time_cost=2, parallelism=1),))
dummy_hash = passwords.hash(secrets.token_urlsafe(32))
storage = Storage(config)
bearer = HTTPBearer(auto_error=False)


class User(Base):
    __tablename__ = "users"
    id = Column(String, primary_key=True, default=lambda: uuid.uuid4().hex)
    email = Column(String(254), unique=True, nullable=False)
    password = Column(Text, nullable=False)
    org = Column(String(80), nullable=False)
    admin = Column(Boolean, default=False, nullable=False)
    active = Column(Boolean, default=True, nullable=False)
    used_bytes = Column(BigInteger, default=0, nullable=False)


class SessionToken(Base):
    __tablename__ = "sessions"
    id = Column(String, primary_key=True)
    user_id = Column(String, nullable=False, index=True)
    token_hash = Column(String(64), nullable=False)
    expires = Column(Float, nullable=False)
    revoked = Column(Boolean, default=False, nullable=False)


class Invitation(Base):
    __tablename__ = "invitations"
    token_hash = Column(String(64), primary_key=True)
    org = Column(String(80), nullable=False)
    expires = Column(Float, nullable=False)
    remaining = Column(Integer, default=1, nullable=False)


class CloudFile(Base):
    __tablename__ = "files"
    id = Column(String, primary_key=True)
    user_id = Column(String, nullable=False, index=True)
    name = Column(String(240), nullable=False)
    sha256 = Column(String(64), nullable=False)
    size = Column(Integer, nullable=False)
    key = Column(Text, nullable=False)
    created = Column(Float, nullable=False)
    committed = Column(Boolean, default=False, nullable=False)
    __table_args__ = (UniqueConstraint("user_id", "sha256", name="user_file_hash"),)


class Preference(Base):
    __tablename__ = "preferences"
    user_id = Column(String, primary_key=True)
    version = Column(Integer, default=0, nullable=False)
    payload = Column(Text, nullable=False, default="{}")


class RateLimit(Base):
    __tablename__ = "rate_limits"
    key = Column(String(64), primary_key=True)
    count = Column(Integer, nullable=False)
    until = Column(Float, nullable=False)


@asynccontextmanager
async def lifespan(app):
    Base.metadata.create_all(engine)
    yield
    engine.dispose()


app = FastAPI(title="Ink Canvas Cloud Sync", version="1.0.0", lifespan=lifespan, docs_url=None, redoc_url=None)


@app.middleware("http")
async def security_headers(request, call_next):
    response = await call_next(request)
    response.headers["Cache-Control"] = "no-store"
    response.headers["X-Content-Type-Options"] = "nosniff"
    response.headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'"
    return response


def db():
    with Session() as session:
        yield session


def digest(value: str):
    return hashlib.sha256(value.encode()).hexdigest()


def rate_limit(session, request: Request, purpose: str, maximum=20):
    key = digest(f"{purpose}:{request.client.host}")
    now = time.time()
    row = session.get(RateLimit, key)
    if row is None:
        row = RateLimit(key=key, count=0, until=now + 60)
        session.add(row)
    if row.until < now:
        row.count, row.until = 0, now + 60
    row.count += 1
    try:
        session.commit()
    except IntegrityError:
        session.rollback()
        raise HTTPException(409, "Concurrent request, please retry")
    if row.count > maximum:
        raise HTTPException(429, "Too many attempts", headers={"Retry-After": "60"})


def current_user(credentials: HTTPAuthorizationCredentials = Depends(bearer), session=Depends(db)):
    try:
        claims = jwt.decode(credentials.credentials, config.signing_key, algorithms=["HS256"], audience="inkcanvas-sync", issuer="inkcanvas-cloud")
        token = session.get(SessionToken, claims["sid"])
        user = session.get(User, claims["sub"])
        if not token or token.revoked or token.user_id != claims["sub"] or token.expires <= time.time() or not user or not user.active:
            raise ValueError()
        return user
    except (jwt.InvalidTokenError, AttributeError, KeyError, ValueError):
        raise HTTPException(401, "Please sign in again")


def admin_user(user=Depends(current_user)):
    if not user.admin:
        raise HTTPException(403, "Administrator access required")
    return user


class Login(BaseModel):
    email: str = Field(min_length=3, max_length=254)
    password: str = Field(min_length=12, max_length=256)

    @field_validator("email")
    @classmethod
    def email_valid(cls, value):
        value = value.strip().lower()
        if "@" not in value or any(c.isspace() for c in value):
            raise ValueError("Invalid account address")
        return value


class Registration(Login):
    invitation: str = Field(min_length=24, max_length=256)


class Refresh(BaseModel):
    refresh_token: str = Field(min_length=32, max_length=256)


def issue(session, user):
    refresh = secrets.token_urlsafe(48)
    token = SessionToken(id=uuid.uuid4().hex, user_id=user.id, token_hash=digest(refresh), expires=time.time() + config.refresh_days * 86400)
    session.add(token)
    session.commit()
    access = jwt.encode({"sub": user.id, "sid": token.id, "iss": "inkcanvas-cloud", "aud": "inkcanvas-sync", "iat": int(time.time()), "exp": int(time.time() + config.access_minutes * 60)}, config.signing_key, algorithm="HS256")
    return {"access_token": access, "refresh_token": refresh, "expires_in": config.access_minutes * 60, "user": {"id": user.id, "email": user.email, "organization": user.org, "admin": user.admin}}


@app.get("/healthz")
def health():
    return {"status": "ok", "protocol": 1}


@app.post("/v1/auth/login")
def login(data: Login, request: Request, session=Depends(db)):
    rate_limit(session, request, "auth")
    user = session.scalar(select(User).where(User.email == data.email))
    valid = passwords.verify(data.password, user.password if user else dummy_hash)
    if not user or not valid or not user.active:
        raise HTTPException(401, "Invalid account or password")
    return issue(session, user)


@app.post("/v1/auth/register")
def register(data: Registration, request: Request, session=Depends(db)):
    rate_limit(session, request, "register", 10)
    invite = session.get(Invitation, digest(data.invitation))
    if not invite or invite.expires <= time.time() or invite.remaining < 1:
        raise HTTPException(403, "Invitation is invalid or expired")
    if session.scalar(select(User).where(User.email == data.email)):
        raise HTTPException(409, "Account already exists")
    consumed = session.execute(update(Invitation).where(Invitation.token_hash == invite.token_hash, Invitation.remaining > 0).values(remaining=Invitation.remaining - 1))
    if consumed.rowcount != 1:
        session.rollback()
        raise HTTPException(409, "Invitation already used")
    user = User(email=data.email, password=passwords.hash(data.password), org=invite.org)
    session.add(user)
    session.commit()
    return issue(session, user)


@app.post("/v1/auth/refresh")
def refresh(data: Refresh, request: Request, session=Depends(db)):
    rate_limit(session, request, "refresh", 30)
    token = session.scalar(select(SessionToken).where(SessionToken.token_hash == digest(data.refresh_token)))
    if not token or token.revoked or token.expires <= time.time():
        raise HTTPException(401, "Please sign in again")
    consumed = session.execute(update(SessionToken).where(SessionToken.id == token.id, SessionToken.revoked.is_(False)).values(revoked=True))
    user = session.get(User, token.user_id)
    if consumed.rowcount != 1 or not user or not user.active:
        session.rollback()
        raise HTTPException(401, "Please sign in again")
    return issue(session, user)


@app.post("/v1/auth/logout")
def logout(credentials: HTTPAuthorizationCredentials = Depends(bearer), user=Depends(current_user), session=Depends(db)):
    claims = jwt.decode(credentials.credentials, config.signing_key, algorithms=["HS256"], audience="inkcanvas-sync", issuer="inkcanvas-cloud")
    session.execute(update(SessionToken).where(SessionToken.id == claims["sid"]).values(revoked=True))
    session.commit()
    return {"ok": True}


@app.get("/v1/me")
def me(user=Depends(current_user)):
    return {"id": user.id, "email": user.email, "organization": user.org, "admin": user.admin}


class InviteRequest(BaseModel):
    count: int = Field(default=1, ge=1, le=200)
    valid_days: int = Field(default=7, ge=1, le=30)


@app.post("/v1/admin/invitations")
def invitations(data: InviteRequest, user=Depends(admin_user), session=Depends(db)):
    codes = [secrets.token_urlsafe(32) for _ in range(data.count)]
    for code in codes:
        session.add(Invitation(token_hash=digest(code), org=user.org, remaining=1, expires=time.time() + data.valid_days * 86400))
    session.commit()
    return {"invitations": codes}


class Upload(BaseModel):
    name: str = Field(min_length=1, max_length=200)
    sha256: str = Field(pattern=r"^[0-9a-f]{64}$")
    size: int = Field(gt=0, le=536870912)

    @field_validator("name")
    @classmethod
    def safe_name(cls, value):
        if not value.lower().endswith(".icink") or any(c in value for c in '/\\\0\r\n') or value in (".", ".."):
            raise ValueError("A whiteboard filename is required")
        return value


def file_info(item):
    return {"id": item.id, "name": item.name, "sha256": item.sha256, "size": item.size, "created_at": datetime.fromtimestamp(item.created, timezone.utc).isoformat()}


@app.post("/v1/files/uploads")
def begin_upload(data: Upload, request: Request, user=Depends(current_user), session=Depends(db)):
    rate_limit(session, request, "upload", 60)
    if data.size > config.max_file_bytes:
        raise HTTPException(413, "Whiteboard is too large")
    item = session.scalar(select(CloudFile).where(CloudFile.user_id == user.id, CloudFile.sha256 == data.sha256))
    if item and item.committed:
        return {"complete": True, "file": file_info(item)}
    if item and item.size != data.size:
        raise HTTPException(409, "Upload metadata differs from the reserved file")
    if item is None:
        reserved = session.execute(update(User).where(User.id == user.id, User.used_bytes + data.size <= config.user_quota_bytes)
                                   .values(used_bytes=User.used_bytes + data.size))
        if reserved.rowcount != 1:
            session.rollback()
            raise HTTPException(413, "Cloud storage quota reached")
        identity = uuid.uuid4().hex
        item = CloudFile(id=identity, user_id=user.id, name=data.name, sha256=data.sha256, size=data.size,
                         key=f"users/{user.id}/{identity}.icink", created=time.time(), committed=False)
        session.add(item)
    else:
        item.created = time.time()
        item.key = f"users/{user.id}/{uuid.uuid4().hex}.icink"
    try:
        session.commit()
    except IntegrityError:
        session.rollback()
        raise HTTPException(409, "Concurrent upload, please retry")
    url, headers = storage.url(item.key, "PUT", item.size)
    return {"id": item.id, "upload_url": url, "headers": headers, "complete": False}


def owned_file(session, identity, user):
    item = session.get(CloudFile, identity)
    if not item or item.user_id != user.id:
        raise HTTPException(404, "File not found")
    return item


@app.post("/v1/files/{identity}/commit")
def commit(identity: str, user=Depends(current_user), session=Depends(db)):
    item = owned_file(session, identity, user)
    if not item.committed:
        if item.created + 900 < time.time():
            raise HTTPException(410, "Upload expired")
        try:
            valid = storage.verify(item.key, item.size, item.sha256)
        except (OSError, oss2.exceptions.OssError):
            valid = False
        if not valid:
            raise HTTPException(409, "Uploaded content failed integrity validation")
        committed = session.execute(update(CloudFile).where(CloudFile.id == identity, CloudFile.key == item.key,
                                                          CloudFile.committed.is_(False)).values(committed=True))
        if committed.rowcount != 1:
            session.rollback()
            raise HTTPException(409, "Upload changed during verification, please retry")
        session.commit()
        session.refresh(item)
    return file_info(item)


@app.get("/v1/files")
def files(user=Depends(current_user), session=Depends(db), before: float | None = None, limit: int = 100):
    query = select(CloudFile).where(CloudFile.user_id == user.id, CloudFile.committed.is_(True))
    if before is not None:
        query = query.where(CloudFile.created < before)
    count = max(1, min(limit, 200))
    items = session.scalars(query.order_by(CloudFile.created.desc()).limit(count + 1)).all()
    has_more = len(items) > count
    items = items[:count]
    return {"files": [file_info(item) for item in items], "next_before": items[-1].created if has_more else None}


@app.get("/v1/files/{identity}/download")
def download(identity: str, user=Depends(current_user), session=Depends(db)):
    item = owned_file(session, identity, user)
    if not item.committed:
        raise HTTPException(404, "File not found")
    url, _ = storage.url(item.key, "GET")
    return {"download_url": url, "file": file_info(item)}


ALLOWED_CONFIG = {"appearance", "canvas", "gesture", "inkToShape", "behavior", "features"}


class Preferences(BaseModel):
    version: int = Field(ge=0)
    values: dict


@app.get("/v1/preferences")
def get_preferences(user=Depends(current_user), session=Depends(db)):
    row = session.get(Preference, user.id)
    return {"version": row.version if row else 0, "values": json.loads(row.payload) if row else {}}


@app.put("/v1/preferences")
def put_preferences(data: Preferences, user=Depends(current_user), session=Depends(db)):
    if set(data.values) - ALLOWED_CONFIG:
        raise HTTPException(422, "Only portable preferences may be synced")
    if any(not isinstance(value, dict) for value in data.values.values()):
        raise HTTPException(422, "Preference groups must be objects")
    payload = json.dumps(data.values, ensure_ascii=False)
    if len(payload.encode()) > 65536:
        raise HTTPException(413, "Preferences are too large")
    row = session.get(Preference, user.id)
    if row is None:
        if data.version != 0:
            raise HTTPException(409, "Preferences changed on another device")
        row = Preference(user_id=user.id, version=1, payload=payload)
        session.add(row)
    else:
        changed = session.execute(update(Preference).where(Preference.user_id == user.id, Preference.version == data.version)
                                  .values(version=Preference.version + 1, payload=payload))
        if changed.rowcount != 1:
            raise HTTPException(409, "Preferences changed on another device")
    try:
        session.commit()
    except IntegrityError:
        session.rollback()
        raise HTTPException(409, "Preferences changed on another device")
    session.refresh(row)
    return {"version": row.version, "values": json.loads(row.payload)}


def check_object_ticket(key, method, expires, signature, size):
    if config.storage != "local" or expires < time.time() or expires > time.time() + 610 or not hmac.compare_digest(storage.signature(key, method, expires, size), signature):
        raise HTTPException(403, "Object ticket is invalid or expired")


@app.put("/v1/objects/{key:path}")
async def local_upload(key: str, request: Request, expires: int, signature: str, size: int):
    check_object_ticket(key, "PUT", expires, signature, size)
    path = storage.local_path(key)
    if path.exists():
        raise HTTPException(409, "Object already exists")
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    await run_in_threadpool(path.parent.mkdir, parents=True, exist_ok=True)
    received = 0
    try:
        with temporary.open("wb") as output:
            async for chunk in request.stream():
                received += len(chunk)
                if received > size or received > config.max_file_bytes:
                    raise HTTPException(413, "Object is too large")
                await run_in_threadpool(output.write, chunk)
        if received != size:
            raise HTTPException(400, "Object size differs from the upload reservation")
        try:
            await run_in_threadpool(os.link, temporary, path)
        except FileExistsError:
            raise HTTPException(409, "Object already exists")
    finally:
        temporary.unlink(missing_ok=True)
    return Response(status_code=204)


@app.get("/v1/objects/{key:path}")
def local_download(key: str, expires: int, signature: str, size: int):
    check_object_ticket(key, "GET", expires, signature, size)
    path = storage.local_path(key)
    if not path.is_file():
        raise HTTPException(404)
    return FileResponse(path, media_type="application/octet-stream")


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("create-admin")
    parser.add_argument("--email", required=True)
    parser.add_argument("--organization", default="default")
    args = parser.parse_args()
    Base.metadata.create_all(engine)
    with Session() as session:
        if session.scalar(select(User).where(User.email == args.email.lower())):
            raise SystemExit("Account already exists")
        password = secrets.token_urlsafe(24)
        session.add(User(email=args.email.lower(), password=passwords.hash(password), org=args.organization, admin=True))
        session.commit()
        print(json.dumps({"email": args.email, "password": password}))
