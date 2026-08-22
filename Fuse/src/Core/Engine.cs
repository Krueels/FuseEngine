namespace Fuse.Core;

public static class Engine
{
    private static float s_dt;
    private static int s_fps;
    private static float s_fpsAccum;
    private static int s_fpsCount;
    private static float s_time;

    public static void Tick(float dt)
    {
        s_dt = dt;
        s_time += dt;
        s_fpsAccum += dt;
        s_fpsCount++;
        if (s_fpsAccum >= 0.5f)
        {
            s_fps = (int)(s_fpsCount / s_fpsAccum);
            s_fpsAccum = 0.0f;
            s_fpsCount = 0;
        }
    }

    public static int FPS => s_fps;
    public static float DeltaTime => s_dt;
    public static float Time => s_time;
}
