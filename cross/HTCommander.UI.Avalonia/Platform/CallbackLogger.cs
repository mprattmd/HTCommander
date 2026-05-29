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

using System;
using HTCommander.Core.Abstractions;

namespace HTCommander.UI.Avalonia.Platform;

/// <summary>
/// <see cref="ILogger"/> that forwards every message to a sink delegate (and the
/// console). Used to surface transport/debug output in the UI log.
/// </summary>
public sealed class CallbackLogger : ILogger
{
    private readonly Action<string> sink;

    public CallbackLogger(Action<string> sink) => this.sink = sink;

    public void Debug(string message) => Emit("DBG", message);
    public void Info(string message) => Emit("INF", message);
    public void Warn(string message) => Emit("WRN", message);
    public void Error(string message, Exception? ex = null) =>
        Emit("ERR", ex == null ? message : $"{message} :: {ex.Message}");

    private void Emit(string level, string message)
    {
        string line = $"[{level}] {message}";
        Console.WriteLine(line);
        try { sink(line); } catch (Exception) { }
    }
}
