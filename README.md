# MailPulse

跨平台邮件验证码/确认邮件监控服务，内置 WebUI 配置界面，支持本地 Qwen 模型和云端 API 两种 AI 兜底方式。

MailPulse 2.x 已从 Windows WPF 常驻应用改造为 Go 编写的单文件后台服务。服务负责收信、规则识别、LLM 兜底和事件推送；浏览器 WebUI 负责全部配置与通知展示。

## 功能

- **跨平台单文件**：Linux、macOS、Windows 均可运行，WebUI 和静态资源内嵌在二进制中
- **多协议收信**：IMAP 未读轮询（支持基础认证和 Microsoft OAuth 刷新）、POP3 UIDL 轮询
- **WebUI 配置**：邮箱、规则、模型、提示词、事件查询和连接测试全部在浏览器完成
- **实时通知**：SSE 推送匹配事件，支持一键复制验证码、打开确认链接、标记已读
- **系统级提醒**：服务匹配邮件后直接弹出 macOS/Linux/Windows 桌面通知，可自动复制验证码，不要求打开网页
- **规则引擎**：主题关键词、正文正则、发件人白名单，验证码捕获组提取
- **AI 兜底**：内置 `Qwen3.5-0.8B` 本地模型配置，也可切换 DashScope/OpenAI 兼容 API、Anthropic API、llama.cpp HTTP 服务
- **本地数据**：配置和提醒去重状态保存在本机，配置文件权限为 `0600`

## 快速开始

需要 Go 1.23+。

```bash
make build
./bin/mailpulsed
```

打开 <http://127.0.0.1:8787>。

常用参数：

```bash
./bin/mailpulsed \
  -address 127.0.0.1:8787 \
  -data ~/.local/share/MailPulse \
  -log ~/.local/share/MailPulse/mailpulse.log
```

| 参数 | 说明 |
|---|---|
| `-address` | HTTP/WebUI 监听地址，默认仅绑定本机 `127.0.0.1:8787` |
| `-config` | 指定 JSON 配置路径 |
| `-data` | 数据目录，包含配置、日志和 `seen.json` |
| `-log` | JSON 日志文件；未指定时输出到 stderr |

## 邮箱配置

### IMAP

1. WebUI 进入「邮箱」→「添加邮箱」
2. 选择 Gmail / QQ / Outlook 预设，或填写 IMAP 服务器
3. Gmail、QQ 通常需要应用专用密码/授权码
4. Outlook 可在账号弹窗中点击「Microsoft 设备码授权」，按提示完成设备码登录；服务会保存刷新令牌并自动换取访问令牌

### POP3

选择 POP3 协议并填写 POP3 服务器（默认 SSL 端口 `995`）。服务通过 `UIDL` 去重，不会删除服务器邮件。POP3 无“标记已读”语义，因此该选项只对 IMAP 生效。

## Qwen 模型

默认配置包含两个模型：

1. **Qwen3.5-0.8B（本地）**
   - Runtime：Ollama
   - 模型：`qwen3.5:0.8b`
   - Base URL：`http://127.0.0.1:11434/v1`
   - 首次启用时可自动下载模型
2. **Qwen API**
   - OpenAI 兼容协议
   - Base URL：`https://dashscope.aliyuncs.com/compatible-mode/v1`
   - 在 WebUI 填写 DashScope API Key 后即可测试

本地运行时安装：

```bash
# Linux
curl -fsSL https://ollama.com/install.sh | sh

# macOS
brew install ollama
brew services start ollama

# Windows
winget install Ollama.Ollama
```

模型权重不嵌入 MailPulse 二进制（约数百 MB 到数 GB，会显著增加发布包体积）。服务内置该模型配置和首次下载能力；点击 WebUI 的「下载/准备模型」会执行 `ollama pull qwen3.5:0.8b`。如需完全离线部署，可提前拉取模型，或改用已运行的 llama.cpp OpenAI 兼容 HTTP 服务。

### 自定义模型

「AI 模型」→「添加模型」支持：

- OpenAI 兼容 Chat：OpenAI、DashScope、DeepSeek、vLLM、llama.cpp、Ollama 等
- Anthropic Messages API
- 本地 llama.cpp：先自行启动 `llama-server`，再填写其 `/v1` 地址

规则优先执行；只有规则未命中且 LLM 兜底开启时，才会把邮件主题和正文发送到所选模型。

## 服务安装

### systemd

创建 `/etc/systemd/system/mailpulse.service`，并将 `YOUR_USER` 替换为运行服务的用户：

```ini
[Unit]
Description=MailPulse mail monitor
After=network-online.target

[Service]
ExecStart=/usr/local/bin/mailpulsed -address 127.0.0.1:8787
Restart=always
User=YOUR_USER

[Install]
WantedBy=multi-user.target
```

```bash
sudo cp bin/mailpulsed /usr/local/bin/
sudo systemctl daemon-reload
sudo systemctl enable --now mailpulse.service
```

### macOS launchd

创建 `~/Library/LaunchAgents/com.bighamx.mailpulse.plist`：

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>com.bighamx.mailpulse</string>
  <key>ProgramArguments</key><array>
    <string>/usr/local/bin/mailpulsed</string>
    <string>-address</string><string>127.0.0.1:8787</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
</dict></plist>
```

```bash
launchctl load ~/Library/LaunchAgents/com.bighamx.mailpulse.plist
```

### Windows

```powershell
sc.exe create MailPulse binPath= "C:\Tools\mailpulsed.exe -address 127.0.0.1:8787" start= auto
sc.exe start MailPulse
```

## HTTP API

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/health` | 服务状态 |
| `GET/PUT` | `/api/config` | 读取/保存配置 |
| `GET/DELETE` | `/api/events` | 查询/清空事件 |
| `GET` | `/api/stream` | SSE 实时事件 |
| `POST` | `/api/accounts/test` | 测试邮箱连接 |
| `POST` | `/api/llm/test` | 测试当前模型 |
| `POST` | `/api/llm/install` | 准备/下载本地模型 |
| `POST` | `/api/classify/test` | 测试规则 |
| `POST` | `/api/events/{id}/read` | 标记匹配邮件已读 |
| `POST` | `/api/oauth/microsoft/start` | 发起 Microsoft 设备码授权 |
| `POST` | `/api/oauth/microsoft/exchange` | 轮询设备码授权结果 |
| `POST` | `/api/notifications/test` | 测试系统通知与剪贴板 |

### 本地提醒

总览页提供「系统通知」和「自动复制验证码」开关。匹配成功后，服务会先复制验证码，再调用操作系统通知接口；Linux 服务器需要桌面会话和 `notify-send`/D-Bus，纯无头服务器会记录通知失败但不影响收信与 WebUI 事件。

GET 配置不会回显真实密钥，返回 `__KEEP__` 占位；PUT 时留空或传 `__KEEP__` 保持旧值，传 `__CLEAR__` 清除密钥。

## 构建与验证

```bash
make fmt
make test
make lint
make build
make package
```

`make package` 输出 Linux amd64/arm64、macOS amd64/arm64、Windows amd64。

## 安全说明

- 默认只绑定 `127.0.0.1`，不要在无额外认证时直接暴露到公网
- 配置中包含邮箱凭据/API Key，请确保数据目录权限仅当前用户可读
- 如需远程访问，建议通过 SSH 隧道或带认证和 TLS 的反向代理访问
- 邮件正文仅在规则未命中且 LLM 兜底开启时发送至已选择的模型服务

## License

MIT
