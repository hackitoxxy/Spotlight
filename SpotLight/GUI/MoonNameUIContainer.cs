#if ODYSSEY
using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GL_EditorFramework;
using GL_EditorFramework.EditorDrawables;
using Spotlight.EditorDrawables;
using Spotlight.FileFormats;
using Spotlight.Storage;
using static GL_EditorFramework.EditorDrawables.EditorSceneBase;

namespace Spotlight.GUI
{
    /// <summary>A localized title editor for selected moons and moon-producing actors.</summary>
    public sealed class MoonNameUIContainer : IObjectUIContainer
    {
        private readonly General3dWorldObject obj;
        private readonly SM3DWorldScene scene;
        private readonly MoonNameStore store;
        private string[] languages = Array.Empty<string>();
        private StageMessageDocument document;
        private string text = "";
        private string error;
        private string loadedId;
        private bool exists;
        private byte[] capture;

        public MoonNameUIContainer(General3dWorldObject obj, SM3DWorldScene scene)
        {
            this.obj = obj;
            this.scene = scene;
            store = scene.EditZone.MoonNames;
            Reload();
        }

        private void Reload()
        {
            document = null;
            capture = null;
            error = null;
            loadedId = obj.ID;
            try
            {
                languages = store.GetLanguages();
                if (languages.Length == 0) throw new FileNotFoundException("No StageMessage.szs found in LocalizedData.");
                if (!languages.Contains(store.Language)) store.Language = languages[0];
                document = store.GetDocument(scene.EditZone.StageName);
                exists = document.TryGetTitle(obj.ID, out string value);
                text = value ?? "";
            }
            catch (Exception ex) when (IsEditError(ex)) { error = ex.Message; }
        }

        public void DoUI(IObjectUIControl control)
        {
            if (loadedId != obj.ID) Reload();
            if (languages.Length > 0)
            {
                string language = (string)control.ChoicePicker("Language", store.Language, languages);
                if (language != store.Language) { store.Language = language; Reload(); }
            }
            control.PlainText("Message: ScenarioName_" + obj.ID);
            control.PlainText("For moons and moon-producing objects.");
            if (error != null)
            {
                control.SetTooltip(error);
                control.PlainText("Moon name unavailable.");
                if (control.Button("Show details")) MessageBox.Show(error, "Moon Name", MessageBoxButtons.OK, MessageBoxIcon.Information);
                control.SetTooltip(null);
                if (control.Button("Retry loading moon name")) Reload();
                return;
            }
            control.SetTooltip("In-game title for this object if it produces a moon. Saved for this language only to: " + document.OutputPath);
            text = control.TextInput(text, "Moon Name");
            control.SetTooltip(null);
            if (!exists) control.PlainText("No title yet; entering a name creates it.");
        }

        public void OnValueChangeStart() { capture = document?.Snapshot(); }
        public void OnValueChanged() { }
        public void OnValueSet()
        {
            if (capture == null || document == null || error != null) return;
            try
            {
                if (!exists && text.Length == 0) return;
                document.SetTitle(obj.ID, text);
                if (!capture.SequenceEqual(document.Snapshot())) scene.AddToUndo(new MessageUndo(document, capture));
                exists = true;
            }
            catch (Exception ex) when (IsEditError(ex))
            {
                document.Restore(capture);
                error = ex.Message;
            }
            finally { capture = null; scene.Refresh(); }
        }
        public void UpdateProperties() { Reload(); }

        private static bool IsEditError(Exception ex) => ex is IOException || ex is UnauthorizedAccessException ||
            ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is OverflowException;

        private sealed class MessageUndo : IRevertable
        {
            private readonly StageMessageDocument document;
            private readonly byte[] snapshot;
            public MessageUndo(StageMessageDocument document, byte[] snapshot) { this.document = document; this.snapshot = snapshot; }
            public IRevertable Revert(EditorSceneBase scene)
            {
                var inverse = new MessageUndo(document, document.Snapshot());
                document.Restore(snapshot);
                return inverse;
            }
        }
    }
}
#endif
