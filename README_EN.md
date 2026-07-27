# Nintendo Pro Bridge

[中文说明](README.md)

A Dalamud plugin that reads an official Nintendo Switch Pro Controller over HID and replaces FFXIV's gamepad state after the game's own controller poll.

Translation is scoped to the FFXIV process. It creates no virtual Xbox device, does not use HidHide, installs no driver, requires no elevation, and does not change how Windows sees the physical controller.

## Features

- USB and Bluetooth support for official Nintendo Switch Pro Controllers.
- Sticks, D-pad, face and shoulder buttons, triggers, Plus/Minus, and stick clicks.
- Optional A/B and X/Y swaps plus independent stick deadzones.
- Replaces duplicate native Pro Controller and Steam Input state inside FFXIV.
- Ignores gyroscope, accelerometer, and NFC data.
- `/npro` opens settings localized in Simplified/Traditional Chinese, Japanese, English, German, French, and Korean.

## Local loading

Select the following file in Dalamud's developer plugin settings:

`NintendoProBridge/bin/Release/net10.0-windows/NintendoProBridge.dll`

Keep the `.deps.json` beside the plugin DLL. The settings window opens on first load; no driver setup is required. The plugin uses native Windows HID/SetupAPI and ships no third-party HID manager.

## Steam

The plugin replaces native or Steam Input state inside FFXIV, preventing two gamepad states from being combined. Steam Desktop Layout runs outside the game process and cannot be changed by this plugin. If Steam holds the controller exclusively, disable Steam Input for FFXIV or close Steam and reconnect the controller.

Rumble forwarding is currently unavailable in the in-process mode because FFXIV has no virtual Xbox output device to target.
