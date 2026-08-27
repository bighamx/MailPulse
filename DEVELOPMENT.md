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
- 发布版本：v0.3.1（仅发布 ZIP 附件，内含 exe、exe.config、许可证及发布说明）
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
- 每次请求独立 `HttpRequestMessage` 带鉴权头、独立 `HttpClient/HttpClientHandler` 连接组，用完释放；后台分类与翻译不复用连接或取消生命周期，分类超时默认 8s、最小 3s
- 仅第一个启用且含有效 Key 的配置被使用（`FirstEnabled`）
- `LlmClient` 明确请求非流式 JSON，使用 `ResponseHeadersRead` 增量读取；完整根 JSON 到齐后立即解析，不等待网关连接结束或尾部 chunk。支持 gzip/deflate，限制响应为 4 MiB
- 响应头与正文读取均有独立的取消竞速保障；取消时释放响应，并回收忽略取消后迟到的响应。日志仅记录请求编号、阶段、状态码、字节数及耗时，不输出正文或凭据
- NewAPI 兼容性：显式设置 `ExpectContinue=false`、`ConnectionClose=true`，避免等待中间握手或复用异常连接。曾出现后端完成而客户端一直停留在 `stage=sending`；仅更换连接组仍有超时，关闭上述握手/复用后实测 4 轮并发分类+翻译及 3 轮真实 WPF 翻译全部完成。不能只用“新进程单次请求成功”作为验收标准
- 编辑 LLM 配置不回显 Key；留空或仅空白保留原加密值，填写新值才替换。新增或旧 Key 无法解密时必须输入，名称/模型/超时等修改不要求重复输入有效的已保存 Key

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
- `LlmClient` 共享三协议请求；分类保留原提示词和超时。纯文本邮件按约 1800 字符分段，优先段落/句子边界并保护 URL、邮箱地址、标识符和 UTF-16 代理对；每段独立等待至少 120 秒（若配置更长则沿用），最大输出 8192 tokens。主题只在首段翻译
- **HTML 邮件原地织入排版（XLIFF 风格不透明占位符）**：`HtmlMailLayout`（HtmlAgilityPack）把正文解析成**块级翻译单元**——每个不含块级子元素的元素（`p`/`div`/`li`/`td`/`h1-h6`/`pre` 等）整体一个单元；块内每个内联元素（`<b>`/`<a>`/`<sup>`/`<img>`/`<br>` 等）变为不透明占位符 `⟦N⟧...⟦/N⟧`，整块作为一个带占位符的**模板字符串**发给 LLM（`body` 字段）。模型看到完整句子上下文，必须原样保留每个占位符（开/闭、顺序、嵌套）。回填时解析模板树并校验：占位符缺失/重复/乱序/嵌套非法 → 整段降级（去标记并入首片段），绝不写坏结构。图片/链接/表格/内联样式不丢，单元数≈段落数，首段在第一批并行请求中即完成
- **属性翻译**：`alt`/`title`/`placeholder`/`aria-label`/`aria-description` 作为批量属性单元，一次请求发送 `attributes:[{id,text}]` 数组（`AttributeSystemPrompt`）；校验数量、ID 唯一性和非空译文后整批回填。HTTP 错误、超时、取消及畸形响应均保留原值且保持未完成，重试只补译未完成任务；`translate="no"`/`data-no-translate` 子树跳过
- **排除与净化**：`<head>/<style>/<script>/<title>/<meta>/<link>/<code>/<pre>/<svg>` 整棵子树不翻译；空白/纯标点块（如 `<p>&nbsp;</p>`）不发送。`HtmlAgilityPack` 不解码命名实体，故先 `HtmlDecode` 再折叠空白，避免字面 `&nbsp;` 被送去 LLM 又回显成正文。`<br>/<hr>` 是内联换行、不视为块容器，含 `<br>` 的脚注/页脚段落必须整段成单元（否则 sup/a 各自成段而正文文本被孤立）
- **阅读体验**：首次完成才导航一次到织入文档，之后各段完成通过注入的 `mpApply` 脚本就地替换 `data-mp/data-frag` 标记片段的文本，不重载页面，读者滚动位置不被打断；纯文本邮件逐段合并时也保留 `ScrollViewer` 偏移
- **HTML 完整性和乱序回填**：按块边界划分连续文字/行内元素，不遗漏 `<div>前文<p>中间</p>后文</div>` 的前后文字，也支持无外层标签的片段；不移动原节点或改变块布局。文本和属性分别按成功应用的 ID 集合去重，不把完成数量当连续下标。页面尚未加载或节点未找到时不记为已应用；导航后可幂等重放，晚到的属性通过 `mpApplyAttribute` 更新
- 只发送主题和已提取的文本正文，不发送 HTML 源码、附件、图片；邮件内容明确作为待翻译数据，不执行其中的指令
- 译文作为纯文本（无 HTML 时）或织入原文版式的网页（有 HTML 时）显示，可切换回原文；不覆盖原邮件，不修改回复引用、验证码测试或服务器内容
- `MailTranslationSession` 在内存中保留已完成的段落，失败/取消后重试只请求未完成部分；更改模型、地址或 Key 时重新建立会话，提高超时时间不清除已完成部分。切换邮件/刷新后清除，不持久化邮件内容
- 加载中显示等待动画、完成段数和本次累计等待秒数，可取消；切换邮件/账号/关闭窗口也会取消，过期响应和进度回调不能覆盖新邮件
- 超过 24,000 字符时明确提示且不发送、不静默截断；响应被截断、格式不正确、超时或 HTTP 错误均允许重试
- 本地超时与上游/网络取消分开提示；诊断日志只记录段号、字符数、耗时，不记录邮件内容、Key 或服务响应
- 卡在 `stage=sending` 的含义：`headers` 日志只有在 `SendAsync` 返回后才写入，因此长时间停留在 `sending` 表示请求处于排队/建连/TLS/已发送 body/等待响应头中的某一步，不能证明请求未到达网关。net48 的 ServicePointManager 默认每主机仅 2 个连接（按 scheme+host+port 键控的 ServicePoint 共享，不按 handler 隔离），慢速或已被取消的分类请求与翻译并发时可能占满槽位；而空闲长连接被网关半关闭后复用、`Expect: 100-continue` 中间握手不被中间设备应答，都表现为无并发的单请求停滞。当前实现已按上述机制规避：每调用独立连接组、`ConnectionClose=true`、`ExpectContinue=false`。若仍需定位差异，用日志中的 requestId 与网关侧请求日志对照即可确认瓶颈在本端还是上游
- **并行分段**：`RunParallelAsync` 是纯文本与 HTML 织入共用的并发管线——最多 3 路在途请求、每段独立 `CancelAfter` 超时、某段失败即取消其余在途请求避免在坏配置下浪费额度；已完成段保留在会话里可续传。`Progress<T>`/`HtmlTranslationProgress` 每次都携带合并快照（译文替换、未完成段保留原文），UI 首段完成即切入译文视图并随进度刷新
- **失败/取消回归**：排队和在途任务都链接本轮 `run.Token`，首个失败后不再启动新批次；失败提示使用实际缓存的完成数量，不使用“总数减报错数”。测试覆盖 C→属性→A→B 乱序回填、DOM 未就绪重试、真实 WPF 浏览器脚本、嵌套/裸 HTML、首错停止、超时取消、属性失败及续传；全部使用虚构邮件与模拟接口

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
git tag v0.3.1 && git push origin v0.3.1
# 从该提交构建并验证后，将 exe、exe.config、LICENSE、发布说明及校验值打成 ZIP
gh release create v0.3.1 "publish\v0.3.1\MailPulse-v0.3.1-win.zip" --verify-tag --title "v0.3.1" --notes-file "docs\releases\v0.3.1.md"
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
8. **MailKit 已升至 4.16.0**（修复 NU1902 / CVE-2026-41319 STARTTLS 响应注入）；升级后 IMAP/POP3/SMTP 路径回归通过，构建无告警
9. **UIA 对自定义模板按钮 Invoke 不可靠**：自动化测试建议直接驱动业务层，勿依赖 UI 点击

