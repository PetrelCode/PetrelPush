# PetrelPush

<p>
  <a href="https://github.com/PetrelCode/PetrelPush/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/PetrelCode/PetrelPush/ci.yml?branch=main&amp;label=CI&amp;style=flat-square"></a>
  <a href="https://github.com/PetrelCode/PetrelPush/releases/latest"><img alt="Release" src="https://img.shields.io/github/v/release/PetrelCode/PetrelPush?sort=semver&amp;style=flat-square"></a>
  <a href="https://github.com/PetrelCode/PetrelPush/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/PetrelCode/PetrelPush/total?style=flat-square"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/PetrelCode/PetrelPush?style=flat-square"></a>
</p>

<p>
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D6?style=flat-square&amp;logo=windows&amp;logoColor=white">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&amp;logo=dotnet&amp;logoColor=white">
  <img alt="UI" src="https://img.shields.io/badge/UI-WPF-512BD4?style=flat-square">
  <img alt="Transport" src="https://img.shields.io/badge/transport-SSH%2FSFTP-4D4D4D?style=flat-square">
  <img alt="State" src="https://img.shields.io/badge/state-SQLite%20WAL-003B57?style=flat-square&amp;logo=sqlite&amp;logoColor=white">
  <img alt="Installer" src="https://img.shields.io/badge/installer-MSI-5C2D91?style=flat-square">
  <a href="https://www.petrelcode.com"><img alt="Website" src="https://img.shields.io/badge/website-petrelcode.com-0F766E?style=flat-square&amp;logo=googlechrome&amp;logoColor=white"></a>
</p>

> Windows → Linux 项目文件实时同步工具

