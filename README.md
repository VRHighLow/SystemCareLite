# SystemCareLite

**SystemCareLite** is a super‑lightweight Windows tool that puts a real‑time resource overlay on your desktop and bundles simple system maintenance utilities.

<p align="center">
  <img src="docs/screenshot.png" alt="Overlay Screenshot" width="400"/>
</p>

## 🚀 Features

- **Resource Overlay**  
  - CPU %, RAM %, GPU %  
  - Network ping to 8.8.8.8  
  - FPS counter (only when fullscreen apps detected)  
  - Draggable, always‑on‑top, minimal UI  
- **Maintenance Tools**  
  - **RAM Cleanup** – kill idle background processes to free memory  
  - **Junk Cleaner** – delete old temp files from `%TEMP%` & `C:\Windows\Temp`  
  - **Shortcut Fixer** – remove broken `.lnk` shortcuts from Desktop & Start menu  
  - **Driver Scan** – list installed PnP‑signed drivers (read‑only)  
  - **Service Cleanup** – stop & disable auto‑started services by memory usage  
- **Auto‑Update** – checks GitHub Releases on startup and updates itself  
- **Runs at Startup** – automatically adds itself to your Windows Startup key

## 📥 Download

Grab the latest single‑file `.exe` from our [GitHub Releases](https://github.com/VRHighLow/SystemCareLite/releases).

## 💻 Build from Source

### Prerequisites

- [.NET 6 SDK](https://dotnet.microsoft.com/download) (Windows Desktop workload)  
- Git (optional, for cloning)

```bash
git clone https://github.com/VRHighLow/SystemCareLite.git
cd SystemCareLite

# Build single‑file, self‑contained EXE
dotnet publish SystemCareLite.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  /p:PublishSingleFile=true \
  -o publish
