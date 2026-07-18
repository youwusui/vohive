# Windows WSL2 / EC20 部署说明

本目录包含 VOHIVE for Windows 的 Windows 控制台、独立 WSL2 安装器和可单独运行的维护脚本。目标设备是 DJI 第一代 4G 模块中的 Quectel EC20/兼容型号。

## 推荐安装方式

普通用户应从 GitHub Releases 下载 `VOHIVE-for-Windows-Setup-v1.1.0.exe`。安装器会创建独立的 `VoHive` WSL 发行版，不要求用户提前准备 Ubuntu，也不会改动已有 `Ubuntu-24.04`。

安装包携带 Windows 控制台、Ubuntu 24.04 rootfs、VoHive、Mihomo、WSL MSI、usbipd-win、WebView2 和 Windows ffplay。默认路径是 `C:\Program Files (x86)\VOHIVE for Windows`，WSL 数据路径是 `C:\ProgramData\VOHIVE for Windows\WSL`。

## 目录结构

| 路径 | 用途 |
| --- | --- |
| `control-center/` | Windows Forms + WebView2 控制台源码。 |
| `scripts/` | WSL 启动、USB 重置、局域网开关和通话监听脚本。 |
| `config/` | 可公开发布的 Windows 配置模板。 |
| `installer/` | Inno Setup 源文件、安装/卸载 PowerShell 和空白 WSL 配置。 |

## 安装后的运行结构

```text
DJI 第一代模块 / Quectel EC20
        | usbipd-win
        v
Windows 11
        |
        +-- VOHIVE for Windows.exe / 系统托盘
        +-- ffplay.exe / 实时下行音频
        +-- localhost:7575 / 可选 LocalSubnet 端口转发
        |
        v
WSL2: VoHive
        +-- mihomo.service  -> 127.0.0.1:7891 SOCKS5
        +-- vohive.service  -> 0.0.0.0:7575 Web/API
        +-- /opt/vohive/rtp-probe / 通话音频
```

## 首次启动

1. 插入模块并启动 `VOHIVE for Windows`。
2. 如果模块尚未挂载，点击“重置 VoHive 设备检测”。首次 bind/attach 需要管理员权限。
3. 在代理管理页添加自己的节点。安装包不带任何节点或凭据。
4. 测试节点并选中一个可用出口；需要时启用自动切换。
5. 打开 VoHive 后台，使用初始账号 `admin/admin` 登录并立即修改密码。
6. 确认 SIM 就绪后启用 VoWiFi，观察 Access、Tunnel、IMS、SMS 状态。
7. 按需开启“只监听”或“播放声音并监听”。

## 代理管理

Mihomo 配置位于 WSL 的 `/opt/mihomo/config.yaml`。控制台支持 Mihomo/Clash 节点映射、节点数组或带 `proxies` 字段的 YAML。节点数量不设上限。

保存时先生成候选配置并检查 Mihomo；重启失败时应恢复旧配置。VoHive 配置中的前置代理固定使用 `127.0.0.1:7891` SOCKS5，因此 Trojan、AnyTLS 等上游协议都由 Mihomo 负责。

单个测速只锁定对应节点行，全部测速并发执行但界面其他管理功能保持可用。延迟值只用于比较节点连通性，不代表运营商 ePDG/IMS 一定可用。自动切换应以代理连通与 VoWiFi 注册结果共同判断。

## USB 与设备检测

`Reset VoHive Device Detect.ps1` 会查找 Quectel/DJI USB 设备，执行 usbipd bind/attach，等待 WSL 中出现 QMI/串口设备，并重启 VoHive。该操作不修改 SIM、eSIM、Mihomo 节点或通知配置。

如果一直“等待设备枚举”，检查：

- Windows 设备管理器是否能看到模块。
- `usbipd list` 是否列出设备。
- WSL 中是否出现 `/dev/cdc-wdm0`、`/dev/ttyUSB*`。
- 模块是否被其他虚拟机或串口程序占用。

## 局域网访问

开启局域网访问会创建 Windows portproxy 与仅允许 `LocalSubnet` 的防火墙规则。关闭后删除该映射，但保留本机访问。局域网客户端使用 Windows 主机的局域网 IPv4 和端口 7575。

不建议长期裸开，更不能转发到公网。后台账号密码是 VoHive 自身认证，与 Windows 端口转发无关。

## 通话监听与实时播放

`Start VoHive Call Listener.ps1` 使用全局互斥锁保证只运行一个 sidecar。它读取 QQ Bot `/vocall` 时长，按通话时长加缓冲录制 WAV，并处理 DTMF。

`-PlayLive` 模式额外启动安装目录内的 `Tools\ffmpeg\ffplay.exe`，从 WSL TCP 端口 19090 接收 AMR 下行音频。两个监听模式互斥，切换模式时先停止旧监听，避免抢占 RTP 或 QQ Bot 命令。

控制台以隐藏窗口方式运行 PowerShell 和 WSL。关闭主窗口只隐藏到托盘，监听与代理自动切换继续运行。

## 单独脚本入口

安装后的 `Tools\scripts` 仍保留以下脚本，便于排障或高级用户单独调用：

| 脚本 | 用途 |
| --- | --- |
| `Start VoHive WSL.ps1` | 单独启动 WSL、Mihomo、VoHive、USB 检查和 keepalive。 |
| `Start VoHive WSL With Live Listener.ps1` | 启动整套环境并打开实时播放监听。 |
| `Start VoHive Call Listener.ps1` | 只启动监听；加 `-PlayLive` 可实时播放。 |
| `Enable VoHive LAN Access.ps1` | 开启局域网 7575 管理。 |
| `Disable VoHive LAN Access.ps1` | 关闭局域网 7575 管理。 |
| `Reset VoHive Device Detect.ps1` | 重置 usbipd 与 VoHive 设备检测。 |

## 安装日志

安装脚本日志位于 `C:\ProgramData\VOHIVE for Windows\Logs\install.log`。控制台运行状态只显示简短阶段文字；需要排障时再查看日志，不在界面持续展示原始终端输出。

## 卸载

从 Windows 设置或开始菜单卸载。卸载器会退出控制台、删除 `VoHive` WSL 发行版、移除 hosts/portproxy/防火墙规则及安装目录。它不会注销或删除用户其他 WSL 发行版。

卸载 `VoHive` 发行版会删除其中的本地配置、短信数据库和运行数据，卸载前应自行备份需要保留的内容。

## 隐私边界

任何发布构建都不得包含代理节点、密码、UUID、SNI、QQ Bot Token/OpenID、邮箱、IMEI、SIM/eSIM 身份、短信、日志和录音。模板必须保持通知关闭、节点列表为空、Mihomo 只有 `DIRECT`。
