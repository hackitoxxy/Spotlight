using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Spotlight.FileFormats
{
    /// <summary>The application supplies its existing Yaz0/SARC codec; tests can supply a small fake archive.</summary>
    public interface IMessageArchive
    {
        IDictionary<string, byte[]> Files { get; }
        byte[] Write();
    }

    /// <summary>
    /// One stage/language message document. Undo snapshots are whole MSBT byte arrays, so undoing
    /// insertion restores label buckets, attributes, styles and text together. On save we reopen
    /// the latest archive and replace only our member. Other stages' concurrent edits survive;
    /// changes to this same member are reported as a conflict. Project Directory is the output
    /// when configured; otherwise Game Directory is edited in place. A same-directory temporary
    /// file is staged and validated before saving. Backed-up overwrite is the default and
    /// preserves the destination file identity for readers that permit writes but not replacement.
    /// Call Save with overwriteInPlace:false to request File.Replace instead.
    /// It creates and flushes a backup first, but is not atomic: use only with emulation stopped.
    /// </summary>
    public sealed class StageMessageDocument
    {
        private readonly string gameRoot;
        private readonly string outputRoot;
        private readonly Func<byte[], IMessageArchive> openArchive;
        private byte[] saved;
        private byte[] current;
        private MsbtFile file;
        public string StageName { get; private set; }
        public string Language { get; }
        public string MemberName => StageName + ".msbt";
        public string ArchiveRelativePath => Path.Combine("LocalizedData", Language, "MessageData", "StageMessage.szs");
        public string OutputPath => Path.Combine(outputRoot, ArchiveRelativePath);
        public bool IsDirty => !current.SequenceEqual(saved);
        public static string LabelFor(string objectId)
        {
            if (string.IsNullOrWhiteSpace(objectId)) throw new ArgumentException("The object must have an ID.", nameof(objectId));
            return "ScenarioName_" + objectId;
        }

        public StageMessageDocument(string gameRoot, string projectRoot, string stageName, string language, Func<byte[], IMessageArchive> openArchive)
        {
            ValidateSegment(stageName);
            ValidateSegment(language);
            if (string.IsNullOrWhiteSpace(gameRoot)) throw new InvalidOperationException("Set Game Directory under File > Options.");
            this.gameRoot = Path.GetFullPath(gameRoot);
            outputRoot = ResolveOutputRoot(gameRoot, projectRoot);
            StageName = stageName;
            Language = language;
            this.openArchive = openArchive ?? throw new ArgumentNullException(nameof(openArchive));
            var archive = OpenLatest();
            if (!archive.Files.TryGetValue(MemberName, out byte[] data))
                throw new FileNotFoundException("The message archive has no " + MemberName + ". Create this stage's MSBT in a message editor first.");
            saved = (byte[])data.Clone();
            Restore(saved);
        }

        public bool TryGetTitle(string objectId, out string text) => file.TryGetPlainText(LabelFor(objectId), out text);

        public void SetTitle(string objectId, string text)
        {
            string template = file.Labels.FirstOrDefault(x => x.StartsWith("ScenarioName_", StringComparison.Ordinal));
            file.SetPlainText(LabelFor(objectId), text, template);
            current = file.Write();
        }

        public byte[] Snapshot() => (byte[])current.Clone();
        public void Restore(byte[] snapshot)
        {
            var parsed = MsbtFile.Read(snapshot);
            file = parsed;
            current = (byte[])snapshot.Clone();
        }

        public void CheckProject(string currentGameRoot, string currentProjectRoot)
        {
            if (string.IsNullOrWhiteSpace(currentGameRoot) ||
                !SamePath(gameRoot, Path.GetFullPath(currentGameRoot)) ||
                !SamePath(outputRoot, ResolveOutputRoot(currentGameRoot, currentProjectRoot)))
                throw new InvalidOperationException("Game Directory or the message output directory changed under File > Options. Restore the previous directories and save or discard these message edits before switching.");
        }

        private static string ResolveOutputRoot(string gameRoot, string projectRoot) =>
            Path.GetFullPath(string.IsNullOrWhiteSpace(projectRoot) ? gameRoot : projectRoot);

        private static bool SamePath(string left, string right) =>
            string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        public void Save(string stageName = null, bool overwriteInPlace = true)
        {
            try { SaveCore(stageName, overwriteInPlace); }
            // IOException messages from File.Replace need not identify either locked path.
            // Keep the underlying exception and pending edits; never force past a file lock.
            catch (IOException ex)
            {
                throw new IOException("Could not save moon-name archive:\n" + OutputPath +
                    "\nBackup: " + OutputPath + ".bak\n\n" + ex.Message +
                    "\n\nIf the game is running, stop emulation and retry Save. Leave Spotlight open to retain unsaved edits.", ex);
            }
        }

        private void SaveCore(string stageName, bool overwriteInPlace)
        {
            stageName = stageName ?? StageName;
            ValidateSegment(stageName);
            bool rename = stageName != StageName;
            if (!IsDirty && !rename) return;
            bool outputExisted = File.Exists(OutputPath);
            string sourcePath = outputExisted ? OutputPath : Path.Combine(gameRoot, ArchiveRelativePath);
            byte[] archiveBeforeSave = File.ReadAllBytes(sourcePath);
            var archive = openArchive(archiveBeforeSave);
            string member = stageName + ".msbt";
            bool exists = archive.Files.TryGetValue(member, out byte[] latest);
            if ((!rename && !exists) || (exists && !latest.SequenceEqual(saved) && !latest.SequenceEqual(current)))
                throw new IOException("The message file " + member + " changed outside this document. Reopen the stage before applying the edit again; it was not overwritten.");
            archive.Files[member] = Snapshot();
            byte[] output = archive.Write();
            // Validate the packed result before touching the destination.
            var verify = openArchive(output);
            if (verify.Files.Count != archive.Files.Count || archive.Files.Any(entry =>
                !verify.Files.TryGetValue(entry.Key, out byte[] verified) || !verified.SequenceEqual(entry.Value)))
                throw new InvalidDataException("The message archive did not round-trip correctly.");
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            string temporary = OutputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, output);
                if (File.Exists(OutputPath) != outputExisted || !File.ReadAllBytes(sourcePath).SequenceEqual(archiveBeforeSave))
                    throw new IOException("StageMessage.szs changed during saving. Retry Save to merge the latest archive.");
                if (outputExisted && overwriteInPlace) OverwriteWithBackup(archiveBeforeSave, output);
                else if (File.Exists(OutputPath)) File.Replace(temporary, OutputPath, OutputPath + ".bak");
                else File.Move(temporary, OutputPath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            saved = Snapshot();
            StageName = stageName;
        }

        private void OverwriteWithBackup(byte[] expected, byte[] output)
        {
            // Open does not truncate. Deny other writers/deleters while checking and saving,
            // but allow existing read handles if they granted FileShare.Write.
            using (var destination = new FileStream(OutputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                if (!Matches(destination, expected))
                    throw new IOException("StageMessage.szs changed before overwrite; retry Save to merge it.");
                using (var backup = new FileStream(OutputPath + ".bak", FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    backup.Write(expected, 0, expected.Length);
                    backup.Flush(true);
                }
                // A blocked backup must fail before any destination bytes are changed.
                if (!File.ReadAllBytes(OutputPath + ".bak").SequenceEqual(expected))
                    throw new IOException("Backup verification failed; the archive was not overwritten.");
                try
                {
                    destination.Position = 0;
                    destination.Write(output, 0, output.Length);
                    destination.SetLength(output.Length); // Shorter archives must not retain old trailing bytes.
                    destination.Flush(true);
                    if (!Matches(destination, output)) throw new IOException("Archive verification failed.");
                }
                catch (IOException ex)
                {
                    throw new IOException("Overwrite failed; the destination may be incomplete. Close the emulator before restoring the original archive from " +
                        OutputPath + ".bak. Pending edits remain in Spotlight.\n" + ex.Message, ex);
                }
            }
        }

        private static bool Matches(FileStream stream, byte[] expected)
        {
            if (stream.Length != expected.Length) return false;
            stream.Position = 0;
            var buffer = new byte[8192];
            int offset = 0;
            while (offset < expected.Length)
            {
                int count = stream.Read(buffer, 0, Math.Min(buffer.Length, expected.Length - offset));
                if (count == 0) return false;
                for (int i = 0; i < count; i++) if (buffer[i] != expected[offset + i]) return false;
                offset += count;
            }
            return true;
        }

        private IMessageArchive OpenLatest()
        {
            string path = File.Exists(OutputPath) ? OutputPath : Path.Combine(gameRoot, ArchiveRelativePath);
            return openArchive(File.ReadAllBytes(path));
        }

        private static void ValidateSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '-')))
                throw new ArgumentException("Stage and language names must be single directory/file name components.");
        }
    }
}
