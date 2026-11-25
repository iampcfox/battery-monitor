# Battery Monitor（电池悬浮监控）

Windows 桌面电池监控小工具：桌面悬浮卡 + 任务栏窄条 + 托盘图标三处同拍显示本机电量 / 温度 / 功率，以及已连接蓝牙设备（耳机 / 键盘 / 手柄）的电量。纯 C# WinForms，无第三方依赖，源码单目录、单命令编译。

## 功能

| 部件 | 内容 |
|---|---|
| 悬浮卡 | 电量大字 + 电池图形、温度、功率、充放电状态、蓝牙设备电量行；Ctrl + 拖动、位置记忆、右键菜单 |
| 任务栏窄条 | SetParent 进 `Shell_TrayWnd`，贴在托盘区左边；上行电量、下行温度，右侧一列蓝牙设备电量（多台轮流展示）；右键菜单与卡片一致 |
| 托盘图标 | 电池胶囊 / 数字两种画法，可取"所有在线设备的最低电量"；低电量气泡提醒 |
| 设置面板 | 左侧导航：设备电量 / 历史统计 / 历史管理 / 设置；电量折线图、采样管理 |
| 历史采样 | 本机与蓝牙设备分两张 CSV 落盘，按天清理 |

## 构建与运行

```
编译并启动.cmd        停止旧实例 → csc 编译 src\*.cs → 启动 BatteryMonitor.exe
停止悬浮窗.cmd        结束进程
```

- 编译器用系统自带的 .NET Framework 4 csc.exe，不需要装任何 SDK。
- **注意：`编译并启动.cmd` 编译失败时仍会启动旧 exe**，编译有没有过先看 `build.log`。
- 调试直开设置面板：`BatteryMonitor.exe -panel`。

## 运行时文件（都在 exe 同目录）

| 文件 | 用途 |
|---|---|
| `battery-monitor.ini` | 配置（见下表） |
| `battery-monitor.log` | 运行日志：采样值 + 窄条定位诊断（`tb ok:` / `tb row:` 等） |
| `battery-monitor-crash.log` | 未接异常与关键轨迹（`Program.Trace`） |
| `battery-monitor.pos` | 悬浮卡位置 |
| `battery-monitor-history.csv` / `-bt.csv` | 本机 / 蓝牙设备历史采样 |
| `battery-monitor.taskbar` | 窄条占位标记：进程崩溃没还原按钮区宽度时，下次启动自愈用 |

## 配置（battery-monitor.ini）

| 键 | 默认 | 说明 |
|---|---|---|
| `scale` | 1 | 界面缩放（0.8 ~ 1.6） |
| `refresh` | 5 | 采样间隔（秒，最小 2） |
| `alpha` | 208 | 悬浮卡不透明度 |
| `percent` | true | 是否显示 % 号（窄条 / 卡片共用） |
| `showtemp` / `showstate` / `showwatt` / `showbt` | true | 悬浮卡各信息行 |
| `tray` | true | 托盘图标 |
| `taskbar` | true | 任务栏窄条 |
| `striptemp` / `stripwatt` / `stripbt` | true | 窄条各信息段（与悬浮卡分开配） |
| `stripsolid` | false | 窄条改实底绘制（逐像素透明异常时的退路） |
| `traymin` | false | 托盘数字取所有在线设备的最低电量 |
| `trayicon` | capsule | `capsule`（电池胶囊）/ `number`（数字） |
| `lowwarn` | 0 | 电量低到该值弹一次气泡（0 = 关） |
| `dragctrl` | true | 必须按住 Ctrl 才能拖动卡片 |
| `debug` | true | 每 5 秒把采样值写进日志 |
| `recordmin` / `historydays` | 5 / 30 | 历史采样间隔与保留天数 |
| `bgtop` / `bgbottom` | 浅灰 | 悬浮卡背景渐变色 |

大部分开关也能在设置面板里改，改完写回 ini 并即时生效。

## 界面入口

- 悬浮卡 / 窄条 / 托盘右键菜单共用一份（`AppMenu`）：打开面板 → 电源和电池设置 → 退出。
- 菜单里的 Win32 原生菜单三处共用；开窗口的菜单项延后一拍执行（菜单还没关闭就建窗口会有焦点问题）。
