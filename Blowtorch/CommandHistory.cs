using System;
using System.Collections.Generic;
using Fuse.Scene.Model;
using Fuse.Scene.Terrain;

namespace Blowtorch;

public interface ICommand
{
    void Execute();
    void Undo();
}

public class SnapshotCommand : ICommand
{
    private readonly EditorSceneService _sceneService;
    private readonly EditorAssetService _assetService;
    private readonly string _stateBefore;
    private readonly string _stateAfter;

    public SnapshotCommand(EditorSceneService sceneService, EditorAssetService assetService, string stateBefore, string stateAfter)
    {
        _sceneService = sceneService;
        _assetService = assetService;
        _stateBefore = stateBefore;
        _stateAfter = stateAfter;
    }

    public void Execute()
    {
        RestoreState(_stateAfter);
    }

    public void Undo()
    {
        RestoreState(_stateBefore);
    }

    private void RestoreState(string json)
    {
        var doc = MapDocument.Parse(json);
        if (doc != null)
        {
            _assetService.ClearBrushMeshes();
            _sceneService.SetDocument(doc);
            _sceneService.PopulateScene(_assetService);
        }
    }
}

public sealed class TerrainSnapshotCommand : ICommand
{
    private readonly EditorSceneService _sceneService;
    private readonly EditorAssetService _assetService;
    private readonly string _assetPath;
    private readonly TerrainTileSetSnapshot _before;
    private readonly TerrainTileSetSnapshot _after;

    public TerrainSnapshotCommand(
        EditorSceneService sceneService,
        EditorAssetService assetService,
        string assetPath,
        TerrainTileSetSnapshot before,
        TerrainTileSetSnapshot after)
    {
        _sceneService = sceneService;
        _assetService = assetService;
        _assetPath = assetPath;
        _before = before;
        _after = after;
    }

    public void Execute() => Restore(_after);

    public void Undo() => Restore(_before);

    private void Restore(TerrainTileSetSnapshot snapshot)
    {
        try
        {
            TerrainTileSetAsset terrain = TerrainTileSetAsset.Load(_assetPath);
            if (!terrain.RestoreSnapshot(snapshot))
                return;

            terrain.Save(_assetPath);
            _sceneService.InvalidateTerrainAsset(_assetPath);
            _sceneService.PopulateScene(_assetService);
            _sceneService.MarkModified(_sceneService.Document.Serialize());
        }
        catch (Exception ex)
        {
            Fuse.Core.Logger.Warn($"Terrain undo/redo failed: {ex.Message}");
        }
    }
}

public class CommandHistory
{
    private readonly List<ICommand> _undoStack = new();
    private readonly List<ICommand> _redoStack = new();
    private const int MaxHistorySize = 50;
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void PushCommand(ICommand command)
    {
        _undoStack.Add(command);
        if (_undoStack.Count > MaxHistorySize)
        {
            _undoStack.RemoveAt(0);
        }
        _redoStack.Clear();
    }

    public void Undo()
    {
        if (_undoStack.Count > 0)
        {
            var command = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            command.Undo();
            _redoStack.Add(command);
            if (_redoStack.Count > MaxHistorySize)
            {
                _redoStack.RemoveAt(0);
            }
        }
    }

    public void Redo()
    {
        if (_redoStack.Count > 0)
        {
            var command = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            command.Execute();
            _undoStack.Add(command);
            if (_undoStack.Count > MaxHistorySize)
            {
                _undoStack.RemoveAt(0);
            }
        }
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
