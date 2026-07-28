using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenTK.Graphics.OpenGL;

namespace GlavaSharp.Shaders;

/// <summary>
///     Loads a GLava module directory (e.g. shaders/glava/bars, containing
///     1.frag, 2.frag, ...) as a chain of full-screen fragment passes, per
///     GLava's own convention: "shaders are loaded in numerical order ...
///     the results of each shader (except the final pass) is given to the
///     next shader in the list as a 2D sampler." Passes containing GLava's
///     `#error __disablestage` sentinel (e.g. bars/2.frag when USE_ALPHA=0)
///     are skipped and just pass the previous pass's texture straight through.
/// </summary>
public sealed class ShaderModule : IDisposable
{
    private const string VertexSource = """
                                        #version 430 core
                                        const vec2 verts[3] = vec2[3](vec2(-1,-1), vec2(3,-1), vec2(-1,3));
                                        void main() { gl_Position = vec4(verts[gl_VertexID], 0.0, 1.0); }
                                        """;

    private readonly List<Pass> _passes = new();
    private readonly int _vao;
    private int _fboA, _texA, _fboB, _texB;
    private int _width = -1, _height = -1;

    public ShaderModule(string rootDir, string moduleName)
    {
        RootDir = rootDir;
        ModuleName = moduleName;
        ModuleDir = Path.Combine(rootDir, moduleName);
        if (!Directory.Exists(ModuleDir))
            throw new DirectoryNotFoundException($"Module directory not found: {ModuleDir}");

        _vao = GL.GenVertexArray();

        var i = 1;
        while (true)
        {
            var fragPath = Path.Combine(ModuleDir, $"{i}.frag");
            if (!File.Exists(fragPath)) break;

            var src = GlavaPreprocessor.Process(fragPath, ModuleDir, RootDir);
            var pass = new Pass { SourcePath = fragPath };
            Console.WriteLine($"[GlavaSharp] compiling pass {fragPath} ...");
            if (TryCompilePass(src, fragPath, out var program, out var disabledStage))
            {
                Console.WriteLine($"[GlavaSharp] pass {fragPath} compiled+linked OK");
                pass.Enabled = true;
                pass.Program = program;
            }
            else if (disabledStage)
            {
                // GLava's `#error __disablestage` sentinel fired inside an
                // #if this module's #defines resolved to true (e.g.
                // bars/2.frag under the default USE_ALPHA=0) — this stage
                // intentionally does not run.
                pass.Enabled = false;
            }
            else
            {
                throw new InvalidOperationException($"Shader compile failed for {fragPath} (see stderr above).");
            }

            _passes.Add(pass);
            i++;
        }

        if (_passes.Count == 0)
            throw new InvalidOperationException(
                $"No numbered .frag passes found in {ModuleDir} (expected 1.frag, 2.frag, ...)");
    }

    public string ModuleDir { get; }
    public string RootDir { get; }
    public string ModuleName { get; }

    public void Dispose()
    {
        foreach (var p in _passes.Where(p => p.Enabled)) GL.DeleteProgram(p.Program);
        if (_fboA != 0) GL.DeleteFramebuffer(_fboA);
        if (_fboB != 0) GL.DeleteFramebuffer(_fboB);
        if (_texA != 0) GL.DeleteTexture(_texA);
        if (_texB != 0) GL.DeleteTexture(_texB);
        GL.DeleteVertexArray(_vao);
    }

