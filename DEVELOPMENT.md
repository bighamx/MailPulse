# MailPulse 开发交接文档

> 供后续接手开发者快速理解项目结构、核心机制与注意事项。
> 面向用户的说明见 [README.md](README.md)。

## 1. 项目概览

MailPulse 是一个 **Windows 托盘常驻的邮件验证码监控工具**：

- 后台持续监控用户配置的邮箱（普通邮箱走 IMAP IDLE / POP3；微软邮箱走 Microsoft Graph 轮询）
- 内置规则引擎（主题关键词 或 正文正则）识别「验证码 / 确认链接」类邮件
- 命中后弹出**悬浮 Toast**：大号验证码 + 一键复制、打开链接，点击后把邮件标记为已读
- 可选 **LLM 兜底**（OpenAI Chat / OpenAI Responses / Anthropic）做更智能的匹配与提取
- 内置轻量邮件中心：按账号查看最近邮件、预览正文、回复与发送（微软邮箱走 Graph，其他邮箱走 SMTP）
- 深浅色主题可切换；凭据 DPAPI 加密；提醒历史持久化去重

技术栈：**.NET Framework 4.8** + WPF（UI）+ WinForms（托盘），单文件发布（Costura.Fody）。

## 2. 仓库与发布

- GitHub（公开）：https://github.com/bighamx/MailPulse
- 发布版本：v0.2.0（含单文件 exe + exe.config，另提供 ZIP 压缩包）
- 提交作者使用 GitHub 匿名邮箱（`bighamx@users.noreply.github.com`），**避免泄漏个人邮箱**
- **红线**：本仓库公开，任何提交不得包含真实账号、密码、API Key、`%AppData%` 下的配置/日志/seen.json

## 3. 目录结构

```
MailPulse/
├─ MailPulse.csproj          # SDK-style, net48, WPF+WinForms, MailKit/Newtonsoft/Costura
├─ App.cs                    # 程序入口：托盘、主窗口、监控生命周期
├─ FodyWeavers.xml(.xsd)     # Costura 单文件内嵌配置
├─ README.md                 # 用户文档（含截图）
├─ DEVELOPMENT.md            # 本文档
├─ docs/screenshots/         # UI 截图（README 引用）
├─ Models/Models.cs          # 全部数据模型（Account/Rule/Llm/ClassifyResult/AppConfig）
├─ Services/
│  ├─ ConfigService.cs       # JSON 配置读写 + DPAPI 凭据加解密
│  ├─ MailMonitorService.cs  # 监控核心：Graph/IMAP/POP3、去重、标记已读、LLM 兜底接入
│  ├─ MailCenterService.cs   # 邮件中心：Graph/IMAP/POP3 读取、Graph/SMTP 发信
│  ├─ ClassificationEngine.cs# 本地规则引擎（OR 逻辑 + 正则提取）
│  ├─ LlmClassifier.cs       # LLM 三协议调用 + JSON 解析
│  ├─ LlmClient.cs           # 分类和翻译共用的三协议 HTTP 请求
│  ├─ MailTranslationService.cs # 邮件主题/文本正文翻译为简体中文
│  ├─ MicrosoftOAuthService.cs# 微软设备码 OAuth2 登录 + token 刷新
│  ├─ SeenStore.cs           # 已提醒邮件持久化（seen.json）
│  ├─ AutoStart.cs           # 开机自启（HKCU Run）
│  └─ Logger.cs              # 日志（%AppData%\MailPulse\logs，保留 7 天）
└─ UI/
   ├─ Theme.cs               # 深浅色主题引擎 + 控件样式工厂（按钮/输入框/下拉框/卡片）
   ├─ SettingsWindow.cs      # 主设置窗口（账号管理 + 规则编辑器窗口）
   ├─ AccountDialog.cs       # 账号添加/编辑对话框（含微软 OAuth 按钮）
   ├─ LlmSettingsWindow.cs   # LLM 设置（多配置 + 提示词编辑器 + 测试）
   ├─ ToastWindow.cs         # 悬浮通知（圆角/阴影/动画/倒计时/标记已读）
   ├─ MailCenterWindow.cs    # 三栏邮件中心 + 写邮件/回复弹窗
   └─ (RulesEditorWindow 在 SettingsWindow.cs 内)
```

## 4. 核心机制

