using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VoHiveControl;

public partial class Form1 : Form
{
    private readonly string _wslDistro = InstallLayout.ResolveDistroName();
    private const string BackendHost = "vohive-wsl";
    private const int BackendPort = 7575;

    private readonly string _toolRoot = InstallLayout.ResolveToolScriptRoot();
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(4)
    };
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _proxyTimer = new() { Interval = 15000 };
    private readonly List<string> _activity = [];

    private WebView2 _dashboard = null!;
    private WebView2 _backend = null!;
    private Panel _contentHost = null!;
    private Button _dashboardNav = null!;
    private Button _proxyNav = null!;
    private Button _backendNav = null!;
    private Label _shellStatus = null!;
    private NotifyIcon _trayIcon = null!;
    private ContextMenuStrip _trayMenu = null!;
    private Icon? _trayOwnedIcon;
    private bool _dashboardReady;
    private bool _isBusy;
    private bool _isOnline;
    private bool _lanEnabled;
    private bool _exitRequested;
    private bool _trayHintShown;
    private FormWindowState _windowStateBeforeHide = FormWindowState.Normal;
    private string _listenerMode = "未启动";
    private string _activePage = "control";

    private string BackendUrl => $"http://{BackendHost}:{BackendPort}";

    public Form1()
    {
        InitializeComponent();
        BuildShell();
        BuildTrayIcon();
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _proxyTimer.Tick += async (_, _) => await MonitorProxyAsync();
        Shown += async (_, _) => await StartOnOpenAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        _trayOwnedIcon?.Dispose();
        _statusTimer.Stop();
        _proxyTimer.Stop();
        _http.Dispose();
        base.OnFormClosed(e);
    }

    private void BuildTrayIcon()
    {
        _trayMenu = new ContextMenuStrip { ShowImageMargin = false };
        var openItem = new ToolStripMenuItem("展开主页面");
        var exitItem = new ToolStripMenuItem("退出");
        openItem.Click += (_, _) =>
        {
            ShowDashboard();
            RestoreFromTray();
        };
        exitItem.Click += (_, _) => ExitApplication();
        _trayMenu.Items.AddRange([openItem, exitItem]);

        _trayOwnedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Icon = _trayOwnedIcon ?? SystemIcons.Application;
        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = _trayMenu,
            Icon = _trayOwnedIcon ?? SystemIcons.Application,
            Text = "VOHIVE for Windows - 后台运行中",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.BalloonTipClicked += (_, _) => RestoreFromTray();
    }

    private void HideToTray()
    {
        if (WindowState != FormWindowState.Minimized)
            _windowStateBeforeHide = WindowState;
        ShowInTaskbar = false;
        Hide();
        if (_trayHintShown) return;

        _trayHintShown = true;
        _trayIcon.ShowBalloonTip(
            2500,
            "VoHive 仍在后台运行",
            "双击托盘图标可恢复窗口，右键选择退出可完全关闭。",
            ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        if (!Visible) Show();
        WindowState = _windowStateBeforeHide;
        Activate();
        BringToFront();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _trayIcon.Visible = false;
        Close();
    }

    private void BuildShell()
    {
        Text = "VOHIVE for Windows";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1080, 720);
        Size = new Size(1240, 800);
        BackColor = Color.FromArgb(244, 246, 248);
        Font = new Font("Microsoft YaHei UI", 9F);

        var rail = new Panel
        {
            Dock = DockStyle.Left,
            Width = 224,
            BackColor = Color.FromArgb(19, 27, 43),
            Padding = new Padding(16, 22, 16, 18)
        };
        var appName = new Label
        {
            AutoSize = true,
            Text = "VOHIVE for Windows",
            Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(16, 18)
        };
        var appHint = new Label
        {
            AutoSize = true,
            Text = "WSL · EC20 · VoWiFi",
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(18, 48)
        };
        _dashboardNav = CreateNavButton("控制中心");
        _dashboardNav.Location = new Point(16, 102);
        _dashboardNav.Click += (_, _) => ShowDashboard();
        _proxyNav = CreateNavButton("代理管理");
        _proxyNav.Location = new Point(16, 148);
        _proxyNav.Click += async (_, _) => await ShowProxyManagerAsync();
        _backendNav = CreateNavButton("VoHive 后台");
        _backendNav.Location = new Point(16, 194);
        _backendNav.Click += async (_, _) => await ShowBackendAsync();
        _shellStatus = new Label
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            Height = 52,
            Location = new Point(16, rail.Height - 78),
            Width = rail.Width - 32,
            ForeColor = Color.FromArgb(148, 163, 184),
            Text = "● 正在检查服务",
            TextAlign = ContentAlignment.MiddleLeft
        };
        rail.Resize += (_, _) => _shellStatus.Top = rail.Height - 76;
        rail.Controls.AddRange([appName, appHint, _dashboardNav, _proxyNav, _backendNav, _shellStatus]);

        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(244, 246, 248) };
        _dashboard = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(244, 246, 248) };
        _backend = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White, Visible = false };
        _contentHost.Controls.Add(_backend);
        _contentHost.Controls.Add(_dashboard);
        Controls.Add(_contentHost);
        Controls.Add(rail);
        SetNavigation("control");
    }

    private static Button CreateNavButton(string text) => new()
    {
        Width = 192,
        Height = 38,
        Text = text,
        AccessibleName = text,
        AccessibleRole = AccessibleRole.PushButton,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(14, 0, 0, 0),
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderSize = 0 },
        BackColor = Color.FromArgb(19, 27, 43),
        ForeColor = Color.FromArgb(203, 213, 225),
        Font = new Font("Microsoft YaHei UI", 10F),
        Cursor = Cursors.Hand
    };

    private async Task StartOnOpenAsync()
    {
        await InitializeWebViewsAsync();
        AddActivity("控制台已打开，正在启动核心服务");
        await RefreshStatusAsync();
        await InitializeProxyManagerAsync();
        _statusTimer.Start();
        _proxyTimer.Start();
        _ = StartCoreAsync(manual: false);
    }

    private async Task InitializeWebViewsAsync()
    {
        try
        {
            await _dashboard.EnsureCoreWebView2Async();
            _dashboard.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _dashboard.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _dashboard.CoreWebView2.WebMessageReceived += DashboardMessageReceived;
            _dashboard.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _dashboardReady = true;
                PublishState();
            };
            _dashboard.NavigateToString(LoadDashboardHtml());

            _backend.Visible = false;
            _dashboard.Visible = true;
            _dashboard.BringToFront();
            SetNavigation("control");
        }
        catch (Exception ex)
        {
            AddActivity("界面浏览组件未能初始化");
            Debug.WriteLine(ex);
        }
    }

    private async void DashboardMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            if (!document.RootElement.TryGetProperty("action", out var actionElement)) return;
            switch (actionElement.GetString())
            {
                case "start":
                    await StartCoreAsync(manual: true);
                    break;
                case "lan":
                    await ToggleLanAsync();
                    break;
                case "reset":
                    await ResetDeviceAsync();
                    break;
                case "listen-silent":
                    await StartListenerAsync(playLive: false);
                    break;
                case "listen-live":
                    await StartListenerAsync(playLive: true);
                    break;
                case "backend":
                    await ShowBackendAsync();
                    break;
                case "proxy-page":
                    await ShowProxyManagerAsync();
                    break;
                case "proxy-refresh":
                    await RefreshProxyConfigurationAsync(showMessage: true);
                    break;
                case "proxy-import":
                    await ImportProxyYamlAsync(document.RootElement.GetProperty("yaml").GetString() ?? "");
                    break;
                case "proxy-save":
                    await SaveProxyNodeAsync(document.RootElement.GetProperty("node"));
                    break;
                case "proxy-delete":
                    await DeleteProxyNodeAsync(document.RootElement.GetProperty("name").GetString() ?? "");
                    break;
                case "proxy-switch":
                    await SwitchProxyAsync(document.RootElement.GetProperty("name").GetString() ?? "", automatic: false);
                    break;
                case "proxy-auto":
                    await SetProxyAutoModeAsync(document.RootElement.GetProperty("enabled").GetBoolean());
                    break;
                case "proxy-test-all":
                    await TestAllProxiesAsync();
                    break;
                case "proxy-test-one":
                    await TestOneProxyAsync(document.RootElement.GetProperty("name").GetString() ?? "");
                    break;
            }
        }
        catch (Exception ex)
        {
            AddActivity("操作没有完成");
            Debug.WriteLine(ex);
            PublishState();
        }
    }

    private async Task StartCoreAsync(bool manual)
    {
        if (_isBusy) return;
        if (!Directory.Exists(_toolRoot))
        {
            AddActivity("未找到现有 VoHive 工具目录");
            PublishState();
            return;
        }

        SetBusy(true);
        try
        {
            AddActivity(manual ? "正在重新检查 WSL 和 VoHive" : "正在启动 WSL 保持进程");
            AddActivity("正在检查 EC20 设备连接");
            AddActivity("正在启动 Mihomo 前置代理");
            AddActivity("正在启动 VoHive 服务");
            await RunPowerShellAsync("Start VoHive WSL.ps1", "-NoPause");
            AddActivity("正在检查 VoWiFi 注册状态");
            await RefreshStatusAsync();
            AddActivity("核心服务已经准备完成");
        }
        catch (Exception ex)
        {
            AddActivity("启动没有完成，请检查服务状态");
            Debug.WriteLine(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ToggleLanAsync()
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            AddActivity(_lanEnabled ? "正在关闭局域网访问" : "正在开启局域网访问");
            await RunPowerShellAsync(_lanEnabled ? "Disable VoHive LAN Access.ps1" : "Enable VoHive LAN Access.ps1");
            _lanEnabled = !_lanEnabled;
            AddActivity(_lanEnabled ? "局域网访问已开启" : "局域网访问已关闭");
        }
        catch (Exception ex)
        {
            AddActivity("局域网访问状态未改变");
            Debug.WriteLine(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ResetDeviceAsync()
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            AddActivity("正在重置 VoHive 设备检测");
            await RunPowerShellAsync("Reset VoHive Device Detect.ps1");
            AddActivity("设备检测已重置，正在恢复服务");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            AddActivity("设备检测重置未完成");
            Debug.WriteLine(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StartListenerAsync(bool playLive)
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            AddActivity("正在切换通话监听模式");
            await StopListenerAsync();
            StartListenerAsStandardUser(playLive);
            _listenerMode = playLive ? "播放声音并监听" : "只通话监听";
            AddActivity(playLive ? "已开启实时播放和通话监听" : "已开启静默通话监听");
        }
        catch (Exception ex)
        {
            AddActivity($"通话监听未能切换：{ex.Message}");
            Debug.WriteLine(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StopListenerAsync()
    {
        var command = $"Get-CimInstance Win32_Process | Where-Object {{ $_.Name -eq 'powershell.exe' -and $_.CommandLine -like '*Start VoHive Call Listener.ps1*' }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force }}; wsl.exe -d '{_wslDistro.Replace("'", "''")}' -- bash -lc \"pkill -f '[v]ohive_call_asr_sidecar.py' 2>/dev/null || true\"";
        await RunPowerShellCommandAsync(command);
        await Task.Delay(500);
    }

    // The control panel is elevated, but the long-running listener does not need elevation.
    // Start it with Explorer's medium-integrity token and without creating a console window.
    private void StartListenerAsStandardUser(bool playLive)
    {
        var scriptPath = Path.Combine(_toolRoot, "Start VoHive Call Listener.ps1");
        if (!File.Exists(scriptPath)) throw new FileNotFoundException("未找到通话监听脚本", scriptPath);

        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var commandLine = new StringBuilder(
            $"\"{powershellPath}\" -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"");
        if (playLive) commandLine.Append(" -PlayLive -LiveTcpPort 19090");

        using var currentProcess = Process.GetCurrentProcess();
        using var explorer = Process.GetProcessesByName("explorer")
            .FirstOrDefault(process => process.SessionId == currentProcess.SessionId)
            ?? throw new InvalidOperationException("未找到当前桌面的 Explorer 进程");

        if (!OpenProcessToken(
                explorer.Handle,
                TokenDuplicate | TokenQuery,
                out var explorerToken))
        {
            throw new InvalidOperationException($"无法读取普通用户令牌，错误代码 {Marshal.GetLastWin32Error()}");
        }

        if (!DuplicateTokenEx(
                explorerToken,
                TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId,
                IntPtr.Zero,
                SecurityImpersonation,
                TokenPrimary,
                out var listenerToken))
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(explorerToken);
            throw new InvalidOperationException($"无法复制普通用户令牌，错误代码 {error}");
        }

        var startupInfo = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Desktop = "winsta0\\default",
            Flags = StartfUseShowWindow,
            ShowWindow = SwHide
        };

        try
        {
            if (!CreateProcessAsUserW(
                    listenerToken,
                    powershellPath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateNoWindow | CreateUnicodeEnvironment,
                    IntPtr.Zero,
                    _toolRoot,
                    ref startupInfo,
                    out var processInfo))
            {
                var createAsUserError = Marshal.GetLastWin32Error();
                commandLine = new StringBuilder(
                    $"\"{powershellPath}\" -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"");
                if (playLive) commandLine.Append(" -PlayLive -LiveTcpPort 19090");

                if (!CreateProcessWithTokenW(
                        listenerToken,
                        LogonWithProfile,
                        powershellPath,
                        commandLine,
                        CreateNoWindow | CreateUnicodeEnvironment,
                        IntPtr.Zero,
                        _toolRoot,
                        ref startupInfo,
                        out processInfo))
                {
                    throw new InvalidOperationException(
                        $"无法静默启动普通用户监听，错误代码 {Marshal.GetLastWin32Error()}（备用方式：{createAsUserError}）");
                }
            }

            CloseHandle(processInfo.ThreadHandle);
            CloseHandle(processInfo.ProcessHandle);
        }
        finally
        {
            CloseHandle(listenerToken);
            CloseHandle(explorerToken);
        }
    }

    private async Task RefreshStatusAsync()
    {
        _isOnline = await BackendIsOnlineAsync();
        _shellStatus.Text = _isOnline ? "● VoHive 服务可用" : "● 正在等待 VoHive";
        _shellStatus.ForeColor = _isOnline ? Color.FromArgb(110, 231, 183) : Color.FromArgb(251, 191, 36);

        try
        {
            var output = await RunProcessAsync("wsl.exe", $"-d {_wslDistro} -- systemctl is-active vohive mihomo", TimeSpan.FromSeconds(5));
            if (!_isOnline && output.StandardOutput.Contains("active", StringComparison.OrdinalIgnoreCase))
            {
                AddActivity("VoHive 正在等待后台页面响应");
            }
        }
        catch
        {
            if (!_isOnline) AddActivity("正在等待 WSL 服务");
        }

        _lanEnabled = await DetectLanAccessAsync();
        _listenerMode = DetectListenerMode();
        PublishState();
    }

    private async Task<bool> BackendIsOnlineAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"{BackendUrl}/ping");
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> DetectLanAccessAsync()
    {
        try
        {
            var result = await RunProcessAsync("netsh.exe", "interface portproxy show v4tov4", TimeSpan.FromSeconds(3));
            return result.StandardOutput.Split('\n').Any(line => line.Contains("7575") && !line.Contains("127.0.0.1"));
        }
        catch
        {
            return false;
        }
    }

    private static string DetectListenerMode()
    {
        try
        {
            using var listenerMutex = Mutex.OpenExisting("Global\\VoHiveCallListener");
            return Process.GetProcessesByName("ffplay").Length > 0
                ? "播放声音并监听"
                : "只通话监听";
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return "未启动";
        }
        catch (UnauthorizedAccessException)
        {
            return "监听运行中";
        }
    }

    private void ShowDashboard()
    {
        _activePage = "control";
        _backend.Visible = false;
        _dashboard.Visible = true;
        _dashboard.BringToFront();
        SetNavigation("control");
        PublishState();
    }

    private async Task ShowProxyManagerAsync()
    {
        _activePage = "proxy";
        _backend.Visible = false;
        _dashboard.Visible = true;
        _dashboard.BringToFront();
        SetNavigation("proxy");
        await RefreshProxyConfigurationAsync(showMessage: false);
        PublishState();
    }

    private async Task ShowBackendAsync()
    {
        if (!await BackendIsOnlineAsync())
        {
            AddActivity("后台页面仍在等待服务");
            PublishState();
            return;
        }

        if (_backend.CoreWebView2 is null)
        {
            await _backend.EnsureCoreWebView2Async();
            if (_backend.CoreWebView2 is not { } backendCore)
                throw new InvalidOperationException("VoHive 后台浏览组件未能初始化");
            backendCore.Settings.AreDevToolsEnabled = false;
        }

        _dashboard.Visible = false;
        _backend.Visible = true;
        _backend.BringToFront();
        _activePage = "backend";
        SetNavigation("backend");
        if (_backend.CoreWebView2 is not null && _backend.Source?.ToString() != BackendUrl + "/")
        {
            _backend.Source = new Uri(BackendUrl);
        }
    }

    private void SetNavigation(string selectedPage)
    {
        StyleNavButton(_dashboardNav, selectedPage == "control");
        StyleNavButton(_proxyNav, selectedPage == "proxy");
        StyleNavButton(_backendNav, selectedPage == "backend");
    }

    private static void StyleNavButton(Button button, bool selected)
    {
        button.BackColor = selected ? Color.FromArgb(36, 52, 74) : Color.FromArgb(19, 27, 43);
        button.ForeColor = selected ? Color.White : Color.FromArgb(203, 213, 225);
        button.Font = new Font("Microsoft YaHei UI", 10F, selected ? FontStyle.Bold : FontStyle.Regular);
    }

    private async Task RunPowerShellAsync(string scriptName, string arguments = "")
    {
        var scriptPath = Path.Combine(_toolRoot, scriptName);
        if (!File.Exists(scriptPath)) throw new FileNotFoundException("未找到所需脚本", scriptPath);
        var commandArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {arguments}";
        var result = await RunProcessAsync("powershell.exe", commandArgs, TimeSpan.FromMinutes(4));
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
    }

    private async Task RunPowerShellCommandAsync(string command)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var result = await RunProcessAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}", TimeSpan.FromSeconds(20));
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
    }

    private static Task<ProcessResult> RunProcessAsync(string executable, string arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        return RunProcessAsync(startInfo, timeout);
    }

    private static Task<ProcessResult> RunProcessAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (standardInput is not null)
            startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return RunProcessAsync(startInfo, timeout, standardInput);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        string? standardInput = null)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }
        using var timeoutCts = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(timeoutCts.Token);
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        PublishState();
    }

    private void AddActivity(string message)
    {
        if (_activity.LastOrDefault() == message) return;
        if (_activity.Count >= 7) _activity.RemoveAt(0);
        _activity.Add(message);
        PublishState();
    }

    private void PublishState()
    {
        if (!_dashboardReady || _dashboard.CoreWebView2 is null) return;
        var payload = new
        {
            type = "state",
            state = new
            {
                online = _isOnline,
                busy = _isBusy,
                lanEnabled = _lanEnabled,
                listenerMode = _listenerMode,
                page = _activePage,
                activities = _activity.ToArray(),
                backendUrl = BackendUrl,
                proxyMode = _proxyMode,
                currentProxy = _currentProxyName,
                proxyHealth = _proxyHealth,
                proxyBusy = _proxyOperationBusy,
                proxies = _proxyNodes.Select(node => new
                {
                    node.Name,
                    node.Type,
                    node.Server,
                    node.Port,
                    node.Udp,
                    node.Sni,
                    node.SkipCertVerify,
                    node.Username,
                    node.Cipher,
                    node.HasCredential,
                    LatencyMs = _proxyLatencies.TryGetValue(node.Name, out var latency) ? latency.Milliseconds : null,
                    LatencyState = _proxyLatencies.TryGetValue(node.Name, out latency) ? latency.State : "untested"
                }).ToArray(),
                socksEndpoint = "127.0.0.1:7891",
                proxyNotice = _proxyNotice,
                proxyTestBusy = _proxyTestBusy
            }
        };
        _dashboard.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    private static string LoadDashboardHtml()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("Assets.Dashboard.html", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Dashboard resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const int LogonWithProfile = 0x00000001;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfUseShowWindow = 0x00000001;
    private const short SwHide = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        IntPtr token,
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token,
        int logonFlags,
        string? applicationName,
        StringBuilder commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
