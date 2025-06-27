# Vision Server – High-Performance Image Acquisition Platform

**Author**: Daniel Kereama  
**License**: [Apache 2.0](./LICENCE.txt)  
**Solution**: `ImageAcquisition.sln`

![image](https://github.com/user-attachments/assets/be691e2e-9c0e-4421-8a84-f512182728d4)

## 🚀 Overview

This repository contains a production-grade **vision acquisition server**, purpose-built for high-speed industrial environments such as **AI-powered grading and defect detection** in sawmills and manufacturing.

The system listens for encoder-triggered image requests via gRPC and orchestrates image capture, telemetry, logging, and robust error handling across multiple industrial cameras.

> ⚙️ Built from scratch in C#, this codebase is a showcase of Daniel Kereama’s architectural and implementation expertise in real-time, fault-tolerant .NET systems.

---

## 📁 Projects

| Project                | Description                                                                 |
|------------------------|-----------------------------------------------------------------------------|
| `IA.Vision.App`        | Core host and service logic, including gRPC handling, telemetry, and DI     |
| `IA.Vision.Rpc`        | Protobuf/gRPC-generated contract services for encoder-image communication   |
| `IA.Vision.App.Tests`  | Unit test suite (in progress)                                               |
| `ThirdParty`           | External dependencies or integrations (e.g., GeniCam/GigE Vision)           |

---

## 💡 Key Features

- **gRPC encoder stream ingestion** for real-time frame requests
- **Multi-camera coordination** with per-device telemetry and retries
- **Channel-based frame queues** with async processing per camera
- **Dynamic acquisition modes** (`Run`, `Debug`, `Offline`)
- **Structured logging** via Serilog and console refresh support
- **Graceful shutdown** with error queues and retry logic
- **Health monitoring** for encoder stream, cameras, and queues
- **Flexible debug image saving**, file format toggles (RAW/PNG), and intelligent folder layout

---

## 🔧 Configuration Overview (`appsettings.json`)

All runtime behavior is controlled via `appsettings.json`:

### 🔌 ServerOptions
- `IP` / `PortNumber`: Bind address and port (default `0.0.0.0:19002`)
- `ImageAcquisitionMode`: Run, Debug, or Offline mode
- `Incoming/OutgoingConcurrentRequestLimit`: Request throttling
- `BufferSize`: Retention buffer (~5s at 460)
- `LoggingLevel`: Verbosity (`Trace` → `Critical`)
- `ImageProcessingClientSkip`: Toggle gRPC downstream sending

### 📸 Cameras
- Define up to **8 concurrent GigE cameras** in `CameraOptions.Cameras[]`
- Each camera has:
  - `Id`, `Name`, `CameraAddress`, and `ReceiverAddress/Port`
  - `Resolution` (`Width`, `Height`), `OffsetX/Y`
  - Unique `MacAddress`, `Serial`
  - Visual `Accent` for diagnostics and display

### 🧠 Subsystems
- `RetrySettings`: Millisecond delays between retries
- `FrameProcessingChannel`: Frame queue capacity & overflow mode (`DropOldest`)
- `DebugImageSettings`: Save PNG/RAW images into `DebugImages/{Date}/{CameraId}/`
- `FileWriteQueue`: Control concurrency and format (`SaveRaw`, `SavePng`)
- `StreamMonitor`: Periodic encoder health polling (default: 7s)
- `CameraHealth`: Per-camera status checks (default: 1s, with console refresh)
- `EncoderStreamSettings`: Defines encoder stream IP/port and retry backoff

---

## 🛠️ Technologies Used

- [.NET 8](https://dotnet.microsoft.com/en-us/download)
- **gRPC** for streaming control
- **Serilog** for structured file-based logging
- **System.Threading.Channels**, `SemaphoreSlim`, `ConcurrentDictionary`
- **Protobuf** contract definitions (`IA.Vision.Rpc`)
- Native SDKs for industrial camera capture (GeniCam/GigE Vision)

---

## 🔍 How It Works

1. **Startup**: Cameras connect via GigE with validation and retry logic.
2. **Streaming**: Encoder sends ticks (via gRPC) triggers captures on movement.
3. **Acquisition**: Server captures frames, queues for processing or saving.
4. **Telemetry**: All activity tracked and logged by `VisionMonitorService`.
5. **Shutdown**: Channels flushed, hardware connections disposed, errors saved.

---

## 📦 Getting Started

1. Clone the repository:
   `git clone https://github.com/yourusername/VisionServer.git`

2. Open `ImageAcquisition.sln` in Visual Studio or Rider

3. Configure:
   - Update `appsettings.json` with your camera IPs, encoder address, and operational preferences

4. Build & Run:
   - Start `IA.Vision.App`
   - Connect encoder stream and camera hardware

---

## 📄 License

Licensed under the Apache License 2.0  
© 2025 Daniel Kereama

---

## 👤 About the Author

**Daniel Kereama** is a senior .NET engineer with 20+ years of experience in enterprise-grade systems, real-time image processing, and automation. This system showcases expertise in performance-critical applications, multithreading, and industrial integration across hardware and software domains.

---

## 📨 Contact

- GitHub: [github.com/killahdz](https://github.com/killahdz)
- LinkedIn: [linkedin.com/in/danielkereama](https://linkedin.com/in/danielkereama)
