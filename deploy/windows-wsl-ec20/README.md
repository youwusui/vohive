# 基于 Windows WSL 运行的 VOHIVE 项目，适用于大疆第一代模块，移远 EC20

这是一个面向 Windows 11 + WSL2 的部署包，配合 DJI 第一代模块中使用的 Quectel EC20/兼容 EC25 模块运行 VoHive。它把 USB 设备转发、WSL 服务、VoHive Web 管理端、Mihomo 前置代理、VoWiFi 状态等待、QQ Bot 拨号/按键、通话录音和 Windows 实时播放串起来。

本目录只包含可公开发布的脚本和配置模板。真实的代理节点密码、QQ Bot 密钥、OpenID、VoHive 管理密码和本机日志不应上传到 GitHub。

## 功能入口

| 文件 | 用途 |
| --- | --- |
| `初始化配置.cmd` | 第一次使用时生成本地 `config/vohive-wsl.json`。 |
| `启动 VoHive WSL.cmd` | 单独启动 WSL、挂载 EC20、启动 Mihomo 和 VoHive，并建立本机 `7575` 端口转发。 |
| `启动 VoHive WSL + 实时通话监听.cmd` | 一键启动 WSL/VoHive 后，再启动通话监听和 Windows 实时播放。 |
| `启动 VoHive 通话监听.cmd` | 不碰 WSL 服务，只启动通话监听；默认同时尝试在 Windows 播放下行 AMR 音频。 |
| `开启 VoHive 局域网访问.cmd` | 将 VoHive `7575` 映射到当前局域网 IPv4，并创建仅允许 LocalSubnet 的 Windows 防火墙规则。 |
| `关闭 VoHive 局域网访问.cmd` | 删除局域网映射和对应防火墙规则，保留本机 `http://localhost:7575`。 |
| `重置 VoHive 设备检测.cmd` | 重新 detach/bind/attach EC20，再等待 `/dev/cdc-wdm0` 和 `/dev/ttyUSB*` 出现，最后重启 VoHive。 |

## 运行架构

```text
EC20 USB 模块
    |
    | usbipd-win
    v
Windows 11 <-> WSL2 Ubuntu
                    |
                    +-- mihomo.service  (前置 SOCKS5 出口)
                    +-- vohive.service  (Web/API、SIM、IMS、VoWiFi)
                    +-- vohive_call_asr_sidecar.py
                              |
                              +-- QQ Bot /vocall 和 DTMF
                              +-- RTP -> AMR/WAV 文件
                              +-- TCP AMR -> Windows ffplay 实时播放
```

启动脚本不会在启动时写入代理策略，也不会 PATCH VoWiFi 配置。它只检查和启动尚未运行的服务，等待现有配置自动恢复，并在需要时更新 Windows 到 WSL 的本机端口转发。

## 一、前置条件

### Windows

- Windows 11，已启用虚拟化。
- WSL2 和一个已安装的 Ubuntu 发行版；发行版名称必须与配置中的 `distro` 一致。
- `systemd` 已在 WSL 中启用，因为 `vohive.service` 和 `mihomo.service` 由 systemd 管理。
- 已安装 `usbipd-win`，默认路径为 `C:\Program Files\usbipd-win\usbipd.exe`。
- 当前 Windows 用户可以运行管理员 PowerShell。设备挂载、端口转发和防火墙操作会触发 UAC。
- 如需实时播放，Windows 中需要 `ffplay.exe`。可安装 FFmpeg 并把 `ffplay.exe` 放入 PATH，或在配置模板的 `ffplay_candidates` 中填写实际路径。

### WSL/Ubuntu

WSL 中应已准备好 VoHive 和 Mihomo 的 systemd 服务：

```bash
systemctl is-active vohive
systemctl is-active mihomo
```

两个服务都应能在手动启动后保持运行。VoHive 的默认目录是 `/opt/vohive`，监听端口是 `7575`；如果你的安装位置不同，请修改配置文件对应字段。

监听器运行所需的 Python 包通常包括 `requests`、`PyYAML` 和 RTP/音频处理所需工具。默认功能是录音和实时播放，不启用 Vosk 识别时不需要导入 `vosk`；只有明确使用 `--asr-vosk` 时才需要安装 Vosk 模型。

### Mihomo SOCKS5 前置代理

本项目不附带机场订阅、代理节点或真实 Mihomo 配置。用户需要自行取得合法可用的机场订阅或节点，并在 WSL 中配置 Mihomo。VoHive 不直接读取机场节点；Mihomo 负责连接上游节点，再向 VoHive 提供本机 SOCKS5 出口。

