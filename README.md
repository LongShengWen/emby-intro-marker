# emby-intro-marker

Emby plugin for detecting and managing intro and credits markers.

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

## Build

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release
```

## Output

- `bin/Release/net8.0/IntroMarkerPlugin.dll`
