using System.Numerics;
using Fuse.Scene.Model;

namespace Fuse.Renderer;

/// <summary>
/// CPU-side query of the same spectral ocean field used to build the GPU FFT
/// textures. Physics and waterline tests use this class instead of creating a
/// second, unrelated wave function.
/// </summary>
public readonly struct OceanSurfaceSample
{
    public OceanSurfaceSample(
        float height,
        Vector3 normal,
        Vector3 velocity,
        Vector3 displacement)
    {
        Height = height;
        Normal = normal;
        Velocity = velocity;
        Displacement = displacement;
    }

    public float Height { get; }
    public Vector3 Normal { get; }
    public Vector3 Velocity { get; }
    public Vector3 Displacement { get; }
}

/// <summary>
/// Deterministic CPU representation of the ocean spectrum. The renderer uses
/// the returned initial spectra to populate its H0 textures, while gameplay
/// queries use the same spectra for height, normal and wave velocity.
/// </summary>
public sealed class OceanSurfaceSampler
{
    public const int CascadeCount = 3;
    public const int SimulationResolution = 128;

    // Buoyancy follows the broad displacement of the same spectrum while the
    // shortest ripples remain visual surface detail. A 20-cell window keeps
    // physical queries affordable and avoids feeding sub-collider ripples into
    // the rigid-body torque solver.
    public const int PhysicsFrequencyLimit = 20;

    private const float TwoPi = 6.28318530718f;
    private const float Gravity = 9.81f;

    private static readonly float[] CascadeWeights = [1.0f, 0.45f, 0.20f];
    private static readonly Vector2[] CascadeOffsetFactors =
    [
        new(0.173f, 0.371f),
        new(0.617f, 0.233f),
        new(0.291f, 0.719f)
    ];

    private readonly SpectrumCascade?[] _cascades = new SpectrumCascade?[CascadeCount];
    private bool _configured;
    private float _waveLength = float.NaN;
    private float _windSpeed = float.NaN;
    private float _smallWaveLength = float.NaN;
    private Vector2 _direction;
    private int _seed;

    public bool IsConfigured => _configured;

    /// <summary>
    /// Rebuilds H0 only when a spectrum-defining setting changed. Amplitude,
    /// speed and choppiness are evaluated at query time and do not require a
    /// rebuild.
    /// </summary>
    public void Configure(OceanSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Vector2 direction = NormalizeWaveDirection(settings.WaveDirection);
        if (_configured &&
            NearlyEqual(_waveLength, settings.WaveLength) &&
            NearlyEqual(_windSpeed, settings.WindSpeed) &&
            NearlyEqual(_smallWaveLength, settings.SmallWaveLength) &&
            Vector2.DistanceSquared(_direction, direction) < 1e-8f &&
            _seed == settings.SpectrumSeed)
        {
            return;
        }

        for (int band = 0; band < CascadeCount; band++)
        {
            float patchSize = ComputePatchWorldSize(settings, band);
            Vector2[] initialSpectrum = BuildInitialSpectrum(
                settings,
                band,
                patchSize);
            _cascades[band] = new SpectrumCascade
            {
                PatchSize = patchSize,
                InitialSpectrum = initialSpectrum,
                PhysicsModes = BuildPhysicsModes(
                    initialSpectrum,
                    patchSize,
                    PhysicsFrequencyLimit)
            };
        }

        _waveLength = settings.WaveLength;
        _windSpeed = settings.WindSpeed;
        _smallWaveLength = settings.SmallWaveLength;
        _direction = direction;
        _seed = settings.SpectrumSeed;
        _configured = true;
    }

    public Vector2[] GetInitialSpectrum(int band)
    {
        if ((uint)band >= CascadeCount || _cascades[band] == null)
            throw new InvalidOperationException("The ocean surface sampler is not configured.");

        return _cascades[band]!.InitialSpectrum;
    }

    /// <summary>
    /// Samples the full spectrum for camera waterline queries, or the shared
    /// low-frequency band for rigid-body physics.
    /// </summary>
    public OceanSurfaceSample Sample(
        Vector2 worldPosition,
        float animationTime,
        OceanSettings settings,
        bool physicsQuality = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Configure(settings);

        return SampleUncached(
            worldPosition,
            animationTime,
            settings,
            physicsQuality ? PhysicsFrequencyLimit : -1);
    }

