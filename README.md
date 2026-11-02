# TwinDesk · 双桌

两台电脑共用一套键鼠。鼠标移到屏幕边缘，或按快捷键，切换到另一台电脑。

这是第一版桌面原型。界面使用 C# / Avalonia，键鼠捕获和输入由独立运行的 Deskflow 1.27.0 引擎完成。没有重新实现底层驱动。

## 当前功能

- 提供端、接收端可设置，两台电脑各运行一份程序。
- Windows、macOS、Linux 使用同一套界面和配置逻辑；自动识别当前系统。
- 支持局域网，以及通过可达公网地址或 VPN 地址连接。
- 另一台电脑可放在提供端的左、右、上、下。
- 边缘切换和快捷键切换可分别开启；边缘停留时间可调。
- `Ctrl + Alt + 1` 切回提供端，`Ctrl + Alt + 2` 切到接收端。
- `Ctrl + Alt + Esc` 始终切回提供端，即使关闭普通快捷键切换。
- 双向设备指纹配对、TLS 加密、设置保存、运行日志。
- 系统托盘菜单可打开窗口、开始共享、停止共享或退出程序。
- 剪贴板共享可设置，默认关闭。具体格式与桌面环境支持取决于 Deskflow。

## 使用

1. 在两台电脑上打开 TwinDesk。键鼠接在哪台，就把那台设为“本机提供键鼠”；另一台设为“本机接收键鼠”。
2. 为两台电脑设置不同的本机名称。名称使用英文字母、数字、点、横线或下划线。
3. 在“设备配对”页复制本机配对信息，到另一台电脑粘贴并导入。两边都执行一次。导入会填写对方名称和公开指纹，不包含私钥。
4. 在接收端填写提供端地址。局域网通常会从配对信息带入候选 IP；有多张网卡时，需要选择实际可达的 IP。
5. 在提供端设置另一台的位置、边缘切换和快捷键。先启动提供端，再启动接收端。

提供端与接收端使用相同端口，默认 TCP `24800`。防火墙需要允许接收端访问提供端的该端口。程序不会自动更改防火墙。

“互联网 / VPN”模式使用你已有的网络通路：填入 VPN IP，或已完成端口映射的公网地址。当前没有自动 NAT 穿透、中继服务器、账号登录或设备发现服务。选择该模式会保存连接偏好并显示相应指引；底层仍使用相同的 TLS 连接。

界面显示“输入引擎运行中”只代表本机进程正在运行。实际连接结果在“诊断与引擎”的日志中查看。关闭窗口会缩到系统托盘，共享继续运行；在托盘菜单中选择“停止共享”可停用键鼠共享，选择“退出”会停止引擎并退出程序。

## 各平台运行条件

| 平台 | 运行方式 |
| --- | --- |
| Windows x64 | 解压 Windows 包，打开 `TwinDesk.App.exe`；已附带官方 Deskflow 便携引擎，不需要另装 .NET。 |
| macOS Intel / Apple Silicon | 使用对应的 TwinDesk 包，先安装官方 Deskflow 1.27.0。默认查找 `/Applications/Deskflow.app/Contents/MacOS/deskflow-core`。需要 macOS 授予相应输入监控、辅助功能权限。 |
| Linux x64 / ARM64 | 使用对应的 TwinDesk 包，先安装系统依赖和 Deskflow 1.27.0。程序从 PATH 查找 `deskflow-core`。X11 / Wayland 的输入支持由对应 Deskflow 构建和桌面环境提供。 |

macOS / Linux 解压后，若系统未保留执行权限，执行 `chmod +x TwinDesk.App`，再运行 `./TwinDesk.App`。Linux 界面需要 `libX11`、`libICE`、`libSM`、`fontconfig` 等 Avalonia 桌面依赖。Wayland 输入需要桌面环境支持并允许输入捕获；不要把界面可显示视为键鼠权限已就绪。

在“诊断与引擎”页可以指定引擎路径。Windows 必须选择官方便携版，程序据此隔离配置；不会使用普通安装版的共享配置。Deskflow 的原界面或服务若已在提供同一套键鼠共享，应先停止，避免端口与输入捕获冲突。

官方引擎与依赖说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 构建

需要 .NET 8 或更新 SDK。

```powershell
dotnet build TwinDesk.sln -c Release
dotnet run --project tests/TwinDesk.Checks -c Release
dotnet run --project src/TwinDesk.App -c Release
pwsh -File scripts/Publish.ps1 -Runtime win-x64
```

`Publish.ps1` 支持 `win-x64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`。Windows 包会下载并核对固定版本的官方引擎 SHA-256；其他平台包需要安装该平台的官方引擎。输出在 `artifacts/packages`。

仓库包含源码、测试和打包脚本。首次从源码运行时，需要准备 Deskflow 1.27.0 引擎；Windows 可先执行上面的打包命令，程序会在本项目的 `engine` 目录中找到已校验的官方便携引擎。

原生引擎检查：

```powershell
pwsh -File scripts/Test-NativeEngine.ps1 -IncludeIpv6
```

这项检查在 Windows 上短暂运行一个只监听本机回环地址的真实提供端，用测试证书验证 TLS 信任与拒绝行为，不会启动同一桌面的真实接收端。需要本机 PowerShell 7.4 或更新版本以及 Python 3。检查报告保存在 `.artifacts/native-engine-smoke`。

源码结构：

- `src/TwinDesk.App`：桌面界面、配对流程、连接设置。
- `src/TwinDesk.Core`：配置、布局、证书、引擎查找和进程管理。
- `tests/TwinDesk.Checks`：配置与配对的行为检查。
- `scripts`：打包和真实引擎检查。

配置与设备私钥保存在当前用户的本地应用数据目录 `TwinDesk` 下。设置在 `settings.json`，引擎 INI 和布局分别为 `deskflow.ini`、`layout.conf`，证书在 `tls`。配对时交换公开配对信息即可，不要复制整个数据目录。开发测试可用环境变量 `TWINDESK_PROFILE` 指定独立数据目录，`TWINDESK_ENGINE` 指定引擎。

## 验证范围

已在本机 Windows 验证 Release 编译、配置与配对检查、界面启动与操作，以及真实 Deskflow 引擎的 IPv4 / IPv6 TLS 握手、未信任证书拒绝和停止清理。

尚未验证两台实体电脑之间的鼠标移动、键盘按键、剪贴板、断网后的输入恢复；macOS、Linux 和跨互联网连接也尚未实机验证。跨平台构建成功只说明程序可以编译发布，不代表这些实机检查已经通过。

对应的双机验收步骤见 [双机测试.md](双机测试.md)。
