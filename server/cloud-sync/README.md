# Ink Canvas 云同步服务

FastAPI 提供账号与同步控制接口，白板文件通过短时签名 URL 直接上传至私有 OSS，数据库仅保存账号、版本、哈希和文件索引。客户端默认没有配置服务地址，部署实例的地址和密钥由部署者另行交付。

## 部署

将 `environment.example` 复制到代码仓库以外的受限目录，生成至少 48 字符的随机签名密钥，填写私有 OSS Bucket 及只能访问该 Bucket 的 RAM 凭据。环境文件只允许服务管理员读取。

设置 `INKCLOUD_ENV_FILE` 为该文件的绝对路径，执行 `docker compose up -d --build`。API 只绑定回环地址，使用现有反向代理提供 HTTPS。代理限制请求体和连接速率，生产环境禁止直接暴露 API 端口。

使用 `docker compose exec api python -m app.main create-admin --email admin@example.invalid --organization school` 创建首个管理员。该命令生成的随机密码只显示一次，应写入密码管理器。通过 `/v1/admin/invitations` 生成一次性邀请，为各位教师分别注册个人账号。

个人端在插件设置中填写 HTTPS 服务地址，使用账号密码登录。学校可向 `%PROGRAMDATA%\InkCanvas\cloud-policy.json` 分发只包含 `serviceUrl` 和 `syncIntervalMinutes` 的部署配置。该文件不含密码、令牌或管理员凭据，插件仍要求个人登录。

## 同步行为

白板保存后以 SHA-256 去重，文件名相同但内容不同会保留独立版本。客户端使用流式上传、完整性校验和后台重试。恢复文件创建本地新副本。配置通过版本号执行乐观并发控制，发生冲突时由用户下载云端配置确认，避免覆盖其他设备的配置。

可同步配置限定为外观、画笔、手势、墨迹识别和演示偏好，设备显示器、路径、AI 连接与密钥、插件数据、名单和启动行为均保留在各设备。

## 运维与安全

- 访问令牌 15 分钟有效，刷新令牌轮换，服务端只保存刷新令牌哈希。注销撤销当前会话。
- 密码使用 Argon2，邀请限时单次使用。对象按账号隔离，提交前流式核验大小与 SHA-256。OSS V4 上传签名绑定文件大小、内容类型、禁止覆盖和服务端 AES256 加密参数，避免绕过单文件配额。[OSS V4 签名文档](https://www.alibabacloud.com/help/en/oss/developer-reference/add-signatures-to-urls)
- 默认单文件上限 512 MiB、每账号 10 GiB。SQLite 适用于单实例小规模部署。多副本部署使用 PostgreSQL，并在网关集中限速。
- 定期备份数据库和 OSS，设置 Bucket 版本与生命周期策略以符合学校保留要求。数据库恢复时应一并核对对象索引。
- 不向日志写入口令、令牌、签名 URL 或部署环境。提交前扫描环境文件和部署报告，生产配置保存在仓库外。

使用 `INKCLOUD_STORAGE=local` 可运行离线集成测试，生产部署使用 OSS。协议通过 `/healthz` 返回版本号 1。