    private OceanSurfaceSample SampleUncached(
        Vector2 worldPosition,
        float animationTime,
        OceanSettings settings,
        int frequencyLimit)
    {
        Vector3 displacement = SampleDisplacement(
            worldPosition,
            animationTime,
            settings,
            frequencyLimit,
            out Vector3 velocity,
            out float slopeX,
            out float slopeZ);

        Vector3 normal = new(-slopeX, 1.0f, -slopeZ);
        if (normal.LengthSquared() < 1e-8f || !IsFinite(normal))
            normal = Vector3.UnitY;
        else
            normal = Vector3.Normalize(normal);

        return new OceanSurfaceSample(
            settings.WaterLevel + displacement.Y,
            normal,
            IsFinite(velocity) ? velocity : Vector3.Zero,
            IsFinite(displacement) ? displacement : Vector3.Zero);
    }

    public static float GetCascadeWeight(int band) =>
        CascadeWeights[System.Math.Clamp(band, 0, CascadeCount - 1)];

    public static float ComputePatchWorldSize(OceanSettings settings, int band)
    {
        float baseLength = MathF.Max(settings.WaveLength, 4.0f);
        return band switch
        {
            0 => MathF.Max(baseLength * 64.0f, 1024.0f),
            1 => MathF.Max(baseLength * 16.0f, 256.0f),
            _ => MathF.Max(baseLength * 4.0f, 64.0f)
        };
    }

    public static Vector2 GetCascadeOffset(int band, float patchSize) =>
        CascadeOffsetFactors[System.Math.Clamp(band, 0, CascadeCount - 1)] * patchSize;

    public static Vector2 NormalizeWaveDirection(Vector2 direction) =>
        direction.LengthSquared() > 1e-8f
            ? Vector2.Normalize(direction)
            : Vector2.UnitX;

    public void Reset()
    {
        for (int band = 0; band < CascadeCount; band++)
            _cascades[band] = null;
        _configured = false;
        _waveLength = float.NaN;
        _windSpeed = float.NaN;
        _smallWaveLength = float.NaN;
        _direction = Vector2.Zero;
        _seed = 0;
    }

