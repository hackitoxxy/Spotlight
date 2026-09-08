using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Spotlight.FileFormats;

namespace Spotlight.MessageTests
{
    [TestClass]
    public class StageMessageDocumentTests
    {
        private string directory, game, project;
        private const string Relative = "LocalizedData/USen/MessageData/StageMessage.szs";
        [TestInitialize]
        public void Setup()
        {
            directory = Path.Combine(Path.GetTempPath(), "Spotlight.MessageTests-" + Guid.NewGuid().ToString("N"));
            game = Path.Combine(directory, "game"); project = Path.Combine(directory, "project");
            WriteArchive(game, new Dictionary<string, byte[]> { ["StageA.msbt"] = MsbtFixture.Create(), ["StageB.msbt"] = MsbtFixture.Create(), ["unrelated.bin"] = new byte[] { 1, 2, 3 } });
        }
        [TestCleanup]
        public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        private StageMessageDocument Open(string stage = "StageA") => new StageMessageDocument(game, project, stage, "USen", bytes => new FakeArchive(bytes));

        [TestMethod]
        public void OverwriteWorksWithReaderThatAllowsWritesButBlocksReplacement()
        {
            var doc = new StageMessageDocument(game, null, "StageA", "USen", bytes => new FakeArchive(bytes));
            byte[] original = File.ReadAllBytes(doc.OutputPath);
            using (var emulator = File.Open(doc.OutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                doc.SetTitle("obj55", "Ben farted - longer title");
                Assert.ThrowsExactly<IOException>(() => doc.Save(overwriteInPlace: false)); // Explicit replacement is blocked.
                doc.Save(); // Default must work with the retained reader, without a UI checkbox.
                CollectionAssert.AreEqual(original, File.ReadAllBytes(doc.OutputPath + ".bak"));
                Assert.IsFalse(doc.IsDirty);
                long longerLength = emulator.Length;
                doc.SetTitle("obj55", "B");
                doc.Save(overwriteInPlace: true);
                Assert.IsTrue(emulator.Length < longerLength);
                emulator.Position = 0;
                using (var bytes = new MemoryStream())
                {
                    emulator.CopyTo(bytes);
                    CollectionAssert.AreEqual(File.ReadAllBytes(doc.OutputPath), bytes.ToArray());
                    var msbt = MsbtFile.Read(new FakeArchive(bytes.ToArray()).Files["StageA.msbt"]);
                    Assert.IsTrue(msbt.TryGetPlainText("ScenarioName_obj55", out string title));
                    Assert.AreEqual("B", title); // Same open handle sees the complete new archive.
                }
            }
        }

        [TestMethod]
        public void OverwriteCannotBypassWriteLockAndKeepsPendingEdit()
        {
            var doc = new StageMessageDocument(game, null, "StageA", "USen", bytes => new FakeArchive(bytes));
            byte[] original = File.ReadAllBytes(doc.OutputPath);
            doc.SetTitle("obj55", "Pending title");
            using (File.Open(doc.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsExactly<IOException>(() => doc.Save(overwriteInPlace: true));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(doc.OutputPath));
            Assert.IsFalse(File.Exists(doc.OutputPath + ".bak"));
            Assert.IsTrue(doc.IsDirty);
            doc.Save(overwriteInPlace: true);
            Assert.IsFalse(doc.IsDirty);
        }

        [TestMethod]
        public void LockedBackupPreventsOverwriteWithoutTouchingDestination()
        {
            var doc = new StageMessageDocument(game, null, "StageA", "USen", bytes => new FakeArchive(bytes));
            byte[] original = File.ReadAllBytes(doc.OutputPath);
            File.WriteAllBytes(doc.OutputPath + ".bak", original);
            doc.SetTitle("obj55", "Pending title");
            using (File.Open(doc.OutputPath + ".bak", FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsExactly<IOException>(() => doc.Save(overwriteInPlace: true));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(doc.OutputPath));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(doc.OutputPath + ".bak"));
            Assert.IsTrue(doc.IsDirty);
        }

        [TestMethod]
        public void OverwriteModeCanCreateProjectArchiveAndRejectStaleMember()
        {
            var first = Open(); var stale = Open();
            first.SetTitle("obj55", "First"); first.Save(overwriteInPlace: true);
            stale.SetTitle("obj55", "Stale");
            Assert.ThrowsExactly<IOException>(() => stale.Save(overwriteInPlace: true));
            Assert.IsTrue(Open().TryGetTitle("obj55", out string title));
            Assert.AreEqual("First", title);
        }

        [TestMethod]
        [DataRow("obj55")]
        [DataRow("obj999")]
        public void FullTitleReplacesPreviouslySavedFirstLetter(string objectId)
        {
            // Cover both an existing title and a newly inserted moon name, in-place.
            Func<StageMessageDocument> reopen = () => new StageMessageDocument(game, null, "StageA", "USen", bytes => new FakeArchive(bytes));
            var doc = reopen();
            doc.SetTitle(objectId, "B");
            doc.Save();
            doc.SetTitle(objectId, "Ben farted");
            doc.Save();
            Assert.IsTrue(reopen().TryGetTitle(objectId, out string title));
            Assert.AreEqual("Ben farted", title);
        }

        [TestMethod]
        public void UndoRedoInsertionRestoresAllMetadataAndDirtyState()
        {
            var doc = Open(); byte[] before = doc.Snapshot();
            doc.SetTitle("obj999", "New moon"); byte[] after = doc.Snapshot(); Assert.IsTrue(doc.IsDirty);
            doc.Restore(before); Assert.IsFalse(doc.IsDirty); Assert.IsFalse(doc.TryGetTitle("obj999", out _));
            doc.Restore(after); Assert.IsTrue(doc.TryGetTitle("obj999", out string name)); Assert.AreEqual("New moon", name);
            doc.Save(); Assert.IsFalse(doc.IsDirty);
            doc.Restore(before); Assert.IsTrue(doc.IsDirty); doc.Save(); Assert.IsFalse(Open().TryGetTitle("obj999", out _));
        }

        [TestMethod]
        public void LockedMessageArchiveNamesFileKeepsEditAndAllowsRetry()
        {
            var doc = new StageMessageDocument(game, null, "StageA", "USen", bytes => new FakeArchive(bytes));
            byte[] original = File.ReadAllBytes(doc.OutputPath);
            doc.SetTitle("obj55", "Ben farted");
            using (File.Open(doc.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Assert.ThrowsExactly<IOException>(() => doc.Save());
                StringAssert.Contains(error.Message, doc.OutputPath);
                Assert.IsTrue(doc.IsDirty);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(doc.OutputPath));
            }
            doc.Save();
            Assert.IsFalse(doc.IsDirty);
            var reopened = new StageMessageDocument(game, null, "StageA", "USen", bytes => new FakeArchive(bytes));
            Assert.IsTrue(reopened.TryGetTitle("obj55", out string title));
            Assert.AreEqual("Ben farted", title);
        }

        [TestMethod]
        public void TwoOpenStagesMergeWithoutOverwritingEachOtherOrBaseGame()
        {
            byte[] original = File.ReadAllBytes(Path.Combine(game, Relative));
            var a = Open("StageA"); var b = Open("StageB");
            a.SetTitle("obj55", "A"); b.SetTitle("obj55", "B"); a.Save(); b.Save();
            Assert.IsTrue(Open("StageA").TryGetTitle("obj55", out string titleA)); Assert.AreEqual("A", titleA);
            Assert.IsTrue(Open("StageB").TryGetTitle("obj55", out string titleB)); Assert.AreEqual("B", titleB);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(game, Relative)));
            var archive = new FakeArchive(File.ReadAllBytes(Path.Combine(project, Relative)));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, archive.Files["unrelated.bin"]);
            Assert.IsTrue(File.Exists(Path.Combine(project, Relative) + ".bak"));
        }