1. 将机场订阅转换或导出为 Mihomo/Clash Meta 支持的配置格式。节点协议、服务器、端口、密码和订阅地址只保存在自己的私有配置中。
2. 将私有配置放到 Mihomo 服务实际使用的位置。常见位置是 `/etc/mihomo/config.yaml`，但应以 `systemctl cat mihomo` 中的 `ExecStart` 参数为准。
3. 确保 Mihomo 在 WSL 本机开放 SOCKS5 端口。下面只是一段不含节点的端口示例：

   ```yaml
   socks-port: 7891
   allow-lan: false
   mode: rule
   ```

4. 在自己的 Mihomo 配置中选择目标代理节点或代理组，并让需要的流量通过该节点。不要把真实 `proxies`、`proxy-providers`、订阅 URL 或认证信息复制进本仓库。
5. 重启并检查 Mihomo：

   ```bash
   sudo systemctl restart mihomo
   systemctl is-active mihomo
   ss -lntp | grep 7891
   curl --socks5-hostname 127.0.0.1:7891 https://api.ipify.org
   ```

6. 将私有 `config/vohive-wsl.json` 中的 `proxy.socks5` 设置为 `127.0.0.1:7891`。这个字段只告诉启动脚本从哪个 SOCKS5 端口检查出口，不包含机场节点本身。

如果使用其他 SOCKS5 端口，请同时修改 Mihomo 和 `proxy.socks5`。建议只监听回环地址，不要把未经认证的 SOCKS5 端口直接开放到局域网或公网。

### 使用 AI Agent 协助安装

本项目可以通过具备终端和文件操作能力的 AI Agent 协助安装、配置与排错，例如检查 WSL2、`usbipd-win`、systemd 服务、Mihomo SOCKS5 端口、VoHive 日志和 Windows 启动脚本。建议让 AI Agent 按本文步骤逐项执行并在每一步输出检查结果，不要一次性运行来源不明的高权限命令。

向 AI Agent 提供信息时，应隐藏机场订阅 URL、节点密码、QQ Bot 密钥、OpenID、邮箱凭证和运营商账户信息。真实 `config/vohive-wsl.json`、Mihomo 配置、日志与录音默认只保存在本机，不应上传到公开仓库或直接粘贴到公共对话中。

### 模块连接

1. 关闭会占用 EC20 USB 设备的其他虚拟机或工具。
2. 将模块接入 Windows。
3. 在管理员 PowerShell 中确认：

   ```powershell
   usbipd list
   ```

4. 设备行应包含 EC20、EC25、Quectel、QDC507 或配置中的其他匹配关键字。

## 二、第一次配置

1. 双击 `初始化配置.cmd`。
2. 打开生成的 `config/vohive-wsl.json`。
3. 根据本机环境修改配置。这个文件已经被 `.gitignore` 排除，不要将它强行加入 Git。

最少需要确认：

```json
{
  "distro": "Ubuntu-24.04",
  "listen_port": 7575,
  "live_tcp_port": 19090,
  "usbipd_path": "C:\\Program Files\\usbipd-win\\usbipd.exe",
  "module_match_regex": "2c7c:0125|Baiwang|Quectel|EC20|EC25|QDC507",
  "vohive": {
    "root": "/opt/vohive",
    "api_username": "admin",
    "api_password": ""
  }
}
```

### 配置字段说明

- `distro`：`wsl.exe -d` 使用的发行版名称，可用 `wsl -l -v` 查看。
- `listen_port`：VoHive Web/API 端口，默认 `7575`。
- `live_tcp_port`：监听器向 Windows 暴露 AMR 实时流的端口，默认 `19090`。
- `usbipd_path`：usbipd-win 可执行文件路径。
- `module_match_regex`：从 `usbipd list` 结果中识别 EC20 的正则表达式。
- `proxy.socks5`：仅用于出口检查的 SOCKS5 地址，例如 `127.0.0.1:7891`。不要把节点用户名或密码写入公开模板。
- `proxy.expected_country_code`：可选出口国家检查，英国为 `GB`；留空可关闭检查。
- `epdg_ip`：可选。只有你明确知道当前运营商 ePDG 地址并确认需要 hosts 覆盖时才填写，否则留空。
- `vohive.root`、`log_dir`、`rtp_dir`、`python`：WSL 中 VoHive 和监听器的路径。
- `vohive.api_password`：VoHive API 密码。为空时启动脚本不会执行 IMS/SMS API readiness 检查，但仍会启动服务和建立端口转发。
- `listener.default_duration`：没有收到 `/vocall` 时每个 RTP 会话的默认录音秒数。
- `listener.duration_padding`：收到 `/vocall ... 秒数` 后额外保留的秒数。
- `listener.record_root`：Windows 录音根目录。支持 `%USERPROFILE%` 等环境变量。
- `listener.ffplay_candidates`：Windows `ffplay.exe` 的搜索路径。