    private Vector3 SampleDisplacement(
        Vector2 worldPosition,
        float animationTime,
        OceanSettings settings,
        int frequencyLimit,
        out Vector3 velocity,
        out float slopeX,
        out float slopeZ)
    {
        Vector3 displacement = Vector3.Zero;
        velocity = Vector3.Zero;
        slopeX = 0.0f;
        slopeZ = 0.0f;

        if (frequencyLimit == PhysicsFrequencyLimit)
        {
            return SamplePhysicsDisplacement(
                worldPosition,
                animationTime,
                settings,
                out velocity,
                out slopeX,
                out slopeZ);
        }

        float inverseSizeSquared =
            1.0f / (SimulationResolution * SimulationResolution);
        float phaseTime = animationTime * settings.WaveSpeed;
        float choppiness = settings.WaveChoppiness;
        float timeScale = settings.WaveSpeed;

        for (int band = 0; band < CascadeCount; band++)
        {
            SpectrumCascade cascade = _cascades[band]!;
            Vector2 samplePosition =
                worldPosition + GetCascadeOffset(band, cascade.PatchSize);
            Vector2[] h0 = cascade.InitialSpectrum;
            int size = SimulationResolution;
            float cascadeAmplitude = settings.WaveAmplitude * GetCascadeWeight(band);

            for (int y = 0; y < size; y++)
            {
                int signedY = y <= size / 2 ? y : y - size;
                int negativeY = (size - y) % size;
                if (frequencyLimit >= 0 && System.Math.Abs(signedY) > frequencyLimit)
                    continue;

                for (int x = 0; x < size; x++)
                {
                    int signedX = x <= size / 2 ? x : x - size;
                    if (frequencyLimit >= 0 && System.Math.Abs(signedX) > frequencyLimit)
                        continue;
                    int negativeX = (size - x) % size;
                    int index = y * size + x;
                    int negativeIndex = negativeY * size + negativeX;
                    Vector2 waveNumber = TwoPi *
                        new Vector2(signedX, signedY) /
                        MathF.Max(cascade.PatchSize, 0.001f);
                    float length = waveNumber.Length();
                    if (length < 0.00001f)
                        continue;

                    float angularFrequency = MathF.Sqrt(Gravity * length);
                    Vector2 forward = ComplexMultiply(
                        h0[index],
                        ComplexExp(angularFrequency * phaseTime));
                    Vector2 backward = ComplexMultiply(
                        ComplexConjugate(h0[negativeIndex]),
                        ComplexExp(-angularFrequency * phaseTime));
                    Vector2 height = (forward + backward) * cascadeAmplitude;
                    Vector2 spatial = ComplexExp(Vector2.Dot(
                        waveNumber,
                        samplePosition));

                    displacement.Y += ComplexMultiply(height, spatial).X *
                                      inverseSizeSquared;

                    slopeX += ComplexMultiply(
                        ComplexMultiply(new Vector2(0.0f, waveNumber.X), height),
                        spatial).X * inverseSizeSquared;
                    slopeZ += ComplexMultiply(
                        ComplexMultiply(new Vector2(0.0f, waveNumber.Y), height),
                        spatial).X * inverseSizeSquared;

                    float inverseLength = 1.0f / length;
                    Vector2 displacementX = ComplexMultiply(
                        new Vector2(
                            0.0f,
                            -waveNumber.X * inverseLength * choppiness),
                        height);
                    Vector2 displacementZ = ComplexMultiply(
                        new Vector2(
                            0.0f,
                            -waveNumber.Y * inverseLength * choppiness),
                        height);
                    displacement.X += ComplexMultiply(
                        displacementX,
                        spatial).X * inverseSizeSquared;
                    displacement.Z += ComplexMultiply(
                        displacementZ,
                        spatial).X * inverseSizeSquared;

                    // phaseTime = animationTime * WaveSpeed, so its derivative
                    // must contain WaveSpeed too. Omitting it made drag use a
                    // fluid velocity different from the visible surface.
                    Vector2 forwardDerivative = ComplexMultiply(
                        new Vector2(0.0f, angularFrequency * timeScale),
                        forward);
                    Vector2 backwardDerivative = ComplexMultiply(
                        new Vector2(0.0f, -angularFrequency * timeScale),
                        backward);
                    Vector2 heightDerivative =
                        (forwardDerivative + backwardDerivative) * cascadeAmplitude;
                    velocity.Y += ComplexMultiply(
                        heightDerivative,
                        spatial).X * inverseSizeSquared;
                    velocity.X += ComplexMultiply(
                        ComplexMultiply(
                            new Vector2(
                                0.0f,
                                -waveNumber.X * inverseLength * choppiness),
                            heightDerivative),
                        spatial).X * inverseSizeSquared;
                    velocity.Z += ComplexMultiply(
                        ComplexMultiply(
                            new Vector2(
                                0.0f,
                                -waveNumber.Y * inverseLength * choppiness),
                            heightDerivative),
                        spatial).X * inverseSizeSquared;
                }
            }
        }

        return displacement;
    }

