# TDPC (Dragon Performance Control) · 蛟龙独立控制台

<p align="center">
  <img src="src/Jiaolong.App/Assets/app.png" width="96" height="96" alt="TDPC Icon" />
</p>

<p align="center">
  <b>专为机械革命蛟龙系列（宝龙达 / Bitland 模具，如 2023 款蛟龙 16 Pro 等）打造的轻量级、原生独立硬件控制台</b><br>
  <i>彻底告别臃肿的原厂控制软件 · 零第三方依赖 · 原生 WPF 流畅极客体验</i>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0_Windows-512BD4?logo=dotnet" alt=".NET 8" />
  <img src="https://img.shields.io/badge/Architecture-Clean--Room_BCL-0078D4" alt="Clean-room" />
  <img src="https://img.shields.io/badge/Dependencies-Zero_NuGet-success" alt="Zero NuGet" />
  <img src="https://img.shields.io/badge/License-GPLv3-blue.svg" alt="License" />
</p>

---

## 💡 项目初衷

华硕玩家有 **G-Helper**，联想玩家有 **Legion Toolkit**。而机械革命蛟龙玩家（尤其是采用宝龙达模具的 2023 款蛟龙 16 Pro 等机型）长期受制于原厂控制中心体积庞大、开机自启拖慢系统、资源占用高、缺乏自定义风扇曲线和 CPU 降压（Curve Optimizer）等痛点。

**TDPC (The Dragon Performance Control / 蛟龙性能控制台)** 应运而生。它是一个完全从零手写实现的轻量级开源替代方案，直连硬件底层，不依赖原厂服务，内存占用仅约 40MB，提供纯粹、自由、高效的硬件调节体验。

---

## ✨ 核心特性

- 🚀 **轻量控制台**
  - 原生 WPF 深浅双主题，首页集中展示 CPU 调校、散热与实时硬件数据。
  - DWM 原生无闪烁缩放动画，针对 Windows 11 全屏与最小化深度调优。
- ❄️ **深度风扇调控 & 自定义温控曲线**
  - **EC 直读直写**：直连 EC 嵌入式控制器（0x66/0x62 端口及 0x4E/0x4F 扩展端口），精确转速控制。
  - **6 点自定义平滑温控曲线**：支持 50℃~100℃ 六段目标温度与转速智能插值联动。
  - **温度保护**：默认阈值 92℃，检测到越过阈值时交还 BIOS 风扇控制。
- ⚡ **CPU 调校**
  - **全核 Curve Optimizer**：通过 AMD SMU 设置 CO 偏移，范围为 -50 到 0 个 CO 单位。
  - **实时功耗墙**：WMI 硬件协议直发，动态调节长时功耗（SPL）、短时功耗（SPPT）及 CPU 温度墙。
  - **两种 CPU 频率上限**：7745HX + V35 BIOS 优先使用 SMU 睿频上限，应用后由固件连续回读确认。Windows 电源限频支持普通电源方案和游戏模式。
  - **限时试跑**：频率和 CO 候选值可试跑 15 分钟，手动确认后才成为启动配置。检测到错误、达到温度阈值或到时未确认时结束试跑并恢复。异常中断后，下一次启动会检查未完成记录。也可使用 60 秒 CPU 自检或在试跑期间手动运行游戏、压测工具。
  - **实时读数**：CPU 占用来自 Windows 忙碌时间，封装功耗按 AMD 能量计数器计算。NVIDIA 独显占用优先使用 Windows 最忙引擎，温度、时钟和功耗来自 NVIDIA 驱动。
- 🎨 **RGB 键盘背光与氛围灯**
  - 键盘背光与氛围灯独立物理开关。
  - 支持常亮、彩虹渐变（10 FPS 平滑色相流转）、自定义十六进制色盘调色及亮度级别调节。
- 🛡️ **日常实用工具**
  - 独显直连物理切换、触摸板一键锁定。
  - 屏幕 OSD 状态微弹窗（Caps Lock、Num Lock、性能模式切换即时浮窗提示）。
  - **开机无 UAC 静默自启**：基于计划任务系统级权限启动，配合托盘图标常驻。
- 📝 **企业级日志轮换**
  - 内置每日自动滚动的 30 天日志存储机制（保存于 `%AppData%\TDPC\logs`），折叠式终端设计，异常追踪简单直观。

