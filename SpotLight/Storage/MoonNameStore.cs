#if ODYSSEY
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Spotlight.FileFormats;
using SARCExt;
using SZS;

namespace Spotlight.Storage
{
    /// <summary>Owned by a zone, shared by every view of that zone, and discarded when the zone closes.</summary>
    public sealed class MoonNameStore
    {
        private readonly Dictionary<string, StageMessageDocument> documents = new Dictionary<string, StageMessageDocument>(StringComparer.Ordinal);
        public string Language { get; set; } = "USen";
        public bool IsDirty => documents.Values.Any(x => x.IsDirty);

        public string[] GetLanguages()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string root in new[] { Program.ProjectPath, Program.GamePath })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string localized = Path.Combine(root, "LocalizedData");
                if (!Directory.Exists(localized)) continue;
                foreach (string directory in Directory.GetDirectories(localized))
                    if (File.Exists(Path.Combine(directory, "MessageData", "StageMessage.szs"))) names.Add(Path.GetFileName(directory));
            }
            return names.OrderBy(x => x != "USen").ThenBy(x => x, StringComparer.Ordinal).ToArray();
        }

        public StageMessageDocument GetDocument(string stage)
        {
            if (!documents.TryGetValue(Language, out var document))
            {
                document = new StageMessageDocument(Program.GamePath, Program.ProjectPath, stage, Language, bytes => new SarcMessageArchive(bytes));
                documents.Add(Language, document);
            }
            document.CheckProject(Program.GamePath, Program.ProjectPath);
            return document;
        }

        public void Save(string stageName)
        {
            foreach (var document in documents.Values)
            {
                if (!document.IsDirty && document.StageName == stageName) continue;
                document.CheckProject(Program.GamePath, Program.ProjectPath);
                document.Save(stageName);
            }
        }

        private sealed class SarcMessageArchive : IMessageArchive
        {
            private readonly SARCExt.SarcData data;
            public IDictionary<string, byte[]> Files => data.Files;
            public SarcMessageArchive(byte[] bytes)
            {
                if (bytes.Length < 16) throw new InvalidDataException("Truncated StageMessage archive.");
                bool yaz0 = bytes[0] == 'Y' && bytes[1] == 'a' && bytes[2] == 'z' && bytes[3] == '0';
                if (!yaz0 && !(bytes[0] == 'S' && bytes[1] == 'A' && bytes[2] == 'R' && bytes[3] == 'C'))
                    throw new InvalidDataException("StageMessage must be a Yaz0 or SARC archive.");
                data = SARCExt.SARC.UnpackRamN(yaz0 ? YAZ0.Decompress(bytes) : bytes);
            }
            public byte[] Write() => YAZ0.Compress(SARCExt.SARC.PackN(data));
        }
    }
}
#endif
