using System.Numerics;

namespace Fuse.Renderer.PostProcess;

public sealed class PostProcessSettings
{
    public bool Enabled = false;

    // Exposure / Tonemap
    public float Exposure = 0.58f;
    public bool TonemapEnabled = true;

    // Bloom
    public bool BloomEnabled = true;
    public float BloomStrength = 2.0f;
    public float BloomThreshold = 1.0f;
    public float BloomKnee = 0.0f;

    // Kawase Blur
    public int KawaseRadius = 2;      // 1=pequeno, 2=médio, 3=grande
    public int KawaseIterations = 4;  // 1-2 iterações (cada iteração = 2 passes Kawase)

    // Bloom Expansion
    public float BloomScale = 1.0f;           // multiplica o raio efetivo
    public Vector3 BloomTint = Vector3.One;   // cor do bloom (RGB)
    public float BloomAnamorphicRatio = 1.0f; // >1 = horizontal, <1 = vertical

    // Legacy (mantido para compatibilidade)
    public int BlurIterations = 1;
    public int BlurRadius = 4;

    // Debug
    public int DebugView = 0;
}