## 三、四种启动方式

### 1. 单独启动 WSL 和 VoHive

双击：

```text
启动 VoHive WSL.cmd
```

脚本依次执行：

1. 启动 WSL 发行版并检查 systemd。
2. 查找 EC20，必要时执行 `usbipd bind` 和 `usbipd attach --wsl`。
3. 等待 `/dev/cdc-wdm0` 与 `/dev/ttyUSB*` 出现。
4. 只启动未运行的 `mihomo.service` 和 `vohive.service`。
5. 将 Windows `127.0.0.1:7575` 转发到 WSL 的 `7575`。
6. 如果配置了 VoHive API 密码，则只读等待 VoWiFi、IMS、SMS 状态达到 ready。

完成后访问 `http://localhost:7575`。如果启用了 LAN 访问，则使用脚本输出的局域网地址。

### 2. 一键启动 WSL、VoHive、监听和实时播放

双击：

```text
启动 VoHive WSL + 实时通话监听.cmd
```

它先调用单独的 WSL/VoHive 启动流程，确认 `vohive` 和 `mihomo` 处于 active，再启动监听器。监听器通过 VoHive 日志发现 RTP relay 端口，接收下行 RTP，生成 AMR/WAV，并把 AMR 通过本机 TCP 端口交给 Windows `ffplay.exe`。

如果启动窗口提示 `ffplay.exe not found`，录音仍会继续生成；安装 FFmpeg 或修改 `ffplay_candidates` 后重新运行即可。

### 3. 单独启动通话监听

在 WSL/VoHive 已经正常运行时，双击：

```text
启动 VoHive 通话监听.cmd
```

这个入口不重新写代理、不修改 VoWiFi，只启动监听器。它使用全局 Mutex 防止重复启动多个 sidecar；如果已经有一个监听器运行，会直接提示并退出。

### 4. 局域网访问开关

双击：

```text
开启 VoHive 局域网访问.cmd
```

脚本会选择有默认网关的活动 IPv4 网卡，在该地址的 `7575` 端口创建 portproxy，并添加只允许 `LocalSubnet` 的入站 TCP 防火墙规则。关闭时双击：

```text
关闭 VoHive 局域网访问.cmd
```

局域网管理会暴露登录页面。不要在公共 Wi-Fi、端口映射或不可信网络中开启；使用后建议关闭。登录凭证失败时，先确认访问的是当前 VoHive 实例、账号密码与 WSL 中配置一致，并清除浏览器缓存的旧凭证。

## 四、QQ Bot 拨号和按键

监听器读取 VoHive 日志中的 QQ Bot `/vocall` 记录，并把后续 RTP 会话关联到本次呼叫。当前示例只建议测试允许的 `888`：

```text
/vocall eSIM 888 35
```

含义：设备名为 `eSIM`，被叫号码为 `888`，通话监听窗口按 35 秒加配置的 padding 记录。实际可用号码、主叫身份和运营商能力由模块、SIM/eSIM、VoWiFi/IMS 注册状态决定。

通话接通后，向 QQ Bot 发送单个数字或：

```text
2
/dtmf 2
```

监听器会把 DTMF RTP 事件发送给当前 RTP relay。数字菜单的响应仍由电话网络提供，QQ Bot 只负责传入按键；默认发布包不启用语音识别和文字推送。

## 五、录音和实时播放

每次监听启动会在 `listener.record_root` 下创建一个带时间戳的目录，例如：

```text
%USERPROFILE%\VoHive-Recordings\call-20260714-153000\
```

目录可能包含：

- `*.amr`：从 RTP 提取的 AMR-NB 原始音频。
- `*.wav`：由 FFmpeg 解码生成的 WAV 文件。
- `*.json`：本次会话的端口、帧数、文件路径和状态信息。
- `session.txt`：监听启动时间、播放端口和结束状态。

实时播放只处理下行音频，不会把 Windows 麦克风作为上行通道。Windows 播放依赖 `ffplay.exe`，声音输出到系统默认播放设备。若没有听到声音，先确认系统音量、默认输出设备、`ffplay.exe` 路径，以及日志中是否出现 RTP relay 和 `Windows live playback`。

## 六、日志位置

部署目录下的 `logs/` 由 `.gitignore` 排除，常见文件包括：

