using System.Diagnostics;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TwinDesk.Core;

namespace TwinDesk.App;

public sealed class MainWindow : Window
{
    private static readonly IBrush Muted = Brush.Parse("#9BAAC3");
    private static readonly IBrush Accent = Brush.Parse("#8FB9FF");
    private readonly SettingsStore _store;
    private readonly IdentityStore _identity;
    private readonly DeskflowEngine _engine;
    private readonly string _profileDir;
    private readonly Queue<string> _logLines = new();
    private bool _busy;
    private bool _closing;
    private bool _allowClose;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayStartItem;
    private NativeMenuItem? _trayStopItem;

    private readonly ComboBox _role = Choice("本机提供键鼠", "本机接收键鼠");
    private readonly ComboBox _network = Choice("局域网", "互联网 / VPN");
    private readonly ComboBox _position = Choice("另一台在左侧", "另一台在右侧", "另一台在上方", "另一台在下方");
    private readonly TextBox _localName = Input("本机名称，例如 work-pc");
    private readonly TextBox _peerName = Input("另一台的名称");
    private readonly TextBox _peerHost = Input("键鼠所在电脑的 IP 或域名");
    private readonly TextBox _listen = Input("0.0.0.0");
    private readonly TextBox _port = Input("24800");
    private readonly TextBox _delay = Input("200");
    private readonly TextBox _enginePath = Input("自动查找输入引擎，也可选择文件");
    private readonly TextBlock _detectedEngine = Text("", 12, Muted);
    private readonly CheckBox _edge = new() { Content = "鼠标移到边缘时切换", IsChecked = true };
    private readonly CheckBox _hotkeys = new() { Content = "快捷键切换（1 / 2）", IsChecked = true };
    private readonly CheckBox _clipboard = new() { Content = "共享剪贴板", IsChecked = false };
    private readonly TextBlock _status = Text("尚未启动", 13, Accent);
    private readonly TextBlock _message = Text("先完成两台电脑的配对，再开始共享。", 13, Muted);
    private readonly TextBlock _networkHint = Text("", 12, Muted);
    private readonly TextBlock _roleHint = Text("", 12, Muted);
    private readonly TextBlock _localCardName = Text("", 18);
    private readonly TextBlock _peerCardName = Text("", 18);
    private readonly TextBlock _diagramHint = Text("", 13, Muted);
    private readonly TextBlock _layoutDirection = Text("⇄", 30, Accent);
    private readonly TextBlock _fingerprint = Text("", 12, Muted);
    private readonly TextBlock _trustStatus = Text("尚未导入另一台电脑的配对信息。", 13, Muted);
    private readonly TextBox _pairingInput = new() { Watermark = "粘贴另一台电脑的 TD1. 配对信息", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 92, MaxHeight = 140 };
    private readonly TextBox _logs = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 210, Height = 235, FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), FontSize = 11 };
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 180, FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), FontSize = 11 };
    private Button _start = null!;
    private Button _stop = null!;
    private Button _save = null!;
    private StackPanel _settingsPanel = null!;
    private Grid _diagram = null!;
    private Border _localMonitor = null!;
    private Border _peerMonitor = null!;

    public MainWindow()
    {
        Title = "TwinDesk · 双桌";
        Width = 1160;
        Height = 900;
        MinWidth = 940;
        MinHeight = 730;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _profileDir = Environment.GetEnvironmentVariable("TWINDESK_PROFILE")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwinDesk");
        _store = new SettingsStore(_profileDir);
        _identity = new IdentityStore(_profileDir);
        _engine = new DeskflowEngine(_profileDir);
        _engine.Log += line => Dispatcher.UIThread.Post(() => AppendLog(line));
        _engine.Exited += code => Dispatcher.UIThread.Post(() =>
        {
            SetRunning(false);
            if (!_closing && !_busy) ShowMessage(code == 0 ? "共享已停止。" : $"输入引擎已退出（{code}），请查看诊断日志。", code != 0);
        });

        Content = BuildContent();
        AppSettings settings;
        string? loadError = null;
        try { settings = _store.Load(); }
        catch (Exception ex) { settings = new AppSettings(); loadError = ex.Message; }
        FillSettings(settings);
        try
        {
            _fingerprint.Text = _identity.EnsureIdentity();
            UpdateTrust();
        }
        catch (Exception ex) { loadError = "设备身份创建失败：" + ex.Message; }
        if (loadError is not null) ShowMessage(loadError, true);
        WireEvents();
        RefreshHints();

        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            HideToTray();
        };
    }

    public void InstallTrayIcon(Application app)
    {
        if (_trayIcon is not null) return;

        WindowIcon icon;
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://TwinDesk.App/Assets/tray.ico"));
            icon = new WindowIcon(stream);
        }
        catch
        {
            // Fallback: empty stream is invalid on Windows tray; rethrow with clearer context.
            throw new InvalidOperationException("无法加载托盘图标 Assets/tray.ico。");
        }

        var openItem = new NativeMenuItem("打开窗口");
        openItem.Click += (_, _) => ShowFromTray();

        _trayStartItem = new NativeMenuItem("开始共享") { IsEnabled = true };
        _trayStartItem.Click += (_, _) => Dispatcher.UIThread.Post(() => _ = StartSharing());

        _trayStopItem = new NativeMenuItem("停止共享") { IsEnabled = false };
        _trayStopItem.Click += (_, _) => Dispatcher.UIThread.Post(() => _ = StopSharing());

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.UIThread.Post(() => _ = ExitFromTrayAsync());

        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_trayStartItem);
        menu.Items.Add(_trayStopItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "TwinDesk · 双桌",
            IsVisible = true,
            Menu = menu
        };
        _trayIcon.Clicked += (_, _) => ShowFromTray();
        TrayIcon.SetIcons(app, new TrayIcons { _trayIcon });
        SetRunning(_engine.IsRunning);
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        ShowMessage("已缩到系统托盘。共享仍在运行；右键托盘图标可启用/停用或退出。");
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task ExitFromTrayAsync()
    {
        if (_closing) return;
        _closing = true;
        try
        {
            await _engine.StopAsync();
        }
        catch (Exception ex)
        {
            AppendLog("停止失败：" + ex.Message);
        }
        finally
        {
            if (_trayIcon is not null)
            {
                _trayIcon.IsVisible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            _allowClose = true;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Close();
        }
    }

    private Control BuildContent()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(28, 23) };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(new StackPanel
        {
            Spacing = 5,
            Children = { Text("TwinDesk  /  双桌", 26, weight: FontWeight.SemiBold), Text("一套键鼠，两个桌面。", 13, Muted) }
        });
        var badge = new Border { Background = Brush.Parse("#20314C"), CornerRadius = new CornerRadius(18), Padding = new Thickness(15, 8), VerticalAlignment = VerticalAlignment.Center, Child = _status };
        Grid.SetColumn(badge, 1);
        heading.Children.Add(badge);
        root.Children.Add(heading);

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "连接与布局", Content = Scroll(BuildSetup()) },
                new TabItem { Header = "设备配对", Content = Scroll(BuildPairing()) },
                new TabItem { Header = "诊断与引擎", Content = Scroll(BuildDiagnostics()) }
            }
        };
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 15, 0, 0) };
        footer.Children.Add(new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Children = { _message, Text("关闭窗口会缩到托盘，共享继续运行。托盘可启用/停用共享；选「退出」才真正关闭。", 11, Muted) } });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        _save = Button("保存设置", SaveSettings);
        _stop = Button("停止共享", async () => await StopSharing());
        _stop.IsEnabled = false;
        _start = Button("开始共享", async () => await StartSharing(), true);
        actions.Children.Add(_save);
        actions.Children.Add(_stop);
        actions.Children.Add(_start);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private Control BuildSetup()
    {
        _settingsPanel = new StackPanel { Spacing = 17, Margin = new Thickness(0, 16, 0, 0) };
        _diagram = new Grid { ColumnDefinitions = new ColumnDefinitions("*,70,*"), Margin = new Thickness(14, 10) };
        _localMonitor = MonitorCard("01   本机", _localCardName, "键鼠连接在提供端电脑", "#20324F");
        _peerMonitor = MonitorCard("02   另一台电脑", _peerCardName, "支持 Windows · macOS · Linux", "#1A293C");
        _layoutDirection.HorizontalAlignment = HorizontalAlignment.Center;
        _layoutDirection.VerticalAlignment = VerticalAlignment.Center;
        _settingsPanel.Children.Add(Card(new StackPanel { Spacing = 8, Children = { _diagram, _diagramHint } }));

        var config = new StackPanel { Spacing = 16 };
        config.Children.Add(Columns(Field("本机角色", _role), Field("连接方式", _network)));
        config.Children.Add(Columns(Field("本机名称", _localName), Field("另一台电脑名称", _peerName)));
        config.Children.Add(_roleHint);
        config.Children.Add(Columns(Field("提供端地址（接收端填写）", _peerHost), Field("连接端口", _port)));
        config.Children.Add(_networkHint);
        _settingsPanel.Children.Add(Card(config));

        var switching = new StackPanel { Spacing = 13 };
        switching.Children.Add(Text("切换方式", 17, weight: FontWeight.SemiBold));
        switching.Children.Add(Columns(Field("另一台相对提供端的位置", _position), Field("边缘停留时间 / 毫秒", _delay)));
        var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 32, Children = { _edge, _hotkeys, _clipboard } };
        switching.Children.Add(checks);
        switching.Children.Add(Text("Ctrl + Alt + 1  提供端     Ctrl + Alt + 2  接收端     Ctrl + Alt + Esc  随时回到提供端", 12, Accent));
        switching.Children.Add(Text("布局和切换方式由提供键鼠的电脑设置。接收端跟随提供端。", 12, Muted));
        _settingsPanel.Children.Add(Card(switching));
        return _settingsPanel;
    }

    private Control BuildPairing()
    {
        var body = new StackPanel { Spacing = 17, Margin = new Thickness(0, 16, 0, 0) };
        body.Children.Add(Card(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Text("01  交换设备信息", 18, weight: FontWeight.SemiBold),
                Text("两台电脑各打开一次此页。复制本机配对信息，发送给自己另一台电脑，再在那台电脑上导入。两边都需要导入。", 14, Muted),
                Button("复制本机配对信息", async () => await CopyPairing(), true),
                Text("本机 SHA-256 指纹", 12, Muted),
                _fingerprint,
                Text("配对信息包含设备名称、候选地址和公开指纹，可通过你常用的方式传到另一台电脑。", 12, Muted)
            }
        }));
        body.Children.Add(Card(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Text("02  导入另一台电脑", 18, weight: FontWeight.SemiBold),
                _pairingInput,
                Button("导入并信任此设备", ImportPairing, true),
                _trustStatus,
                Text("连接会使用 TLS 加密，并核对双方设备指纹。切换本机角色后，需要为该角色重新导入对方信息。", 12, Muted)
            }
        }));
        body.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text("03  开始共享", 18, weight: FontWeight.SemiBold),
                Text("键鼠接在哪台，就将那台设为“本机提供键鼠”；另一台设为“本机接收键鼠”。先启动提供端，再启动接收端。", 14, Muted),
                Text("互联网 / VPN：接收端填入可达的 VPN IP、域名或已映射端口的公网 IP。自动穿透和中继服务尚未提供。", 12, Muted)
            }
        }));
        return body;
    }

    private Control BuildDiagnostics()
    {
        var body = new StackPanel { Spacing = 17, Margin = new Thickness(0, 16, 0, 0) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Button("选择引擎文件", async () => await BrowseEngine()), Button("检测地址连通性", async () => await CheckConnection()), Button("打开数据目录", OpenDataDirectory) } };
        body.Children.Add(Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Text("输入引擎", 17, weight: FontWeight.SemiBold),
                Text("使用 Deskflow 1.27.0。Windows 便携包已附带引擎；其他平台需使用对应系统的 Deskflow 引擎。", 12, Muted),
                _enginePath,
                _detectedEngine,
                Columns(Field("提供端监听 IP", _listen), Field("当前系统", Text(PairingCode.CurrentPlatform + " · 自动识别", 14, Accent))),
                tools,
                Text("macOS 需要授予输入监控和辅助功能权限。Linux Wayland 需要桌面环境允许输入捕获。", 12, Muted)
            }
        }));
        body.Children.Add(Card(new StackPanel { Spacing = 10, Children = { Text("运行日志", 17, weight: FontWeight.SemiBold), _logs } }));
        body.Children.Add(Card(new StackPanel { Spacing = 10, Children = { Text("提供端布局预览", 17, weight: FontWeight.SemiBold), Button("刷新配置预览", RefreshPreview), _preview } }));
        return body;
    }

    private void WireEvents()
    {
        _role.SelectionChanged += (_, _) => { RefreshHints(); UpdateTrust(); };
        _network.SelectionChanged += (_, _) => RefreshHints();
        _position.SelectionChanged += (_, _) => RefreshHints();
        _localName.TextChanged += (_, _) => RefreshHints();
        _peerName.TextChanged += (_, _) => RefreshHints();
        _edge.IsCheckedChanged += (_, _) => RefreshHints();
        _hotkeys.IsCheckedChanged += (_, _) => RefreshHints();
    }

    private void FillSettings(AppSettings s)
    {
        _role.SelectedIndex = (int)s.Role;
        _network.SelectedIndex = (int)s.Network;
        _position.SelectedIndex = (int)s.Position;
        _localName.Text = s.LocalName;
        _peerName.Text = s.PeerName;
        _peerHost.Text = s.PeerHost;
        _listen.Text = s.ListenAddress;
        _port.Text = s.Port.ToString();
        _delay.Text = s.SwitchDelayMs.ToString();
        _edge.IsChecked = s.EdgeSwitching;
        _hotkeys.IsChecked = s.HotkeySwitching;
        _clipboard.IsChecked = s.ClipboardSharing;
        _enginePath.Text = s.EnginePath;
        var found = EngineDiscovery.Find();
        _detectedEngine.Text = found is null ? "未自动找到引擎，请选择对应平台的 deskflow-core。" : "自动发现：" + found;
    }

    private AppSettings ReadSettings()
    {
        if (!int.TryParse(_port.Text, out var port)) throw new InvalidOperationException("连接端口需要填写数字。");
        if (!int.TryParse(_delay.Text, out var delay)) throw new InvalidOperationException("边缘停留时间需要填写数字。");
        return new AppSettings
        {
            Role = (ComputerRole)_role.SelectedIndex,
            Network = (NetworkMode)_network.SelectedIndex,
            Position = (ScreenPosition)_position.SelectedIndex,
            LocalName = (_localName.Text ?? "").Trim(),
            PeerName = (_peerName.Text ?? "").Trim(),
            PeerHost = (_peerHost.Text ?? "").Trim(),
            ListenAddress = (_listen.Text ?? "").Trim(),
            Port = port,
            SwitchDelayMs = delay,
            EdgeSwitching = _edge.IsChecked == true,
            HotkeySwitching = _hotkeys.IsChecked == true,
            ClipboardSharing = _clipboard.IsChecked == true,
            EnginePath = (_enginePath.Text ?? "").Trim()
        };
    }

    private void SaveSettings()
    {
        if (_busy || _engine.IsRunning || _closing) return;
        try
        {
            var settings = ReadSettings();
            var errors = settings.Validate();
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("；", errors));
            _store.Save(settings);
            ShowMessage("设置已保存。开始共享后生效。");
            RefreshPreview();
        }
        catch (Exception ex) { ShowMessage(ex.Message, true); }
    }

    private async Task StartSharing()
    {
        if (_busy || _engine.IsRunning || _closing) return;
        _busy = true;
        SetRunning(false);
        _start.IsEnabled = false;
        try
        {
            var settings = ReadSettings();
            var errors = settings.Validate();
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
            if (string.IsNullOrWhiteSpace(_identity.GetTrustedPeer(settings.Role)))
                throw new InvalidOperationException("请先在“设备配对”页导入另一台电脑的配对信息。");
            _store.Save(settings);
            await _engine.StartAsync(settings);
            SetRunning(_engine.IsRunning);
            ShowMessage(settings.Role == ComputerRole.Server ? "提供端已启动，等待另一台电脑连接。" : "接收端已启动，正在连接提供端。请查看日志确认连接。");
        }
        catch (Exception ex) { ShowMessage(ex.Message, true); SetRunning(false); }
        finally { _busy = false; SetRunning(_engine.IsRunning); }
    }

    private async Task StopSharing()
    {
        if (_busy) return;
        _busy = true;
        _stop.IsEnabled = false;
        try { await _engine.StopAsync(); ShowMessage("共享已停止，键鼠回到本机。"); }
        catch (Exception ex) { ShowMessage(ex.Message, true); }
        finally { _busy = false; SetRunning(_engine.IsRunning); }
    }

    private void SetRunning(bool running)
    {
        var locked = running || _busy;
        _status.Text = _busy ? "正在处理" : running ? "输入引擎运行中" : "尚未启动";
        _start.IsEnabled = !locked;
        _stop.IsEnabled = running && !_busy;
        _save.IsEnabled = !locked;
        _settingsPanel.IsEnabled = !locked;
        _enginePath.IsEnabled = !locked;
        _listen.IsEnabled = !locked;
        _pairingInput.IsEnabled = !locked;
        if (_trayStartItem is not null) _trayStartItem.IsEnabled = !locked && !_closing;
        if (_trayStopItem is not null) _trayStopItem.IsEnabled = running && !_busy && !_closing;
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = _busy ? "TwinDesk · 正在处理" : running ? "TwinDesk · 共享中" : "TwinDesk · 未启动";
    }

    private void RefreshHints()
    {
        var server = _role.SelectedIndex == 0;
        _peerHost.IsEnabled = !server;
        _position.IsEnabled = server;
        _edge.IsEnabled = server;
        _hotkeys.IsEnabled = server;
        _clipboard.IsEnabled = server;
        _delay.IsEnabled = server && _edge.IsChecked == true;
        _localCardName.Text = string.IsNullOrWhiteSpace(_localName.Text) ? "本机" : _localName.Text;
        _peerCardName.Text = string.IsNullOrWhiteSpace(_peerName.Text) ? "另一台电脑" : _peerName.Text;
        _roleHint.Text = server ? "本机提供键鼠；另一台电脑选择“本机接收键鼠”。名称需与对方的本机名称一致。" : "本机接收键鼠。填写提供键鼠电脑的地址，并让提供端将本机名称加入布局。";
        _networkHint.Text = _network.SelectedIndex == 0
            ? "两台电脑连接同一局域网，接收端填写提供端的局域网 IP。"
            : "两端需要已有可达地址。接收端填写 VPN IP、域名或已映射端口的公网 IP；不含自动穿透。";
        var direction = _position.SelectedIndex switch { 0 => "左侧", 1 => "右侧", 2 => "上方", _ => "下方" };
        _layoutDirection.Text = _position.SelectedIndex < 2 ? (_position.SelectedIndex == 0 ? "←" : "→") : (_position.SelectedIndex == 2 ? "↑" : "↓");
        _diagram.Children.Clear();
        var vertical = server && _position.SelectedIndex >= 2;
        _diagram.ColumnDefinitions = new ColumnDefinitions(vertical ? "*" : "*,70,*");
        _diagram.RowDefinitions = new RowDefinitions(vertical ? "Auto,40,Auto" : "Auto");
        var peerFirst = server && _position.SelectedIndex is 0 or 2;
        var first = peerFirst ? _peerMonitor : _localMonitor;
        var last = peerFirst ? _localMonitor : _peerMonitor;
        foreach (var control in new Control[] { first, _layoutDirection, last }) { Grid.SetRow(control, 0); Grid.SetColumn(control, 0); }
        if (vertical) { Grid.SetRow(_layoutDirection, 1); Grid.SetRow(last, 2); }
        else { Grid.SetColumn(_layoutDirection, 1); Grid.SetColumn(last, 2); }
        _diagram.Children.Add(first);
        _diagram.Children.Add(_layoutDirection);
        _diagram.Children.Add(last);
        _diagramHint.Text = server ? $"另一台位于提供端的{direction}。" + (_edge.IsChecked == true ? $"将鼠标移到{direction}边缘即可切换。" : "边缘切换已关闭，使用快捷键切换。") : "本机是接收端；鼠标位置和切换方式由提供端设置。";
    }

    private async Task CopyPairing()
    {
        try
        {
            var settings = ReadSettings();
            var nameErrors = (settings with { Role = ComputerRole.Server }).Validate();
            if (nameErrors.Count != 0) throw new InvalidOperationException(string.Join("；", nameErrors));
            var code = new PairingCode(1, settings.LocalName, _identity.EnsureIdentity(), PairingCode.SuggestedAddress(), settings.Port, PairingCode.CurrentPlatform);
            if (Clipboard is null) throw new InvalidOperationException("当前桌面无法访问剪贴板。");
            await Clipboard.SetTextAsync(code.Encode());
            ShowMessage("本机配对信息已复制。请到另一台电脑的配对页粘贴导入。");
        }
        catch (Exception ex) { ShowMessage(ex.Message, true); }
    }

    private void ImportPairing()
    {
        if (_busy || _engine.IsRunning || _closing) { ShowMessage("请先停止共享，再更改配对设备。", true); return; }
        try
        {
            var code = PairingCode.Decode((_pairingInput.Text ?? "").Trim());
            if (code.Fingerprint.Replace(":", "").Equals(_identity.EnsureIdentity().Replace(":", ""), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("这是本机的配对信息，请导入另一台电脑的信息。");
            var role = (ComputerRole)_role.SelectedIndex;
            var settings = ReadSettings() with { PeerName = code.ComputerName };
            if (settings.LocalName.Equals(code.ComputerName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("两台电脑的名称相同，请先修改其中一台的本机名称。");
            if (role == ComputerRole.Client)
            {
                settings = settings with { PeerHost = string.IsNullOrWhiteSpace(settings.PeerHost) ? code.Address : settings.PeerHost, Port = code.Port };
                if (string.IsNullOrWhiteSpace(settings.PeerHost))
                    throw new InvalidOperationException("对方未提供地址。请先填写提供端地址，再导入配对信息。");
            }
            var errors = settings.Validate();
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("；", errors));
            _identity.TrustPeer(code.Fingerprint, role);
            _store.Save(settings);
            FillSettings(settings);
            _pairingInput.Text = "";
            UpdateTrust();
            RefreshHints();
            ShowMessage($"已导入 {code.ComputerName}（{code.Platform}）。请确保另一台电脑也已导入本机信息。");
        }
        catch (Exception ex) { ShowMessage(ex.Message, true); }
    }

    private void UpdateTrust()
    {
        try
        {
            var trusted = _identity.GetTrustedPeer((ComputerRole)_role.SelectedIndex);
            _trustStatus.Text = string.IsNullOrWhiteSpace(trusted) ? "此角色尚未配对设备。" : "已保存对方设备指纹：\n" + trusted;
        }
        catch (Exception ex) { ShowMessage(ex.Message, true); }
    }

    private async Task BrowseEngine()
    {
        if (_busy || _engine.IsRunning || _closing) return;
        try
        {
            var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择 Deskflow 1.27 的 deskflow-core", AllowMultiple = false });
            if (result.Count > 0) _enginePath.Text = result[0].TryGetLocalPath();
        }
        catch (Exception ex) { ShowMessage(ex.Message, true); }
    }

    private async Task CheckConnection()
    {
        if (_role.SelectedIndex == 0) { ShowMessage("本机候选 IP：" + PairingCode.SuggestedAddress() + "。请在接收端检测连接。"); return; }
        try
        {
            var settings = ReadSettings();
            var errors = settings.Validate();
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
            using var socket = new TcpClient();
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await socket.ConnectAsync(settings.PeerHost.Trim('[', ']'), settings.Port, cancel.Token);
            ShowMessage("地址和端口可达。设备身份与键鼠连接需在开始共享后确认。");
        }
        catch (Exception ex) { ShowMessage("连接未成功：" + ex.Message, true); }
    }

    private void OpenDataDirectory()
    {
        try { Directory.CreateDirectory(_profileDir); Process.Start(new ProcessStartInfo(_profileDir) { UseShellExecute = true }); }
        catch (Exception ex) { ShowMessage(ex.Message, true); }
    }

    private void RefreshPreview()
    {
        try { _preview.Text = ServerConfig.Generate(ReadSettings() with { Role = ComputerRole.Server }); }
        catch (Exception ex) { _preview.Text = ex.Message; }
    }

    private void AppendLog(string line)
    {
        _logLines.Enqueue(line);
        while (_logLines.Count > 200) _logLines.Dequeue();
        _logs.Text = string.Join(Environment.NewLine, _logLines);
        _logs.CaretIndex = _logs.Text.Length;
    }

    private void ShowMessage(string text, bool error = false)
    {
        _message.Text = text;
        _message.Foreground = error ? Brush.Parse("#FFAD9E") : Muted;
    }

    private static TextBlock Text(string value, double size = 14, IBrush? color = null, FontWeight? weight = null) => new()
    {
        Text = value, FontSize = size, Foreground = color ?? Brush.Parse("#E7EDF8"), FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap
    };
    private static TextBox Input(string placeholder) => new() { Watermark = placeholder, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static ComboBox Choice(params string[] values) => new() { ItemsSource = values, SelectedIndex = 0 };
    private static StackPanel Field(string label, Control value) => new() { Spacing = 7, Children = { Text(label, 12, Muted), value } };
    private static Border Card(Control content) => new() { Padding = new Thickness(20), CornerRadius = new CornerRadius(12), Background = Brush.Parse("#1A2130"), BorderBrush = Brush.Parse("#2B3549"), BorderThickness = new Thickness(1), Child = content };
    private static ScrollViewer Scroll(Control content) => new() { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    private static Grid Columns(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,*") };
        grid.Children.Add(left);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }
    private static Border MonitorCard(string title, TextBlock name, string subtitle, string background) => new()
    {
        Padding = new Thickness(19), CornerRadius = new CornerRadius(9), Background = Brush.Parse(background), BorderBrush = Brush.Parse("#49658B"), BorderThickness = new Thickness(1),
        Child = new StackPanel { Spacing = 11, Children = { Text(title, 11, Accent), name, Text(subtitle, 11, Muted) } }
    };
    private static Button Button(string caption, Action action, bool primary = false)
    {
        var button = new Button { Content = caption };
        button.Classes.Add(primary ? "primary" : "secondary");
        button.Click += (_, _) => action();
        return button;
    }
}
