# VOHIVE for Windows

> Windows 11 + WSL2 + DJI 第一代 4G 模块 + Quectel EC20 的 VoHive 集成环境。

VOHIVE for Windows 将 VoHive、Mihomo、WSL2、usbipd-win、WebView2、通话监听、WAV 录音和 Windows 实时播放整合为一个可常驻系统托盘的桌面应用。安装程序使用独立的 `VoHive` WSL 发行版，不覆盖用户已有的 Ubuntu、VoHive 或桌面脚本。

从 GitHub Releases 下载 `VOHIVE-for-Windows-Setup-v1.1.0.exe`。安装完成后，桌面和开始菜单都会创建 `VOHIVE for Windows` 快捷方式。

## 项目来源

本项目基于 `giszh86/vohive` 的工作继续开发；VoHive 核心代码、Go 模块路径及其许可证来源于 `iniwex5/vohive`。本仓库新增 Windows 控制台、托盘后台、WSL2 离线运行时、安装/卸载流程、USB 管理、Mihomo 节点管理和 Windows 通话播放集成。

本项目不是 DJI、Quectel、高通或运营商的官方软件。使用前请阅读仓库许可证与免责声明。

## 安装包包含

- VOHIVE for Windows 图形控制台与系统托盘程序。
- 独立的 Ubuntu 24.04 WSL2 发行版，发行版名称为 `VoHive`。
- VoHive Linux amd64 核心与内置 Web 管理后台。
- Mihomo Linux amd64 与本地 SOCKS5 端口 `7891`。
- usbipd-win，用于把 EC20 USB 设备转发到 WSL2。
- Microsoft WSL 运行时与 Microsoft Edge WebView2 Runtime。
- Windows `ffplay.exe`，用于实时播放通话下行音频。
- 通话监听、WAV 录音、局域网访问和设备检测重置脚本。

安装程序默认安装到 `C:\Program Files (x86)\VOHIVE for Windows`，安装时可以修改。WSL 数据默认保存在 `C:\ProgramData\VOHIVE for Windows\WSL`。

## 系统要求

- Windows 11 x64，BIOS/UEFI 已开启硬件虚拟化。
- 管理员权限，用于启用 WSL、安装驱动、绑定 USB、配置防火墙和端口转发。
- DJI 第一代 4G 模块或可由 usbipd-win 识别的 Quectel EC20/兼容设备。
- 可用的 VoWiFi/IMS SIM 或 eSIM，以及符合运营商要求的英国等出口网络。
- 安装阶段建议保持联网；安装包已携带主要运行组件，但 Windows 功能启用和系统兼容性仍由本机状态决定。

## 安装步骤

1. 在 Releases 下载并运行 `VOHIVE-for-Windows-Setup-v1.1.0.exe`。
2. 接受 UAC 提示，选择安装目录并继续。
3. 安装器会启用 `Microsoft-Windows-Subsystem-Linux` 和 `VirtualMachinePlatform`，安装 WSL、usbipd-win 与 WebView2。
4. 安装器导入独立发行版 `VoHive`，启用 `mihomo.service` 和 `vohive.service`。
5. 如果 Windows 要求重启，安装器会注册一次性的继续安装任务；重启并登录后等待安装完成。
6. 双击桌面 `VOHIVE for Windows` 快捷方式。程序会静默启动 WSL、Mihomo 和 VoHive，不显示终端窗口。

首次后台账号为 `admin`，密码为 `admin`。第一次登录后必须立即修改密码。

## 第一次使用

### 1. 连接模块

把 DJI 第一代模块连接到 Windows。打开控制台后，如果显示“等待设备枚举”，点击“重置 VoHive 设备检测”。该操作会重新执行 usbipd bind/attach、等待 `/dev/cdc-wdm0` 和 `/dev/ttyUSB*`，并重启 VoHive 设备扫描。

### 2. 配置前置代理

安装包不包含任何私人机场节点。进入“代理管理”，添加或导入自己的 Mihomo/Clash 节点。支持无限数量节点、手动切换、自动切换、单个测速和全部测速。

