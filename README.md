# Device Battery Tray

A lightweight Windows 10 desktop widget and notification-area app for monitoring the battery levels of three 2.4 GHz wireless devices:

- MCHOSE A7 Ultra mouse
- MCHOSE V9 Pro2 headset
- BETOP Kunpeng 20 controller

The app polls each receiver directly through Windows HID. MCHOSE HUB and BETOP Game Hall do not need to keep running.

## Use

Run `DeviceBatteryTray.exe`. The transparent desktop widget appears at the lower right and can be dragged; its position is remembered. It stays behind foreground applications. The notification-area icon provides refresh, settings, pinning, and exit actions.

Settings include the polling interval, low-battery alerts and threshold, and start with Windows. Device readings may be unavailable while a device is asleep; the app keeps the last successful reading.

## Build

The project uses .NET Framework 4.x and Windows Forms. From this directory, build with the Windows C# compiler:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:DeviceBatteryTray.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll DeviceBatteryTray.cs
```

## Device protocol references

- [MchoseBatteryTray](https://github.com/HifumiDaisuki01/MchoseBatteryTray)
- [betop-battery](https://github.com/oitsukiii/betop-battery)

This is an independent community project and is not affiliated with MCHOSE or BETOP.
