using System.Numerics;
using Fuse.Core;
using Silk.NET.OpenGL;

namespace Fuse.Renderer;

/// <summary>
/// GPU Forward+ light culling.
///
/// One compute invocation owns one screen tile. It tests the selected point
/// and spot lights against that tile and writes compact local indices into an
/// SSBO. The material fragment shader then evaluates only those indices instead
/// of iterating over every light in the scene.
/// </summary>
public sealed unsafe class ForwardPlusLighting : IDisposable
{
    public const int TileSize = 16;
    public const int MaxPointLights = 128;
    public const int MaxSpotLights = 128;
    public const int MaxLightsPerTile = 64;

    public const uint TileListBinding = 2;
    public const uint LightIndexBinding = 3;
    public const uint LightDataBinding = 4;

    private const int PointLightFloatStride = 12;
    private const int SpotLightFloatStride = 16;

    private readonly GL _gl;
    private readonly ComputeShader _cullingShader;
    private readonly uint _tileListBuffer;
    private readonly uint _lightIndexBuffer;
    private readonly uint _lightDataBuffer;
    private readonly float[] _lightData = new float[
        MaxPointLights * PointLightFloatStride + MaxSpotLights * SpotLightFloatStride];

    private int _width;
    private int _height;
    private int _tileCountX;
    private int _tileCountY;

    public bool IsSupported => _cullingShader.IsValid;
    public int TileCountX => _tileCountX;
    public int TileCountY => _tileCountY;
    public int PointLightCount { get; private set; }
    public int SpotLightCount { get; private set; }

    public ForwardPlusLighting(GL gl, string computeShaderPath)
    {
        _gl = gl;
        _cullingShader = ComputeShader.FromFile(gl, computeShaderPath);
        _tileListBuffer = gl.GenBuffer();
        _lightIndexBuffer = gl.GenBuffer();
        _lightDataBuffer = gl.GenBuffer();

        gl.BindBuffer(GLEnum.ShaderStorageBuffer, _lightDataBuffer);
        gl.BufferData(
            GLEnum.ShaderStorageBuffer,
            (nuint)(_lightData.Length * sizeof(float)),
            null,
            GLEnum.DynamicDraw);
        gl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);

