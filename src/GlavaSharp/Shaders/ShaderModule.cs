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
///
///     Also supports a GlavaSharp-original extension GLava itself has no
///     equivalent for: a pass that declares `#request uniform "history" <name>`
///     gets a persistent ping-pong texture pair that survives across frames
///     instead of being cleared every frame like the normal ping-pong
///     buffers — see <see cref="EnsureHistoryBuffers" />. This is what
///     shaders/glavasharp/waterfall/1.frag uses to accumulate a scrolling
///     spectrogram.
/// </summary>
public sealed class ShaderModule : IDisposable
{
    private const string VertexSource = """
                                        #version 430 core
                                        const vec2 verts[3] = vec2[3](vec2(-1,-1), vec2(3,-1), vec2(-1,3));
                                        void main() { gl_Position = vec4(verts[gl_VertexID], 0.0, 1.0); }
                                        """;

    // Fixed resolution for the optional persistent "history" buffer, deliberately
    // independent of window size (see EnsureHistoryBuffers) -- it's an X-axis
    // (frequency) by Y-axis (time depth) canvas, not a screen-space one.
    private const int HistoryWidth = 1024;
    private const int HistoryHeight = 512;

    private readonly List<Pass> _passes = new();
    private readonly int _vao;
    private int _fboA, _texA, _fboB, _texB;
    private int _width = -1, _height = -1;
    private int _histFboA, _histTexA, _histFboB, _histTexB;
    private bool _historyReadIsA = true;

