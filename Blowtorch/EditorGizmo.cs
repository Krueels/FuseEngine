using System;
using System.Numerics;
using ImGuiNET;

namespace Blowtorch;

public static class EditorGizmo
{
    private static int _activeAxis = -1; // 0=X, 1=Y, 2=Z, 3=YZ, 4=XZ, 5=XY
    private static float _dragStartOffset;
    private static Vector3 _dragStartObjectPos;
    private static Vector3 _dragStartHitPos;
    
    private static float _dragStartAngle;
    private static Quaternion _dragStartRotation;
    private static Vector3 _dragStartObjectScale;

    public static bool ManipulateTranslation(
        Vector3 objectPos, 
        Matrix4x4 view, 
        Matrix4x4 proj, 
        Vector2 vpPos, 
        Vector2 vpSize, 
        out Vector3 newPos,
        float snapAmount = 0.0f,
        bool interactive = true)
    {
        newPos = objectPos;
        bool changed = false;

        var io = ImGui.GetIO();
        var mousePos = io.MousePos;
        bool isMouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        // Release must be handled before projection tests. Otherwise a gizmo
        // whose pivot leaves the viewport can keep its static active axis forever.
        if (!isMouseDown)
            Reset();

        Vector3[] axes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        uint[] colors = { 0xFF0000FF, 0xFF00FF00, 0xFFFF0000 };
        uint[] activeColors = { 0xFF5555FF, 0xFF55FF55, 0xFFFF5555 };

        Vector3[] planeNormals = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }; // YZ, XZ, XY
        uint[] planeColors = { 0x660000FF, 0x6600FF00, 0x66FF0000 };
        uint[] planeActiveColors = { 0xAA5555FF, 0xAA55FF55, 0xAAFF5555 };
        
        float scale = GetGizmoScale(objectPos, view, proj);
        float planeOffset = 0.25f * scale;
        float planeSize = 0.35f * scale;

        var drawList = ImGui.GetWindowDrawList();
        float axisLength = 1.5f * scale;
        float hoverRadius = 15.0f;

        if (!WorldToScreen(objectPos, view, proj, vpPos, vpSize, out Vector2 center2D))
            return false;

        Matrix4x4.Invert(view, out Matrix4x4 invView);
        Vector3 camPos = new Vector3(invView.M41, invView.M42, invView.M43);
        Vector3 dirToCam = Vector3.Normalize(camPos - objectPos);
        Vector3 signs = new Vector3(
            dirToCam.X >= 0 ? 1.0f : -1.0f,
            dirToCam.Y >= 0 ? 1.0f : -1.0f,
            dirToCam.Z >= 0 ? 1.0f : -1.0f
        );

        int hoveredAxis = -1;
        float closestDist = float.MaxValue;

        if (interactive)
        {
            Ray mouseRay = ScreenToWorldRay(mousePos, view, proj, vpPos, vpSize);
            
            // Check planes
            float closestPlaneT = float.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                if (GetRayPlaneIntersection(mouseRay, objectPos, planeNormals[i], out Vector3 hitPoint))
                {
                    Vector3 localHit = hitPoint - objectPos;
                    Vector3 localHitAbs = new Vector3(localHit.X * signs.X, localHit.Y * signs.Y, localHit.Z * signs.Z);
                    
                    float u = 0, v = 0;
                    if (i == 0) { u = localHitAbs.Y; v = localHitAbs.Z; } // YZ
                    else if (i == 1) { u = localHitAbs.X; v = localHitAbs.Z; } // XZ
                    else if (i == 2) { u = localHitAbs.X; v = localHitAbs.Y; } // XY
                    
                    if (u >= planeOffset && u <= planeOffset + planeSize &&
                        v >= planeOffset && v <= planeOffset + planeSize)
                    {
                        float t = (hitPoint - mouseRay.Origin).Length();
                        if (t < closestPlaneT)
                        {
                            closestPlaneT = t;
                            hoveredAxis = i + 3; // 3=YZ, 4=XZ, 5=XY
                        }
                    }
                }
            }