    private Vector3 SamplePhysicsDisplacement(
        Vector2 worldPosition,
        float animationTime,
        OceanSettings settings,
        out Vector3 velocity,
        out float slopeX,
        out float slopeZ)
    {
        Vector3 displacement = Vector3.Zero;
        velocity = Vector3.Zero;
        slopeX = 0.0f;
        slopeZ = 0.0f;

        float inverseSizeSquared =
            1.0f / (SimulationResolution * SimulationResolution);
        float phaseTime = animationTime * settings.WaveSpeed;
        float choppiness = settings.WaveChoppiness;
        float timeScale = settings.WaveSpeed;

        for (int band = 0; band < CascadeCount; band++)
        {
            SpectrumCascade cascade = _cascades[band]!;
            Vector2 samplePosition =
                worldPosition + GetCascadeOffset(band, cascade.PatchSize);
            float cascadeAmplitude = settings.WaveAmplitude *
                                     GetCascadeWeight(band);

            foreach (PhysicsMode mode in cascade.PhysicsModes)
            {
                Vector2 waveNumber = mode.WaveNumber;
                float positionPhase = Vector2.Dot(
                    waveNumber,
                    samplePosition);

                Vector2 forward = ComplexMultiply(
                    mode.ForwardSpectrum,
                    ComplexExp(positionPhase +
                               mode.AngularFrequency * phaseTime));
                Vector2 backward = ComplexMultiply(
                    mode.BackwardSpectrum,
                    ComplexExp(positionPhase -
                               mode.AngularFrequency * phaseTime));
                Vector2 height = (forward + backward) * cascadeAmplitude;

                displacement.Y += height.X * inverseSizeSquared;
                slopeX += ComplexMultiply(
                    new Vector2(0.0f, waveNumber.X),
                    height).X * inverseSizeSquared;
                slopeZ += ComplexMultiply(
                    new Vector2(0.0f, waveNumber.Y),
                    height).X * inverseSizeSquared;

                Vector2 displacementX = ComplexMultiply(
                    new Vector2(
                        0.0f,
                        -waveNumber.X * mode.InverseLength * choppiness),
                    height);
                Vector2 displacementZ = ComplexMultiply(
                    new Vector2(
                        0.0f,
                        -waveNumber.Y * mode.InverseLength * choppiness),
                    height);
                displacement.X += displacementX.X * inverseSizeSquared;
                displacement.Z += displacementZ.X * inverseSizeSquared;

                Vector2 heightDerivative = ComplexMultiply(
                    new Vector2(0.0f, mode.AngularFrequency * timeScale),
                    forward) + ComplexMultiply(
                    new Vector2(0.0f, -mode.AngularFrequency * timeScale),
                    backward);
                heightDerivative *= cascadeAmplitude;
                velocity.Y += heightDerivative.X * inverseSizeSquared;
                velocity.X += ComplexMultiply(
                    new Vector2(
                        0.0f,
                        -waveNumber.X * mode.InverseLength * choppiness),
                    heightDerivative).X * inverseSizeSquared;
                velocity.Z += ComplexMultiply(
                    new Vector2(
                        0.0f,
                        -waveNumber.Y * mode.InverseLength * choppiness),
                    heightDerivative).X * inverseSizeSquared;
            }
        }

        return displacement;
    }

    private static PhysicsMode[] BuildPhysicsModes(
        Vector2[] initialSpectrum,
        float patchSize,
        int frequencyLimit)
    {
        int size = SimulationResolution;
        var modes = new List<PhysicsMode>();
        for (int y = 0; y < size; y++)
        {
            int signedY = y <= size / 2 ? y : y - size;
            if (System.Math.Abs(signedY) > frequencyLimit)
                continue;

            int negativeY = (size - y) % size;
            for (int x = 0; x < size; x++)
            {
                int signedX = x <= size / 2 ? x : x - size;
                if (System.Math.Abs(signedX) > frequencyLimit)
                    continue;

                Vector2 waveNumber = TwoPi *
                    new Vector2(signedX, signedY) /
                    MathF.Max(patchSize, 0.001f);
                float length = waveNumber.Length();
                if (!float.IsFinite(length) || length < 0.00001f)
                    continue;

                int negativeX = (size - x) % size;
                int index = y * size + x;
                int negativeIndex = negativeY * size + negativeX;
                modes.Add(new PhysicsMode(
                    waveNumber,
                    MathF.Sqrt(Gravity * length),
                    1.0f / length,
                    initialSpectrum[index],
                    ComplexConjugate(initialSpectrum[negativeIndex])));
            }
        }

        return modes.ToArray();
    }

