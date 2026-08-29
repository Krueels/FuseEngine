using System.Numerics;
using Silk.NET.OpenGL;

namespace Fuse.Renderer;

/// <summary>
/// Shared std140 lighting block consumed by world, skinned and decal shaders.
/// Keeping this in one UBO avoids uploading the same light set to every program.
/// </summary>
public sealed unsafe class LightingBuffer : IDisposable
{
    public const uint BindingPoint = 1;
    public const int MaxPointLights = 8;
    public const int MaxSpotLights = 4;

    private const int HeaderFloatCount = 28; // Seven vec4 values.
    private const int CascadeMatrixOffset = HeaderFloatCount;
    private const int SpotMatrixOffset = CascadeMatrixOffset + 3 * 16;
    private const int PointLightOffset = SpotMatrixOffset + MaxSpotLights * 16;
    private const int PointLightStride = 12; // Three vec4 values.
    private const int SpotLightOffset = PointLightOffset + MaxPointLights * PointLightStride;
    private const int SpotLightStride = 16; // Four vec4 values.
    private const int FloatCount = SpotLightOffset + MaxSpotLights * SpotLightStride;

    private readonly GL _gl;
    private readonly uint _buffer;
    private readonly float[] _data = new float[FloatCount];

    public LightingBuffer(GL gl)
    {
        _gl = gl;
        _buffer = gl.GenBuffer();
        gl.BindBuffer(GLEnum.UniformBuffer, _buffer);
        gl.BufferData(GLEnum.UniformBuffer, (nuint)(FloatCount * sizeof(float)), null, GLEnum.DynamicDraw);
        gl.BindBufferBase(GLEnum.UniformBuffer, BindingPoint, _buffer);
        gl.BindBuffer(GLEnum.UniformBuffer, 0);
    }

    public void Upload(
        Vector3 cameraPosition,
        Vector3 directionalLightDirection,
        Vector3 directionalLightColor,
        float ambient,
        bool directionalShadowsEnabled,
        bool localShadowsEnabled,
        bool shadowFilterEnabled,
        float shadowBiasBase,
        float shadowBiasFactor,
        float shadowSpread,
        float shadowFarPlane,
        float cascadeBlendFraction,
        float shadowFadeStart,
        ReadOnlySpan<float> cascadeDistances,
        ReadOnlySpan<float> cascadeTexelSizes,
        ReadOnlySpan<Matrix4x4> cascadeMatrices,
        ReadOnlySpan<Light> pointLights,
        ReadOnlySpan<Light> shadowPointLights,
        ReadOnlySpan<Light> spotLights,
        ReadOnlySpan<Matrix4x4> spotMatrices)
    {
        Array.Clear(_data);

        WriteVec4(0, pointLights.Length, spotLights.Length, directionalShadowsEnabled ? 1.0f : 0.0f, shadowFilterEnabled ? 1.0f : 0.0f);
        WriteVec4(4, directionalLightDirection.X, directionalLightDirection.Y, directionalLightDirection.Z, ambient);
        WriteVec4(8, directionalLightColor.X, directionalLightColor.Y, directionalLightColor.Z, cascadeBlendFraction);
        WriteVec4(12, shadowBiasBase, shadowBiasFactor, shadowSpread, shadowFarPlane);
        WriteVec4(16,
            cascadeDistances.Length > 0 ? cascadeDistances[0] : 0.0f,
            cascadeDistances.Length > 1 ? cascadeDistances[1] : 0.0f,
            cascadeDistances.Length > 2 ? cascadeDistances[2] : 0.0f,
            shadowFadeStart);
        WriteVec4(20,
            cascadeTexelSizes.Length > 0 ? cascadeTexelSizes[0] : 0.0f,
            cascadeTexelSizes.Length > 1 ? cascadeTexelSizes[1] : 0.0f,
            cascadeTexelSizes.Length > 2 ? cascadeTexelSizes[2] : 0.0f,
            0.0f);
        WriteVec4(24, cameraPosition.X, cameraPosition.Y, cameraPosition.Z, 1.0f);

        for (int i = 0; i < System.Math.Min(3, cascadeMatrices.Length); i++)
            WriteMatrix(CascadeMatrixOffset + i * 16, cascadeMatrices[i]);
        for (int i = 0; i < System.Math.Min(MaxSpotLights, spotMatrices.Length); i++)
            WriteMatrix(SpotMatrixOffset + i * 16, spotMatrices[i]);

        for (int i = 0; i < pointLights.Length && i < MaxPointLights; i++)
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

            int offset = PointLightOffset + i * PointLightStride;
            WriteVec4(offset, light.Position.X, light.Position.Y, light.Position.Z, light.Radius);
            Vector3 color = light.Color * light.Intensity;
            WriteVec4(offset + 4, color.X, color.Y, color.Z, shadowMapIndex);
            WriteVec4(offset + 8, light.ShadowBias, 1.0f / MathF.Max(light.Radius, 0.001f), 0.0f, 0.0f);
        }

        for (int i = 0; i < spotLights.Length && i < MaxSpotLights; i++)
        {
            Light light = spotLights[i];
            Vector3 direction = light.Direction.LengthSquared() > 1e-8f
                ? Vector3.Normalize(light.Direction)
                : -Vector3.UnitY;
            Vector3 color = light.Color * light.Intensity;
            int offset = SpotLightOffset + i * SpotLightStride;
            WriteVec4(offset, light.Position.X, light.Position.Y, light.Position.Z, light.Radius);
            WriteVec4(offset + 4, direction.X, direction.Y, direction.Z, light.InnerCos);
            WriteVec4(offset + 8, color.X, color.Y, color.Z, light.OuterCos);
            WriteVec4(offset + 12,
                light.CastShadows && localShadowsEnabled ? 1.0f : 0.0f,
                light.ShadowBias,
                i,
                0.0f);
        }

        _gl.BindBuffer(GLEnum.UniformBuffer, _buffer);
        fixed (float* pointer = _data)
            _gl.BufferSubData(GLEnum.UniformBuffer, 0, (nuint)(FloatCount * sizeof(float)), pointer);
        _gl.BindBufferBase(GLEnum.UniformBuffer, BindingPoint, _buffer);
        _gl.BindBuffer(GLEnum.UniformBuffer, 0);
    }

    private void WriteVec4(int offset, float x, float y, float z, float w)
    {
        _data[offset] = x;
        _data[offset + 1] = y;
        _data[offset + 2] = z;
        _data[offset + 3] = w;
    }

    // Matches Shader.SetMat4: row-major System.Numerics data is interpreted as
    // the transposed column-major matrix on the GLSL side.
    private void WriteMatrix(int offset, Matrix4x4 matrix)
    {
        _data[offset] = matrix.M11; _data[offset + 1] = matrix.M12; _data[offset + 2] = matrix.M13; _data[offset + 3] = matrix.M14;
        _data[offset + 4] = matrix.M21; _data[offset + 5] = matrix.M22; _data[offset + 6] = matrix.M23; _data[offset + 7] = matrix.M24;
        _data[offset + 8] = matrix.M31; _data[offset + 9] = matrix.M32; _data[offset + 10] = matrix.M33; _data[offset + 11] = matrix.M34;
        _data[offset + 12] = matrix.M41; _data[offset + 13] = matrix.M42; _data[offset + 14] = matrix.M43; _data[offset + 15] = matrix.M44;
    }

    public void Dispose() => _gl.DeleteBuffer(_buffer);
}