### 4.1 配置与安全
- 配置：`%AppData%\MailPulse\config.json`（`ConfigService` 读写，Newtonsoft.Json）
- 凭据：密码 / API Key / OAuth refresh token 一律经 `SecureStore`（DPAPI，用户级 + 固定熵）加密后存 `Encrypted*` 字段
- 规则加载时按「名称+正则」去重（历史版本曾出现规则无限累积的 bug，已加防御）
- 主题：`config.json` 的 `ThemeMode`（"Light"/"Dark"），启动时 `Theme.Apply()`

### 4.2 邮件监控（MailMonitorService）
- 每个启用账号独立 `Task` 循环；Graph 账号轮询最近未读邮件；IMAP 走 `ImapClient` + `IdleAsync`（25 分钟心跳）；POP3 轮询最后 10 封
- 连续失败使用指数退避（最高 5 分钟），避免短间隔认证轰击触发服务端保护；异常记录日志
- **去重**：会话内 UID 集合（避免重复抓取）+ `SeenStore` 持久化（`accountId|MessageId`），只记录「真正提醒过」的邮件
- **标记已读**：Toast 点击后把 `(accountId, UniqueId)` 入队，下次 IDLE 唤醒时 `AddFlags(Seen)`；POP3 无此概念
- 微软 Graph 账号刷新 token 后调用 `/me/mailFolders/inbox/messages`；普通 OAuth IMAP 兼容路径才使用 `SaslMechanismOAuth2`

### 4.3 分类（ClassificationEngine）
- 触发规则：**主题关键词 或 正文正则任一命中**（OR）
- 正则按序尝试，命中即取捕获组 1（无捕获组取整段），默认支持字母数字混合验证码
- 正文取 `TextBody`，HTML 先剥标签；发件人白名单可拦截
- 未命中且启用 LLM 兜底时进入 `LlmClassifier`

### 4.4 LLM 兜底（LlmClassifier）
- 协议：OpenAI Chat（`/chat/completions`）、OpenAI Responses（`/responses`）、Anthropic（`/messages`，`x-api-key` + `anthropic-version`）
- 提示词支持 `{subject}` / `{body}` 占位符；要求模型输出 JSON：`{is_urgent, code, url, reason}`
- 解析容错：截取首个 `{` 到末个 `}` 再 JObject.Parse
- 每次请求独立 `HttpRequestMessage` 带鉴权头（共享 HttpClient 防串头），统一超时（默认 8s，最小 3s）
- 仅第一个启用且含有效 Key 的配置被使用（`FirstEnabled`）

### 4.5 通知（ToastWindow）
- 无边框 + `AllowsTransparency`，圆角卡片 + 阴影，淡入 + 上滑动画，底部 30s 蓝色倒计时条
- 「复制 / 打开链接 / 忽略」点击后触发 `MarkAsRead` 回调并关闭
- 自动复制验证码（`AutoCopyCode` 可关）

### 4.6 主题（Theme）
- 静态可变色板 + `Apply(ThemeMode)`；所有窗口构建时读取当前色板
- `Theme.CreateButton / StyleTextBox / StyleComboBox / Card / Label` 工厂
- 深色下输入框/下拉框文字可读性：`InputBg` 随主题切换，ComboBox 使用**完全自定义模板**（深色弹层 + 选中项绑定 `SelectionBoxItem`）

### 4.7 托盘与生命周期（App.cs）
- `ShutdownMode = OnExplicitShutdown`，关主窗不退，托盘常驻
- 启动：加载配置 → 应用主题 → 启动监控 → 打开主窗口
- 托盘菜单：设置 / 暂停监控 / 退出；双击开设置
- 账号增删改、规则保存后调用 `RestartMonitoring()`（重读配置 + 重启监控）

### 4.8 邮件中心（MailCenterService / MailCenterWindow）
- 三栏布局：账号选择与最近邮件列表 / 邮件正文预览，支持刷新、写邮件和回复
- 微软账号有 `EncryptedGraphRefreshToken` 时优先走 Microsoft Graph：列表、正文、标为已读、删除、后台监控和发信均不再连接 IMAP/SMTP
- 普通邮箱：IMAP 按 UID 读取，POP3 使用服务器 UID 定位；发信使用 SMTP。Gmail、QQ 及常见 `imap.*` 域名可自动推断服务器
- 正文加载后保持查看同一封未读邮件 5 秒，会自动调用服务端标为已读；切换邮件/账号或关窗会取消
- Graph 回复时发件人通常为 `显示名称 <address@example.com>`；提交前必须用 MimeKit `InternetAddressList.Parse` 提取真正地址，不能把整段显示文本塞给 Graph

#### 邮件翻译

