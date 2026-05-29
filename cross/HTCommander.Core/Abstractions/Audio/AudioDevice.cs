/*
Copyright 2026 Ylian Saint-Hilaire

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

namespace HTCommander.Core.Abstractions.Audio;

/// <summary>
/// A selectable audio endpoint. <see cref="Id"/> is the platform-specific stable
/// identifier passed to <c>SetDevice</c> (NAudio's MMDevice.ID on Windows, the
/// PortAudio device index as a string on Linux/macOS); <see cref="Name"/> is the
/// human-readable label for UI lists.
/// </summary>
public sealed record AudioDevice(string Id, string Name)
{
    /// <summary>True if this is the system default endpoint for its direction.</summary>
    public bool IsDefault { get; init; }
}
