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
namespace IA.Vision.App.Models
{
    // ImageFrame class to represent a frame with data and metadata
    public class ImageFrame
    {
        public byte[] Data { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public long CameraId { get; init; }
        public long RequestEncoderValue { get; set; }
        public long EncoderValue { get; set; }
        public bool IsRequestValid { get; set; }
    }
}

