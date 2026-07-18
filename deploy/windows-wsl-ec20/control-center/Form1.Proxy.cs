using System.Net.Http.Headers;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace VoHiveControl;

public partial class Form1
{
    private const string MihomoConfigPath = "/opt/mihomo/config.yaml";
    private const int AutoFailureThreshold = 3;
    private static readonly TimeSpan AutoSwitchGrace = TimeSpan.FromSeconds(90);

    private readonly List<ProxyNodeView> _proxyNodes = [];
    private readonly Dictionary<string, ProxyLatencyResult> _proxyLatencies = new(StringComparer.Ordinal);
    private bool _proxyStateLoaded;
    private bool _hasPersistedProxyState;
    private bool _proxyOperationBusy;
    private bool _proxyTestBusy;
    private string _proxyMode = "manual";
    private string? _currentProxyName;
    private string _proxyHealth = "正在读取代理配置";
    private string _proxyNotice = "";
    private int _proxyFailureCount;
    private DateTimeOffset _lastProxySwitchAt = DateTimeOffset.MinValue;

    private string ProxyStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoHive Control", "proxy-state.json");

    private async Task InitializeProxyManagerAsync()
    {
        var state = LoadProxyStateFile();
        if (state is not null)
        {
            _proxyMode = state.Mode == "auto" ? "auto" : "manual";
            _currentProxyName = state.CurrentName;
            _hasPersistedProxyState = true;
        }

        await RefreshProxyConfigurationAsync(showMessage: false);
        _proxyStateLoaded = true;
        PublishState();
    }

    private async Task RefreshProxyConfigurationAsync(bool showMessage)
    {
        try
        {
            var config = await ReadMihomoConfigurationAsync();
            RefreshProxySnapshot(config, inferMode: !_hasPersistedProxyState);
            _proxyHealth = _proxyNodes.Count == 0
                ? "尚未添加节点"
                : _proxyHealth == "正在读取代理配置" ? "等待 VoWiFi 状态检测" : _proxyHealth;
            if (showMessage) _proxyNotice = $"已读取 {_proxyNodes.Count} 个本机节点";
        }
        catch (Exception ex)
        {
            _proxyHealth = "代理配置不可用";
            _proxyNotice = SafeProxyError(ex, "无法读取 Mihomo 配置");
        }
        finally
        {
            PublishState();
        }
    }

    private async Task ImportProxyYamlAsync(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            _proxyNotice = "请先粘贴 Mihomo 或 Clash 节点 YAML";
            PublishState();
            return;
        }