## 7. 验证方法

1. 添加 QQ/Gmail（授权码/应用密码）→ 测试连接
2. 发测试邮件（主题含"验证码"，正文 `你的验证码是 ASFE466`）→ 应弹 Toast 且剪贴板已复制
3. 点「忽略」→ 邮箱里该邮件变已读；重启应用后不再重复提醒
4. 深色模式下检查：主界面 / 账号对话框 / 规则编辑器 / LLM 设置 / 各下拉框展开态文字可读
5. LLM：配置后点「测试选中」应返回识别结果
6. 邮件翻译回归：构建 Debug 后运行 `powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File scripts\Test-MailTranslation.ps1`；使用本地模拟 HTTP 验证三协议、分类兼容性、分段完整性、断点重试、超时预算、异常、取消、原文切换和过期响应，不读取真实配置、不发送真实邮件。可传入 `-PreviewPath <png路径>` 和 `-ThemeMode Light/Dark` 渲染测试界面
7. 响应读取回归包含不发送 EOF 的模拟流，以及真实本机 TCP 网关：发送完整 JSON chunk 但不发送结尾零块，连续请求三次验证仍能立即完成，并检查无 Expect 握手、Connection 关闭；覆盖 LLM 编辑保留/替换 Key 和缺失 Key 校验。测试日志隔离在构建目录的 `test-logs`，不混入真实应用日志
8. HTML 织入回归：占位符模板（`⟦N⟧`）映射回填、占位符缺失降级、`alt/title/aria-label` 属性批量翻译、`&nbsp;` 解码与空白块排除、含 `<br>/<sup>/<a>` 的脚注段落整段成单元、真实邮件端到端翻译（`scripts\Translate-HtmlMail.ps1`，用本机 LLM 配置，产物为独立 HTML 文件）

## 8. 待办 / 路线图

- [ ] LLM 二期：更多协议（Gemini / Ollama）、多套提示词切换、LLM 判断日志
- [ ] 历史通知记录窗口
- [ ] Toast 打开邮件原文（深链到网页邮箱）
- [ ] Inno Setup 安装包（桌面快捷方式、卸载、开机自启）
- [ ] 深色模式的对话框截图补全