- 正文加载后点击“翻译为中文”，复用第一个启用且模型/API Key 有效的 LLM 配置；不依赖验证码的 `LlmFallbackEnabled` 开关
- `LlmClient` 共享三协议请求；分类保留原提示词和超时。翻译按约 1800 字符分段，优先段落/句子边界并保护 URL、邮箱地址、标识符和 UTF-16 代理对；每段独立等待至少 120 秒（若配置更长则沿用），最大输出 8192 tokens。主题只在首段翻译
- 只发送主题和已提取的文本正文，不发送 HTML 源码、附件、图片；邮件内容明确作为待翻译数据，不执行其中的指令
- 译文作为纯文本显示，可切换回包含 HTML/图片的原文；不覆盖原邮件，不修改回复引用、验证码测试或服务器内容
- `MailTranslationSession` 在内存中保留已完成的段落，失败/取消后重试只请求未完成部分；更改模型、地址或 Key 时重新建立会话，提高超时时间不清除已完成部分。切换邮件/刷新后清除，不持久化邮件内容
- 加载中显示等待动画、完成段数和本次累计等待秒数，可取消；切换邮件/账号/关闭窗口也会取消，过期响应和进度回调不能覆盖新邮件
- 超过 24,000 字符时明确提示且不发送、不静默截断；响应被截断、格式不正确、超时或 HTTP 错误均允许重试
- 本地超时与上游/网络取消分开提示；诊断日志只记录段号、字符数、耗时，不记录邮件内容、Key 或服务响应

### 4.9 微软邮箱：推荐接入方式与踩坑记录

#### 推荐方案：自有 Entra 公共客户端 + Microsoft Graph

当前唯一经过完整实测的微软个人邮箱方案是 Microsoft Graph。它覆盖读取列表、正文、已读、删除、后台监控和发送，不依赖 Exchange IMAP/SMTP OAuth。

Entra 应用配置：

1. 在 Microsoft Entra 管理中心注册应用；支持的帐户类型必须包含“个人 Microsoft 帐户”（本项目 OAuth 端点使用 `consumers`）
2. “身份验证”中启用 **允许公共客户端流 / Allow public client flows**；桌面应用不创建、不保存客户端密钥
3. “API 权限”→ Microsoft Graph → 委托的权限，添加：
   - `Mail.ReadWrite`：读取正文、监控、标为已读和删除
   - `Mail.Send`：发送和回复
4. 在账号编辑页选择“自有 Entra + Microsoft Graph（读取和发送）”，填写应用程序（客户端）ID，完成设备码登录

代码路径：

- `MicrosoftOAuthService` 请求 Graph scopes：`Mail.ReadWrite Mail.Send offline_access openid profile email`
- refresh token 由 DPAPI 加密保存在 `EncryptedGraphRefreshToken`
- `MailCenterService.IsGraphAccount()` 决定是否绕过 IMAP/SMTP
- Graph 根地址：`https://graph.microsoft.com/v1.0/`
- 列表：`GET /me/mailFolders/inbox/messages`
- 正文：`GET /me/messages/{id}`
- 已读：`PATCH /me/messages/{id}`，载荷 `{ "isRead": true }`
- 删除：`DELETE /me/messages/{id}`
- 发送：`POST /me/sendMail`

#### 已确认的坑

1. **设备码流程仍可用；借用旧微软第一方客户端 ID 的方案目前不稳定**：它在历史版本以及本轮排障早期确实成功读取过，但当前对同一账号重新发起最小 IMAP scope 授权时，设备码页面连续返回 `invalid_request`，内容为 first party application 未预授权，用户无权自行 consent。不要据此断言微软已全局永久关闭；已有 refresh token 可能仍有效，但新 consent 不可作为可靠入口。自有 Entra 公共客户端启用 public client flow 后，Graph 设备码授权已经实测成功
2. **不要用自有 Entra 的 Exchange IMAP OAuth 作为首选读取方式**：token 端点可以成功签发 token，但 `outlook.office365.com:993` 可能连续返回 `Authentication failed`；增加重试只能偶尔成功，不能视为可靠修复
3. **不要不断刷新 token 试图修复 IMAP 认证失败**：曾导致后台每约 10 秒刷新/认证一次，并出现 `AADSTS50196` request loop；失败必须退避
4. **Outlook SMTP 587 使用 STARTTLS**：历史配置的 465 + SSL-on-connect 会出现 TLS handshake 错误。当前 Graph 账号不会使用 SMTP；此条仅用于兼容旧配置
5. **Microsoft Graph 和 Exchange scopes 不是一回事**：Graph 使用 `https://graph.microsoft.com/...`；IMAP/SMTP 协议使用 `https://outlook.office.com/...`。拿 Graph token 去做 XOAUTH2 IMAP 会失败，反之亦然
6. **收件人显示名称不是邮箱地址**：Graph 的 `emailAddress.address` 必须是解析后的纯地址。回复场景务必处理 `Name <user@example.com>`
7. **同一个账号的 Graph refresh token 要独立存储**：不要用 IMAP/SMTP refresh token 覆盖 `EncryptedGraphRefreshToken`

