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
using IA.Vision.App.Models;

namespace IA.Vision.App.Interfaces
{
    public interface ICameraService
    {
        Task<bool> ConnectAsync();

        Task<bool> StartStreamingAsync();

        Task StopStreamingAsync();

        Task DisconnectAsync();

        ImageFrame? GetFrameByEncoderValue(long encoderValue);

        bool IsConnected { get; }

        bool IsStreaming { get; }

        long EncoderValue { get; set; }

        double Fps { get; }

        long FrameCount { get; }

        string LastError { get; }

        StreamingState StreamingState { get; }

        CameraConfiguration Configuration { get; }

        //Handle in the event streaming stops
        event Action? OnStreamingStopped;
    }

    public enum StreamingState
    {
        NotStarted,
        Starting,
        Active,
        Failed
    }
}

