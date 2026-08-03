# ProbeLoom

ProbeLoom 是一款面向后端开发者的 API 调试工具。它使用 WinUI 3 构建，围绕请求编辑、环境与变量管理、响应检查和网络诊断提供紧凑的桌面工作区。

## 功能

- 使用 Project、Environment、嵌套 Group、Endpoint 和 Request Case 组织接口。
- 组合 Base URL、Project Route Parts、Group Prefix、Endpoint Route、Path Parameters 和 Query Parameters。
- 编辑 HTTP Method、Headers、Raw JSON Body 和结构化认证配置。
- 发送 HTTP/HTTPS 请求，支持超时、取消、响应格式化及最近请求历史。
- 检查最终 URL、Headers、Body、变量来源和认证来源，并导出掩码后的 PowerShell `curl.exe` 命令。
- 手动跟随重定向，并在跨主机跳转时移除敏感 Header。
- 分阶段执行 DNS、TCP、TLS 和 HTTP 诊断。
- 在多个作用域中定义 `{{variable.name}}` 变量，支持继承、覆盖、Secret 和循环引用检查。
- 管理 Bearer Token、Basic Auth 和 Header/Query API Key。
- 从 JSON 响应提取和刷新 Access Token、Refresh Token 与过期时间。
- 通过 Route Map 搜索接口、检查路由冲突，并从同一项目模型生成 Markdown API 文档。
- 使用版本化 JSON 项目文件保存工作区，并在启动时恢复最近项目。

## 技术栈

- C# / .NET 10
- WinUI 3
- Windows App SDK 2.3
- XAML
- Windows 11

## 环境要求

- Windows 11
- .NET SDK 10.0.302 或兼容的 .NET 10 SDK
- Visual Studio 2026，或其他支持 .NET 10、WinUI 3 与 Windows App SDK 的 Visual Studio 版本
- Windows SDK 10.0.26100.0 或更高版本
- Visual Studio 的“.NET 桌面开发”和 Windows 应用开发相关组件

## 构建

在仓库根目录运行：

```powershell
dotnet restore ProbeLoom.slnx
dotnet build ProbeLoom.slnx --configuration Debug -p:Platform=x64
```

Release 构建：

```powershell
dotnet build ProbeLoom.slnx --configuration Release -p:Platform=x64
```

## 运行

### Visual Studio

1. 打开 `ProbeLoom.slnx`。
2. 将 `ProbeLoom` 设为启动项目。
3. 选择 `Debug | x64`。
4. 选择 `ProbeLoom (Package)` 启动配置。
5. 按 `F5` 启动调试。

### 命令行

```powershell
dotnet run --project ProbeLoom.csproj --configuration Debug -p:Platform=x64
```

应用需要 Windows App SDK 的打包身份。请使用上述命令或 Visual Studio 启动，不要直接运行构建目录中的可执行文件。

## 测试

核心测试使用仓库内置的轻量测试运行器，不依赖 UI 或外部测试框架：

```powershell
dotnet run --project ProbeLoom.Core.Tests\ProbeLoom.Core.Tests.csproj --configuration Release -p:Platform=x64
```

测试覆盖项目操作与持久化、请求校验与执行、变量和安全存储、Token 流程、重定向、响应分类、网络诊断、Route Catalog 和文档生成。网络相关测试使用模拟处理器、本地监听器和临时证书，不访问公共服务。

## 项目文件与安全存储

项目默认保存为版本化 UTF-8 JSON 文件：

```text
<project-name>.probeloom.json
```

当前项目格式版本为 4，并支持读取旧版本项目。保存操作使用同目录临时文件和原子替换，避免写入中断造成文件损坏。

普通变量和认证结构保存在项目文件中。Secret 变量只保存名称、作用域和 ID；实际 Secret 与 Token 会话使用 Windows Data Protection API 按当前用户加密，保存在：

```text
%LOCALAPPDATA%\ProbeLoom\secure-values.dat
```

项目文件复制到其他 Windows 用户或设备后，需要重新填写 Secret 和 Token。

## 项目结构

```text
ProbeLoom.slnx
├─ ProbeLoom.csproj              WinUI 3 桌面应用
├─ ProbeLoom.Core/               领域模型、请求执行、持久化与诊断
├─ ProbeLoom.Core.Tests/         核心测试与测试运行器
├─ Presentation/                工作区、请求编辑器、Inspector 等 UI
├─ Services/                    Windows 平台服务
├─ Styles/                      主题与控件样式
├─ Assets/                      应用图标和包资源
└─ TestServer/                  可选的本地 HTTP 测试服务
```

本地测试服务的接口和启动方式见 [`TestServer/README.md`](TestServer/README.md)。

## 常用快捷键

| 快捷键 | 操作 |
| --- | --- |
| `Ctrl+Shift+N` | 新建 Project |
| `Ctrl+O` | 打开 Project |
| `Ctrl+S` | 保存 |
| `Ctrl+N` | 新建 Endpoint |
| `F5` | 校验当前请求 |
| `Ctrl+Enter` | 发送当前请求 |
| `Ctrl+Space` | 显示 Raw JSON 上下文补全 |
| `Ctrl+Z` / `Ctrl+Y` | 撤销 / 重做 Raw JSON 编辑 |
| `Alt+方向键` | 调整 Route Builder 中可排序项的位置 |

## 当前限制

ProbeLoom 暂不支持 OAuth 浏览器授权、WebSocket、完整 Cookie Jar、代理、自动重试和 OpenAPI 导入/导出。
