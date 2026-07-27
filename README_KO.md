# Nintendo Pro Bridge

[简体中文](README.md) | [English](README_EN.md) | [日本語](README_JA.md) | [한국어](README_KO.md)

공식 Nintendo Switch Pro 컨트롤러를 FFXIV에서 인식하고 사용할 수 있게 해 주는 Dalamud 플러그인입니다.

플러그인은 컨트롤러 입력을 직접 읽으며 FFXIV 프로세스 안에서만 작동합니다. 드라이버, 가상 컨트롤러 또는 관리자 권한이 필요하지 않습니다.

## 기능

- USB 및 Bluetooth 연결 지원.
- A/B 및 X/Y 교체 지원.
- 좌우 스틱의 데드존을 각각 조절 가능.
- 게임 진동을 지원하며 설정 창에서 테스트 가능.
- 중국어 간체, 중국어 번체, 일본어, 영어, 독일어, 프랑스어 및 한국어 UI 지원.

## 설치

Dalamud의 사용자 지정 플러그인 저장소에 다음 URL을 추가합니다.

```text
https://raw.githubusercontent.com/Luckyumimi/MyDalamudPlugins/master/pluginmaster.json
```

`Nintendo Pro Bridge`를 검색하여 설치합니다.

## 사용 방법

컨트롤러를 연결한 후 게임을 실행합니다. `/npro`로 설정 창을 열거나 닫을 수 있습니다.

진동은 기본적으로 활성화되어 있습니다. 설정 창에서 비활성화하거나 “테스트” 버튼으로 확인할 수 있습니다. 게임 내 진동 강도는 FFXIV의 게임패드 설정으로 조절합니다.

플러그인이 컨트롤러를 읽지 못하면 FFXIV의 Steam Input을 비활성화한 후 컨트롤러를 다시 연결하세요.

## 제한 사항

- 공식 Nintendo Switch Pro 컨트롤러만 지원합니다.
- 자이로스코프, 가속도계 및 NFC 데이터는 처리하지 않습니다.
