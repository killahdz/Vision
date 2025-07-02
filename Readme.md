# 👁️‍🗨️ Vision Server – Industrial Image Acquisition & Processing Platform

**Author:** Daniel Kereama  
**License:** Viewing Only – All Rights Reserved ([details](./LICENSE))  
**Solution:** `ImageAcquisition.sln`

---

## 🚀 Overview

Vision Server is a high-performance, production-grade platform for **real-time multi-camera image acquisition** in demanding industrial environments.  
It is designed for use cases such as **AI-powered grading, defect detection, and automation** on high-speed production lines.

Built in modern C# with .NET 8, it integrates industry-standard technologies such as **GigE Vision** and **GeniCam** for robust, scalable hardware integration and high-throughput image streaming.

> ⚙️ This project demonstrates advanced architectural patterns, real-time streaming, and deep integration with industrial vision standards.

---

## 🏗️ Technologies & Protocols

- **GigE Vision** — Industry-standard interface for high-speed industrial cameras
- **GeniCam** — Generic machine vision camera API for flexible multi-vendor integration
- **.NET 8** — Modern, robust, and high-performance application runtime
- **gRPC & Protobuf** — Strongly-typed, high-throughput remote procedure calls for control and image streaming
- **Serilog** — Structured, real-time logging for observability and diagnostics
- **Async Processing** — Channel-based frame queues and multi-threaded orchestration for optimal throughput


---

## 🧩 Solution Structure

| Project              | Description                                               |
|----------------------|-----------------------------------------------------------|
| `Vision.App`         | Core service host: gRPC, DI, health, telemetry, orchestration |
| `Vision.Rpc`         | Protobuf/gRPC service contracts for control/image APIs    |
| `Vision.App.Tests`   | Unit tests (sample/WIP)                                   |
| `ThirdParty`         | External SDK/driver integration, e.g., GeniCam, GigE Vision |

---

## ✨ Highlighted Features

- **Multi-Camera Acquisition**: Orchestrates up to 8 concurrent GigE cameras, each independently managed and monitored.
- **Dynamic Acquisition Modes**: Supports `Run`, `Debug`, and `Offline` for production and diagnostics.
- **gRPC Streaming**: Real-time frame and telemetry streaming using strongly-typed contracts.
- **Object Tracking**: Encoder-synchronized acquisition for object tracking 
- **Structured Telemetry & Health Reporting**: Real-time monitoring, diagnostics, and error channels.
- **Flexible Image Output**: Supports multiple formats (RAW, PNG, TIFF), OpenCV compatibility.

---

## 🛡️ IP Protection Notice

This codebase is provided strictly for reference and viewing.  
No proprietary operational details, algorithms, or business logic are exposed.  
Please see the [LICENSE](./LICENSE) for terms and contact for further collaboration.

---

## 📄 License & Contact

**Viewing Only – All Rights Reserved**  
For commercial use, integration, or licensing inquiries, please contact:

- GitHub: [github.com/killahdz](https://github.com/killahdz)
- LinkedIn: [linkedin.com/in/danielkereama](https://linkedin.com/in/danielkereama)

---
