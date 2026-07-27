# Nintendo Pro Bridge

[简体中文](README.md) | [English](README_EN.md) | [日本語](README_JA.md) | [한국어](README_KO.md)

让 最终幻想14 识别并正常使用官方 Nintendo Switch Pro 手柄的 Dalamud 插件。

插件直接读取手柄输入，只在 FFXIV 进程内生效，无需驱动、虚拟手柄或管理员权限。

## 示例

![Nintendo Pro Bridge 设置窗口示例](https://raw.githubusercontent.com/Luckyumimi/FFXIV-NintendoProBridge/main/example.png)

## 功能

- 支持 USB 和蓝牙连接。
- 支持自动选择或手动指定要使用的 Pro 手柄。
- 支持交换 A/B、X/Y。
- 左右摇杆死区可独立调整。
- 支持回中与满推校准，每根摇杆分别记录 8 个方向端点。
- 支持游戏震动，并可在设置窗口中测试。
- 支持简体中文、繁体中文、日语、英语、德语、法语和韩语。

## 安装

在 Dalamud 的自定义插件仓库中添加：

```text
https://raw.githubusercontent.com/Luckyumimi/MyDalamudPlugins/master/pluginmaster.json
```

搜索并安装 `Nintendo Pro Bridge`。

## 使用

连接手柄后进入游戏。使用 `/npro` 打开或关闭设置窗口。

震动默认启用，可在设置窗口中关闭或使用“测试”按钮验证。游戏内震动强度由 FFXIV 的手柄设置控制。

如果满推摇杆后角色仍未达到正常速度，请先执行“回中校准”，再开始“满推校准”，并将两个摇杆沿外圈完整转动后点击“完成”。

如果插件无法读取手柄，请关闭 FFXIV 的 Steam Input，然后重新连接手柄。

## 限制

- 仅支持官方 Nintendo Switch Pro 手柄。
- 不处理陀螺仪、加速度计和 NFC 数据。
