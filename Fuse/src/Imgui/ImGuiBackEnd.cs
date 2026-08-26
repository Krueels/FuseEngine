using System.Numerics;
using System.Runtime.InteropServices;
using Fuse.Core;
using Fuse.Renderer;
using ImGuiNET;
using Silk.NET.OpenGL;

namespace Fuse.Imgui;

public unsafe class ImGuiBackEnd : IDisposable
{
    private readonly GL _gl;
    private bool _showDebug = true;

    private uint _vao, _vbo, _ebo, _shader;
    private int _uTex, _uProj;
    private uint _fontTexture;

    private const int VtxSize = 20;

    public ImGuiBackEnd(GL gl) => _gl = gl;

    public void Init()
    {
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        CreateDeviceObjects();
    }

    public void NewFrame(float dt, int fbWidth, int fbHeight)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(fbWidth, fbHeight);
        io.DisplayFramebufferScale = Vector2.One;
        io.DeltaTime = dt;
        io.MousePos = new Vector2(Input.Input.MouseX, Input.Input.MouseY);
        io.AddMouseButtonEvent(0, Input.Input.LeftMouseDown());
        io.AddMouseButtonEvent(1, Input.Input.RightMouseDown());
        io.AddMouseButtonEvent(2, Input.Input.MiddleMouseDown());
        ImGui.NewFrame();
    }

    public void DrawWindows(Player.Player? player, Renderer.MasterRenderer renderer)
    {
        if (!_showDebug || player == null) return;

        ImGui.Begin("Debug");
        ImGui.Text($"PLAYER_IsOnGround: {(player.IsOnGround ? "true" : "false")}");
        Vector3 feet = player.FeetPosition;
        ImGui.Text($"PLAYER_Position: {feet.X:F2} {feet.Y:F2} {feet.Z:F2}");
        Vector3 vel = player.NativeCharacter.LinearVelocity;
        float speed = vel.Length();
        ImGui.Text($"PLAYER_Speed: {speed:F2}");
        float playerFov = player.Camera.FOV;
        ImGui.SliderFloat("FOV: ", ref playerFov, 1.0f, 120.0f);
        bool surfMode = player.SurfMode;
        if (ImGui.Checkbox("Surf Mode", ref surfMode))
            player.SurfMode = surfMode;
        if (ImGuiNET.ImGui.CollapsingHeader("Post-Process"))
        {
            var pp = renderer.PostPipeline.Settings; // precisamos expor property pública no MasterRenderer

            ImGuiNET.ImGui.Checkbox("Enabled", ref pp.Enabled);
            ImGuiNET.ImGui.Checkbox("Tonemap", ref pp.TonemapEnabled);
            ImGuiNET.ImGui.Checkbox("Bloom Enabled", ref pp.BloomEnabled);
            ImGuiNET.ImGui.Checkbox("Motion Blur Enabled", ref pp.MotionBlurEnabled);
            ImGuiNET.ImGui.Checkbox("SSAO Enabled", ref pp.SsaoEnabled);

            if (pp.Enabled)
            {
                if (pp.TonemapEnabled)
                {
                    ImGuiNET.ImGui.SeparatorText("Tonemap");
                    ImGuiNET.ImGui.DragFloat("Exposure", ref pp.Exposure, 0.01f, 0.1f, 10.0f);
                }

                if (pp.BloomEnabled)
                {
                    ImGuiNET.ImGui.SeparatorText("Bloom");
                    ImGuiNET.ImGui.DragFloat("Strength", ref pp.BloomStrength, 0.01f, 0f, 2f);
                    ImGuiNET.ImGui.DragFloat("Threshold", ref pp.BloomThreshold, 0.01f, 0f, 5f);
                    ImGuiNET.ImGui.DragFloat("Knee", ref pp.BloomKnee, 0.01f, 0f, 1f);

                    ImGuiNET.ImGui.SeparatorText("Kawase Blur");
                    ImGuiNET.ImGui.DragInt("Radius", ref pp.KawaseRadius, 1, 1, 8);
                    ImGuiNET.ImGui.DragInt("Iterations", ref pp.KawaseIterations, 1, 1, 4);

                    ImGuiNET.ImGui.SeparatorText("Bloom Expansion");
                    ImGuiNET.ImGui.DragFloat("Scale", ref pp.BloomScale, 0.05f, 0f, 5f);
                    ImGuiNET.ImGui.DragFloat3("Tint", ref pp.BloomTint, 0.05f, 0f, 2f);
                    ImGuiNET.ImGui.DragFloat("Anamorphic Ratio", ref pp.BloomAnamorphicRatio, 0.05f, 0f, 4f);
                }
                
                if (pp.MotionBlurEnabled)
                {
                    ImGuiNET.ImGui.SeparatorText("Motion Blur");
                    ImGuiNET.ImGui.DragFloat("MB Intensity", ref pp.MotionBlurIntensity, 0.05f, 0f, 5f);
                    ImGuiNET.ImGui.DragInt("MB Samples", ref pp.MotionBlurSamples, 1, 1, 16);
                }

                if (pp.SsaoEnabled)
                {
                    ImGuiNET.ImGui.SeparatorText("SSAO");
                    ImGuiNET.ImGui.DragFloat("SSAO Intensity", ref pp.SsaoIntensity, 0.05f, 0f, 5f);
                    ImGuiNET.ImGui.DragFloat("SSAO Radius", ref pp.SsaoRadius, 0.05f, 0f, 5f);
                    ImGuiNET.ImGui.DragFloat("SSAO Bias", ref pp.SsaoBias, 0.05f, 0f, 5f);
                    ImGuiNET.ImGui.DragInt("SSAO Kernel", ref pp.SsaoKernelSize, 1, 1, 128);
                    ImGuiNET.ImGui.DragFloat("SSAO Scale", ref pp.SsaoScale, 0.05f, 0f, 5f);


                }

                ImGuiNET.ImGui.SeparatorText("Debug View");
                ImGuiNET.ImGui.Combo("View", ref pp.DebugView, ["Final", "Scene", "Bloom", "Extract"], 4);
            }
        }
        ImGui.End();
    }

    public void Render()
    {
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());
    }

    public void Shutdown()
    {
        ImGui.DestroyContext();
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteProgram(_shader);
        _gl.DeleteTexture(_fontTexture);
    }

    public static bool WantsCaptureMouse => ImGui.GetIO().WantCaptureMouse;
    public static bool WantsCaptureKeyboard => ImGui.GetIO().WantCaptureKeyboard;

    public void SetMouseButton(int button, bool down) => ImGui.GetIO().AddMouseButtonEvent(button, down);
    public void SetMouseScroll(float y) => ImGui.GetIO().AddMouseWheelEvent(0, y);

    private void CreateDeviceObjects()
    {
        string vsSrc = """
            #version 330 core
            layout(location = 0) in vec2 aPos;
            layout(location = 1) in vec2 aUV;
            layout(location = 2) in vec4 aColor;
            uniform mat4 uProj;
            out vec2 vUV;
            out vec4 vColor;
            void main() {
                vUV = aUV;
                vColor = aColor;
                gl_Position = uProj * vec4(aPos, 0, 1);
            }
            """;

        string fsSrc = """
            #version 330 core
            in vec2 vUV;
            in vec4 vColor;
            uniform sampler2D uTex;
            out vec4 fragColor;
            void main() {
                fragColor = vColor * texture(uTex, vUV);
            }
            """;

        _shader = CreateShader(vsSrc, fsSrc);
        _uTex = _gl.GetUniformLocation(_shader, "uTex");
        _uProj = _gl.GetUniformLocation(_shader, "uProj");

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, VtxSize, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, VtxSize, (void*)8);
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, VtxSize, (void*)16);
        _gl.BindVertexArray(0);

        CreateFontsTexture();
    }

    private void CreateFontsTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);

        _fontTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _fontTexture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        io.Fonts.SetTexID((IntPtr)_fontTexture);
        io.Fonts.ClearTexData();
    }

    private void RenderDrawData(ImDrawData* drawData)
    {
        if (drawData->CmdListsCount == 0) return;

        int fbWidth = (int)(drawData->DisplaySize.X * drawData->FramebufferScale.X);
        int fbHeight = (int)(drawData->DisplaySize.Y * drawData->FramebufferScale.Y);
        if (fbWidth <= 0 || fbHeight <= 0) return;

        var clipOff = drawData->DisplayPos;
        var clipScale = drawData->FramebufferScale;

        // Save GL state
        var prevBlend = _gl.IsEnabled(EnableCap.Blend);
        var prevDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        var prevCullFace = _gl.IsEnabled(EnableCap.CullFace);

        // Save GL state
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.ScissorTest);

        _gl.PolygonMode(GLEnum.FrontAndBack, GLEnum.Fill);
        _gl.Viewport(0, 0, (uint)fbWidth, (uint)fbHeight);

        float left = drawData->DisplayPos.X;
        float right = drawData->DisplayPos.X + drawData->DisplaySize.X;
        float top = drawData->DisplayPos.Y;
        float bottom = drawData->DisplayPos.Y + drawData->DisplaySize.Y;
        var proj = Matrix4x4.CreateOrthographicOffCenter(left, right, bottom, top, -1, 1);

        float[] mat = [proj.M11, proj.M12, proj.M13, proj.M14, proj.M21, proj.M22, proj.M23, proj.M24, proj.M31, proj.M32, proj.M33, proj.M34, proj.M41, proj.M42, proj.M43, proj.M44];

        _gl.UseProgram(_shader);
        fixed (float* p = mat)
            _gl.UniformMatrix4(_uProj, 1, false, p);
        _gl.Uniform1(_uTex, 0);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);

        var lists = (ImDrawList**)(void*)drawData->CmdLists.Data;
        for (int n = 0; n < drawData->CmdListsCount; n++)
        {
            var cmdList = lists[n];
            int vtxSize = cmdList->VtxBuffer.Size * VtxSize;
            int idxSize = cmdList->IdxBuffer.Size * sizeof(ushort);

            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)vtxSize, (void*)cmdList->VtxBuffer.Data, BufferUsageARB.StreamDraw);
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)idxSize, (void*)cmdList->IdxBuffer.Data, BufferUsageARB.StreamDraw);

            var cmds = (ImDrawCmd*)(void*)cmdList->CmdBuffer.Data;
            for (int i = 0; i < cmdList->CmdBuffer.Size; i++)
            {
                var cmd = cmds[i];
                if (cmd.UserCallback != IntPtr.Zero) continue;

                var clipRect = new Vector4(
                    (cmd.ClipRect.X - clipOff.X) * clipScale.X,
                    (cmd.ClipRect.Y - clipOff.Y) * clipScale.Y,
                    (cmd.ClipRect.Z - clipOff.X) * clipScale.X,
                    (cmd.ClipRect.W - clipOff.Y) * clipScale.Y);

                if (clipRect.X < fbWidth && clipRect.Y < fbHeight && clipRect.Z >= 0 && clipRect.W >= 0)
                {
                    _gl.Scissor((int)clipRect.X, (int)(fbHeight - clipRect.W),
                        (uint)(clipRect.Z - clipRect.X), (uint)(clipRect.W - clipRect.Y));
                    _gl.Enable(EnableCap.ScissorTest);

                    _gl.ActiveTexture(TextureUnit.Texture0);
                    _gl.BindTexture(TextureTarget.Texture2D, (uint)cmd.TextureId);
                    _gl.DrawElements(PrimitiveType.Triangles, (uint)cmd.ElemCount, DrawElementsType.UnsignedShort, (void*)(cmd.IdxOffset * sizeof(ushort)));
                }
            }
        }

        _gl.Disable(EnableCap.ScissorTest);
        _gl.BindVertexArray(0);

        // Restore GL state
        if (prevBlend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
        if (prevDepthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
        if (prevCullFace) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
    }

    private uint CreateShader(string vsSrc, string fsSrc)
    {
        uint vs = CompileShader(ShaderType.VertexShader, vsSrc);
        uint fs = CompileShader(ShaderType.FragmentShader, fsSrc);
        uint prog = _gl.CreateProgram();
        _gl.AttachShader(prog, vs);
        _gl.AttachShader(prog, fs);
        _gl.LinkProgram(prog);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        return prog;
    }

    private uint CompileShader(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        byte[] srcBytes = System.Text.Encoding.UTF8.GetBytes(source);
        fixed (byte* ptr = srcBytes)
        {
            int len = srcBytes.Length;
            _gl.ShaderSource(shader, 1, (byte**)&ptr, &len);
        }
        _gl.CompileShader(shader);
        _gl.GetShader(shader, GLEnum.CompileStatus, out int success);
        if (success == 0)
        {
            string info = _gl.GetShaderInfoLog(shader);
            Logger.Error($"ImGui shader compile ({type}): {info}");
        }
        return shader;
    }
}
