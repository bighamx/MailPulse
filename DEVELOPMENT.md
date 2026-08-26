# MailPulse 开发交接文档

> 供后续接手开发者快速理解项目结构、核心机制与注意事项。
> 面向用户的说明见 [README.md](README.md)。

## 1. 项目概览

MailPulse 是一个 **Windows 托盘常驻的邮件验证码监控工具**：

- 后台持续监控用户配置的邮箱（IMAP IDLE 实时 / POP3 轮询）
- 内置规则引擎（主题关键词 或 正文正则）识别「验证码 / 确认链接」类邮件
- 命中后弹出**悬浮 Toast**：大号验证码 + 一键复制、打开链接，点击后把邮件标记为已读
- 可选 **LLM 兜底**（OpenAI Chat / OpenAI Responses / Anthropic）做更智能的匹配与提取
- 深浅色主题可切换；凭据 DPAPI 加密；提醒历史持久化去重

技术栈：**.NET Framework 4.8** + WPF（UI）+ WinForms（托盘），单文件发布（Costura.Fody）。

## 2. 仓库与发布

- GitHub（公开）：https://github.com/bighamx/MailPulse
- 最新 Release：v0.1.0（含单文件 exe + exe.config）
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
│  ├─ MailMonitorService.cs  # 监控核心：IMAP IDLE / POP3 轮询、去重、标记已读、LLM 兜底接入
│  ├─ ClassificationEngine.cs# 本地规则引擎（OR 逻辑 + 正则提取）
│  ├─ LlmClassifier.cs       # LLM 三协议调用 + JSON 解析
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
   └─ (RulesEditorWindow 在 SettingsWindow.cs 内)
```

## 4. 核心机制

### 4.1 配置与安全
- 配置：`%AppData%\MailPulse\config.json`（`ConfigService` 读写，Newtonsoft.Json）
- 凭据：密码 / API Key / OAuth refresh token 一律经 `SecureStore`（DPAPI，用户级 + 固定熵）加密后存 `Encrypted*` 字段
- 规则加载时按「名称+正则」去重（历史版本曾出现规则无限累积的 bug，已加防御）
- 主题：`config.json` 的 `ThemeMode`（"Light"/"Dark"），启动时 `Theme.Apply()`

### 4.2 邮件监控（MailMonitorService）
- 每个启用账号独立 `Task` 循环；IMAP 走 `ImapClient` + `IdleAsync`（25 分钟心跳，唤醒后查未读）；POP3 每 N 秒轮询最后 10 封
- 断线自动 10 秒重连；异常记录日志
- **去重**：会话内 UID 集合（避免重复抓取）+ `SeenStore` 持久化（`accountId|MessageId`），只记录「真正提醒过」的邮件
- **标记已读**：Toast 点击后把 `(accountId, UniqueId)` 入队，下次 IDLE 唤醒时 `AddFlags(Seen)`；POP3 无此概念
- OAuth 账号：连接时用 `SaslMechanismOAuth2`，refresh token 过期自动刷新并回写配置

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

## 8. 待办 / 路线图

- [ ] LLM 二期：更多协议（Gemini / Ollama）、多套提示词切换、LLM 判断日志
- [ ] 历史通知记录窗口
- [ ] Toast 打开邮件原文（深链到网页邮箱）
- [ ] Inno Setup 安装包（桌面快捷方式、卸载、开机自启）
- [ ] 深色模式的对话框截图补全
- [ ] 升级 MailKit 消除 NU1902 告警并回归测试