        await RunProxyOperationAsync("正在导入代理节点", async () =>
        {
            List<YamlMappingNode> imported;
            try
            {
                imported = ParseImportedProxyNodes(yaml);
            }
            catch (YamlException)
            {
                throw new InvalidOperationException("YAML 格式无法解析，请检查缩进和字段");
            }

            if (imported.Count == 0) throw new InvalidOperationException("没有找到可导入的代理节点");

            var config = await ReadMihomoConfigurationAsync();
            var root = GetRoot(config);
            var proxies = GetOrCreateSequence(root, "proxies");
            var importedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var node in imported)
            {
                ValidateProxyNode(node);
                var name = GetScalar(node, "name")!;
                if (!importedNames.Add(name)) throw new InvalidOperationException($"导入内容中存在重名节点：{name}");

                var existingIndex = FindProxyIndex(proxies, name);
                if (existingIndex >= 0) proxies.Children[existingIndex] = node;
                else proxies.Add(node);
            }

            var names = GetProxyNames(proxies);
            _currentProxyName = names.Contains(_currentProxyName, StringComparer.Ordinal)
                ? _currentProxyName
                : names.FirstOrDefault();
            await ApplyMihomoConfigurationAsync(config, _currentProxyName);
            _proxyLatencies.Clear();
            RefreshProxySnapshot(config, inferMode: false);
            SaveProxyStateFile();
            _proxyNotice = $"已导入 {imported.Count} 个节点，敏感字段仅保存在 WSL";
            AddActivity($"已导入 {imported.Count} 个代理节点");
        });
    }

    private async Task SaveProxyNodeAsync(JsonElement nodeData)
    {
        await RunProxyOperationAsync("正在保存代理节点", async () =>
        {
            var originalName = GetJsonString(nodeData, "originalName");
            var name = GetRequiredJsonString(nodeData, "name", "节点名称");
            var type = GetRequiredJsonString(nodeData, "type", "协议类型").ToLowerInvariant();
            var server = GetRequiredJsonString(nodeData, "server", "服务器地址");
            var port = nodeData.TryGetProperty("port", out var portElement) && portElement.TryGetInt32(out var parsedPort)
                ? parsedPort
                : 0;
            if (port is < 1 or > 65535) throw new InvalidOperationException("端口必须在 1 到 65535 之间");

            var config = await ReadMihomoConfigurationAsync();
            var proxies = GetOrCreateSequence(GetRoot(config), "proxies");
            var originalIndex = string.IsNullOrWhiteSpace(originalName) ? -1 : FindProxyIndex(proxies, originalName);
            var duplicateIndex = FindProxyIndex(proxies, name);
            if (duplicateIndex >= 0 && duplicateIndex != originalIndex)
                throw new InvalidOperationException($"已经存在名为 {name} 的节点");

            var node = originalIndex >= 0
                ? (YamlMappingNode)proxies.Children[originalIndex]
                : new YamlMappingNode();
            SetScalar(node, "name", name);
            SetScalar(node, "type", type);
            SetScalar(node, "server", server);
            SetScalar(node, "port", port.ToString());
            SetBoolean(node, "udp", GetJsonBoolean(nodeData, "udp", true));
            SetBoolean(node, "skip-cert-verify", GetJsonBoolean(nodeData, "skipCertVerify", false));
            SetOptionalScalar(node, "username", GetJsonString(nodeData, "username"));
            SetOptionalScalar(node, "cipher", GetJsonString(nodeData, "cipher"));
            SetOptionalScalar(node, "sni", GetJsonString(nodeData, "sni"));

            var credential = GetJsonString(nodeData, "credential");
            if (!string.IsNullOrWhiteSpace(credential))
            {
                RemoveKey(node, type is "vmess" or "vless" ? "password" : "uuid");
                SetScalar(node, type is "vmess" or "vless" ? "uuid" : "password", credential);
            }

            if (originalIndex < 0) proxies.Add(node);
            if (string.Equals(_currentProxyName, originalName, StringComparison.Ordinal) || _currentProxyName is null)
                _currentProxyName = name;

            await ApplyMihomoConfigurationAsync(config, _currentProxyName);
            _proxyLatencies.Clear();
            RefreshProxySnapshot(config, inferMode: false);
            SaveProxyStateFile();
            _proxyNotice = $"节点 {name} 已保存";
            AddActivity($"代理节点 {name} 已保存");
        });
    }

    private async Task DeleteProxyNodeAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunProxyOperationAsync("正在删除代理节点", async () =>
        {
            var config = await ReadMihomoConfigurationAsync();
            var proxies = GetOrCreateSequence(GetRoot(config), "proxies");
            var index = FindProxyIndex(proxies, name);
            if (index < 0) throw new InvalidOperationException("没有找到要删除的节点");
            proxies.Children.RemoveAt(index);

            var names = GetProxyNames(proxies);
            if (string.Equals(_currentProxyName, name, StringComparison.Ordinal))
                _currentProxyName = names.FirstOrDefault();

            await ApplyMihomoConfigurationAsync(config, _currentProxyName);
            _proxyLatencies.Remove(name);
            RefreshProxySnapshot(config, inferMode: false);
            SaveProxyStateFile();
            _proxyNotice = $"节点 {name} 已删除";
            AddActivity($"代理节点 {name} 已删除");
        });
    }

    private async Task SwitchProxyAsync(string name, bool automatic)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunProxyOperationAsync(automatic ? "正在自动切换代理节点" : "正在切换代理节点", async () =>
        {
            var config = await ReadMihomoConfigurationAsync();
            var proxies = GetOrCreateSequence(GetRoot(config), "proxies");
            if (FindProxyIndex(proxies, name) < 0) throw new InvalidOperationException("没有找到目标节点");

            _currentProxyName = name;
            if (!automatic) _proxyMode = "manual";
            await ApplyMihomoConfigurationAsync(config, name);
            RefreshProxySnapshot(config, inferMode: false);
            SaveProxyStateFile();
            _proxyFailureCount = 0;
            _lastProxySwitchAt = DateTimeOffset.Now;

            var socksReady = await TestSocksEndpointAsync();
            _proxyHealth = socksReady ? "SOCKS5 可用，等待 VoWiFi 注册" : "SOCKS5 联网检测未通过";
            _proxyNotice = automatic ? $"已自动切换到 {name}" : $"已手动切换到 {name}";
            AddActivity(_proxyNotice);
            if (automatic && !socksReady)
            {
                _lastProxySwitchAt = DateTimeOffset.Now - AutoSwitchGrace;
                _proxyFailureCount = AutoFailureThreshold - 1;
            }
        });
    }

    private async Task SetProxyAutoModeAsync(bool enabled)
    {
        await RunProxyOperationAsync(enabled ? "正在开启代理自动切换" : "正在关闭代理自动切换", async () =>
        {
            _proxyMode = enabled ? "auto" : "manual";
            var config = await ReadMihomoConfigurationAsync();
            var proxies = GetOrCreateSequence(GetRoot(config), "proxies");
            var names = GetProxyNames(proxies);
            _currentProxyName = names.Contains(_currentProxyName, StringComparer.Ordinal)
                ? _currentProxyName
                : names.FirstOrDefault();
            await ApplyMihomoConfigurationAsync(config, _currentProxyName);
            RefreshProxySnapshot(config, inferMode: false);
            SaveProxyStateFile();
            _proxyFailureCount = 0;
            _lastProxySwitchAt = DateTimeOffset.MinValue;
            _proxyNotice = enabled
                ? "自动切换已开启，将根据 VoWiFi IMS 状态轮换节点"
                : "自动切换已关闭，当前节点保持不变";
            AddActivity(_proxyNotice);
        });
    }

    private async Task TestAllProxiesAsync()
    {
        if (_proxyTestBusy || _proxyOperationBusy || _isBusy) return;
        await RefreshProxyConfigurationAsync(showMessage: false);
        if (_proxyNodes.Count == 0) return;
        var names = _proxyNodes.Select(node => node.Name).ToArray();
        _proxyTestBusy = true;
        foreach (var name in names) _proxyLatencies[name] = new ProxyLatencyResult(null, "testing");
        _proxyNotice = $"正在测试 {names.Length} 个节点";
        PublishState();

        try
        {
            var results = await MeasureProxyLatenciesAsync(names);
            foreach (var name in names)
            {
                _proxyLatencies[name] = results.TryGetValue(name, out var delay) && delay is not null
                    ? new ProxyLatencyResult(delay, "ok")
                    : new ProxyLatencyResult(null, "failed");
            }
            var successCount = results.Values.Count(delay => delay is not null);
            _proxyNotice = $"全部测速完成：{successCount}/{names.Length} 个节点可用";
        }
        catch (Exception ex)
        {
            foreach (var name in names) _proxyLatencies[name] = new ProxyLatencyResult(null, "failed");
            _proxyNotice = SafeProxyError(ex, "节点测速未完成");
        }
        finally
        {
            _proxyTestBusy = false;
            PublishState();
        }
    }

    private async Task TestOneProxyAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || _proxyTestBusy || _proxyOperationBusy || _isBusy) return;
        await RefreshProxyConfigurationAsync(showMessage: false);
        if (!_proxyNodes.Any(node => string.Equals(node.Name, name, StringComparison.Ordinal)))
        {
            _proxyNotice = "节点列表已经更新，请重新点击测速";
            PublishState();
            return;
        }
        _proxyTestBusy = true;
        _proxyLatencies[name] = new ProxyLatencyResult(null, "testing");
        _proxyNotice = $"正在测试节点 {name}";
        PublishState();

        try
        {
            var results = await MeasureProxyLatenciesAsync([name]);
            var delay = results.GetValueOrDefault(name);
            _proxyLatencies[name] = delay is not null
                ? new ProxyLatencyResult(delay, "ok")
                : new ProxyLatencyResult(null, "failed");
            _proxyNotice = delay is not null ? $"节点 {name} 延迟 {delay} ms" : $"节点 {name} 测速失败";
        }
        catch (Exception ex)
        {
            _proxyLatencies[name] = new ProxyLatencyResult(null, "failed");
            _proxyNotice = SafeProxyError(ex, $"节点 {name} 测速失败");
        }
        finally
        {
            _proxyTestBusy = false;
            PublishState();
        }
    }

    private async Task<Dictionary<string, int?>> MeasureProxyLatenciesAsync(IReadOnlyCollection<string> names)
    {
        var controller = await EnsureMihomoControllerAsync();
        var payload = JsonSerializer.Serialize(new
        {
            endpoint = controller.Endpoint,
            secret = controller.Secret,
            names
        });
        const string probeScript = """
            import concurrent.futures, json, sys, time, urllib.error, urllib.parse, urllib.request

            payload = json.load(sys.stdin)
            endpoint = payload["endpoint"]
            secret = payload["secret"]
            names = payload["names"]

            def measure(name):
                targets = (
                    "http://www.gstatic.com/generate_204",
                    "http://cp.cloudflare.com/generate_204",
                )
                for target in targets:
                    query = urllib.parse.urlencode({"timeout": 8000, "url": target})
                    url = "http://%s/proxies/%s/delay?%s" % (endpoint, urllib.parse.quote(name, safe=""), query)
                    request = urllib.request.Request(url, headers={"Authorization": "Bearer " + secret})
                    samples = []
                    for attempt in range(3):
                        try:
                            with urllib.request.urlopen(request, timeout=10) as response:
                                value = json.loads(response.read().decode()).get("delay")
                                if value is not None:
                                    samples.append(int(value))
                        except Exception:
                            pass
                        if attempt < 2:
                            time.sleep(0.25)
                    if samples:
                        samples.sort()
                        return name, samples[len(samples) // 2]
                return name, None

            workers = max(1, min(4, len(names)))
            with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as executor:
                results = dict(executor.map(measure, names))
            print(json.dumps(results, ensure_ascii=False))
            """;
        var timeoutSeconds = Math.Min(900, 20 + (int)Math.Ceiling(names.Count / 4d) * 65);
        var result = await RunProcessAsync(
            "wsl.exe",
            ["-d", _wslDistro, "--", "python3", "-c", probeScript],
            TimeSpan.FromSeconds(timeoutSeconds),
            payload);
        if (result.ExitCode != 0) throw new InvalidOperationException("Mihomo 测速接口没有响应");

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var delay)
                    ? (int?)delay
                    : null,
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Mihomo 返回了无效的测速结果");
        }
    }

    private async Task<MihomoController> EnsureMihomoControllerAsync()
    {
        var config = await ReadMihomoConfigurationAsync();
        var root = GetRoot(config);
        var configuredEndpoint = GetScalar(root, "external-controller");
        var endpoint = "127.0.0.1:9090";
        var changed = false;
        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            var separator = configuredEndpoint.LastIndexOf(':');
            if (separator >= 0 && int.TryParse(configuredEndpoint[(separator + 1)..], out var port))
                endpoint = $"127.0.0.1:{port}";
        }
        if (!string.Equals(configuredEndpoint, endpoint, StringComparison.Ordinal))
        {
            SetScalar(root, "external-controller", endpoint);
            changed = true;
        }

        var secret = GetScalar(root, "secret");
        if (string.IsNullOrWhiteSpace(secret))
        {
            secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            SetScalar(root, "secret", secret);
            changed = true;
        }

        if (changed)
        {
            await ApplyMihomoConfigurationAsync(config, _currentProxyName);
            _proxyNotice = "本地测速接口已经准备完成";
        }
        return new MihomoController(endpoint, secret);
    }

    private async Task MonitorProxyAsync()
    {
        if (!_proxyStateLoaded || _proxyMode != "auto" || _proxyOperationBusy || _proxyTestBusy || _isBusy) return;
        if (_proxyNodes.Count < 2)
        {
            _proxyHealth = _proxyNodes.Count == 0 ? "尚未添加节点" : "自动切换至少需要两个节点";
            PublishState();
            return;
        }

        var health = await GetVoWifiHealthAsync();
        if (!health.Available)
        {
            _proxyHealth = "暂时无法读取 VoWiFi 状态";
            PublishState();
            return;
        }

        if (health.Ready)
        {
            _proxyFailureCount = 0;
            _proxyHealth = "VoWiFi IMS 已连接";
            PublishState();
            return;
        }

        if (DateTimeOffset.Now - _lastProxySwitchAt < AutoSwitchGrace)
        {
            _proxyHealth = $"等待 VoWiFi 注册（{health.Phase}）";
            PublishState();
            return;
        }

        _proxyFailureCount++;
        _proxyHealth = $"VoWiFi 未就绪（{_proxyFailureCount}/{AutoFailureThreshold}，{health.Phase}）";
        PublishState();
        if (_proxyFailureCount < AutoFailureThreshold) return;

        var currentIndex = _proxyNodes.FindIndex(node => string.Equals(node.Name, _currentProxyName, StringComparison.Ordinal));
        var nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % _proxyNodes.Count;
        await SwitchProxyAsync(_proxyNodes[nextIndex].Name, automatic: true);
    }

    private async Task<VoWifiHealth> GetVoWifiHealthAsync()
    {
        try
        {
            using var loginContent = new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = "admin" }),
                System.Text.Encoding.UTF8,
                "application/json");
            using var loginResponse = await _http.PostAsync($"{BackendUrl}/api/auth/login", loginContent);
            if (!loginResponse.IsSuccessStatusCode) return new VoWifiHealth(false, false, "登录失败");
            using var loginJson = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
            if (!loginJson.RootElement.TryGetProperty("token", out var tokenElement))
                return new VoWifiHealth(false, false, "登录失败");

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BackendUrl}/api/devices/eSIM/overview");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenElement.GetString());
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new VoWifiHealth(false, false, "状态不可用");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!document.RootElement.TryGetProperty("devices", out var devices) || devices.GetArrayLength() == 0)
                return new VoWifiHealth(true, false, "等待设备");

            var device = devices[0];
            var active = GetJsonBoolean(device, "vowifi_active", false);
            if (!device.TryGetProperty("vowifi_runtime", out var runtime))
                return new VoWifiHealth(true, false, active ? "正在注册" : "未启动");
            var tunnel = GetJsonBoolean(runtime, "tunnel_ready", false);
            var ims = GetJsonBoolean(runtime, "ims_ready", false);
            var phase = GetJsonString(runtime, "phase");
            return new VoWifiHealth(true, active && tunnel && ims, string.IsNullOrWhiteSpace(phase) ? "正在注册" : phase);
        }
        catch
        {
            return new VoWifiHealth(false, false, "状态不可用");
        }
    }

    private async Task<bool> TestSocksEndpointAsync()
    {
        try
        {
            await Task.Delay(1500);
            var result = await RunProcessAsync(
                "wsl.exe",
                ["-d", _wslDistro, "--", "curl", "-sS", "-o", "/dev/null", "-w", "%{http_code}",
                    "--connect-timeout", "6", "--max-time", "12", "--socks5-hostname", "127.0.0.1:7891",
                    "https://www.gstatic.com/generate_204"],
                TimeSpan.FromSeconds(16));
            return result.ExitCode == 0 && result.StandardOutput.Trim() == "204";
        }
        catch
        {
            return false;
        }
    }

    private async Task RunProxyOperationAsync(string activity, Func<Task> operation)
    {
        if (_proxyOperationBusy || _isBusy) return;
        if (_proxyTestBusy)
        {
            _proxyNotice = "测速进行中，当前操作将在测速完成后执行";
            PublishState();
            while (_proxyTestBusy && !_isBusy)
                await Task.Delay(150);
            if (_proxyOperationBusy || _isBusy) return;
        }
        var previousMode = _proxyMode;
        var previousCurrent = _currentProxyName;
        var previousHealth = _proxyHealth;
        _proxyOperationBusy = true;
        SetBusy(true);
        AddActivity(activity);
        _proxyNotice = "";
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            _proxyMode = previousMode;
            _currentProxyName = previousCurrent;
            _proxyHealth = previousHealth;
            _proxyNotice = SafeProxyError(ex, "代理操作未完成");
            AddActivity(_proxyNotice);
        }
        finally
        {
            _proxyOperationBusy = false;
            SetBusy(false);
            PublishState();
        }
    }

    private async Task<YamlStream> ReadMihomoConfigurationAsync()
    {
        var result = await RunProcessAsync(
            "wsl.exe",
            ["-d", _wslDistro, "-u", "root", "--", "cat", MihomoConfigPath],
            TimeSpan.FromSeconds(10));
        if (result.ExitCode != 0)
        {
            if (result.StandardError.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                return CreateEmptyMihomoConfiguration();
            throw new InvalidOperationException("无法读取 WSL 中的 Mihomo 配置");
        }

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(result.StandardOutput));
            return stream.Documents.Count == 0 ? CreateEmptyMihomoConfiguration() : stream;
        }
        catch (YamlException)
        {
            throw new InvalidOperationException("现有 Mihomo 配置不是有效 YAML");
        }
    }

    private async Task ApplyMihomoConfigurationAsync(YamlStream config, string? selectedName)
    {
        var root = GetRoot(config);
        SetScalar(root, "mixed-port", "7891");
        SetBoolean(root, "allow-lan", false);
        if (GetNode(root, "mode") is null) SetScalar(root, "mode", "rule");
        if (GetNode(root, "log-level") is null) SetScalar(root, "log-level", "info");

        var proxies = GetOrCreateSequence(root, "proxies");
        var names = GetProxyNames(proxies);
        selectedName = names.Contains(selectedName, StringComparer.Ordinal) ? selectedName : names.FirstOrDefault();
        _currentProxyName = selectedName;

        var groups = GetOrCreateSequence(root, "proxy-groups");
        var proxyGroup = groups.Children.OfType<YamlMappingNode>()
            .FirstOrDefault(group => string.Equals(GetScalar(group, "name"), "PROXY", StringComparison.Ordinal));
        if (proxyGroup is null)
        {
            proxyGroup = new YamlMappingNode();
            groups.Add(proxyGroup);
        }
        SetScalar(proxyGroup, "name", "PROXY");
        SetScalar(proxyGroup, "type", "select");
        RemoveKey(proxyGroup, "url");
        RemoveKey(proxyGroup, "interval");
        var orderedNames = new List<string>();
        if (selectedName is not null) orderedNames.Add(selectedName);
        orderedNames.AddRange(names.Where(name => !string.Equals(name, selectedName, StringComparison.Ordinal)));
        if (orderedNames.Count == 0) orderedNames.Add("DIRECT");
        SetNode(proxyGroup, "proxies", new YamlSequenceNode(orderedNames.Select(name => new YamlScalarNode(name))));

        if (GetNode(root, "rules") is null)
            SetNode(root, "rules", new YamlSequenceNode(new YamlScalarNode("MATCH,PROXY")));

        using var writer = new StringWriter();
        config.Save(writer, assignAnchors: false);
        var yaml = writer.ToString();
        const string temporaryPath = "/opt/mihomo/config.yaml.vohive.new";
        const string backupPath = "/opt/mihomo/config.yaml.vohive-control.bak";

        try
        {
            var write = await RunWslRootAsync(
                ["bash", "-c", $"umask 077; cat > {temporaryPath}"],
                TimeSpan.FromSeconds(15),
                yaml);
            if (write.ExitCode != 0) throw new InvalidOperationException("无法写入 Mihomo 临时配置");

            var test = await RunWslRootAsync(
                ["/usr/local/bin/mihomo", "-t", "-f", temporaryPath],
                TimeSpan.FromSeconds(20));
            if (test.ExitCode != 0)
                throw new InvalidOperationException("Mihomo 拒绝了新配置，现有配置未改变");

            await RunWslRootAsync(["cp", "-a", MihomoConfigPath, backupPath], TimeSpan.FromSeconds(10));
            var permissions = await RunWslRootAsync(["chown", "root:root", temporaryPath], TimeSpan.FromSeconds(10));
            if (permissions.ExitCode == 0)
                permissions = await RunWslRootAsync(["chmod", "600", temporaryPath], TimeSpan.FromSeconds(10));
            if (permissions.ExitCode != 0) throw new InvalidOperationException("无法设置 Mihomo 配置权限");

            var replace = await RunWslRootAsync(["mv", "-f", temporaryPath, MihomoConfigPath], TimeSpan.FromSeconds(10));
            if (replace.ExitCode != 0) throw new InvalidOperationException("无法替换 Mihomo 配置");

            var restart = await RunWslRootAsync(["systemctl", "restart", "mihomo"], TimeSpan.FromSeconds(20));
            var active = restart.ExitCode == 0
                ? await RunWslRootAsync(["systemctl", "is-active", "--quiet", "mihomo"], TimeSpan.FromSeconds(10))
                : restart;
            if (restart.ExitCode != 0 || active.ExitCode != 0)
            {
                await RunWslRootAsync(["cp", "-a", backupPath, MihomoConfigPath], TimeSpan.FromSeconds(10));
                await RunWslRootAsync(["systemctl", "restart", "mihomo"], TimeSpan.FromSeconds(20));
                throw new InvalidOperationException("Mihomo 重启失败，已经恢复旧配置");
            }
            _proxyFailureCount = 0;
            _lastProxySwitchAt = DateTimeOffset.Now;
        }
        finally
        {
            await RunWslRootAsync(["rm", "-f", temporaryPath], TimeSpan.FromSeconds(10));
        }
    }

    private Task<ProcessResult> RunWslRootAsync(
        IEnumerable<string> command,
        TimeSpan timeout,
        string? standardInput = null)
    {
        var arguments = new List<string> { "-d", _wslDistro, "-u", "root", "--" };
        arguments.AddRange(command);
        return RunProcessAsync("wsl.exe", arguments, timeout, standardInput);
    }

    private void RefreshProxySnapshot(YamlStream config, bool inferMode)
    {
        var root = GetRoot(config);
        var proxies = GetOrCreateSequence(root, "proxies");
        _proxyNodes.Clear();
        foreach (var mapping in proxies.Children.OfType<YamlMappingNode>())
        {
            var name = GetScalar(mapping, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            _proxyNodes.Add(new ProxyNodeView(
                name,
                GetScalar(mapping, "type") ?? "unknown",
                GetScalar(mapping, "server") ?? "",
                int.TryParse(GetScalar(mapping, "port"), out var port) ? port : 0,
                GetYamlBoolean(mapping, "udp", true),
                GetScalar(mapping, "sni") ?? GetScalar(mapping, "servername"),
                GetYamlBoolean(mapping, "skip-cert-verify", false),
                GetScalar(mapping, "username"),
                GetScalar(mapping, "cipher"),
                HasAnyKey(mapping, "password", "uuid", "token", "auth")));
        }

        var group = GetSequence(root, "proxy-groups")?.Children.OfType<YamlMappingNode>()
            .FirstOrDefault(item => string.Equals(GetScalar(item, "name"), "PROXY", StringComparison.Ordinal));
        var groupNames = group is null ? [] : GetSequence(group, "proxies")?.Children
            .OfType<YamlScalarNode>().Select(node => node.Value ?? "").Where(value => value.Length > 0).ToList() ?? [];
        if (inferMode && string.Equals(GetScalar(group, "type"), "fallback", StringComparison.OrdinalIgnoreCase))
            _proxyMode = "auto";
        if (!_proxyNodes.Any(node => string.Equals(node.Name, _currentProxyName, StringComparison.Ordinal)))
            _currentProxyName = groupNames.FirstOrDefault(name => _proxyNodes.Any(node => node.Name == name))
                ?? _proxyNodes.FirstOrDefault()?.Name;
        var knownNames = _proxyNodes.Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var removedName in _proxyLatencies.Keys.Where(name => !knownNames.Contains(name)).ToArray())
            _proxyLatencies.Remove(removedName);
    }

    private static List<YamlMappingNode> ParseImportedProxyNodes(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0) return [];
        var root = stream.Documents[0].RootNode;
        if (root is YamlSequenceNode sequence) return sequence.Children.OfType<YamlMappingNode>().ToList();
        if (root is not YamlMappingNode mapping) return [];
        if (GetNode(mapping, "proxies") is YamlSequenceNode proxies)
            return proxies.Children.OfType<YamlMappingNode>().ToList();
        return [mapping];
    }

    private static void ValidateProxyNode(YamlMappingNode node)
    {
        foreach (var field in new[] { "name", "type", "server", "port" })
        {
            if (string.IsNullOrWhiteSpace(GetScalar(node, field)))
                throw new InvalidOperationException($"节点缺少必填字段：{field}");
        }
        if (!int.TryParse(GetScalar(node, "port"), out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException($"节点 {GetScalar(node, "name")} 的端口无效");
    }

    private static YamlStream CreateEmptyMihomoConfiguration()
    {
        var root = new YamlMappingNode();
        SetScalar(root, "mixed-port", "7891");
        SetBoolean(root, "allow-lan", false);
        SetScalar(root, "mode", "rule");
        SetScalar(root, "log-level", "info");
        SetBoolean(root, "ipv6", false);
        SetNode(root, "proxies", new YamlSequenceNode());
        var group = new YamlMappingNode();
        SetScalar(group, "name", "PROXY");
        SetScalar(group, "type", "select");
        SetNode(group, "proxies", new YamlSequenceNode(new YamlScalarNode("DIRECT")));
        SetNode(root, "proxy-groups", new YamlSequenceNode(group));
        SetNode(root, "rules", new YamlSequenceNode(new YamlScalarNode("MATCH,PROXY")));
        return new YamlStream(new YamlDocument(root));
    }

    private ProxyStateFile? LoadProxyStateFile()
    {
        try
        {
            return File.Exists(ProxyStatePath)
                ? JsonSerializer.Deserialize<ProxyStateFile>(File.ReadAllText(ProxyStatePath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void SaveProxyStateFile()
    {
        var directory = Path.GetDirectoryName(ProxyStatePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            ProxyStatePath,
            JsonSerializer.Serialize(new ProxyStateFile(_proxyMode, _currentProxyName)));
        _hasPersistedProxyState = true;
    }

    private static YamlMappingNode GetRoot(YamlStream stream) =>
        stream.Documents[0].RootNode as YamlMappingNode
        ?? throw new InvalidOperationException("Mihomo 配置根节点必须是映射");

    private static YamlNode? GetNode(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? GetScalar(YamlMappingNode? mapping, string key) =>
        mapping is null ? null : (GetNode(mapping, key) as YamlScalarNode)?.Value;

    private static YamlSequenceNode? GetSequence(YamlMappingNode mapping, string key) =>
        GetNode(mapping, key) as YamlSequenceNode;

    private static YamlSequenceNode GetOrCreateSequence(YamlMappingNode mapping, string key)
    {
        if (GetNode(mapping, key) is YamlSequenceNode sequence) return sequence;
        sequence = new YamlSequenceNode();
        SetNode(mapping, key, sequence);
        return sequence;
    }

    private static void SetNode(YamlMappingNode mapping, string key, YamlNode value) =>
        mapping.Children[new YamlScalarNode(key)] = value;

    private static void SetScalar(YamlMappingNode mapping, string key, string value) =>
        SetNode(mapping, key, new YamlScalarNode(value));

    private static void SetBoolean(YamlMappingNode mapping, string key, bool value) =>
        SetScalar(mapping, key, value ? "true" : "false");

    private static void SetOptionalScalar(YamlMappingNode mapping, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) RemoveKey(mapping, key);
        else SetScalar(mapping, key, value);
    }

    private static void RemoveKey(YamlMappingNode mapping, string key) =>
        mapping.Children.Remove(new YamlScalarNode(key));

    private static bool HasAnyKey(YamlMappingNode mapping, params string[] keys) =>
        keys.Any(key => GetNode(mapping, key) is not null);

    private static bool GetYamlBoolean(YamlMappingNode mapping, string key, bool fallback) =>
        bool.TryParse(GetScalar(mapping, key), out var value) ? value : fallback;

    private static int FindProxyIndex(YamlSequenceNode proxies, string name)
    {
        for (var index = 0; index < proxies.Children.Count; index++)
        {
            if (proxies.Children[index] is YamlMappingNode mapping &&
                string.Equals(GetScalar(mapping, "name"), name, StringComparison.Ordinal)) return index;
        }
        return -1;
    }

    private static List<string> GetProxyNames(YamlSequenceNode proxies) =>
        proxies.Children.OfType<YamlMappingNode>()
            .Select(mapping => GetScalar(mapping, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();

    private static string GetRequiredJsonString(JsonElement element, string property, string displayName)
    {
        var value = GetJsonString(element, property);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"请填写{displayName}")
            : value.Trim();
    }

    private static string GetJsonString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool GetJsonBoolean(JsonElement element, string property, bool fallback) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static string SafeProxyError(Exception exception, string fallback)
    {
        var message = exception.Message?.Trim();
        return string.IsNullOrWhiteSpace(message) || message.Length > 180 ? fallback : message;
    }

    private sealed record ProxyNodeView(
        string Name,
        string Type,
        string Server,
        int Port,
        bool Udp,
        string? Sni,
        bool SkipCertVerify,
        string? Username,
        string? Cipher,
        bool HasCredential);

    private sealed record ProxyStateFile(string Mode, string? CurrentName);
    private sealed record VoWifiHealth(bool Available, bool Ready, string Phase);
    private sealed record ProxyLatencyResult(int? Milliseconds, string State);
    private sealed record MihomoController(string Endpoint, string Secret);
}