    public ShaderModule(string rootDir, string moduleName)
    {
        RootDir = rootDir;
        ModuleName = moduleName;
        ModuleDir = Path.Combine(rootDir, moduleName);
        if (!Directory.Exists(ModuleDir))
        {
            // GlavaSharp-original modules (not part of GLava's own bundled
            // tree, which shaders/glava/ reproduces unmodified) live in a
            // sibling "glavasharp" directory instead, e.g.
            // shaders/glavasharp/waterfall next to shaders/glava/. Falling
            // back here means --module waterfall works the same way as any
            // GLava module, without callers needing to know which root it
            // actually lives under.
            var sibling = Path.Combine(Path.GetDirectoryName(rootDir.TrimEnd('/', '\\')) ?? rootDir,
                "glavasharp", moduleName);
            if (Directory.Exists(sibling))
                ModuleDir = sibling;
            else
                throw new DirectoryNotFoundException(
                    $"Module directory not found: {ModuleDir} (also checked {sibling})");
        }

        _vao = GL.GenVertexArray();

        var i = 1;
        while (true)
        {
            var fragPath = Path.Combine(ModuleDir, $"{i}.frag");
            if (!File.Exists(fragPath)) break;

            var (src, uniformBindings) = GlavaPreprocessor.Process(fragPath, ModuleDir, RootDir);
            var pass = new Pass { SourcePath = fragPath };
            Console.WriteLine($"[GlavaSharp] compiling pass {fragPath} ...");
            if (TryCompilePass(src, fragPath, out var program, out var disabledStage))
            {
                Console.WriteLine($"[GlavaSharp] pass {fragPath} compiled+linked OK");
                pass.Enabled = true;
                pass.Program = program;
                // GLava lets a pass name its previous-pass sampler2D anything
                // via `#request uniform "prev" <name>` -- the bundled tree
                // always picks "tex" (not "tex0"), so binding a hardcoded
                // "tex0" here silently misses it on every module with a real
                // second pass (circle/graph/wave all do this; bars/radial's
                // second passes are disabled by default, which is why this
                // went unnoticed there).
                pass.PrevUniformName = uniformBindings.GetValueOrDefault("prev", "tex0");
                // GlavaSharp-original extension, not a GLava convention --
                // see the class doc comment and EnsureHistoryBuffers.
                pass.HistoryUniformName = uniformBindings.GetValueOrDefault("history");
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
        if (_histFboA != 0) GL.DeleteFramebuffer(_histFboA);
        if (_histFboB != 0) GL.DeleteFramebuffer(_histFboB);
        if (_histTexA != 0) GL.DeleteTexture(_histTexA);
        if (_histTexB != 0) GL.DeleteTexture(_histTexB);
        GL.DeleteVertexArray(_vao);
    }

    private void EnsureFramebuffers(int width, int height)
    {
        if (_width == width && _height == height) return;
        _width = width;
        _height = height;

        RecreateTarget(width, height, ref _fboA, ref _texA);
        RecreateTarget(width, height, ref _fboB, ref _texB);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    ///     Lazily allocates the persistent "history" ping-pong pair the first
    ///     time any pass needs one. Fixed <see cref="HistoryWidth" />x
    ///     <see cref="HistoryHeight" /> resolution, deliberately NOT tied to
    ///     window size/resize like <see cref="EnsureFramebuffers" /> -- a
    ///     resize shouldn't reset a scrolling spectrogram's accumulated
    ///     history, and the display pass just samples/stretches it like any
    ///     other texture regardless of the window's actual size.
    /// </summary>
    private void EnsureHistoryBuffers()
    {
        if (_histFboA != 0) return;
        RecreateTarget(HistoryWidth, HistoryHeight, ref _histFboA, ref _histTexA);
        RecreateTarget(HistoryWidth, HistoryHeight, ref _histFboB, ref _histTexB);
        // Both start black/transparent -- an empty history, not garbage VRAM.
        GL.ClearColor(0, 0, 0, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _histFboA);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _histFboB);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private static void RecreateTarget(int width, int height, ref int fbo, ref int tex)
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

    /// <param name="audioL">Sampler1D of left-channel magnitude spectrum (GpuFft output).</param>
    /// <param name="audioR">Sampler1D of right-channel magnitude spectrum.</param>
    /// <param name="audioSz">Number of bins in audioL/audioR.</param>
    public void Render(int width, int height, int audioL, int audioR, int audioSz)
    {
        EnsureFramebuffers(width, height);
        GL.BindVertexArray(_vao);

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

            var isHistoryPass = pass.HistoryUniformName != null;
            int targetFbo;
            int passWidth, passHeight;
            int historyReadTex = -1;

            if (isHistoryPass)
            {
                EnsureHistoryBuffers();
                // Render into whichever buffer ISN'T this frame's "read" one,
                // and don't clear it -- the whole point is that it carries
                // last frame's content forward for the shader to shift/reuse.
                targetFbo = _historyReadIsA ? _histFboB : _histFboA;
                historyReadTex = _historyReadIsA ? _histTexA : _histTexB;
                passWidth = HistoryWidth;
                passHeight = HistoryHeight;
            }
            else
            {
                targetFbo = isLast ? 0 : enabledIndex % 2 == 0 ? _fboA : _fboB;
                passWidth = width;
                passHeight = height;
            }

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
            GL.Viewport(0, 0, passWidth, passHeight);
            if (!isHistoryPass)
            {
                GL.ClearColor(0, 0, 0, 0);
                GL.Clear(ClearBufferMask.ColorBufferBit);
            }

            GL.UseProgram(pass.Program);
            SetUniform2i(pass.Program, "screen", passWidth, passHeight);
            SetUniform1i(pass.Program, "audio_sz", audioSz);

            var unit = 0;
            if (prevTex >= 0) BindSampler(pass.Program, pass.PrevUniformName, prevTex, unit++);
            if (isHistoryPass) BindSampler(pass.Program, pass.HistoryUniformName!, historyReadTex, unit++);
            BindSampler1D(pass.Program, "audio_l", audioL, unit++);
            BindSampler1D(pass.Program, "audio_r", audioR, unit++);

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.Disable(EnableCap.Blend);

            if (isHistoryPass)
            {
                // The buffer just written becomes both this frame's "prev"
                // for the next pass (e.g. a display pass) and next frame's
                // "read" buffer.
                prevTex = _historyReadIsA ? _histTexB : _histTexA;
                _historyReadIsA = !_historyReadIsA;
            }
            else if (!isLast)
            {
                prevTex = enabledIndex % 2 == 0 ? _texA : _texB;
            }

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

        /// <summary>
        ///     GLSL identifier this pass uses for the previous pass's output
        ///     sampler2D, from `#request uniform "prev" <name>` (defaults to
        ///     "tex0" if the pass doesn't declare one, matching GLava's own
        ///     fallback -- though every bundled module that reads a previous
        ///     pass actually names it "tex").
        /// </summary>
        public string PrevUniformName = "tex0";

        /// <summary>
        ///     GLSL identifier for the persistent "history" sampler2D, from
        ///     `#request uniform "history" <name>` -- a GlavaSharp-original
        ///     extension (see the class doc comment), null if this pass
        ///     doesn't declare one (the common case). A pass that declares
        ///     this is assumed NOT to be the module's last enabled pass --
        ///     it writes into the persistent buffer, and a later pass reads
        ///     that as its own <see cref="PrevUniformName" /> to display it.
        /// </summary>
        public string? HistoryUniformName;
    }
}