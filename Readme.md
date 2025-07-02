# 👁️‍🗨️ GigE-Vision Server – High-Performance Image Acquisition Platform

**Author:** Daniel Kereama  
**License:** Viewing Only – All Rights Reserved ([details](./LICENSE))  
**Solution:** `ImageAcquisition.sln`

<p align="center">
  <img src="https://github.com/user-attachments/assets/be691e2e-9c0e-4421-8a84-f512182728d4" alt="Vision Server Diagram" width="600"/>
</p>

---

## 🚀 Overview

Vision Server is a production-grade, high-throughput **vision acquisition platform** designed for industrial environments like **AI-powered grading and defect detection** in sawmills and manufacturing.  
Built in C#, it leverages modern .NET technologies for real-time, fault-tolerant, multi-camera image capture and telemetry.

> ⚙️ Crafted from the ground up, this project is a showcase of Daniel Kereama’s expertise in robust, large-scale .NET systems, real-time communication, and industrial hardware integration.

**🔒 This codebase is provided for viewing purposes only. No copying, modification, or redistribution is permitted. Please see the [LICENSE](./LICENSE) file for details.**

---

## 📁 Projects Structure

| Project              | Description                                                                 |
|----------------------|-----------------------------------------------------------------------------|
| `Vision.App`         | Core host and service logic – gRPC, telemetry, dependency injection         |
| `Vision.Rpc`         | Protobuf/gRPC-generated contracts for encoder/image communication           |
| `Vision.App.Tests`   | Unit tests (WIP)                                                            |
| `ThirdParty`         | External dependencies/integrations (e.g., GeniCam/GigE Vision)              |

---

## ✨ Key Features

- ⚡ **gRPC encoder stream ingestion** for real-time frame requests
- 🎥 **Multi-camera orchestration** with per-device telemetry & retries
- 🌀 **Channel-based frame queues** and async processing per camera
- 🔄 **Dynamic acquisition modes**: `Run`, `Debug`, `Offline`
- 📊 **Structured logging** via Serilog with live console refresh
- 🛑 **Graceful shutdown** with error queues and retry logic
- 🩺 **Health monitoring** for streams, cameras, & queues
- 💾 **Flexible debug image saving**, file format toggles (RAW/PNG), intelligent foldering

---

## ⚙️ Configuration (`appsettings.json`)

All runtime behavior is configured via `appsettings.json`:

### 🔌 `ServerOptions`
- `IP` / `PortNumber`: Bind IP and port (default `0.0.0.0:19002`)
- `ImageAcquisitionMode`: Run, Debug, or Offline
- `Incoming/OutgoingConcurrentRequestLimit`: Throttling
- `BufferSize`: Retention buffer size (~5s at 460 fps)
- `LoggingLevel`: Verbosity (`Trace` → `Critical`)
- `ImageProcessingClientSkip`: Toggle gRPC downstream sending

### 📸 `CameraOptions.Cameras[]`
- Up to **8 concurrent GigE cameras**
- Each with:
  - `Id`, `Name`, `CameraAddress`, `ReceiverAddress/Port`
  - `Resolution` (`Width`, `Height`), `OffsetX/Y`
  - Unique `MacAddress`, `Serial`
  - Visual `Accent` for diagnostics

### 🧠 Subsystems
- `RetrySettings`: Delay between retries (ms)
- `FrameProcessingChannel`: Queue capacity & overflow policy (`DropOldest`)
- `DebugImageSettings`: Save PNG/RAW images to `DebugImages/{Date}/{CameraId}/`
- `FileWriteQueue`: Concurrency and format toggles (`SaveRaw`, `SavePng`)
- `StreamMonitor`: Encoder health polling (default: 7s)
- `CameraHealth`: Per-camera checks (default: 1s, with console refresh)
- `EncoderStreamSettings`: Encoder IP/port, retry backoff

---

## 🛠️ Technologies

- ![dotnet](https://img.shields.io/badge/.NET-8.0-blue?logo=dotnet) [.NET 8](https://dotnet.microsoft.com/en-us/download)
- [gRPC](https://grpc.io/) for streaming control
- [Serilog](https://serilog.net/) for structured logging
- `System.Threading.Channels`, `SemaphoreSlim`, `ConcurrentDictionary`
- [Protobuf](https://developers.google.com/protocol-buffers)
- Native SDKs for GigE Vision (GeniCam, etc.)

---

## 🔍 How It Works

1. **Startup:** Cameras connect via GigE, with validation and retry logic.
2. **Streaming:** Encoder sends gRPC ticks, triggering camera captures.
3. **Acquisition:** Frames are captured, queued, and processed or saved.
4. **Telemetry:** All activity tracked and logged by `VisionMonitorService`.
5. **Shutdown:** Channels are flushed, hardware disposed, errors persisted.

---

## 📦 Getting Started

> **Code in this repository is for viewing only.**  
> No permission is granted to copy, modify, build, or run the software.

1. **Browse the repository:**  
   - Explore the source in your browser or text viewer.
   - Review `ImageAcquisition.sln` for solution structure.
   - Inspect `appsettings.json` for configuration examples.

2. **Interested in use or collaboration?**  
   - Contact the author for license requests or commercial inquiries.

---

## 📄 License

**Viewing Only – All Rights Reserved**  
This code is made available for reference and inspection purposes only.  
No permission is granted to copy, modify, distribute, or use in any form without written consent.  
See [LICENSE](./LICENSE) for full details.  
© 2025 Daniel Kereama

---

## 👤 About the Author

**Daniel Kereama**  
Senior .NET engineer with 20+ years' experience in enterprise systems, real-time image processing, and automation.  
This project demonstrates advanced skills in high-performance, multithreaded, and hardware-integrated applications.

---

## 📨 Contact

- GitHub: [github.com/killahdz](https://github.com/killahdz)
- LinkedIn: [linkedin.com/in/danielkereama](https://linkedin.com/in/danielkereama)

---
