using System.IO;

namespace Fuse.Scene.Terrain;

/// <summary>
/// Sparse, tiled R8 density masks for procedural grass. Missing tiles are
/// implicitly white (fully enabled), so an 80,000 km world consumes disk and
/// memory only where an artist actually paints.
/// </summary>
public sealed class ProceduralGrassDensityMaskStore
{
    private const uint FileMagic = 0x314D5247; // GRM1
    private const int FileVersion = 1;
    private readonly object _sync = new();
    private readonly Dictionary<TerrainTileCoordinate, byte[]?> _tiles = [];
    private string _rootPath = "";
    private int _resolution;

    public float Sample(
        ProceduralGrassSettings settings,
        TerrainTileCoordinate coordinate,
        float normalizedX,
        float normalizedZ)
    {
        if (string.IsNullOrWhiteSpace(settings.DensityMaskPath))
            return 1.0f;

        lock (_sync)
        {
            ConfigureNoLock(settings);
            byte[]? data = GetTileNoLock(coordinate, create: false);
            if (data == null)
                return 1.0f;

            float x = System.Math.Clamp(normalizedX, 0.0f, 1.0f) * (_resolution - 1);
            float z = System.Math.Clamp(normalizedZ, 0.0f, 1.0f) * (_resolution - 1);
            int x0 = (int)MathF.Floor(x);
            int z0 = (int)MathF.Floor(z);
            int x1 = System.Math.Min(x0 + 1, _resolution - 1);
            int z1 = System.Math.Min(z0 + 1, _resolution - 1);
            float tx = x - x0;
            float tz = z - z0;
            float a = Lerp(data[z0 * _resolution + x0], data[z0 * _resolution + x1], tx);
            float b = Lerp(data[z1 * _resolution + x0], data[z1 * _resolution + x1], tx);
            return Lerp(a, b, tz) / 255.0f;
        }
    }

    public bool PaintAndSave(
        ProceduralGrassSettings settings,
        TerrainTileCoordinate coordinate,
        float localX,
        float localZ,
        float tileWidth,
        float tileDepth,
        float radiusMeters,
        float strength,
        bool erase)
    {
        if (string.IsNullOrWhiteSpace(settings.DensityMaskPath) ||
            tileWidth <= 0.0f || tileDepth <= 0.0f || radiusMeters <= 0.0f)
            return false;

        lock (_sync)
        {
            ConfigureNoLock(settings);
            byte[] data = GetTileNoLock(coordinate, create: true)!;
            float centerX = localX / tileWidth * (_resolution - 1);
            float centerZ = localZ / tileDepth * (_resolution - 1);
            float radiusX = MathF.Max(0.5f, radiusMeters / tileWidth * (_resolution - 1));
            float radiusZ = MathF.Max(0.5f, radiusMeters / tileDepth * (_resolution - 1));
            int minX = System.Math.Clamp((int)MathF.Floor(centerX - radiusX), 0, _resolution - 1);
            int maxX = System.Math.Clamp((int)MathF.Ceiling(centerX + radiusX), 0, _resolution - 1);
            int minZ = System.Math.Clamp((int)MathF.Floor(centerZ - radiusZ), 0, _resolution - 1);
            int maxZ = System.Math.Clamp((int)MathF.Ceiling(centerZ + radiusZ), 0, _resolution - 1);
            float signedStrength = (erase ? -1.0f : 1.0f) * MathF.Max(0.0f, strength) * 255.0f;
            bool changed = false;

            for (int z = minZ; z <= maxZ; z++)
            {
                float dz = (z - centerZ) / radiusZ;
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = (x - centerX) / radiusX;
                    float distanceSquared = dx * dx + dz * dz;
                    if (distanceSquared >= 1.0f)
                        continue;
                    float falloff = 1.0f - SmoothStep(0.0f, 1.0f, MathF.Sqrt(distanceSquared));
                    int index = z * _resolution + x;
                    byte next = (byte)System.Math.Clamp(
                        (int)MathF.Round(data[index] + signedStrength * falloff),
                        0,
                        255);
                    if (next == data[index])
                        continue;
                    data[index] = next;
                    changed = true;
                }
            }

            if (changed)
                SaveTileNoLock(coordinate, data);
            return changed;
        }
    }

    public void Invalidate()
    {
        lock (_sync)
            _tiles.Clear();
    }

    private void ConfigureNoLock(ProceduralGrassSettings settings)
    {
        string root = ResolveRoot(settings.DensityMaskPath);
        int resolution = System.Math.Clamp(settings.DensityMaskResolution, 16, 1024);
        if (root.Equals(_rootPath, StringComparison.OrdinalIgnoreCase) && resolution == _resolution)
            return;
        _rootPath = root;
        _resolution = resolution;
        _tiles.Clear();
    }

    private byte[]? GetTileNoLock(TerrainTileCoordinate coordinate, bool create)
    {
        if (_tiles.TryGetValue(coordinate, out byte[]? cached))
        {
            if (cached != null || !create)
                return cached;
        }

        string path = GetTilePath(coordinate);
        byte[]? data = TryReadTile(path, coordinate);
        if (data == null && create)
        {
            data = new byte[checked(_resolution * _resolution)];
            Array.Fill(data, byte.MaxValue);
        }
        _tiles[coordinate] = data;
        return data;
    }

    private byte[]? TryReadTile(string path, TerrainTileCoordinate coordinate)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != FileMagic || reader.ReadInt32() != FileVersion ||
                reader.ReadInt64() != coordinate.X || reader.ReadInt64() != coordinate.Z ||
                reader.ReadInt32() != _resolution)
                return null;
            int count = reader.ReadInt32();
            if (count != checked(_resolution * _resolution))
                return null;
            byte[] data = reader.ReadBytes(count);
            return data.Length == count ? data : null;
        }
        catch (Exception ex)
        {
            Core.Logger.Warn($"Grass density mask '{path}' could not be read: {ex.Message}");
            return null;
        }
    }

    private void SaveTileNoLock(TerrainTileCoordinate coordinate, byte[] data)
    {
        Directory.CreateDirectory(_rootPath);
        string path = GetTilePath(coordinate);
        string temporaryPath = path + ".tmp";
        try
        {
            using (var stream = File.Create(temporaryPath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(FileMagic);
                writer.Write(FileVersion);
                writer.Write(coordinate.X);
                writer.Write(coordinate.Z);
                writer.Write(_resolution);
                writer.Write(data.Length);
                writer.Write(data);
            }
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
        }
    }

    private string GetTilePath(TerrainTileCoordinate coordinate) => Path.Combine(
        _rootPath,
        $"tile_{coordinate.X}_{coordinate.Z}.grassmask");

    private static string ResolveRoot(string path) => Path.GetFullPath(
        Path.IsPathRooted(path)
            ? path
            : Path.Combine(ResPath.Path, path));

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = System.Math.Clamp((value - edge0) / MathF.Max(edge1 - edge0, 0.000001f), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}
