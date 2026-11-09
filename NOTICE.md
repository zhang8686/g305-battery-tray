# 来源与声明

## 代码来源

本项目的 Windows HID 设备枚举、独立 overlapped 传输、HID++ 帧归属校验与托盘框架，改编自 [gpw-battery-tray](https://github.com/xrfc/gpw-battery-tray)（MIT，Copyright (c) 2026 xrfc）。

协议实现参考以下公开资料：

- [OpenLogi](https://github.com/AprilNEA/OpenLogi) 的 HID++ 特性实现与文档站（`0x1000 batteryStatus`、`0x1004 unifiedBattery`、`0x1001 batteryVoltage`）；
- [Solaar](https://github.com/pwr-Solaar/Solaar) 的 `hidpp20.py` 电量解码与电压档位表；
- [微软 HID 报告文档](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/obtaining-hid-reports)。

未使用 G HUB 的二进制文件、资源包或运行时接口。

## 商标声明

Logitech、logi、G305、LIGHTSPEED、G HUB 等名称与商标归罗技公司所有。程序托盘图标与 `source/app.ico` 均为本项目自绘的风格化标识，**不是罗技官方 logo**，未使用罗技的任何图标资源、安装包或品牌素材。本项目是个人非官方工具，与罗技无任何关联或背书。

## 许可

MIT，详见 [LICENSE](LICENSE)。
