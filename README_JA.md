# Nintendo Pro Bridge

[简体中文](README.md) | [English](README_EN.md) | [日本語](README_JA.md) | [한국어](README_KO.md)

公式 Nintendo Switch Pro コントローラーを FFXIV で認識・使用できるようにする Dalamud プラグインです。

このプラグインはコントローラー入力を直接読み取り、FFXIV プロセス内でのみ動作します。ドライバー、仮想コントローラー、管理者権限は必要ありません。

## 機能

- USB 接続と Bluetooth 接続に対応。
- A/B、X/Y の入れ替えに対応。
- 左右スティックのデッドゾーンを個別に調整可能。
- ゲームの振動に対応し、設定ウィンドウでテスト可能。
- 簡体字中国語、繁体字中国語、日本語、英語、ドイツ語、フランス語、韓国語の UI に対応。

## インストール

Dalamud のカスタムプラグインリポジトリに次の URL を追加します。

```text
https://raw.githubusercontent.com/Luckyumimi/MyDalamudPlugins/master/pluginmaster.json
```

`Nintendo Pro Bridge` を検索してインストールします。

## 使い方

コントローラーを接続してからゲームを起動します。`/npro` で設定ウィンドウを開閉できます。

振動はデフォルトで有効です。設定ウィンドウで無効にしたり、「テスト」ボタンで確認したりできます。ゲーム内の振動強度は FFXIV のゲームパッド設定で調整します。

プラグインがコントローラーを読み取れない場合は、FFXIV の Steam Input を無効にしてからコントローラーを再接続してください。

## 制限事項

- 公式 Nintendo Switch Pro コントローラーのみ対応。
- ジャイロスコープ、加速度計、NFC のデータは処理しません。