            // Check axes if no plane was hovered
            if (hoveredAxis == -1)
            {
                for (int i = 0; i < 3; i++)
                {
                    Vector3 tipPos = objectPos + axes[i] * axisLength;
                    if (WorldToScreen(tipPos, view, proj, vpPos, vpSize, out Vector2 tip2D))
                    {
                        float dist = DistancePointToSegment(mousePos, center2D, tip2D);
                        if (dist < hoverRadius && dist < closestDist)
                        {
                            hoveredAxis = i;
                            closestDist = dist;
                        }
                    }
                }
            }
        }

        IsHovered = hoveredAxis != -1;

        if (_activeAxis == -1 && interactive && isMouseDown && hoveredAxis != -1 && ImGui.IsWindowHovered())
        {
            _activeAxis = hoveredAxis;
            _dragStartObjectPos = objectPos;
            
            Ray mouseRay = ScreenToWorldRay(mousePos, view, proj, vpPos, vpSize);
            if (_activeAxis < 3)
            {
                _dragStartOffset = GetRayAxisIntersection(mouseRay, objectPos, axes[_activeAxis], view);
            }
            else
            {
                if (GetRayPlaneIntersection(mouseRay, objectPos, planeNormals[_activeAxis - 3], out Vector3 hit))
                {
                    _dragStartHitPos = hit;
                }
            }
        }

        // Render planes
        for (int i = 0; i < 3; i++)
        {
            GetPlaneHandlePoints(i, objectPos, signs, planeOffset, planeSize, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
            
            if (WorldToScreen(p0, view, proj, vpPos, vpSize, out Vector2 p0_2d) &&
                WorldToScreen(p1, view, proj, vpPos, vpSize, out Vector2 p1_2d) &&
                WorldToScreen(p2, view, proj, vpPos, vpSize, out Vector2 p2_2d) &&
                WorldToScreen(p3, view, proj, vpPos, vpSize, out Vector2 p3_2d))
            {
                bool isActive = (_activeAxis == i + 3) || (_activeAxis == -1 && hoveredAxis == i + 3);
                uint color = isActive ? planeActiveColors[i] : planeColors[i];
                
                Vector2[] pts = new Vector2[] { p0_2d, p1_2d, p2_2d, p3_2d };
                unsafe 
                {
                    fixed (Vector2* ptsPtr = pts)
                    {
                        drawList.AddConvexPolyFilled(ref ptsPtr[0], 4, color);
                    }
                }
                
                uint outlineColor = isActive ? activeColors[i] : colors[i];
                unsafe
                {
                    fixed (Vector2* ptsPtr = pts)
                    {
                        drawList.AddPolyline(ref ptsPtr[0], 4, outlineColor, ImDrawFlags.Closed, 1.5f);
                    }
                }
            }
        }

        // Render axes
        for (int i = 0; i < 3; i++)
        {
            Vector3 tipPos = objectPos + axes[i] * axisLength;
            if (WorldToScreen(tipPos, view, proj, vpPos, vpSize, out Vector2 tip2D))
            {
                bool isActive = (_activeAxis == i) || (_activeAxis == -1 && hoveredAxis == i);
                uint color = isActive ? activeColors[i] : colors[i];
                float thickness = isActive ? 5.0f : 3.0f;
                drawList.AddLine(center2D, tip2D, color, thickness);
            }
        }

        if (_activeAxis != -1)
        {
            Ray mouseRay = ScreenToWorldRay(mousePos, view, proj, vpPos, vpSize);
            
            if (_activeAxis < 3)
            {
                float currentOffset = GetRayAxisIntersection(mouseRay, _dragStartObjectPos, axes[_activeAxis], view);
                float delta = currentOffset - _dragStartOffset;
                newPos = _dragStartObjectPos + axes[_activeAxis] * delta;

                if (snapAmount > 0.0f)
                {
                    if (_activeAxis == 0) // X
                        newPos.X = MathF.Round(newPos.X / snapAmount) * snapAmount;
                    else if (_activeAxis == 1) // Y
                        newPos.Y = MathF.Round(newPos.Y / snapAmount) * snapAmount;
                    else if (_activeAxis == 2) // Z
                        newPos.Z = MathF.Round(newPos.Z / snapAmount) * snapAmount;
                }
            }
            else
            {
                int planeIdx = _activeAxis - 3;
                if (GetRayPlaneIntersection(mouseRay, _dragStartObjectPos, planeNormals[planeIdx], out Vector3 currentHitPos))
                {
                    Vector3 delta3D = currentHitPos - _dragStartHitPos;
                    newPos = _dragStartObjectPos + delta3D;

                    if (snapAmount > 0.0f)
                    {
                        if (planeIdx == 0) // YZ
                        {
                            newPos.Y = MathF.Round(newPos.Y / snapAmount) * snapAmount;
                            newPos.Z = MathF.Round(newPos.Z / snapAmount) * snapAmount;
                        }
                        else if (planeIdx == 1) // XZ
                        {
                            newPos.X = MathF.Round(newPos.X / snapAmount) * snapAmount;
                            newPos.Z = MathF.Round(newPos.Z / snapAmount) * snapAmount;
                        }
                        else if (planeIdx == 2) // XY
                        {
                            newPos.X = MathF.Round(newPos.X / snapAmount) * snapAmount;
                            newPos.Y = MathF.Round(newPos.Y / snapAmount) * snapAmount;
                        }
                    }
                }
            }

            if (newPos != objectPos)
            {
                changed = true;
            }
        }

        return changed;
    }

    public static bool ManipulateScale(
        Vector3 objectPos, 
        Vector3 objectScale,
        Matrix4x4 view, 
        Matrix4x4 proj, 
        Vector2 vpPos, 
        Vector2 vpSize, 
        out Vector3 newScale,
        float snapAmount = 0.0f,
        bool interactive = true)
    {
        newScale = objectScale;
        bool changed = false;

        var io = ImGui.GetIO();
        var mousePos = io.MousePos;
        bool isMouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        if (!isMouseDown)
            Reset();

        Vector3[] axes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        uint[] colors = { 0xFF0000FF, 0xFF00FF00, 0xFFFF0000 };
        uint[] activeColors = { 0xFF5555FF, 0xFF55FF55, 0xFFFF5555 };

        float scale = GetGizmoScale(objectPos, view, proj);
        var drawList = ImGui.GetWindowDrawList();
        float axisLength = 1.5f * scale;
        float hoverRadius = 15.0f;

        if (!WorldToScreen(objectPos, view, proj, vpPos, vpSize, out Vector2 center2D)) return false;

        int hoveredAxis = -1;
        float closestDist = float.MaxValue;

        if (interactive)
        {
            for (int i = 0; i < 3; i++)
            {
                Vector3 tipPos = objectPos + axes[i] * axisLength;
                if (WorldToScreen(tipPos, view, proj, vpPos, vpSize, out Vector2 tip2D))
                {
                    float dist = DistancePointToSegment(mousePos, center2D, tip2D);
                    if (dist < hoverRadius && dist < closestDist)
                    {
                        hoveredAxis = i;
                        closestDist = dist;
                    }
                }
            }
        }

        IsHovered = hoveredAxis != -1;

        if (_activeAxis == -1 && interactive && isMouseDown && hoveredAxis != -1 && ImGui.IsWindowHovered())
        {
            _activeAxis = hoveredAxis;
            _dragStartObjectScale = objectScale;
            
            Ray mouseRay = ScreenToWorldRay(mousePos, view, proj, vpPos, vpSize);
            _dragStartOffset = GetRayAxisIntersection(mouseRay, objectPos, axes[_activeAxis], view);
        }

        for (int i = 0; i < 3; i++)
        {
            Vector3 tipPos = objectPos + axes[i] * axisLength;
            if (WorldToScreen(tipPos, view, proj, vpPos, vpSize, out Vector2 tip2D))
            {
                bool isActive = (_activeAxis == i) || (_activeAxis == -1 && hoveredAxis == i);
                uint color = isActive ? activeColors[i] : colors[i];
                float thickness = isActive ? 5.0f : 3.0f;
                drawList.AddLine(center2D, tip2D, color, thickness);
                drawList.AddRectFilled(new Vector2(tip2D.X - 6, tip2D.Y - 6), new Vector2(tip2D.X + 6, tip2D.Y + 6), color);
            }
        }

        if (_activeAxis != -1)
        {
            Ray mouseRay = ScreenToWorldRay(mousePos, view, proj, vpPos, vpSize);
            float currentOffset = GetRayAxisIntersection(mouseRay, objectPos, axes[_activeAxis], view);
            
            float delta = currentOffset - _dragStartOffset;
            newScale = _dragStartObjectScale + axes[_activeAxis] * delta;

            if (snapAmount > 0.0f)
            {
                if (_activeAxis == 0) // X
                    newScale.X = MathF.Round(newScale.X / snapAmount) * snapAmount;
                else if (_activeAxis == 1) // Y
                    newScale.Y = MathF.Round(newScale.Y / snapAmount) * snapAmount;
                else if (_activeAxis == 2) // Z
                    newScale.Z = MathF.Round(newScale.Z / snapAmount) * snapAmount;
            }

            newScale.X = MathF.Max(0.01f, newScale.X);
            newScale.Y = MathF.Max(0.01f, newScale.Y);
            newScale.Z = MathF.Max(0.01f, newScale.Z);

            if (newScale != objectScale)
            {
                changed = true;
            }
        }

        return changed;
    }

    public static bool ManipulateRotation(
        Vector3 objectPos, 
        Quaternion objectRot,
        Matrix4x4 view, 
        Matrix4x4 proj, 
        Vector2 vpPos, 
        Vector2 vpSize, 
        out Quaternion newRot,
        float snapAmount = 0.0f,
        bool interactive = true)
    {
        newRot = objectRot;
        bool changed = false;

        var io = ImGui.GetIO();
        var mousePos = io.MousePos;
        bool isMouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        if (!isMouseDown)
            Reset();

        Vector3[] axes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        uint[] colors = { 0xFF0000FF, 0xFF00FF00, 0xFFFF0000 };
        uint[] activeColors = { 0xFF5555FF, 0xFF55FF55, 0xFFFF5555 };

        float scale = GetGizmoScale(objectPos, view, proj);
        var drawList = ImGui.GetWindowDrawList();
        float radius = 1.5f * scale;
        int segments = 64;

        int hoveredAxis = -1;
        float closestDist = float.MaxValue;
        float hoverRadius = 10.0f;

        Vector2[][] axisPoints2D = new Vector2[3][];
        int[] axisValidPoints = new int[3];

        for (int i = 0; i < 3; i++)
        {
            Vector3 u = axes[(i + 1) % 3];
            Vector3 v = axes[(i + 2) % 3];

            axisPoints2D[i] = new Vector2[segments];
            axisValidPoints[i] = 0;

            for (int j = 0; j < segments; j++)
            {
                float angle = (float)j / segments * MathF.PI * 2.0f;
                Vector3 p3D = objectPos + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius;
                if (WorldToScreen(p3D, view, proj, vpPos, vpSize, out Vector2 p2D))
                {
                    axisPoints2D[i][axisValidPoints[i]++] = p2D;
                }
            }

            if (interactive && axisValidPoints[i] > 1)
            {
                for (int j = 0; j < axisValidPoints[i]; j++)
                {
                    Vector2 pA = axisPoints2D[i][j];
                    Vector2 pB = axisPoints2D[i][(j + 1) % axisValidPoints[i]];
                    float dist = DistancePointToSegment(mousePos, pA, pB);
                    if (dist < hoverRadius && dist < closestDist)
                    {
                        hoveredAxis = i;
                        closestDist = dist;
                    }
                }
            }
        }
        
        IsHovered = hoveredAxis != -1;

        if (_activeAxis == -1 && interactive && isMouseDown && hoveredAxis != -1 && ImGui.IsWindowHovered())
        {
            _activeAxis = hoveredAxis;
            _dragStartRotation = objectRot;
            
            Ray mouseRay = ScreenToWorldRay(mousePos, view, proj, vpPos, vpSize);
            _dragStartAngle = GetRayPlaneAngle(mouseRay, objectPos, axes[_activeAxis]);
        }

        for (int i = 0; i < 3; i++)
        {
            if (axisValidPoints[i] > 1)
            {
                bool isActive = (_activeAxis == i) || (_activeAxis == -1 && hoveredAxis == i);
                uint color = isActive ? activeColors[i] : colors[i];
                float thickness = isActive ? 4.0f : 2.0f;
                
                Vector2[] renderPoints = new Vector2[axisValidPoints[i]];
                Array.Copy(axisPoints2D[i], renderPoints, axisValidPoints[i]);
                
                unsafe 
                {
                    fixed (Vector2* pPtr = renderPoints)
                    {
                        drawList.AddPolyline(ref pPtr[0], axisValidPoints[i], color, ImDrawFlags.Closed, thickness);
                    }
                }
            }
        }

        if (_activeAxis != -1)
        {
            Ray mouseRay = ScreenToWorldRay(mousePos, view, proj, vpPos, vpSize);
            float currentAngle = GetRayPlaneAngle(mouseRay, objectPos, axes[_activeAxis]);
            
            float deltaAngle = currentAngle - _dragStartAngle;

            if (snapAmount > 0.0f)
            {
                float snapRad = snapAmount * MathF.PI / 180.0f;
                deltaAngle = MathF.Round(deltaAngle / snapRad) * snapRad;
            }

            if (deltaAngle != 0.0f)
            {
                newRot = Quaternion.CreateFromAxisAngle(axes[_activeAxis], deltaAngle) * _dragStartRotation;
                changed = true;
            }
        }

        return changed;
    }

    public static bool IsHovered { get; private set; }
    public static bool IsUsing() => _activeAxis != -1;

    /// <summary>
    /// Clears global gizmo ownership when the editor changes context, selection
    /// or tool. The gizmo is shared by all editor viewports, so this must not
    /// depend on a later viewport callback to recover from a drag.
    /// </summary>
    public static void Reset()
    {
        _activeAxis = -1;
        _dragStartOffset = 0.0f;
        _dragStartObjectPos = Vector3.Zero;
        _dragStartHitPos = Vector3.Zero;
        _dragStartAngle = 0.0f;
        _dragStartRotation = Quaternion.Identity;
        _dragStartObjectScale = Vector3.One;
        IsHovered = false;
    }

    private static bool WorldToScreen(Vector3 worldPos, Matrix4x4 view, Matrix4x4 proj, Vector2 vpPos, Vector2 vpSize, out Vector2 screenPos)
    {
        screenPos = Vector2.Zero;
        Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1.0f), view * proj);
        if (clip.W <= 0) return false;

        Vector2 ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        screenPos.X = vpPos.X + (ndc.X + 1.0f) * 0.5f * vpSize.X;
        screenPos.Y = vpPos.Y + (1.0f - ndc.Y) * 0.5f * vpSize.Y;
        return true;
    }

    private struct Ray { public Vector3 Origin; public Vector3 Direction; }

    public static void GetMouseRay(Vector2 mousePos, Matrix4x4 view, Matrix4x4 proj, Vector2 vpPos, Vector2 vpSize, out Vector3 rayOrigin, out Vector3 rayDir)
    {
        Ray r = ScreenToWorldRay(mousePos, view, proj, vpPos, vpSize);
        rayOrigin = r.Origin;
        rayDir = r.Direction;
    }

    private static Ray ScreenToWorldRay(Vector2 screenPos, Matrix4x4 view, Matrix4x4 proj, Vector2 vpPos, Vector2 vpSize)
    {
        float x = (screenPos.X - vpPos.X) / vpSize.X * 2.0f - 1.0f;
        float y = 1.0f - (screenPos.Y - vpPos.Y) / vpSize.Y * 2.0f;

        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        Matrix4x4.Invert(view, out Matrix4x4 invView);

        bool isOrtho = MathF.Abs(proj.M44 - 1.0f) < 0.0001f;

        if (isOrtho)
        {
            Vector4 clipPos = new Vector4(x, y, -1.0f, 1.0f);
            Vector4 eyePos = Vector4.Transform(clipPos, invProj);
            Vector4 worldPos = Vector4.Transform(eyePos, invView);
            Vector3 rayOrigin = new Vector3(worldPos.X, worldPos.Y, worldPos.Z);

            Vector4 eyeDir = new Vector4(0, 0, -1.0f, 0.0f);
            Vector4 worldDir = Vector4.Transform(eyeDir, invView);
            Vector3 rayDir = Vector3.Normalize(new Vector3(worldDir.X, worldDir.Y, worldDir.Z));

            return new Ray { Origin = rayOrigin, Direction = rayDir };
        }
        else
        {
            Vector4 rayEye = Vector4.Transform(new Vector4(x, y, -1.0f, 1.0f), invProj);
            rayEye.Z = -1.0f;
            rayEye.W = 0.0f;

            Vector4 rayWorld = Vector4.Transform(rayEye, invView);
            Vector3 rayDir = Vector3.Normalize(new Vector3(rayWorld.X, rayWorld.Y, rayWorld.Z));

            Vector3 rayOrigin = new Vector3(invView.M41, invView.M42, invView.M43);

            return new Ray { Origin = rayOrigin, Direction = rayDir };
        }
    }

    private static float GetRayAxisIntersection(Ray ray, Vector3 axisOrigin, Vector3 axisDir, Matrix4x4 view)
    {
        Vector3 camForward = new Vector3(-view.M13, -view.M23, -view.M33);
        
        Vector3 planeNormal = Vector3.Cross(axisDir, Vector3.Cross(axisDir, camForward));
        if (planeNormal.LengthSquared() < 0.0001f)
        {
            planeNormal = Vector3.Cross(axisDir, Vector3.UnitY);
            if (planeNormal.LengthSquared() < 0.0001f)
                planeNormal = Vector3.Cross(axisDir, Vector3.UnitX);
        }
        planeNormal = Vector3.Normalize(planeNormal);

        float denom = Vector3.Dot(planeNormal, ray.Direction);
        if (MathF.Abs(denom) > 0.0001f)
        {
            float t = Vector3.Dot(planeNormal, axisOrigin - ray.Origin) / denom;
            Vector3 intersectionPoint = ray.Origin + ray.Direction * t;
            return Vector3.Dot(intersectionPoint - axisOrigin, axisDir);
        }
        return 0.0f;
    }

    private static float GetRayPlaneAngle(Ray ray, Vector3 planeOrigin, Vector3 planeNormal)
    {
        float denom = Vector3.Dot(planeNormal, ray.Direction);
        if (MathF.Abs(denom) > 0.0001f)
        {
            float t = Vector3.Dot(planeNormal, planeOrigin - ray.Origin) / denom;
            Vector3 intersectionPoint = ray.Origin + ray.Direction * t;
            
            Vector3 offset = intersectionPoint - planeOrigin;
            
            Vector3[] axes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
            int i = 0;
            if (planeNormal == Vector3.UnitX) i = 0;
            if (planeNormal == Vector3.UnitY) i = 1;
            if (planeNormal == Vector3.UnitZ) i = 2;
            
            Vector3 u = axes[(i + 1) % 3];
            Vector3 v = axes[(i + 2) % 3];

            float x = Vector3.Dot(u, offset);
            float y = Vector3.Dot(v, offset);
            return MathF.Atan2(y, x);
        }
        return 0.0f;
    }

    private static bool GetRayPlaneIntersection(Ray ray, Vector3 planeOrigin, Vector3 planeNormal, out Vector3 intersectionPoint)
    {
        intersectionPoint = Vector3.Zero;
        float denom = Vector3.Dot(planeNormal, ray.Direction);
        if (MathF.Abs(denom) > 0.0001f)
        {
            float t = Vector3.Dot(planeNormal, planeOrigin - ray.Origin) / denom;
            if (t >= 0.0f)
            {
                intersectionPoint = ray.Origin + ray.Direction * t;
                return true;
            }
        }
        return false;
    }

    private static void GetPlaneHandlePoints(int planeIndex, Vector3 objectPos, Vector3 signs, float offset, float size, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3)
    {
        p0 = objectPos;
        p1 = objectPos;
        p2 = objectPos;
        p3 = objectPos;
        
        if (planeIndex == 0) // YZ
        {
            p0 += new Vector3(0, offset * signs.Y, offset * signs.Z);
            p1 += new Vector3(0, (offset + size) * signs.Y, offset * signs.Z);
            p2 += new Vector3(0, (offset + size) * signs.Y, (offset + size) * signs.Z);
            p3 += new Vector3(0, offset * signs.Y, (offset + size) * signs.Z);
        }
        else if (planeIndex == 1) // XZ
        {
            p0 += new Vector3(offset * signs.X, 0, offset * signs.Z);
            p1 += new Vector3((offset + size) * signs.X, 0, offset * signs.Z);
            p2 += new Vector3((offset + size) * signs.X, 0, (offset + size) * signs.Z);
            p3 += new Vector3(offset * signs.X, 0, (offset + size) * signs.Z);
        }
        else if (planeIndex == 2) // XY
        {
            p0 += new Vector3(offset * signs.X, offset * signs.Y, 0);
            p1 += new Vector3((offset + size) * signs.X, offset * signs.Y, 0);
            p2 += new Vector3((offset + size) * signs.X, (offset + size) * signs.Y, 0);
            p3 += new Vector3(offset * signs.X, (offset + size) * signs.Y, 0);
        }
    }

    private static float GetGizmoScale(Vector3 objectPos, Matrix4x4 view, Matrix4x4 proj)
    {
        bool isOrtho = MathF.Abs(proj.M44 - 1.0f) < 0.0001f;
        if (isOrtho)
        {
            return 1.0f / proj.M11 * 0.2f;
        }
        else
        {
            Matrix4x4.Invert(view, out Matrix4x4 invView);
            Vector3 camPos = new Vector3(invView.M41, invView.M42, invView.M43);
            float dist = Vector3.Distance(camPos, objectPos);
            return MathF.Max(0.1f, dist * 0.15f);
        }
    }

    private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 pa = p - a, ba = b - a;
        float lengthSq = Vector2.Dot(ba, ba);
        if (lengthSq == 0.0f) return (p - a).Length();
        float h = Math.Clamp(Vector2.Dot(pa, ba) / lengthSq, 0.0f, 1.0f);
        return (pa - ba * h).Length();
    }
}