---

## 🖥️ 验证环境与机型

- **当前主力实测机型**：
  - 机械革命 蛟龙 16 Pro (2023 款)
  - CPU: AMD Ryzen 7 7745HX (Zen4 Dragon Range)
  - GPU: NVIDIA GeForce RTX 4070 Laptop (140W)
  - BIOS 版本: `MRID6_23_P_V35`
- **硬件与代工模具背景**：
  - **宝龙达 (Bitland)** 模具与主板设计：2023 款机械革命蛟龙 16 Pro 采用宝龙达代工主板（内部项目代号 MRID6，BIOS 格式如 `MRID6_23_P_V35`）。
  - 其嵌入式控制器（EC）、WMI 协议接口（`MICommonInterface` / `MIFS_0`）以及 AMD Dragon Range 平台的 SMU 邮箱深度定制。
  - 本项目完全基于该宝龙达机型的真实硬件行为逆向与重构，欢迎同类宝龙达模具或机械革命笔记本用户测试并提交适配数据！

---

## 🛠️ 技术架构亮点

1. **绝对纯净：Zero NuGet**
   整个项目没有任何第三方 NuGet 依赖包，完全基于 .NET 8 BCL、Win32 P/Invoke 与 COM 封装构建，彻底避免供应链隐患和依赖冲突。
2. **底层硬件驱动**
   附带由 OpenLibSys 开发的签名 Ring-0 驱动 `WinRing0x64`（HWiNFO、LibreHardwareMonitor 等知名软件同款驱动），用于端口 I/O、PCI 配置空间读写与 MSR 寄存器访问。
3. **轻量 CLI 工具**
   项目附带 `jiatool`（`Jiaolong.Cli`）纯命令行探针，支持在终端中进行 `get`、`set`、`watch`、`power`、`ec` 等无头调试与自动化控制。

---

## 📦 下载与安装

进入 [Releases](https://github.com/kirigayakazima/TDPC/releases) 页面，下载最新的 `TDPC-vX.X.X-win-x64.zip`：
1. 完整解压至固定目录，保留 `TDPC.exe` 和同目录的依赖文件。发布包自带 .NET 8 运行时。
2. 运行 `TDPC.exe` 并确认 Windows 管理员权限提示（软件需加载 Ring-0 驱动以访问 EC）。
3. 可以在“系统控制”卡片中勾选“开机自启”与“关闭时最小化到托盘”，即可常驻后台自动生效。

如果新版解压到了其他目录，先从托盘退出旧版，再打开新版。若 CPU 调校卡片提示自启路径待更新，点击「使用本版自启」，让异常重启后的试跑恢复由新版处理。

---

## 🔨 本地编译构建

### 前置环境
- Windows 10 / 11 (64-bit)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 编译命令
```powershell
# 克隆仓库
git clone https://github.com/kirigayakazima/TDPC.git
cd TDPC

# Release 编译完整解决方案
dotnet build TDPC.sln -c Release

# 打包发布独立可执行程序
dotnet publish src/Jiaolong.App/Jiaolong.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugSymbols=false -p:DebugType=None -o publish/TDPC
```

---

## ⚠️ 免责声明 (Disclaimer)

1. 本项目为独立开源软件，与机械革命（MECHREVO）、宝龙达（Bitland）或 AMD、NVIDIA 等品牌官方无任何隶属或背书关系。
2. 软件涉及嵌入式控制器（EC）读写、CPU 电源策略调整与降压超频（Curve Optimizer）。开发者已在程序中部署了高温自动切回 BIOS 的保护看门狗，但仍不对因任何超频不稳定、散热失控、硬件磨损或数据丢失承担责任。
3. 请根据自身笔记本散热状况合理配置温控曲线与降压幅度。若遇系统蓝屏（BSOD），重启后在控制台重置为默认值即可。
4. **非商业性与防倒卖特别声明**：本项目完全开源且永久免费，严禁任何个人或组织将本项目或其修改版本用于任何商业牟利、打包收费倒卖或捆绑恶意推广行为。凡在闲鱼、淘宝、第三方网站付费购买的均为侵权受骗，请直接向平台投诉举报！

---

## 📄 开源许可证

本项目基于 [GNU General Public License v3.0](LICENSE) 协议开源。
欢迎提交 Issue 和 Pull Request 共同完善机型支持！
