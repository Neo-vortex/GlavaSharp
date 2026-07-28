using System;
using OpenTK.Graphics.OpenGL;

namespace GlavaSharp.Shaders;

/// <summary>1D R32F texture holding one channel's magnitude spectrum, uploaded fresh each frame.</summary>
public sealed class AudioSpectrumTexture : IDisposable
{
    public AudioSpectrumTexture(int size)
    {
        Size = size;
        Handle = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture1D, Handle);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage1D(TextureTarget.Texture1D, 0, PixelInternalFormat.R32f, size, 0, PixelFormat.Red, PixelType.Float,
            IntPtr.Zero);
        GL.BindTexture(TextureTarget.Texture1D, 0);
    }

    public int Handle { get; }
    public int Size { get; }

    public void Dispose()
    {
        GL.DeleteTexture(Handle);
    }

    public void Upload(ReadOnlySpan<float> magnitudes)
    {
        GL.BindTexture(TextureTarget.Texture1D, Handle);
        var count = Math.Min(Size, magnitudes.Length);
        // .ToArray() here: OpenTK's  GL.TexSubImage1D overloads take
        // T[]/ref T, not Span<T>, in the 4.9.x binding surface.
        GL.TexSubImage1D(TextureTarget.Texture1D, 0, 0, count, PixelFormat.Red, PixelType.Float,
            magnitudes[..count].ToArray());
        GL.BindTexture(TextureTarget.Texture1D, 0);
    }
}