        Resize(1, 1);
    }

    public void Resize(int width, int height)
    {
        width = System.Math.Max(width, 1);
        height = System.Math.Max(height, 1);
        int tileCountX = (width + TileSize - 1) / TileSize;
        int tileCountY = (height + TileSize - 1) / TileSize;
        if (_width == width && _height == height)
            return;

        _width = width;
        _height = height;
        _tileCountX = tileCountX;
        _tileCountY = tileCountY;

        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, _tileListBuffer);
        _gl.BufferData(
            GLEnum.ShaderStorageBuffer,
            (nuint)(_tileCountX * _tileCountY * 4 * sizeof(uint)),
            null,
            GLEnum.DynamicDraw);

        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, _lightIndexBuffer);
        int indexCount = _tileCountX * _tileCountY * MaxLightsPerTile * 2;
        _gl.BufferData(
            GLEnum.ShaderStorageBuffer,
            (nuint)(indexCount * sizeof(uint)),
            null,
            GLEnum.DynamicDraw);
        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
    }

    public void UploadLights(
        ReadOnlySpan<Light> pointLights,
        ReadOnlySpan<Light> spotLights,
        ReadOnlySpan<Light> shadowPointLights,
        bool localShadowsEnabled)
    {
        PointLightCount = System.Math.Min(pointLights.Length, MaxPointLights);
        SpotLightCount = System.Math.Min(spotLights.Length, MaxSpotLights);
        Array.Clear(_lightData);

        int pointOffset = 0;
        for (int i = 0; i < PointLightCount; i++)
        {
            Light light = pointLights[i];
            int shadowMapIndex = -1;
            if (localShadowsEnabled)
            {
                for (int shadowIndex = 0; shadowIndex < shadowPointLights.Length; shadowIndex++)
                {
                    if (ReferenceEquals(shadowPointLights[shadowIndex], light))
                    {
                        shadowMapIndex = shadowIndex;
                        break;
                    }
                }
            }

            WriteVec4(pointOffset,
                light.Position.X, light.Position.Y, light.Position.Z, light.Radius);
            Vector3 color = light.Color * light.Intensity;
            WriteVec4(pointOffset + 4, color.X, color.Y, color.Z, shadowMapIndex);
            WriteVec4(pointOffset + 8,
                light.ShadowBias,
                1.0f / MathF.Max(light.Radius, 0.001f),
                0.0f,
                0.0f);
            pointOffset += PointLightFloatStride;
        }

        int spotOffset = MaxPointLights * PointLightFloatStride;
        for (int i = 0; i < SpotLightCount; i++)
        {
            Light light = spotLights[i];
            Vector3 direction = light.Direction.LengthSquared() > 1e-8f
                ? Vector3.Normalize(light.Direction)
                : -Vector3.UnitY;
            Vector3 color = light.Color * light.Intensity;
            bool hasShadowMap = localShadowsEnabled &&
                                 light.CastShadows &&
                                 i < LightingBuffer.MaxSpotLights;

            WriteVec4(spotOffset,
                light.Position.X, light.Position.Y, light.Position.Z, light.Radius);
            WriteVec4(spotOffset + 4,
                direction.X, direction.Y, direction.Z, light.InnerCos);
            WriteVec4(spotOffset + 8,
                color.X, color.Y, color.Z, light.OuterCos);
            WriteVec4(spotOffset + 12,
                hasShadowMap ? 1.0f : 0.0f,
                light.ShadowBias,
                i,
                0.0f);
            spotOffset += SpotLightFloatStride;
        }

        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, _lightDataBuffer);
        fixed (float* data = _lightData)
        {
            _gl.BufferSubData(
                GLEnum.ShaderStorageBuffer,
                0,
                (nuint)(_lightData.Length * sizeof(float)),
                data);
        }
        _gl.BindBufferBase(GLEnum.ShaderStorageBuffer, LightDataBinding, _lightDataBuffer);
        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
    }

    public void Dispatch(Matrix4x4 view, Matrix4x4 projection, int width, int height)
    {
        if (!IsSupported)
            return;

        Resize(width, height);
        _cullingShader.Use();
        _cullingShader.SetMat4("uView", view);
        _cullingShader.SetMat4("uProj", projection);
        _cullingShader.SetVec2("uScreenSize", new Vector2(_width, _height));
        _cullingShader.SetInt("uTileCountX", _tileCountX);
        _cullingShader.SetInt("uTileCountY", _tileCountY);
        _cullingShader.SetInt("uPointLightCount", PointLightCount);
        _cullingShader.SetInt("uSpotLightCount", SpotLightCount);

        _gl.BindBufferBase(GLEnum.ShaderStorageBuffer, TileListBinding, _tileListBuffer);
        _gl.BindBufferBase(GLEnum.ShaderStorageBuffer, LightIndexBinding, _lightIndexBuffer);
        _gl.BindBufferBase(GLEnum.ShaderStorageBuffer, LightDataBinding, _lightDataBuffer);
        _gl.DispatchCompute((uint)_tileCountX, (uint)_tileCountY, 1);
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
    }

    public void ConfigureShader(Shader shader)
    {
        shader.SetBool("uUseForwardPlus", IsSupported);
        shader.SetInt("uForwardPlusTileCountX", _tileCountX);
        shader.SetInt("uForwardPlusTileCountY", _tileCountY);
        shader.SetInt("uForwardPlusPointCount", PointLightCount);
        shader.SetInt("uForwardPlusSpotCount", SpotLightCount);
    }

    public bool ReloadShader() => _cullingShader.Reload();

    private void WriteVec4(int offset, float x, float y, float z, float w)
    {
        _lightData[offset] = x;
        _lightData[offset + 1] = y;
        _lightData[offset + 2] = z;
        _lightData[offset + 3] = w;
    }

    public void Dispose()
    {
        _cullingShader.Dispose();
        _gl.DeleteBuffer(_tileListBuffer);
        _gl.DeleteBuffer(_lightIndexBuffer);
        _gl.DeleteBuffer(_lightDataBuffer);
    }
}
