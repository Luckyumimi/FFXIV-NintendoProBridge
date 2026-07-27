# Nintendo Pro Bridge

[中文](README.md)

A Dalamud plugin that lets FFXIV recognize and use an official Nintendo Switch Pro Controller.

The plugin reads the controller directly and operates only inside the FFXIV process. No driver, virtual controller, or administrator access is required.

## Features

- USB and Bluetooth support.
- Sticks, D-pad, face buttons, shoulder buttons, triggers, Plus/Minus, and stick clicks.
- Optional A/B and X/Y swaps.
- Independent deadzone settings for both sticks.
- Simplified Chinese, Traditional Chinese, Japanese, English, German, French, and Korean interfaces.

## Installation

Add this URL to Dalamud's custom plugin repositories:

```text
https://raw.githubusercontent.com/Luckyumimi/MyDalamudPlugins/master/pluginmaster.json
```

Search for and install `Nintendo Pro Bridge`.

## Usage

Connect the controller and start the game. Use `/npro` to open or close the settings window.

If the plugin cannot read the controller, disable Steam Input for FFXIV and reconnect it.

## Limitations

- Official Nintendo Switch Pro Controllers only.
- Gyroscope, accelerometer, and NFC data are ignored.
- Rumble is not currently supported.
