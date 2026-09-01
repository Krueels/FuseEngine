using System.Numerics;
using Silk.NET.OpenGL;
using Fuse.Scene.Model;

namespace Fuse.Renderer;

/// <summary>Precomputed environment resources used by the Cook-Torrance material shader.</summary>
public unsafe sealed class ImageBasedLighting : IDisposable
{
    public const int DiffuseUnit = 15;
    public const int PrefilteredUnit = 16;
    public const int BrdfLutUnit = 17;

    private readonly GL _gl;
    private uint _environment, _irradiance, _prefiltered, _brdfLut;
    private uint _fbo, _cubeVao, _cubeVbo, _quadVao, _quadVbo;
    private readonly bool _ownsEnvironment;
    private bool _disposed;

    public uint EnvironmentCubemap => _environment;
    public uint DiffuseIrradianceMap => _irradiance;
    public uint PrefilteredEnvironmentMap => _prefiltered;
    public uint BrdfLut => _brdfLut;

    public ImageBasedLighting(GL gl, Texture source)
        : this(gl)
    {
        if (source.ID == 0)
        {
            Dispose();
            throw new InvalidOperationException("Cannot create IBL from an invalid environment texture.");
        }

        try
        {
            BuildAll(() => BuildEnvironment(source));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the same irradiance, prefiltered environment and BRDF resources
    /// used by texture skyboxes, but captures the analytic sky into the source
    /// cubemap first. This is intentionally an explicit factory because the
    /// operation is expensive and must not happen every frame.
    /// </summary>
    public static ImageBasedLighting CreateProcedural(
        GL gl,
        SkyboxSettings settings,
        Vector3 sunDirection,
        Vector3 directionalLightColor)
    {
        var lighting = new ImageBasedLighting(gl);
        try
        {
            lighting.BuildAll(() => lighting.BuildProceduralEnvironment(
                settings,
                sunDirection,
                directionalLightColor));
            return lighting;
        }
        catch
        {
            lighting.Dispose();
            throw;
        }
    }

    private ImageBasedLighting(GL gl)
    {
        _gl = gl;
        _ownsEnvironment = true;
        CreateGeometry();
        _fbo = _gl.GenFramebuffer();
    }

    public void Bind(Shader shader, float intensity = 1.0f)
    {
        bool valid = !_disposed && _environment != 0 && _irradiance != 0 &&
                     _prefiltered != 0 && _brdfLut != 0;
        shader.Use();
        shader.SetBool("uUseIbl", valid);
        shader.SetFloat("uIblIntensity", intensity);
        if (!valid)
            return;

        BindTexture(TextureTarget.TextureCubeMap, _irradiance, DiffuseUnit);
        shader.SetInt("uDiffuseIrradianceMap", DiffuseUnit);
        BindTexture(TextureTarget.TextureCubeMap, _prefiltered, PrefilteredUnit);
        shader.SetInt("uPrefilteredEnvMap", PrefilteredUnit);
        BindTexture(TextureTarget.Texture2D, _brdfLut, BrdfLutUnit);
        shader.SetInt("uBrdfLut", BrdfLutUnit);
    }

    private void BuildAll(Action buildEnvironment)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        // The capture/convolution shaders render a cube from its interior.
        // The editor and game initialize OpenGL with back-face culling enabled,
        // which would discard the faces visible from inside the cube and leave
        // the environment maps black. Isolate the temporary capture state and
        // restore the caller's state before returning to the normal renderer.
        bool cullFaceWasEnabled = _gl.IsEnabled(EnableCap.CullFace);
        bool depthTestWasEnabled = _gl.IsEnabled(EnableCap.DepthTest);
        bool blendWasEnabled = _gl.IsEnabled(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(false);
        try
        {
            buildEnvironment();
            BuildIrradiance();
            BuildPrefiltered();
            BuildBrdfLut();
        }
        finally
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.BindVertexArray(0);
            _gl.DepthMask(true);
            if (cullFaceWasEnabled) _gl.Enable(EnableCap.CullFace);
            else _gl.Disable(EnableCap.CullFace);
            if (depthTestWasEnabled) _gl.Enable(EnableCap.DepthTest);
            else _gl.Disable(EnableCap.DepthTest);
            if (blendWasEnabled) _gl.Enable(EnableCap.Blend);
            else _gl.Disable(EnableCap.Blend);
        }
    }

    private void BuildEnvironment(Texture source)
    {
        const int size = 256;
        _environment = CreateCubemap(size, 1, InternalFormat.Rgba16f, TextureMinFilter.Linear);
        using Shader shader = new(_gl, CaptureVertex, EquirectangularFragment);
        EnsureShader(shader, "environment conversion");
        shader.Use();
        shader.SetInt("uEquirectangularMap", 0);
        shader.SetMat4("uProjection", Projection);
        for (int face = 0; face < 6; face++)
        {
            AttachCubemapFace(_environment, face, 0);
            _gl.Viewport(0, 0, size, size);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            shader.SetMat4("uView", CaptureViews[face]);
            source.Bind(0);
            DrawCube();
        }
    }

    private void BuildProceduralEnvironment(
        SkyboxSettings settings,
        Vector3 sunDirection,
        Vector3 directionalLightColor)
    {
        const int size = 256;
        _environment = CreateCubemap(size, 1, InternalFormat.Rgba16f, TextureMinFilter.Linear);

        string shaderDirectory = Path.Combine(Fuse.ResPath.Path, "Shaders");
        string vertexPath = Path.Combine(shaderDirectory, "skybox_capture.vert");
        string fragmentPath = Path.Combine(shaderDirectory, "skybox.frag");
        using Shader shader = Shader.FromFile(_gl, vertexPath, fragmentPath);
        EnsureShader(shader, "procedural sky environment");
        shader.Use();
        shader.SetMat4("uProj", Projection);
        ProceduralSky.ApplyShaderParameters(
            shader,
            settings,
            sunDirection,
            directionalLightColor);
        shader.SetBool("uOutputSrgb", false);
        shader.SetInt("uSkyTexture", 0);

        for (int face = 0; face < 6; face++)
        {
            AttachCubemapFace(_environment, face, 0);
            _gl.Viewport(0, 0, size, size);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            shader.SetMat4("uView", CaptureViews[face]);
            DrawCube();
        }
    }

    private void BuildIrradiance()
    {
        const int size = 32;
        _irradiance = CreateCubemap(size, 1, InternalFormat.Rgba16f, TextureMinFilter.Linear);
        using Shader shader = new(_gl, CaptureVertex, IrradianceFragment);
        EnsureShader(shader, "irradiance convolution");
        shader.Use();
        shader.SetInt("uEnvironmentMap", 0);
        shader.SetMat4("uProjection", Projection);
        for (int face = 0; face < 6; face++)
        {
            AttachCubemapFace(_irradiance, face, 0);
            _gl.Viewport(0, 0, size, size);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            shader.SetMat4("uView", CaptureViews[face]);
            BindTexture(TextureTarget.TextureCubeMap, _environment, 0);
            DrawCube();
        }
    }

    private void BuildPrefiltered()
    {
        const int baseSize = 128;
        const int mipCount = 5;
        _prefiltered = CreateCubemap(baseSize, mipCount, InternalFormat.Rgba16f, TextureMinFilter.LinearMipmapLinear);
        using Shader shader = new(_gl, CaptureVertex, PrefilterFragment);
        EnsureShader(shader, "prefiltered environment");
        shader.Use();
        shader.SetInt("uEnvironmentMap", 0);
        shader.SetMat4("uProjection", Projection);
        for (int mip = 0; mip < mipCount; mip++)
        {
            int size = System.Math.Max(1, baseSize >> mip);
            shader.SetFloat("uRoughness", (float)mip / (mipCount - 1));
            _gl.Viewport(0, 0, (uint)size, (uint)size);
            for (int face = 0; face < 6; face++)
            {
                AttachCubemapFace(_prefiltered, face, mip);
                _gl.Clear(ClearBufferMask.ColorBufferBit);
                shader.SetMat4("uView", CaptureViews[face]);
                BindTexture(TextureTarget.TextureCubeMap, _environment, 0);
                DrawCube();
            }
        }
    }

    private void BuildBrdfLut()
    {
        const int size = 256;
        _brdfLut = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _brdfLut);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, 0x822F, (uint)size, (uint)size, 0,
            (PixelFormat)0x8227, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _brdfLut, 0);
        CheckFramebuffer("BRDF LUT");
        _gl.Viewport(0, 0, size, size);
        using Shader shader = new(_gl, FullscreenVertex, BrdfFragment);
        EnsureShader(shader, "BRDF LUT");
        shader.Use();
        DrawQuad();
    }

    private uint CreateCubemap(int size, int mipCount, InternalFormat format, TextureMinFilter minFilter)
    {
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.TextureCubeMap, texture);
        for (int mip = 0; mip < mipCount; mip++)
        {
            int mipSize = System.Math.Max(1, size >> mip);
            for (int face = 0; face < 6; face++)
                _gl.TexImage2D((TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face), mip,
                    (int)format, (uint)mipSize, (uint)mipSize, 0, PixelFormat.Rgba, PixelType.Float, null);
        }
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)minFilter);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMaxLevel, mipCount - 1);
        return texture;
    }

    private void AttachCubemapFace(uint texture, int face, int mip)
    {
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            (TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face), texture, mip);
        CheckFramebuffer("cubemap convolution");
    }

    private void CreateGeometry()
    {
        float[] cube =
        [
            -1,-1,-1, 1,-1,-1, 1,1,-1, 1,1,-1,-1,1,-1,-1,-1,-1,
            -1,-1,1, 1,-1,1, 1,1,1, 1,1,1,-1,1,1,-1,-1,1,
            -1,1,1,-1,1,-1,-1,-1,-1,-1,-1,-1,-1,-1,1,-1,1,1,
            1,1,1, 1,1,-1, 1,-1,-1, 1,-1,-1, 1,-1,1, 1,1,1,
            -1,-1,-1, 1,-1,-1, 1,-1,1, 1,-1,1,-1,-1,1,-1,-1,-1,
            -1,1,-1, 1,1,-1, 1,1,1, 1,1,1,-1,1,1,-1,1,-1
        ];
        _cubeVao = _gl.GenVertexArray();
        _cubeVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_cubeVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _cubeVbo);
        fixed (float* ptr = cube)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(cube.Length * sizeof(float)), ptr, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), null);

        float[] quad = [-1,-1, 1,-1, 1,1, -1,-1, 1,1, -1,1];
        _quadVao = _gl.GenVertexArray();
        _quadVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_quadVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        fixed (float* ptr = quad)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), ptr, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), null);
        _gl.BindVertexArray(0);
    }

    private void DrawCube()
    {
        _gl.BindVertexArray(_cubeVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 36);
        _gl.BindVertexArray(0);
    }

    private void DrawQuad()
    {
        _gl.BindVertexArray(_quadVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        _gl.BindVertexArray(0);
    }

    private void BindTexture(TextureTarget target, uint texture, int slot)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + slot);
        _gl.BindTexture(target, texture);
    }

    private void CheckFramebuffer(string name)
    {
        if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"IBL framebuffer incomplete during {name}.");
    }

    private static void EnsureShader(Shader shader, string name)
    {
        if (!shader.IsValid)
            throw new InvalidOperationException($"IBL shader compilation failed during {name}.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsEnvironment && _environment != 0) _gl.DeleteTexture(_environment);
        if (_irradiance != 0) _gl.DeleteTexture(_irradiance);
        if (_prefiltered != 0) _gl.DeleteTexture(_prefiltered);
        if (_brdfLut != 0) _gl.DeleteTexture(_brdfLut);
        if (_fbo != 0) _gl.DeleteFramebuffer(_fbo);
        if (_cubeVao != 0) _gl.DeleteVertexArray(_cubeVao);
        if (_cubeVbo != 0) _gl.DeleteBuffer(_cubeVbo);
        if (_quadVao != 0) _gl.DeleteVertexArray(_quadVao);
        if (_quadVbo != 0) _gl.DeleteBuffer(_quadVbo);
    }

    private static readonly Matrix4x4 Projection =
        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2.0f, 1.0f, 0.1f, 10.0f);

    private static readonly Matrix4x4[] CaptureViews =
    [
        Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitX, -Vector3.UnitY),
        Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitX, -Vector3.UnitY),
        Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ),
        Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitY, -Vector3.UnitZ),
        Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitZ, -Vector3.UnitY),
        Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, -Vector3.UnitY)
    ];

    private const string CaptureVertex = """
#version 330 core
layout(location = 0) in vec3 aPosition;
out vec3 vLocalPosition;
uniform mat4 uView;
uniform mat4 uProjection;
void main() {
    vLocalPosition = aPosition;
    gl_Position = uProjection * uView * vec4(aPosition, 1.0);
}
""";

    private const string EquirectangularFragment = """
#version 330 core
in vec3 vLocalPosition;
out vec4 FragColor;
uniform sampler2D uEquirectangularMap;
vec2 Spherical(vec3 v) {
    vec2 uv = vec2(atan(v.z, v.x), asin(clamp(v.y, -1.0, 1.0)));
    uv *= vec2(0.15915494309, 0.31830988618);
    uv += 0.5;
    return vec2(uv.x, 1.0 - uv.y);
}
void main() {
    FragColor = vec4(texture(uEquirectangularMap, Spherical(normalize(vLocalPosition))).rgb, 1.0);
}
""";

    private const string IrradianceFragment = """
#version 330 core
in vec3 vLocalPosition;
out vec4 FragColor;
uniform samplerCube uEnvironmentMap;
const float PI = 3.14159265359;
void main() {
    vec3 n = normalize(vLocalPosition);
    vec3 up = abs(n.y) < 0.999 ? vec3(0,1,0) : vec3(0,0,1);
    vec3 right = normalize(cross(up, n));
    up = normalize(cross(n, right));
    vec3 sum = vec3(0);
    float count = 0.0;
    const float delta = 0.20;
    for (float phi = 0.0; phi < 2.0 * PI; phi += delta)
        for (float theta = 0.0; theta < 0.5 * PI; theta += delta) {
            vec3 local = vec3(sin(theta)*cos(phi), sin(theta)*sin(phi), cos(theta));
            vec3 sampleDir = local.x*right + local.y*up + local.z*n;
            sum += texture(uEnvironmentMap, sampleDir).rgb * cos(theta) * sin(theta);
            count += 1.0;
        }
    FragColor = vec4(PI * sum / max(count, 1.0), 1.0);
}
""";

    private const string PrefilterFragment = """
#version 330 core
in vec3 vLocalPosition;
out vec4 FragColor;
uniform samplerCube uEnvironmentMap;
uniform float uRoughness;
const float PI = 3.14159265359;
float RadicalInverse(uint bits) {
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10;
}
vec2 Hammersley(uint i, uint n) { return vec2(float(i)/float(n), RadicalInverse(i)); }
vec3 Importance(vec2 xi, vec3 n, float roughness) {
    float a = roughness * roughness;
    float phi = 2.0 * PI * xi.x;
    float cosTheta = sqrt((1.0-xi.y) / (1.0+(a*a-1.0)*xi.y));
    float sinTheta = sqrt(max(1.0-cosTheta*cosTheta, 0.0));
    vec3 h = vec3(cos(phi)*sinTheta, sin(phi)*sinTheta, cosTheta);
    vec3 up = abs(n.z) < 0.999 ? vec3(0,0,1) : vec3(1,0,0);
    vec3 t = normalize(cross(up,n));
    return normalize(t*h.x + cross(n,t)*h.y + n*h.z);
}
void main() {
    vec3 n = normalize(vLocalPosition);
    vec3 v = n;
    vec3 sum = vec3(0);
    float weight = 0.0;
    const uint COUNT = 128u;
    for (uint i=0u; i<COUNT; i++) {
        vec3 h = Importance(Hammersley(i, COUNT), n, max(uRoughness, 0.001));
        vec3 l = normalize(2.0*dot(v,h)*h-v);
        float nDotL = max(dot(n,l), 0.0);
        if (nDotL > 0.0) {
            sum += textureLod(uEnvironmentMap, l, uRoughness*4.0).rgb*nDotL;
            weight += nDotL;
        }
    }
    FragColor = vec4(sum/max(weight,0.001), 1.0);
}
""";

    private const string FullscreenVertex = """
#version 330 core
layout(location = 0) in vec2 aPosition;
out vec2 vUv;
void main() {
    vUv = aPosition * 0.5 + 0.5;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
""";

    private const string BrdfFragment = """
#version 330 core
in vec2 vUv;
layout(location = 0) out vec2 FragColor;
const float PI = 3.14159265359;
float RadicalInverse(uint bits) {
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10;
}
vec2 Hammersley(uint i, uint n) { return vec2(float(i)/float(n), RadicalInverse(i)); }
vec3 Importance(vec2 xi, float roughness) {
    float a = roughness * roughness;
    float phi = 2.0 * PI * xi.x;
    float cosTheta = sqrt((1.0-xi.y) / (1.0+(a*a-1.0)*xi.y));
    float sinTheta = sqrt(max(1.0-cosTheta*cosTheta, 0.0));
    return normalize(vec3(cos(phi)*sinTheta, sin(phi)*sinTheta, cosTheta));
}
float G(float n, float r) {
    float k = r*r/2.0;
    return n/max(n*(1.0-k)+k, 0.0001);
}
vec2 Integrate(float nDotV, float roughness) {
    vec3 v = vec3(sqrt(1.0-nDotV*nDotV), 0, nDotV);
    float a = 0, b = 0;
    const uint COUNT = 128u;
    for (uint i=0u; i<COUNT; i++) {
        vec3 h = Importance(Hammersley(i, COUNT), roughness);
        vec3 l = normalize(2.0*dot(v,h)*h-v);
        float nDotL = max(l.z,0), nDotH = max(h.z,0), vDotH = max(dot(v,h),0);
        if (nDotL > 0) {
            float geometry = G(nDotV,roughness)*G(nDotL,roughness);
            float vis = geometry*vDotH/max(nDotH*nDotV,0.001);
            float f = pow(1.0-vDotH,5.0);
            a += (1.0-f)*vis;
            b += f*vis;
        }
    }
    return vec2(a,b)/float(COUNT);
}
void main() { FragColor = Integrate(vUv.x, vUv.y); }
""";

}
