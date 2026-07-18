# VOHIVE for Windows 控制台

`control-center` 是 VOHIVE for Windows 的桌面前端源码。程序使用 Windows Forms、WebView2 和内嵌 HTML/CSS/JavaScript 构建，负责管理独立 `VoHive` WSL2 发行版以及安装目录中的维护脚本。

## 用户界面

应用包含两个主页面：

- 控制中心：显示简短启动阶段，提供启动、局域网开关、设备检测重置、监听模式和代理管理。
- VoHive 后台：通过 WebView2 内嵌 `http://vohive-wsl:7575`，无需单独打开浏览器。

启动和维护命令全部隐藏运行，不展示 PowerShell、CMD 或 WSL 终端。状态区域只显示“正在启动 Mihomo”“正在连接 VoHive”等阶段文字。

## 系统托盘

程序启动后创建通知区域图标。关闭窗口或按 `Alt+F4` 只隐藏主界面，不停止 WSL、代理自动切换或通话监听。

- 双击托盘图标：恢复主页面。
- 右键“展开主页面”：显示窗口。
- 右键“退出”：完全结束控制台。

托盘和应用图标使用 VoHive Web favicon。

## WSL 布局发现

发行版名称按以下顺序读取：

1. 环境变量 `VOHIVE_WSL_DISTRO`。
2. `Tools\config\vohive-wsl.json` 中的 `distro`。
3. 默认值 `VoHive`。

安装版的脚本目录是应用旁的 `Tools\scripts`。源码调试时仍保留旧桌面工具目录回退，便于兼容已有手工部署。

## 控制中心功能

### 启动 VoHive

调用 `Start VoHive WSL.ps1 -NoPause`，检查 WSL、usbipd、Mihomo、VoHive 和 localhost/hosts 映射。已经运行的服务不会重复部署。

### 局域网访问

开关 `netsh interface portproxy` 和 Windows 防火墙 LocalSubnet 规则。界面状态读取实际系统配置，不只保存一个 UI 开关值。

### 重置设备检测

调用 `Reset VoHive Device Detect.ps1`，重新 bind/attach EC20，等待 QMI/串口设备并重启 VoHive 设备扫描。

### 通话监听

提供“只通话监听，不播放声音”和“播放声音并监听”两个互斥按钮。实时模式使用内置 `Tools\ffmpeg\ffplay.exe` 和 TCP 19090；录音仍保存为 WAV。监听进程使用普通用户令牌和隐藏窗口启动。

### 代理管理

直接管理 WSL 的 `/opt/mihomo/config.yaml`。支持无限节点、YAML 导入、编辑、删除、手动切换、自动切换、单个测速和全部测速。

凭据不会返回给 WebView；编辑已有节点时，空白密码字段表示保留原值。保存前构建候选配置并验证 Mihomo，失败时恢复旧配置。

测速任务是异步的。单个测速只禁用对应按钮；全部测速期间仍可切换页面和使用不冲突的控制功能。测速结果显示在节点按钮旁。

## 配置模板

`..\config\vohive-wsl.example.json` 会发布为 `Tools\config\vohive-wsl.example.json`。安装脚本首次运行时复制为 `vohive-wsl.json`。模板只包含：

- 发行版 `VoHive`。
- VoHive 端口 7575。
- Mihomo SOCKS5 端口 7891。
- 监听 TCP 端口 19090。
- 初始后台账号 `admin`，密码 `admin`。

模板不得加入真实代理节点、Bot Token、OpenID、邮箱或 SIM 身份数据。

## 构建

需要 .NET 10 SDK 和 Windows x64：

```powershell
dotnet restore .\VoHiveControl.csproj
dotnet build .\VoHiveControl.csproj -c Release
dotnet publish .\VoHiveControl.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
```

必须使用多文件自包含发布。WebView2、YamlDotNet、.NET runtime DLL 和 `Tools` 目录都需要与 `VoHiveControlCenter.exe` 一起分发；不要只复制 EXE。

正式安装器额外把 `ffplay.exe` 放入 `Tools\ffmpeg`，把独立 WSL rootfs 放入 `Payload`，再由 Inno Setup 生成安装程序。

## 诊断

- 页面打不开：检查 `wsl -d VoHive -- systemctl status vohive.service` 和 7575。
- 等待设备枚举：运行设备检测重置，检查 `usbipd list` 与 `/dev/cdc-wdm0`。
- 实时无声：确认监听处于实时模式、`ffplay.exe` 正在运行、TCP 19090 可达。
- IKE 硬超时：优先检查代理节点、出口国家、UDP 和 ePDG 可达性。
- 局域网凭证失败：使用 VoHive 后台账号；端口转发不修改认证。
