# emby-intro-marker

Emby plugin for detecting and managing intro and credits markers.

Emby 电视剧 / 动画 / 综艺片头片尾识别插件。

## Features

- Reuse native Emby intro/credits markers when available
- TheIntroDB integration
- Local multimodal detection:
  - audio fingerprint
  - video frame hash
  - subtitle text signatures
  - temporal consistency checks
  - sampled season-level matching
- Emby scheduled task support
- Configuration UI with tabs and runtime status

## 功能特性

- 优先复用 Emby 原生片头 / 片尾标记
- 支持 TheIntroDB
- 支持本地多模态识别：
  - 音频指纹
  - 视频帧哈希
  - 字幕文本特征
  - 时序一致性校验
  - 按季度采样匹配
- 支持 Emby 计划任务
- 提供带分页标签的配置页面和运行状态展示

## 适用场景

- 自动识别电视剧、动画、综艺的片头与片尾
- 尽量适配不同编码版本、时间偏移、轻微音频差异
- 过滤上集回顾、冷开场、静音段、超短重复片段等误判

## 构建

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release
```

## 输出文件

- `bin/Release/net8.0/IntroMarkerPlugin.dll`