    private static Vector2[] BuildInitialSpectrum(
        OceanSettings settings,
        int band,
        float patchSize)
    {
        int size = SimulationResolution;
        Vector2[] values = new Vector2[size * size];
        Random random = new(unchecked(settings.SpectrumSeed + (band + 1) * 104729));
        Vector2 windDirection = NormalizeWaveDirection(settings.WaveDirection);
        float windSpeed = MathF.Max(settings.WindSpeed, 0.1f);
        float largestWave = MathF.Max(windSpeed * windSpeed / Gravity, 1.0f);
        float largestWaveSquared = largestWave * largestWave;
        float smallWaveLength = MathF.Max(settings.SmallWaveLength, 0.05f);
        float smallWaveLengthSquared = smallWaveLength * smallWaveLength;
        double totalPower = 0.0;

        for (int y = 0; y < size; y++)
        {
            int signedY = y <= size / 2 ? y : y - size;
            for (int x = 0; x < size; x++)
            {
                int signedX = x <= size / 2 ? x : x - size;
                int index = y * size + x;
                Vector2 waveNumber = TwoPi *
                    new Vector2(signedX, signedY) /
                    MathF.Max(patchSize, 0.001f);
                float length = waveNumber.Length();
                if (length < 0.00001f)
                    continue;

                Vector2 direction = waveNumber / length;
                float alignment = Vector2.Dot(direction, windDirection);
                float directionalEnergy = alignment * alignment;
                if (alignment < 0.0f)
                    directionalEnergy *= 0.25f;

                float lengthSquared = length * length;
                float longWaveEnvelope = MathF.Exp(
                    -1.0f / MathF.Max(lengthSquared * largestWaveSquared, 0.000001f));
                float smallWaveEnvelope = MathF.Exp(
                    -lengthSquared * smallWaveLengthSquared);
                float phillips = longWaveEnvelope *
                                 directionalEnergy *
                                 smallWaveEnvelope /
                                 MathF.Max(lengthSquared * lengthSquared, 0.000001f);
                if (phillips <= 0.0f || !float.IsFinite(phillips))
                    continue;

                Vector2 gaussian = new(
                    NextGaussian(random),
                    NextGaussian(random));
                values[index] = gaussian * MathF.Sqrt(phillips * 0.5f);
                totalPower += phillips;
            }
        }

        float normalization = totalPower > 1e-12
            ? (size * size) / MathF.Sqrt((float)totalPower)
            : 1.0f;
        for (int i = 0; i < values.Length; i++)
            values[i] *= normalization;

        return values;
    }

    private static float NextGaussian(Random random)
    {
        float first = MathF.Max((float)random.NextDouble(), 1e-6f);
        float second = (float)random.NextDouble();
        return MathF.Sqrt(-2.0f * MathF.Log(first)) *
               MathF.Cos(TwoPi * second);
    }

    private static Vector2 ComplexMultiply(Vector2 a, Vector2 b) =>
        new(
            a.X * b.X - a.Y * b.Y,
            a.X * b.Y + a.Y * b.X);

    private static Vector2 ComplexConjugate(Vector2 value) =>
        new(value.X, -value.Y);

    private static Vector2 ComplexExp(float angle) =>
        new(MathF.Cos(angle), MathF.Sin(angle));

    private static bool NearlyEqual(float left, float right) =>
        float.IsFinite(left) && float.IsFinite(right) &&
        MathF.Abs(left - right) <= 0.00001f;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private sealed class SpectrumCascade
    {
        public float PatchSize;
        public Vector2[] InitialSpectrum = Array.Empty<Vector2>();
        public PhysicsMode[] PhysicsModes = Array.Empty<PhysicsMode>();
    }

    private readonly struct PhysicsMode
    {
        public PhysicsMode(
            Vector2 waveNumber,
            float angularFrequency,
            float inverseLength,
            Vector2 forwardSpectrum,
            Vector2 backwardSpectrum)
        {
            WaveNumber = waveNumber;
            AngularFrequency = angularFrequency;
            InverseLength = inverseLength;
            ForwardSpectrum = forwardSpectrum;
            BackwardSpectrum = backwardSpectrum;
        }

        public Vector2 WaveNumber { get; }
        public float AngularFrequency { get; }
        public float InverseLength { get; }
        public Vector2 ForwardSpectrum { get; }
        public Vector2 BackwardSpectrum { get; }
    }

}
