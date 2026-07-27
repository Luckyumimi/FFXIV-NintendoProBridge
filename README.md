# Nintendo Pro Bridge

[English README](README_EN.md)

用于《最终幻想 XIV》卫月（Dalamud）的官方 Nintendo Switch Pro 手柄输入插件。插件直接读取手柄 HID 报告，并在 FFXIV 的手柄轮询完成后用转换后的状态覆盖游戏内输入。

整个转换只发生在 FFXIV 进程内：不创建虚拟 Xbox 设备、不使用 HidHide、不安装驱动、不申请管理员权限，也不会改变 Windows 对实体手柄的识别状态。

## 功能

- 支持官方 Nintendo Switch Pro 手柄的 USB 与蓝牙连接。
- 映射双摇杆、十字键、面键、肩键、扳机、加减键和摇杆按下。
- 可交换 A/B、X/Y，并分别调整左右摇杆死区。
- 覆盖 FFXIV 内原生 Pro 手柄和 Steam Input 产生的重复输入，避免游戏内乱跳。
- 忽略陀螺仪、加速度计和 NFC 数据。
- `/npro` 打开设置窗口；支持简体中文、繁体中文、日语、英语、德语、法语和韩语。

## 本地加载

在 Dalamud 的开发插件设置中选择：

`NintendoProBridge/bin/Release/net10.0-windows/NintendoProBridge.dll`

保留 DLL 同目录下的 `.deps.json`。首次加载会自动打开设置窗口，不需要安装或配置任何驱动。插件使用 Windows 原生 HID/SetupAPI，不携带第三方 HID 管理器。

## Steam

插件会在 FFXIV 内覆盖 Steam Input 或游戏原生输入，因此不会出现两套游戏手柄状态叠加。Steam 的桌面布局发生在游戏进程之外，插件无法修改；如果 Steam 独占手柄导致插件无法读取，请针对 FFXIV 关闭 Steam Input，或关闭 Steam 后重新连接手柄。

当前纯游戏内模式不转发震动；FFXIV 没有可直接复用的 Xbox 震动输出设备。
