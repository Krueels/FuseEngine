using System;
using System.Numerics;
using Fuse.Scene;
using Fuse.Scene.Model;
using Fuse.Debug;
using ImGuiNET;
using Brush = Fuse.Scene.Model.Brush;

namespace Blowtorch
{
    public class BrushPreviewManager
    {
        public bool HasPreview { get; set; } = false;
        public Vector3 Min { get; set; }
        public Vector3 Max { get; set; }
        public bool IsDrawing { get; set; } = false;
        public EditorViewport? DrawingViewport { get; set; }
        public bool IsDraggingHandle { get; set; } = false;

        public void Reset()
        {
            HasPreview = false;
            IsDrawing = false;
            DrawingViewport = null;
            IsDraggingHandle = false;
        }

        public void HandleDrawingInput(EditorViewport viewport, Vector3 hitPoint, bool isMouseClicked)
        {
            if (isMouseClicked)
            {
                IsDrawing = true;
                DrawingViewport = viewport;
                
                if (!HasPreview)
                {
                    Min = hitPoint;
                    Max = hitPoint;
                    if (viewport.Camera.ViewType == CameraViewType.Top) { Min = new Vector3(Min.X, -1, Min.Z); Max = new Vector3(Max.X, 1, Max.Z); }
                    if (viewport.Camera.ViewType == CameraViewType.Front) { Min = new Vector3(Min.X, Min.Y, -1); Max = new Vector3(Max.X, Max.Y, 1); }
                    if (viewport.Camera.ViewType == CameraViewType.Side) { Min = new Vector3(-1, Min.Y, Min.Z); Max = new Vector3(1, Max.Y, Max.Z); }
                }
                else
                {
                    if (viewport.Camera.ViewType == CameraViewType.Top) { Min = new Vector3(hitPoint.X, Min.Y, hitPoint.Z); }
                    if (viewport.Camera.ViewType == CameraViewType.Front) { Min = new Vector3(hitPoint.X, hitPoint.Y, Min.Z); }
                    if (viewport.Camera.ViewType == CameraViewType.Side) { Min = new Vector3(Min.X, hitPoint.Y, hitPoint.Z); }
                }
            }
            
            if (IsDrawing && DrawingViewport == viewport)
            {
                if (viewport.Camera.ViewType == CameraViewType.Top) { Max = new Vector3(hitPoint.X, Max.Y, hitPoint.Z); }
                if (viewport.Camera.ViewType == CameraViewType.Front) { Max = new Vector3(hitPoint.X, hitPoint.Y, Max.Z); }
                if (viewport.Camera.ViewType == CameraViewType.Side) { Max = new Vector3(Max.X, hitPoint.Y, hitPoint.Z); }
                HasPreview = true;
            }
        }

        public void EndDrawing()
        {
            if (IsDrawing)
            {
                IsDrawing = false;
                DrawingViewport = null;
                Vector3 min = Vector3.Min(Min, Max);
                Vector3 max = Vector3.Max(Min, Max);
                Min = min;
                Max = max;
            }
        }

        public void UpdateBoundsFromDrag(Vector3 currentMin, Vector3 currentMax)
        {
            Min = currentMin;
            Max = currentMax;
        }

        public Brush CreateBrush()
        {
            Vector3 min = Vector3.Min(Min, Max);
            Vector3 max = Vector3.Max(Min, Max);
            Vector3 size = max - min;
            
            // Prevent flat brushes
            if (size.X < 0.1f) size.X = 1.0f;
            if (size.Y < 0.1f) size.Y = 1.0f;
            if (size.Z < 0.1f) size.Z = 1.0f;

            Vector3 pos = min + size * 0.5f;

            var brush = Brush.CreateCube(pos, size);
            return brush;
        }

        public void Draw3DPreview(DebugDrawer drawer)
        {
            if (!HasPreview) return;

            Vector3 min = Vector3.Min(Min, Max);
            Vector3 max = Vector3.Max(Min, Max);
            Vector3 size = max - min;
            Vector3 pos = min + size * 0.5f;

            // Draw as cyan/light blue outline
            drawer.DrawBox(pos, Quaternion.Identity, size * 0.5f, new Vector3(0.0f, 1.0f, 1.0f));
        }
    }
}