#### 实际验证基线

开发调试必须使用业务层做端到端验证（敏感 token 不输出）：

1. 新进程加载 DPAPI 加密的 Graph refresh token，刷新后读取最近 3 封邮件
2. 加载第一封正文，确认非空
3. 向测试者自己的邮箱发送一封带时间戳的测试邮件，并轮询确认投递
4. 使用 `显示名称 <邮箱地址>` 作为收件人再发送一次，覆盖地址解析回归

本轮排障已完成以上四项，列表、正文、刷新 token 后读取、Graph 发送、自发自收投递及显示名称收件人均成功。

## 5. 构建与发布

```bash
# 常规构建（单文件，Costura 内嵌依赖）
dotnet build MailPulse.csproj -c Release
# 产物：bin\Release\net48\MailPulse.exe（+ exe.config，必需同目录）

# 发布新版本
git tag v0.2.0 && git push origin v0.2.0
gh release create v0.2.0 "bin\Release\net48\MailPulse.exe" "bin\Release\net48\MailPulse.exe.config" --title "v0.2.0" --notes "..."
```

> 系统需 .NET SDK（含 net48 开发包）；Win10/11 自带 .NET Framework 4.8 运行时。

## 6. 给接手者的注意事项（踩坑记录）

1. **语言版本 C# 7.3**（net48 SDK 风格）：无 switch 表达式、无 target-typed new；`async`/await 可用
2. **`lock` 内不能 `await`**：POP3 扫描已改为先在锁内收集、锁外异步处理（CS1996）
3. **Theme 类里有静态字段 `Border`（颜色）**：会遮蔽 WPF `Border` 类型，模板代码必须用 `System.Windows.Controls.Border` 全限定名
4. **ComboBox 自定义模板**：收起文字依赖 `SelectionBoxItem` 绑定；否则折叠后空白（已修复）。弹层颜色用 `InputBg`，否则深色模式下白底浅字
5. **PowerShell 5.1 读取无 BOM 的 UTF-8 脚本中文乱码**：给 .ps1 加 UTF-8 BOM
6. **截图/演示模式**：临时用 `%AppData%\MailPulse\showdemo` 标记文件自动开窗（`account/rules/llm/llmcfg/toast`），用完已移除；如需复现可重新加
7. **配置勿提交**：config.json / seen.json / logs 都在 `%AppData%`，仓库 .gitignore 已排除 `bin/ obj/ *.log seen.json publish/ *.pdb`
8. **MailKit 4.8.0 有 NU1902 安全告警**：升级前需回归测试 IDLE/OAuth 路径
9. **UIA 对自定义模板按钮 Invoke 不可靠**：自动化测试建议直接驱动业务层，勿依赖 UI 点击

## 7. 验证方法

1. 添加 QQ/Gmail（授权码/应用密码）→ 测试连接
2. 发测试邮件（主题含"验证码"，正文 `你的验证码是 ASFE466`）→ 应弹 Toast 且剪贴板已复制
3. 点「忽略」→ 邮箱里该邮件变已读；重启应用后不再重复提醒
4. 深色模式下检查：主界面 / 账号对话框 / 规则编辑器 / LLM 设置 / 各下拉框展开态文字可读
5. LLM：配置后点「测试选中」应返回识别结果
6. 邮件翻译回归：构建 Debug 后运行 `powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File scripts\Test-MailTranslation.ps1`；使用本地模拟 HTTP 验证三协议、分类兼容性、分段完整性、断点重试、超时预算、异常、取消、原文切换和过期响应，不读取真实配置、不发送真实邮件。可传入 `-PreviewPath <png路径>` 和 `-ThemeMode Light/Dark` 渲染测试界面

## 8. 待办 / 路线图

- [ ] LLM 二期：更多协议（Gemini / Ollama）、多套提示词切换、LLM 判断日志
- [ ] 历史通知记录窗口
- [ ] Toast 打开邮件原文（深链到网页邮箱）
- [ ] Inno Setup 安装包（桌面快捷方式、卸载、开机自启）
- [ ] 深色模式的对话框截图补全
- [ ] 升级 MailKit 消除 NU1902 告警并回归测试
