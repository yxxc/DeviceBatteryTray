# Device Battery Tray

A lightweight Windows 10 desktop widget and notification-area app for monitoring the battery levels of three 2.4 GHz wireless devices:

- MCHOSE A7 Ultra mouse
- MCHOSE V9 Pro2 headset
- BETOP Kunpeng 20 controller

The app polls each receiver directly through Windows HID. MCHOSE HUB and BETOP Game Hall do not need to keep running.

## Use

Run `DeviceBatteryTray.exe`. The transparent desktop widget appears at the lower right and can be dragged; its position is remembered. It stays behind foreground applications. The notification-area icon provides refresh, settings, pinning, and exit actions.

Settings include the polling interval, low-battery alerts and threshold, and start with Windows. Device readings may be unavailable while a device is asleep; the app keeps the last successful reading and displays cached readings in gray.

## Build

The project uses .NET Framework 4.x and Windows Forms. From this directory, build with the Windows C# compiler:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:DeviceBatteryTray.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll DeviceBatteryTray.cs
```

## Device protocol references

- [MchoseBatteryTray](https://github.com/HifumiDaisuki01/MchoseBatteryTray)
- [betop-battery](https://github.com/oitsukiii/betop-battery)

This is an independent community project and is not affiliated with MCHOSE or BETOP.

## 中文说明

这是一个适用于 Windows 10 的轻量级桌面电量组件，可在同一处查看以下 2.4G 无线设备的电量：

- 迈从 A7 Ultra 鼠标
- 迈从 V9 Pro2 耳机
- 北通鲲鹏 20 手柄

程序通过 Windows HID 直接读取各自的无线接收器，不需要让迈从 HUB 或北通游戏厅保持运行。设备断开、休眠或暂时无响应时，程序会保留上次成功读取的电量，并将该设备图标、电量和电量条显示为灰色；本次成功读取时恢复彩色。灰色表示当前未读取到新电量，不代表上次电量数值本身不准确。

### 使用方法

运行 `DeviceBatteryTray.exe`。透明桌面组件会自动显示在屏幕右下角，可以拖动，位置会自动记忆；它不会置顶遮挡前台程序。右键点击通知区域图标可刷新设备、打开设置或退出程序。设置中可以调整刷新间隔、低电量通知阈值、通知开关和 Windows 登录时启动。

### 构建

项目使用 .NET Framework 4.x 和 Windows Forms。在项目目录中运行以下命令即可用 Windows 自带的 C# 编译器构建：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:DeviceBatteryTray.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll DeviceBatteryTray.cs
```

本项目为独立社区工具，与迈从、北通均无隶属关系。
