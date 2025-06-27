# Vision Server – High-Performance Image Acquisition Platform

**Author**: Daniel Kereama  
**License**: [Apache 2.0](./LICENCE.txt)  
**Solution**: `ImageAcquisition.sln`

---

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
| `ThirdParty`           | External dependencies or integrations (e.g. GeniCam/GigEvision )            |
|------------------------|-----------------------------------------------------------------------------|

---

## 💡 Key Features

- **gRPC encoder stream ingestion** for real-time frame requests
- **Multi-camera coordination** with per-device telemetry and retries
- **Channel-based frame queues** with async processing per camera
- **Dynamic acquisition modes** (normal, debug, offline)
- **Structured logging** via Serilog and health monitoring via `VisionMonitorService`
- **Robust fault isolation and resource cleanup** on shutdown
- **Metrics exposure** for visibility (requests/sec, dropped frames, encoder sync, etc.)

---

## 🛠️ Technologies Used

- [.NET 8](https://dotnet.microsoft.com/en-us/download)  
- **gRPC** for streaming control
- **Serilog** for structured, file-based logging
- **System.Threading.Channels**, **Semaphores**, **ConcurrentDictionary**
- **Dependency Injection** via Microsoft.Extensions
- **Protobuf** and service contracts in `IA.Vision.Rpc`

---

## 🔍 How It Works

1. **Startup**: Cameras are configured and connected with retry logic.
2. **Streaming**: The server listens to `EncoderStream` for image requests (when `IsValid = true`).
3. **Capture & Process**: Each camera captures a frame for the encoder tick, queues it, and it’s streamed/saved based on mode.
4. **Telemetry**: All activity is logged and tracked through `TelemetryTracker` for monitoring.
5. **Shutdown**: Graceful disposal of streams, queues, and resources.

---

## 📦 Getting Started

1. Clone the repo  
   ```bash
   git clone https://github.com/yourusername/VisionServer.git
Open ImageAcquisition.sln in Visual Studio or Rider

Build and run IA.Vision.App
Configuration via:

appsettings.json

hostsettings.json

Ensure camera SDKs and encoder stream service are available

📄 License
Licensed under the Apache License 2.0.
© 2025 Daniel Kereama

👤 About the Author
Daniel Kereama is a senior .NET engineer with 20+ years of experience in enterprise-grade systems, real-time image processing, and automation.
Bringing hands-on expertise in architecture, performance-critical C# applications, and integrating hardware/software pipelines at scale.

📨 Contact
Want to connect?
https://www.linkedin.com/in/danielkereama/