        [TestMethod]
        public void SameStageConflictPreservesDiskAndPendingEdit()
        {
            var first = Open(); var second = Open();
            first.SetTitle("obj55", "First"); second.SetTitle("obj55", "Second"); first.Save();
            byte[] saved = File.ReadAllBytes(Path.Combine(project, Relative));
            Assert.ThrowsExactly<IOException>(() => second.Save()); Assert.IsTrue(second.IsDirty);
            CollectionAssert.AreEqual(saved, File.ReadAllBytes(Path.Combine(project, Relative)));
        }

        [TestMethod]
        public void ProjectOverrideWinsEvenWhenItsTimestampIsOlder()
        {
            var doc = Open(); doc.SetTitle("obj55", "Project title"); doc.Save();
            File.SetLastWriteTimeUtc(Path.Combine(project, Relative), DateTime.UtcNow.AddYears(-1));
            Assert.IsTrue(Open().TryGetTitle("obj55", out string title)); Assert.AreEqual("Project title", title);
        }

        [TestMethod]
        public void SaveAsCopiesMessageMemberAndKeepsOriginal()
        {
            var doc = Open(); doc.SetTitle("obj55", "Renamed stage"); doc.Save("StageC");
            Assert.AreEqual("StageC", doc.StageName); Assert.IsFalse(doc.IsDirty);
            Assert.IsTrue(Open("StageC").TryGetTitle("obj55", out string title)); Assert.AreEqual("Renamed stage", title);
            Assert.IsTrue(Open().TryGetTitle("obj55", out string old)); Assert.AreEqual("Old moon", old);
        }

