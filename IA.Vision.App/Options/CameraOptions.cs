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
using GigeVision.Core.Enums;

public class CameraOptions
{
    public uint GvcpPort { get; set; }
    public List<CameraConfiguration> Cameras { get; set; } = new();
}

public class CameraConfiguration
{
    public int Id { get; set; } = 0;
    public string Name { get; set; } = string.Empty;
    public string CameraAddress { get; set; } = string.Empty;
    public string ReceiverAddress { get; set; } = string.Empty;
    public uint ReceiverPort { get; set; }
    public int EndpointPort { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint OffsetX { get; set; }
    public uint OffsetY { get; set; }
    public PixelFormat PixelFormat { get; set; } = PixelFormat.BayerRG8;
    public bool EnforceJumboFrames { get; set; } = true;
    public ConsoleColor Accent { get; set; } = ConsoleColor.White;
    public string MacAddress { get; set; }
}

