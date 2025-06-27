// --------------------------------------------------------------------------------------
// Copyright 2025 Daniel Kereama
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// Author      : Daniel Kereama
// Created     : 2025-06-27
// --------------------------------------------------------------------------------------
using Microsoft.Extensions.Logging;

public class ServerOptions
{
    public string IP { get; set; }
    public int PortNumber { get; set; }
    public int IncomingConcurrentRequestLimit { get; set; } = 100;
    public int OutgoingConcurrentRequestLimit { get; set; } = 100;
    public uint BufferSize { get; set; } = 100;
    public LogLevel LoggingLevel { get; set; } = LogLevel.Information;
    public IA.Vision.Rpc.Services.ImageAcquisitionMode ImageAcquisitionMode { get; set; } = IA.Vision.Rpc.Services.ImageAcquisitionMode.Run;
    public bool ImageProcessingClientSkip { get; set; } = false;
    public int MaxCameraConnections { get; set; } = 8;
    public RetrySettings RetrySettings { get; set; } = new();
    public FrameProcessingChannelSettings FrameProcessingChannel { get; set; } = new();
    public DebugImageSettings DebugImageSettings { get; set; } = new();
    public FileWriteQueueSettings FileWriteQueue { get; set; } = new();
    public int ErrorQueueCapacity { get; set; } = 100;
    public StreamMonitorSettings StreamMonitor { get; set; } = new();
    public CameraHealthSettings CameraHealth { get; set; } = new();
    public EncoderStreamSettings EncoderStreamSettings { get; set; } = new();
}

public class RetrySettings
{
    public int InitialRetryDelayMs { get; set; } = 2000;
    public int MaxRetryDelayMs { get; set; } = 30000;
}

public class FrameProcessingChannelSettings
{
    public int Capacity { get; set; } = 2250;
    public string FullMode { get; set; } = "DropOldest"; // Options: "DropOldest", "DropNewest", "Wait"
}

public class DebugImageSettings
{
    public string BasePath { get; set; } = "DebugImages";
    public bool UseCameraIdSubfolder { get; set; } = true;
    public bool UseDateSubfolder { get; set; } = true;
}

public class FileWriteQueueSettings
{
    public int MaxConcurrentWrites { get; set; } = 0;
}

public class CameraHealthSettings : PollingTaskSettings
{
    public bool RefreshConsoleOnUpdate { get; set; } = false;
}

public class EncoderStreamSettings : RetrySettings
{
    public string EncoderStreamAddress { get; set; }
    public int EncoderStreamPort { get; set; }
}

public class StreamMonitorSettings : PollingTaskSettings
{ }

public class PollingTaskSettings
{
    public bool EnableLogging { get; set; }
    public int PollIntervalMs { get; set; }
}

