using System;
using System.Runtime.InteropServices;

namespace GlavaSharp.Audio;

internal static unsafe partial class PipeWireNative
{
    private const string Lib = "pwshim"; // statically linked in via Native AOT (see native/pwshim/ + <NativeLibrary> in GlavaSharp.csproj) — no separate .so is shipped

    // Raw unmanaged function pointer instead of a marshaled delegate: the
    // delegate-as-native-callback path relies on a runtime-generated
    // marshaling stub, which Native AOT can't always produce ahead-of-time.
    // A [UnmanagedCallersOnly] static method + `delegate* unmanaged<...>`
    // has no such stub, so it stays fully AOT-compatible.
    [LibraryImport(Lib)]
    public static partial IntPtr pwshim_start(
        uint rate,
        uint channels,
        int targetId, // -1 = default sink monitor; else a PipeWire node id
        delegate* unmanaged[Cdecl]<float*, uint, IntPtr, void> cb,
        IntPtr userData);

    [LibraryImport(Lib)]
    public static partial void pwshim_stop(IntPtr ctx);

    // Returns a JSON array of {id, kind, name, description}. Owned by the
    // native side — must be released with pwshim_free_string.
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr pwshim_list_targets();

    [LibraryImport(Lib)]
    public static partial void pwshim_free_string(IntPtr s);
}