VoHive 使用 Mihomo 暴露的 `127.0.0.1:7891` SOCKS5 出口。原始节点可以是 Trojan、AnyTLS 或 Mihomo 支持的其他协议；控制台负责写入 Mihomo 配置，VoHive 只连接本地 SOCKS5。

自动切换会按节点可用性和 VoWiFi 连接结果选择候选节点。测速仅表示测试目标的网络往返时间，不等同于 VoWiFi 一定能够注册；运营商 ePDG、出口国家、UDP、SNI 和节点稳定性都会影响结果。

### 3. 开启 VoWiFi

进入内嵌的 VoHive 后台，确认 SIM 已就绪，再启用 VoWiFi。正常链路依次包括 SIM、Access、IKE Tunnel、IMS 和 SMS。遇到 IKE 硬超时，先检查代理节点是否可用并尝试切换节点，不要反复覆盖运行中的配置文件。

### 4. 通话监听

控制中心提供两种互斥模式：

- “只通话监听，不播放声音”：接收通话音频并保存 WAV，不在电脑扬声器播放。
- “播放声音并监听”：在保存 WAV 的同时，通过内置 `ffplay.exe` 实时播放下行音频。

关闭主窗口只会隐藏到系统托盘，监听继续运行。双击托盘图标可恢复窗口；右键菜单包含“展开主页面”和“退出”。只有选择“退出”才会完全关闭后台。

QQ Bot 配置属于用户私有配置。配置完成后，可用 `/vocall <设备名> <号码> <秒数>` 发起允许的 VoWiFi 通话，并在接通后发送数字或 `/dtmf <数字>` 操作按键菜单。请遵守当地法律、运营商条款和号码限制。

## 控制台功能

| 功能 | 说明 |
| --- | --- |
| 启动 VoHive | 启动独立 WSL、Mihomo、VoHive 和后台保持逻辑。 |
| 局域网访问 | 开关 Windows `7575` 端口转发和仅限 LocalSubnet 的防火墙规则。 |
| 重置设备检测 | 重新绑定/挂载 EC20 并重启设备扫描。 |
| 代理管理 | 添加、导入、编辑、删除、手动/自动切换节点。 |
| 单个/全部测速 | 独立测试节点，测速期间不冻结整个界面。 |
| 通话监听 | 录音模式或实时播放模式，两个模式不会同时抢占 RTP。 |
| VoHive 后台 | 在应用第二页内嵌打开 `http://vohive-wsl:7575`。 |

局域网设备访问时应使用 Windows 电脑的局域网 IP，例如 `http://192.168.x.x:7575`，不能使用访问设备自己的 `localhost`。不要把 7575 直接暴露到公网。

## 隐私与安全

公开仓库和安装包默认不包含代理节点、节点密码、QQ Bot 密钥、OpenID、邮箱、SIM/eSIM 数据、IMEI、短信、日志或录音。初始 Mihomo 配置只有 `DIRECT`，通知渠道全部关闭。

通话录音和运行日志保存在本机。发布问题报告前应删除号码、短信内容、鉴权信息和运营商身份数据。

## AI Agent 协助安装

本项目可以由具备 Windows、PowerShell、WSL2 和 GitHub 操作能力的 AI Agent 协助安装与排障。建议让 Agent 先读取本 README 和 `deploy/windows-wsl-ec20/README.md`，再检查 WSL 发行版、usbipd 状态、systemd 服务和安装日志；不要把私人节点、Bot Token 或 SIM 身份信息提交到公开任务。

## 开发与构建

Windows 控制台源码位于 `deploy/windows-wsl-ec20/control-center`，部署脚本位于 `deploy/windows-wsl-ec20/scripts`，安装器位于 `deploy/windows-wsl-ec20/installer`。前端源码在 `web`，正式构建输出嵌入 Go Web 后台。

详细脚本说明见 `deploy/windows-wsl-ec20/README.md`，控制台开发说明见 `deploy/windows-wsl-ec20/control-center/README.md`。

## License

沿用上游项目许可证。详见仓库中的 `LICENSE` 与源文件声明。VoHive 的非商业限制、第三方组件许可证和免责声明在再发布时必须保留。
