from functools import lru_cache
from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="INKCLOUD_", env_file=None)
    database_url: str = "sqlite:////data/cloud.db"
    signing_key: str = Field(min_length=48)
    public_url: str = "https://sync.example.invalid"
    storage: str = "oss"
    object_directory: str = "/data/objects"
    oss_endpoint: str = ""
    oss_region: str = ""
    oss_bucket: str = ""
    oss_access_key_id: str = ""
    oss_access_key_secret: str = ""
    access_minutes: int = 15
    refresh_days: int = 30
    max_file_bytes: int = 536870912
    user_quota_bytes: int = 10737418240
    allow_http_localhost: bool = False


@lru_cache
def settings():
    return Settings()
