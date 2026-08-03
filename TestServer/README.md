# ProbeLoom TestServer

一个只用于本机调试 ProbeLoom 的 Spring Boot HTTP 靶场。它不依赖数据库或外部服务，默认监听：

```text
http://localhost:5080
```

## 启动

Windows：

```powershell
.\gradlew.bat bootRun
```

macOS / Linux：

```bash
./gradlew bootRun
```

验证服务：

```text
GET http://localhost:5080/api/v1/health
```

## 推荐的组合式 Route 配置

在 ProbeLoom 中可以这样验证路径来源：

| 来源 | 值 |
| --- | --- |
| Environment Base URL | `http://localhost:5080` |
| Project Route Part：API Prefix | `/api` |
| Project Route Part：API Version | `/v1` |
| Group Route Prefix | `/diagnostics` |
| Endpoint Route | `/echo/{resourceId}` |
| Path Parameter | `resourceId = user 42` |
| Query Parameter | `tag = spring boot` |

最终地址应为：

```text
http://localhost:5080/api/v1/diagnostics/echo/user%2042?tag=spring%20boot
```

Echo 支持 GET、POST、PUT、PATCH、DELETE、HEAD 和 OPTIONS，并返回实际收到的 Method、Path、Query、Headers 和 Body。

## 测试接口

| Method | Route | 用途 |
| --- | --- | --- |
| GET | `/api/v1/health` | 健康检查与普通 JSON |
| 常用 Method | `/api/v1/echo/{resourceId}` | Echo 请求各组成部分 |
| 常用 Method | `/api/v1/diagnostics/echo/{resourceId}` | 验证 Group Prefix |
| GET | `/api/v1/delay/{milliseconds}` | 超时和主动取消，最大 60000 ms |
| 任意 | `/api/v1/status/{statusCode}` | 返回指定的 200–599 状态码 |
| GET | `/api/v1/response/json?name=ProbeLoom` | 嵌套 JSON 和 Unicode |
| GET | `/api/v1/response/text` | 多行纯文本 |
| GET | `/api/v1/response/html` | HTML 内容 |
| GET | `/api/v1/response/empty` | 204 空响应 |
| GET | `/api/v1/response/binary` | 256 bytes 二进制 |
| GET | `/api/v1/response/invalid-json` | JSON Content-Type 下的损坏内容 |
| GET | `/api/v1/response/large?kilobytes=6144` | 大响应与截断，最大 10240 KiB |
| GET | `/api/v1/response/headers` | 自定义 Response Headers |
| GET | `/api/v1/redirect/start` | 可重复的 302 → 307 → 200 重定向链 |
| GET | `/api/v1/redirect/loop-a` | `loop-a` / `loop-b` 重定向循环 |
| POST | `/api/v1/auth/login` | 使用固定测试账号登录并签发 Token |
| POST | `/api/v1/auth/refresh` | 使用一次性 Refresh Token 换取新会话 |
| GET | `/api/v1/auth/protected` | 校验 Bearer Access Token 与过期状态 |

## 认证流程

登录账号固定为 `developer` / `probe`。这是本机测试数据，不应复用为真实凭据。

```json
POST /api/v1/auth/login
{
  "username": "developer",
  "password": "probe",
  "expiresInSeconds": 120
}
```

响应中的 Token Capture 路径为：

| 值 | JSON path |
| --- | --- |
| Access Token | `$.accessToken` |
| Refresh Token | `$.refreshToken` |
| Expires In | `$.expiresIn` |

将登录 Endpoint 启用 Token Capture 后，ProbeLoom 会把响应保存到当前 Environment 的安全会话。受保护接口使用 Bearer Token 且 Token 模板留空，即可使用该会话。Refresh Endpoint 的 Body 可写为：

```json
{"refreshToken":"{{token.refresh}}"}
```

并启用相同的 Token Capture 路径，再在 ProbeLoom 的 Token 对话框中选为 Refresh request。Refresh Token 每次仅可使用一次；成功刷新后旧 Access Token 立即失效。将 `expiresInSeconds` 设为 `0` 可稳定验证过期流程。

使用 `https://localhost:5080` 访问这个纯 HTTP 端口，可以触发 TLS/协议错误；使用一个未监听的本机端口可以验证连接失败。

## 测试与构建

```powershell
.\gradlew.bat test
.\gradlew.bat build
```

测试使用 Spring MockMvc，不会启动外部服务，也不访问公共网络。