    private void EnsureFramebuffers(int width, int height)
    {
        if (_width == width && _height == height) return;
        _width = width;
        _height = height;

        Recreate(ref _fboA, ref _texA);
        Recreate(ref _fboB, ref _texB);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return;

        void Recreate(ref int fbo, ref int tex)
        {
            if (fbo != 0) GL.DeleteFramebuffer(fbo);
            if (tex != 0) GL.DeleteTexture(tex);
            tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);

            fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, tex, 0);
        }
    }

    /// <param name="audioL">Sampler1D of left-channel magnitude spectrum (GpuFft output).</param>
    /// <param name="audioR">Sampler1D of right-channel magnitude spectrum.</param>
    /// <param name="audioSz">Number of bins in audioL/audioR.</param>
    public void Render(int width, int height, int audioL, int audioR, int audioSz)
    {
        EnsureFramebuffers(width, height);
        GL.BindVertexArray(_vao);
        GL.Viewport(0, 0, width, height);

        var lastEnabled = -1;
        for (var i = 0; i < _passes.Count; i++)
            if (_passes[i].Enabled)
                lastEnabled = i;
        if (lastEnabled < 0)
            throw new InvalidOperationException(
                $"All passes in module '{ModuleName}' are disabled — nothing to render.");

        var prevTex = -1; // -1 = no previous pass output yet
        var enabledIndex = 0;
        for (var i = 0; i < _passes.Count; i++)
        {
            var pass = _passes[i];
            // "Last" means last ENABLED pass, not last file: a disabled
            // trailing pass (e.g. bars/2.frag when USE_ALPHA=0) must not
            // stop the previous pass's output from reaching the screen.
            var isLast = i == lastEnabled;

            if (!pass.Enabled)
                // This stage doesn't run at all (GLava's __disablestage);
                // prevTex carries straight through to the next real pass.
                continue;

            var targetFbo = isLast ? 0 : enabledIndex % 2 == 0 ? _fboA : _fboB;
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
            if (isLast)
                GL.Viewport(0, 0, width, height);
            GL.ClearColor(0, 0, 0, 0);
            GL.Clear(ClearBufferMask.ColorBufferBit);

            GL.UseProgram(pass.Program);
            SetUniform2i(pass.Program, "screen", width, height);
            SetUniform1i(pass.Program, "audio_sz", audioSz);

            var unit = 0;
            if (prevTex >= 0) BindSampler(pass.Program, "tex0", prevTex, unit++);
            BindSampler1D(pass.Program, "audio_l", audioL, unit++);
            BindSampler1D(pass.Program, "audio_r", audioR, unit++);

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.Disable(EnableCap.Blend);

            if (!isLast)
                prevTex = enabledIndex % 2 == 0 ? _texA : _texB;
            enabledIndex++;
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private static void SetUniform2i(int program, string name, int x, int y)
    {
        var loc = GL.GetUniformLocation(program, name);
        if (loc >= 0) GL.Uniform2(loc, x, y);
    }

    private static void SetUniform1i(int program, string name, int v)
    {
        var loc = GL.GetUniformLocation(program, name);
        if (loc >= 0) GL.Uniform1(loc, v);
    }

    private static void BindSampler(int program, string name, int texture, int unit)
    {
        var loc = GL.GetUniformLocation(program, name);
        if (loc < 0) return;
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.Uniform1(loc, unit);
    }

    private static void BindSampler1D(int program, string name, int texture, int unit)
    {
        var loc = GL.GetUniformLocation(program, name);
        if (loc < 0) return;
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture1D, texture);
        GL.Uniform1(loc, unit);
    }

    /// <returns>
    ///     False on any compile failure. When false, <paramref name="disabledStage" />
    ///     tells the caller whether it was specifically GLava's `#error __disablestage`
    ///     sentinel (an intentional no-op stage) versus a real shader bug.
    /// </returns>
    private static bool TryCompilePass(string fragSource, string path, out int program, out bool disabledStage)
    {
        program = 0;
        disabledStage = false;

        Console.WriteLine("[GlavaSharp]   compiling vertex shader ...");
        var vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, VertexSource);
        GL.CompileShader(vs);
        CheckShader(vs, "vertex (fullscreen triangle)"); // this one's ours; a failure here is always a real bug
        Console.WriteLine("[GlavaSharp]   vertex shader compiled OK");

        var fullFrag = "#version 430 core\n" + fragSource;
        var fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fullFrag);
        Console.WriteLine($"[GlavaSharp]   compiling fragment shader (source below) ...\n{fullFrag}");
        GL.CompileShader(fs);
        Console.WriteLine("[GlavaSharp]   fragment shader glCompileShader() returned");
        GL.GetShader(fs, ShaderParameter.CompileStatus, out var fsOk);
        if (fsOk == 0)
        {
            GL.GetShaderInfoLog(fs, out var log);
            GL.DeleteShader(fs);
            GL.DeleteShader(vs);
            if (log.Contains("__disablestage"))
            {
                disabledStage = true;
                return false;
            }

            Console.Error.WriteLine($"Shader compile failed [{path}]:\n{log}");
            return false;
        }

        program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        Console.WriteLine("[GlavaSharp]   linking program ...");
        GL.LinkProgram(program);
        Console.WriteLine("[GlavaSharp]   glLinkProgram() returned");
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
        GL.DetachShader(program, vs);
        GL.DetachShader(program, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        if (linked != 0) return true;
        {
            GL.GetProgramInfoLog(program, out var log);
            Console.Error.WriteLine($"Link failed for {path}:\n{log}");
            GL.DeleteProgram(program);
            program = 0;
            return false;
        }
    }

    private static void CheckShader(int shader, string label)
    {
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
        if (ok != 0) return;
        GL.GetShaderInfoLog(shader, out var log);
        throw new InvalidOperationException($"Shader compile failed [{label}]:\n{log}");
    }

    private sealed class Pass
    {
        public bool Enabled;
        public int Program;
        public string SourcePath = "";
    }
}