- `logs/start-vohive-wsl.log`：WSL、usbipd、服务、端口转发和 readiness 检查。
- `logs/start-vohive-combined.log`：一键启动编排过程。
- WSL 中 VoHive 的日志目录：由 `vohive.log_dir` 指定，默认 `/opt/vohive/logs`。
- WSL 中 RTP 中转目录：由 `vohive.rtp_dir` 指定，默认 `/opt/vohive/rtp-probe`。

常用检查命令：

```powershell
wsl -l -v
usbipd list
wsl -d Ubuntu-24.04 -- systemctl is-active mihomo vohive
wsl -d Ubuntu-24.04 -- ls -l /dev/cdc-wdm0 /dev/ttyUSB*
netsh interface portproxy show v4tov4
```

## 七、故障排查

### “设备检测不到”或一直等待枚举

1. 关闭占用模块的其他虚拟机。
2. 拔插 EC20，确认 `usbipd list` 中能看到它。
3. 以管理员运行 `重置 VoHive 设备检测.cmd`。
4. 检查 `/dev/cdc-wdm0` 和 `/dev/ttyUSB*` 是否出现。
5. 如果 USB 行显示已被其他客户端附加，先在其他环境 detach。

### 启动几秒后退出

按顺序检查：配置文件是否存在、发行版名称是否正确、usbipd 路径是否正确、systemd 是否可用、VoHive/Mihomo 是否能在 WSL 中手动保持 active。不要把真实路径硬编码到脚本；脚本使用自身目录和配置文件定位资源。

### IKE 硬超时、ePDG 会话退出、IMS/SMS 未就绪

这通常与网络出口、运营商 ePDG、SIM/eSIM Profile、时间同步或 VoWiFi 资格有关，不等同于 Windows 监听器故障。检查 Mihomo 的出口、DNS、运营商允许的 VoWiFi 条件和 VoHive 的 IMS 日志。发布版启动脚本不会强行写入代理策略或 PATCH VoWiFi，避免启动阶段破坏已经稳定的注册状态。

### 能拨号但 Windows 没声音

确认监听器正在运行且只运行一个实例；确认 `ffplay.exe` 可执行；检查监听器窗口是否打印 `Windows live playback: tcp://...:19090`；检查日志是否发现 `RTP relay`。先保留 WAV/AMR 录音，再处理播放链路。

### 没有 WAV 录音

确认 sidecar 使用的 `python`、`ffmpeg` 和 `vohive_rtp_probe.py` 可用，确认 `rtp_dir` 可写，并查看会话目录中的 `session.txt` 与 JSON。没有下行 RTP 时不会产生有效 WAV；这表示呼叫或 RTP relay 尚未建立，不应先判断为文件复制错误。

### 局域网访问失败或凭证失败

先在本机确认 `http://localhost:7575` 正常，再关闭并重新开启 LAN 映射。确认客户端与 Windows 在同一局域网，防火墙规则允许 LocalSubnet，访问的是脚本输出的当前 IPv4。凭证由 VoHive 自身验证，不是 Windows 登录密码。

### 出现 `-File ""` 或“路径的形式不合法”

不要从命令提示符手工拼接空的 `-File` 参数，直接双击本目录提供的 `.cmd` 入口。脚本使用 `%~dp0` 定位自身目录，并对带空格的 PowerShell 路径进行完整引用。

## 八、安全、隐私和合规

- 不要提交 `config/vohive-wsl.json`、日志、录音、QQ Bot `app_secret`、OpenID、邮箱密码、代理节点密码或运营商凭证。
- `开启 VoHive 局域网访问.cmd` 只适合受信任的家庭/实验室网络；完成管理后关闭。
- 通话录音可能包含个人信息和第三方通信内容，请遵守所在地隐私、电信和录音法律。
- VoHive、模块固件、运营商服务和第三方 Bot 平台可能有各自的许可证和服务条款。本部署包是实验性 Windows/WSL 集成脚本，不代表 DJI、Quectel、运营商或任何第三方的官方支持。
- 使用 VoWiFi、短信、拨号和代理功能前，请确认拥有相应 SIM/eSIM 和服务权限，并遵守当地法律及运营商条款。

## 九、开发和发布说明

此目录是部署辅助包，不替代 VoHive 核心源码。脚本按相对目录工作，可复制到其他 Windows 机器后重新生成本地配置。提交前请运行：

```powershell
[System.Management.Automation.Language.Parser]::ParseFile(
  (Resolve-Path '.\scripts\Start VoHive WSL.ps1'), [ref]$null, [ref]$null)
python -m py_compile .\scripts\vohive_call_asr_sidecar.py .\scripts\vohive_rtp_probe.py
```

发布包默认只录音和实时播放；语音识别代码保留为可选能力，必须显式传入 `--asr-vosk` 并提供模型，不会在普通启动流程中加载识别模型。