PetrelPush 是 [PetrelCode](https://www.petrelcode.com) 旗下的 Windows 桌面工具，面向开发机、测试机与部署机的文件分发场景：把本地项目目录**实时单向镜像**到 Linux 服务器。远端只需要 sshd，不需要安装任何 agent；工具也**不读写你的 `.git`**，不会干扰 `git add / commit / gc / push`。

## 目录

[特性](#特性) · [界面预览](#界面预览) · [工作原理](#工作原理) · [环境要求](#环境要求) · [快速开始](#快速开始) · [Linux 端准备](#linux-端准备) · [配置与数据位置](#配置与数据位置) · [构建与测试](#构建与测试) · [项目结构](#项目结构) · [已知限制](#已知限制) · [参与贡献](#参与贡献) · [许可证](#许可证)

## 特性

### 同步引擎

- **实时增量同步**：文件系统监听 + 500ms 去抖，增删改秒级传播；文件与目录重命名直接映射为远端 `rename`，不重传内容
- **最终一致**：SQLite 快照记录 `path / size / mtime / sha256` 与远端哈希，事件丢失、断网、进程重启后自动对账；失败任务退避重试并持久化，网络恢复后自动补齐
- **深度校验与目录自愈**：定期或手动忽略 mtime 捷径、全量重算哈希；本地或远端根目录变更后自动清空旧快照、按全量重传到新目录
- **忽略规则**：内置规则 + 逐目录 `.gitignore` + 根目录 `.syncignore`（优先级最高，支持 `!` 重新包含），忽略页可直接编辑并即时生效，也能查询某个路径命中了哪条规则
- **传输与安全**：SFTP 连接池（默认 4 路）、应用层限速、临时文件 + 原子 rename；主机指纹 TOFU 校验、密码 / 私钥两种认证、凭据经 DPAPI 加密、日志脱敏

### 连接与界面

- **多连接**：一条连接一套本地目录 / 远端主机 / 凭据 / 快照 / 日志，逐条「启用」开关控制启停，可同时运行；列表按「本地目录 → 用户@主机:端口 远端目录」展示，并实时标出启用与传输状态
- **连接与参数**：连接配置弹窗管理名称、启停、两端目录与 SSH 认证（测试连接确认指纹）；同步参数弹窗按连接调整去抖、轮询、并发、限速、空目录等，保存即重建引擎
- **文件页**：双栏浏览本地与远端目录，带同步状态与权限标记；支持右键上传、新建文件夹、下载、删除，以及从资源管理器拖拽上传，路径条一键复制
- **状态、日志与托盘**：状态页汇总连接摘要、待处理 / 失败任务与实时日志；托盘常驻，提供立即同步、暂停、深度校验、重置失败任务；日志页按级别与连接筛选

### 终端与工程

- **内置终端**：本地 ConPTY / 远端 SSH PTY，支持全屏程序与 256 色；顶栏「快速命令」「快速路径」浮层点一条即把命令填进输入行（不自动回车）
- **配套**：命令行 `init / run / test / check-ignore / profiles`；WiX 中文安装向导，自包含发布（无需预装 .NET），卸载清理用户数据

## 界面预览

|  |  |
|:--:|:--:|
| **状态**<br>![状态页](docs/screenshots/status.png) | **配置**<br>![配置页](docs/screenshots/config.png) |
| **文件**<br>![文件页](docs/screenshots/files.png) | **设置**<br>![设置页](docs/screenshots/settings.png) |
| **忽略规则**<br>![忽略页](docs/screenshots/ignore.png) | **日志**<br>![日志页](docs/screenshots/log.png) |
| **帮助 · 关于**<br>![帮助-关于](docs/screenshots/help-about.png) | **帮助 · 说明文档**<br>![帮助-说明文档](docs/screenshots/help-guide.png) |

## 工作原理

```text
Windows 本地目录
  ├─ FS Watcher        只产生「该检查了」的廉价信号
  ├─ 扫描器 + 忽略规则   .gitignore / .syncignore，不调用 git
  ├─ SQLite 快照表      path + size + mtime + sha256 + 远端已知哈希
  └─ Diff → 任务队列 → SFTP 连接池（原子写 / rename / delete / mkdir）
                                │
                                ▼
                     Linux 目标目录（仅需 sshd）
```

正确性由快照对比保证，而不是由监听事件保证：事件只是触发器，丢失或重启最多影响延迟，不影响最终结果。

## 环境要求

| 项目 | 要求 |
|---|---|
| 客户端 | Windows 10 / 11（x64）；内置终端需要 Windows 10 1809 以上 |
| 服务端 | 任意 Linux，启用 sshd 与 SFTP 子系统（OpenSSH 默认配置即可） |
| WebView2 | 终端渲染依赖 Microsoft Edge WebView2 Runtime（Windows 11 预装；Windows 10 随 Edge 更新安装） |
| 运行时 | 安装包自包含 .NET 8；从源码构建需 .NET 8 SDK |
| 状态存储 | SQLite（WAL 模式） |
| 打包 | WiX Toolset 5（通过 NuGet 自动还原，无需单独安装） |
| 其他 | 不需要 Git、rsync 或远端 agent |

## 快速开始

三种方式按需选择：装 MSI 用于日常使用，跑源码用于开发调试，CLI 用于首次验收与自动化。

### 获取源码

```powershell
git clone https://github.com/PetrelCode/PetrelPush.git
cd PetrelPush
```

### 构建安装包

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1 -Version 0.4.0
```

输出：`artifacts\PetrelPushSetup-0.4.0-x64.msi`（x64，perMachine 安装到 `C:\Program Files\PetrelPush`）。

安装向导界面语言由 `installer\PetrelPush.Setup.wixproj` 的 `<Cultures>zh-CN</Cultures>` 决定，使用 WiXUI 自带的中文本地化；如需英文界面改为 `en-US` 即可。

### 从源码运行

```powershell
dotnet run --project PetrelPush.App
```

### 首次配置

1. 打开「配置」页，「连接」卡片里已有默认连接；需要更多目标点右上角「新建连接」
2. 点该行右侧的铅笔图标（悬停提示「编辑连接」）打开连接配置弹窗，首行填写**配置名称**（右侧「启用连接」开关决定保存后是否立即启动同步），再填**本地目录**（Windows 路径）
3. 填写**远端目录**（Linux 绝对路径，不存在会自动创建）
4. 填写主机、端口、用户名，选择认证方式：**密码**（DPAPI 加密保存）或 **OpenSSH 私钥**（支持带口令的私钥）
5. 点弹窗底部「测试连接」：首次连接会固定主机指纹（TOFU），成功后按配置完整度自动启用；再点「保存」让配置生效
6. 指纹与当时的 host:port 绑定：改 IP / 端口后旧指纹自动作废，重新点「测试连接」确认即可；同一地址上远端重装 / 换机导致指纹变化时，会弹窗要求确认后才更新
7. 需要调整去抖、轮询、并发、限速、空目录等同步参数时，点该行的齿轮图标（悬停提示「同步参数」），在弹窗里改完点「保存」；首次会全量上传，完成后状态显示「已同步」
8. 之后可在「忽略」页维护 `.syncignore`，在「日志」页按级别查看运行日志
9. 需要同步到别的机器 / 目录时重复以上步骤新建一条连接；暂时不用的连接关掉列表里该行的「启用」开关即可停掉

> 首次验收可先勾选 dry-run，只查看变更集而不写入远端。

### 命令行

```powershell
# 创建 profile
PetrelPush init --profile dev --local D:\work\myapp --remote-root /srv/myapp `
    --host 192.168.1.10 --port 22 --user root --password "your-password"

# 测试连接
PetrelPush test --profile dev

# 只跑一轮（首次验收）
PetrelPush run --profile dev --once

# 常驻运行
PetrelPush run --profile dev

# 检查某个路径是否被忽略及原因
PetrelPush check-ignore --profile dev --path dist/runtime.json
```

CLI 编译产物位于 `PetrelPush.Cli\bin\Release\net8.0\PetrelPush.exe`，与桌面端共用同一份 profile 配置。

## Linux 端准备

- 启动 sshd 并保证 SFTP 可用（`Subsystem sftp` 为默认配置，通常无需改动）
- 目标目录对登录用户可写；使用 root 时无需额外授权
- 密码认证需允许 `PasswordAuthentication`（部分发行版通过 PAM 走 `KbdInteractiveAuthentication`，客户端会自动回退）
- 私钥认证建议使用 ed25519：`ssh-keygen -t ed25519`，公钥追加到目标账号的 `~/.ssh/authorized_keys`（目录 700、文件 600）
- 若启用了 fail2ban，建议把客户端 IP 加入白名单（客户端已做长连接保活与串行重连）

## 配置与数据位置

| 内容 | 位置 |
|---|---|
| 同步配置 | `%APPDATA%\PetrelPush\profiles\<id>\config.json` |
| 凭据 | `%APPDATA%\PetrelPush\profiles\<id>\secrets.bin`（DPAPI CurrentUser 加密，换用户/换机不可解密） |
| 状态库 | `%APPDATA%\PetrelPush\profiles\<id>\state.db`（SQLite 快照表） |
| 日志 | `%APPDATA%\PetrelPush\profiles\<id>\logs\PetrelPush-*.log`（按天滚动；多连接共用一份，每行带 `[连接名]`） |
| 忽略规则 | `<本地目录>\.syncignore`（文件名可通过配置修改） |
| 开机自启 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `PetrelPush` 值 |

卸载时会删除所有本地用户的上述用户数据目录及开机自启项，重装后需重新配置。

## 构建与测试

```powershell
dotnet restore PetrelPush.sln
dotnet build PetrelPush.sln -c Release
dotnet test PetrelPush.Tests\PetrelPush.Tests.csproj -c Release
```

测试覆盖忽略规则语义、哈希捷径、diff 与重命名配对、任务排序、路径穿越防护、退避计算、配置存储、远端文件名合法化与浏览状态标记等。

## 项目结构

| 目录 | 说明 |
|---|---|
| `PetrelPush.App` | WPF 托盘应用（状态 / 配置 / 文件 / 忽略 / 日志 / 设置 / 帮助）；`Terminal/` 为终端渲染层（xterm.js 资源 + ConPTY 会话） |
| `PetrelPush.Core` | 扫描、忽略规则、哈希、diff、同步引擎、配置模型 |
| `PetrelPush.State` | SQLite 快照库与任务持久化 |
| `PetrelPush.Transport` | SFTP 传输层（连接池、原子写、限速） |
| `PetrelPush.Cli` | 命令行宿主 |
| `PetrelPush.Tests` | xUnit 单元测试 |
| `installer` | WiX 安装包定义、构建脚本与卸载清理脚本 |

## 已知限制

- 单向同步（本地 → 远端），不做双向合并与冲突处理
- 不支持同步历史回滚
- 仅提供 Windows 客户端
- 远端被手工修改后不会主动检测并修复（v1 不扫描远端）；「文件」页的远端列表只做浏览与手工操作（下载 / 上传 / 新建目录 / 删除），不参与 diff，也不改变同步队列
- 远端浏览范围限定在配置的远端目录之内，单个目录最多显示 2000 项
- 内置终端内可执行任意命令，**不受**「浏览范围限定在远端目录之内」这条只读约束；窗口顶部会显示当前登录的主机与用户，操作前请确认目标机器
- 每个终端窗口独立建立一条 SSH 连接（不进传输连接池，也不与其它终端窗口共享）
- 拖拽直传到远端的文件不写入本地目录，之后在列表里显示为「仅远端」，引擎不会自动删除
- 单机单用户常驻，通过单实例互斥避免状态库并发写

## 参与贡献

欢迎提交 Issue 与 Pull Request。提交前请确保 `dotnet build` 与 `dotnet test` 均通过；涉及界面或样式的改动请遵循项目内的设计规范。

## 许可证

[MIT](LICENSE)

---

© 2026 PetrelCode · [www.petrelcode.com](https://www.petrelcode.com)
