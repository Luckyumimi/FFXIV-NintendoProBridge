# Nintendo Pro Bridge

[简体中文](README.md) | [English](README_EN.md) | [日本語](README_JA.md) | [한국어](README_KO.md)

A Dalamud plugin that lets FFXIV recognize and use an official Nintendo Switch Pro Controller.

The plugin reads the controller directly and operates only inside the FFXIV process. No driver, virtual controller, or administrator access is required.

## Example

![Nintendo Pro Bridge settings window example](https://raw.githubusercontent.com/Luckyumimi/FFXIV-NintendoProBridge/main/example.png)

## Features

- USB and Bluetooth support.
- Automatic or manual selection of the Pro Controller to use.
- Sticks, D-pad, face buttons, shoulder buttons, triggers, Plus/Minus, and stick clicks.
- Optional A/B and X/Y swaps.
- Independent deadzone settings for both sticks.
- Center and full-range calibration with 8 independently recorded endpoints for each stick.
- Game rumble support with a test button in the settings window.
- Simplified Chinese, Traditional Chinese, Japanese, English, German, French, and Korean interfaces.

## Installation

Add this URL to Dalamud's custom plugin repositories:

```text
https://raw.githubusercontent.com/Luckyumimi/MyDalamudPlugins/master/pluginmaster.json
```

Search for and install `Nintendo Pro Bridge`.

## Usage

Connect the controller and start the game. Use `/npro` to open or close the settings window.

Rumble is enabled by default. It can be disabled or tested from the settings window. In-game rumble strength is controlled by FFXIV's gamepad settings.

If full stick movement does not reach normal speed, run center calibration first. Then start full-range calibration, rotate both sticks around their complete outer edge, and select Finish.

If the plugin cannot read the controller, disable Steam Input for FFXIV and reconnect it.

## Limitations

- Official Nintendo Switch Pro Controllers only.
- Gyroscope, accelerometer, and NFC data are ignored.
