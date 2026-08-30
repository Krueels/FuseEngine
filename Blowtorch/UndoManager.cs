using Blowtorch;
using Fuse.Scene;
using ImGuiNET;

namespace Blowtorch
{
    public class UndoManager
    {
        private string _preEditState = "";
        private bool _needsCommit = false;

        public void RecordState(string frameBeginState)
        {
            if (string.IsNullOrEmpty(_preEditState))
                _preEditState = frameBeginState;
        }

        public void TrackItem(string frameBeginState)
        {
            if (ImGui.IsItemActivated())
            {
                RecordState(frameBeginState);
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                _needsCommit = true;
            }
        }

        public void EndFrame(CommandHistory history, EditorSceneService sceneService, EditorAssetService assetService)
        {
            if (_needsCommit)
            {
                var postEditState = sceneService.Document.Serialize();
                if (_preEditState != "" && _preEditState != postEditState)
                {
                    sceneService.MarkModified(postEditState);
                    history.PushCommand(new SnapshotCommand(sceneService, assetService, _preEditState, postEditState));
                }
                Reset();
            }
        }

        public void ForceStart(string frameBeginState)
        {
            _preEditState = frameBeginState;
            _needsCommit = false;
        }

        public void ForceEnd(CommandHistory history, EditorSceneService sceneService, EditorAssetService assetService)
        {
            var postEditState = sceneService.Document.Serialize();
            if (_preEditState != "" && _preEditState != postEditState)
            {
                sceneService.MarkModified(postEditState);
                history.PushCommand(new SnapshotCommand(sceneService, assetService, _preEditState, postEditState));
            }
            Reset();
        }

        public void Reset()
        {
            _preEditState = "";
            _needsCommit = false;
        }
    }
}
