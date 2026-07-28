using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlavaSharp.Audio;

public enum AudioTargetKind
{
    SinkMonitor, // "what you hear" — an output device's monitor
    Source // a physical/virtual input (mic, etc.)
}

public sealed class AudioTarget
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    public AudioTargetKind ParsedKind =>
        Kind == "source" ? AudioTargetKind.Source : AudioTargetKind.SinkMonitor;

    public override string ToString()
    {
        return $"[{Id,4}] {(ParsedKind == AudioTargetKind.Source ? "source " : "sink   ")} {Description} ({Name})";
    }
}

/// <summary>
///     Enumerates PipeWire capture targets (sink monitors and sources) by
///     doing a short, one-shot registry scan in the Rust shim. Independent of
///     PipeWireAudioSource's long-lived capture stream.
/// </summary>
public static class AudioTargetEnumerator
{
    public static List<AudioTarget> List()
    {
        var ptr = PipeWireNative.pwshim_list_targets();
        try
        {
            if (ptr == IntPtr.Zero) return [];
            var json = Marshal.PtrToStringUTF8(ptr) ?? "[]";
            return JsonSerializer.Deserialize(
                json, AudioTargetJsonContext.Default.ListAudioTarget) ?? new List<AudioTarget>();
        }
        finally
        {
            if (ptr != IntPtr.Zero) PipeWireNative.pwshim_free_string(ptr);
        }
    }
}

// Native-AOT can't reflect at runtime for JSON — source-generated context
// keeps deserialization AOT/trim-safe, matching the rest of this project's
// PublishAot=true stance.
[JsonSerializable(typeof(List<AudioTarget>))]
internal partial class AudioTargetJsonContext : JsonSerializerContext
{
}