        [TestMethod]
        public void UnmodifiedSaveDoesNotCreateOutput()
        {
            Open().Save(); Assert.IsFalse(File.Exists(Path.Combine(project, Relative)));
        }

        [TestMethod]
        public void RepackFailureDoesNotWriteArchiveOrClearDirtyState()
        {
            int opens = 0;
            var doc = new StageMessageDocument(game, project, "StageA", "USen", bytes =>
            {
                var archive = new FakeArchive(bytes);
                // Third decode is the verification of the newly packed archive.
                if (++opens == 3) archive.Files["unrelated.bin"] = new byte[] { 99 };
                return archive;
            });
            doc.SetTitle("obj55", "New");
            Assert.ThrowsExactly<InvalidDataException>(() => doc.Save());
            Assert.IsTrue(doc.IsDirty); Assert.IsFalse(File.Exists(Path.Combine(project, Relative)));
        }

        [TestMethod]
        public void ArchiveChangedDuringPackingIsNotOverwritten()
        {
            int opens = 0;
            var replacement = new Dictionary<string, byte[]> { ["StageA.msbt"] = MsbtFixture.Create(), ["external.bin"] = new byte[] { 9 } };
            var doc = new StageMessageDocument(game, project, "StageA", "USen", bytes =>
            {
                if (++opens == 3) WriteArchive(project, replacement);
                return new FakeArchive(bytes);
            });
            doc.SetTitle("obj55", "Pending");
            Assert.ThrowsExactly<IOException>(() => doc.Save());
            Assert.IsTrue(doc.IsDirty);
            Assert.IsTrue(new FakeArchive(File.ReadAllBytes(Path.Combine(project, Relative))).Files.ContainsKey("external.bin"));
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(Path.Combine(project, Relative)), "*.tmp").Length);
        }

        [TestMethod]
        public void RejectsMissingGameDirectoryMissingStagesAndPathTraversal()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => new StageMessageDocument("", project, "StageA", "USen", bytes => new FakeArchive(bytes)));
            Assert.ThrowsExactly<FileNotFoundException>(() => Open("MissingStage"));
            Assert.ThrowsExactly<ArgumentException>(() => Open("../StageA"));
            Assert.ThrowsExactly<InvalidOperationException>(() => Open().CheckProject(game, directory));
            Assert.AreEqual("ScenarioName_obj123", StageMessageDocument.LabelFor("obj123"));
        }

        [TestMethod]
        [DataRow(null)] [DataRow("")] [DataRow("   ")]
        public void UnsetProjectLoadsAndSavesInGameDirectoryWithBackup(string projectRoot)
        {
            string archivePath = Path.Combine(game, Relative);
            byte[] original = File.ReadAllBytes(archivePath);
            var doc = new StageMessageDocument(game, projectRoot, "StageA", "USen", bytes => new FakeArchive(bytes));
            Assert.AreEqual(Path.GetFullPath(archivePath), doc.OutputPath);
            Assert.IsTrue(doc.TryGetTitle("obj55", out string oldTitle)); Assert.AreEqual("Old moon", oldTitle);
            doc.CheckProject(game, projectRoot);
            doc.Save(); // Merely loading/viewing a name must not create files or backups.
            Assert.IsFalse(File.Exists(archivePath + ".bak"));
            doc.SetTitle("obj999", "In-place moon"); doc.Save();
            Assert.IsFalse(doc.IsDirty);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(archivePath + ".bak"));
            var reopened = new StageMessageDocument(game, projectRoot, "StageA", "USen", bytes => new FakeArchive(bytes));
            Assert.IsTrue(reopened.TryGetTitle("obj999", out string title)); Assert.AreEqual("In-place moon", title);
            var archive = new FakeArchive(File.ReadAllBytes(archivePath));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, archive.Files["unrelated.bin"]);
            Assert.IsFalse(Directory.Exists(project));
        }

        [TestMethod]
        public void ProjectDirectoryMayExplicitlyEqualGameDirectory()
        {
            var doc = new StageMessageDocument(game, game, "StageA", "USen", bytes => new FakeArchive(bytes));
            doc.CheckProject(game, ""); // Same effective output location.
            doc.SetTitle("obj55", "Same directory"); doc.Save();
            Assert.IsTrue(new StageMessageDocument(game, "", "StageA", "USen", bytes => new FakeArchive(bytes)).TryGetTitle("obj55", out string title));
            Assert.AreEqual("Same directory", title);
        }

        [TestMethod]
        public void InPlaceEditsStillDetectOutputDirectoryChanges()
        {
            var doc = new StageMessageDocument(game, "", "StageA", "USen", bytes => new FakeArchive(bytes));
            doc.SetTitle("obj55", "Pending");
            doc.CheckProject(game, null);
            doc.CheckProject(game, game + Path.DirectorySeparatorChar);
            Assert.ThrowsExactly<InvalidOperationException>(() => doc.CheckProject(game, project));
            Assert.ThrowsExactly<InvalidOperationException>(() => doc.CheckProject(project, ""));
            Assert.ThrowsExactly<InvalidOperationException>(() => Open().CheckProject(game, ""));
        }

        [TestMethod]
        public void InPlaceStageEditsMergeAndConflictingEditsAreRejected()
        {
            var a = new StageMessageDocument(game, "", "StageA", "USen", bytes => new FakeArchive(bytes));
            var b = new StageMessageDocument(game, "", "StageB", "USen", bytes => new FakeArchive(bytes));
            var staleA = new StageMessageDocument(game, "", "StageA", "USen", bytes => new FakeArchive(bytes));
            a.SetTitle("obj55", "A"); b.SetTitle("obj55", "B"); staleA.SetTitle("obj55", "Conflict");
            a.Save(); b.Save();
            Assert.ThrowsExactly<IOException>(() => staleA.Save());
            Assert.IsTrue(staleA.IsDirty);
            var reopened = new StageMessageDocument(game, "", "StageA", "USen", bytes => new FakeArchive(bytes));
            Assert.IsTrue(reopened.TryGetTitle("obj55", out string title)); Assert.AreEqual("A", title);
            reopened = new StageMessageDocument(game, "", "StageB", "USen", bytes => new FakeArchive(bytes));
            Assert.IsTrue(reopened.TryGetTitle("obj55", out title)); Assert.AreEqual("B", title);
        }

        private static void WriteArchive(string root, Dictionary<string, byte[]> files)
        {
            string path = Path.Combine(root, Relative); Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new FakeArchive(files).Write());
        }

        // Deterministic fake container isolates document ownership and file-saving behavior from SARC/Yaz0.
        private sealed class FakeArchive : IMessageArchive
        {
            public IDictionary<string, byte[]> Files { get; }
            public FakeArchive(Dictionary<string, byte[]> files) { Files = files; }
            public FakeArchive(byte[] bytes)
            {
                Files = new Dictionary<string, byte[]>();
                using (var reader = new BinaryReader(new MemoryStream(bytes)))
                {
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++) { string name = reader.ReadString(); Files.Add(name, reader.ReadBytes(reader.ReadInt32())); }
                }
            }
            public byte[] Write()
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(Files.Count);
                    foreach (var file in Files) { writer.Write(file.Key); writer.Write(file.Value.Length); writer.Write(file.Value); }
                    return stream.ToArray();
                }
            }
        }
    }
}
