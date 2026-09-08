using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Spotlight.Storage;

namespace Spotlight.MessageTests
{
    [TestClass]
    public class ArchiveChangeTrackerTests
    {
        private string directory, map, design;
        private ArchiveChangeTracker tracker;

        [TestInitialize]
        public void Setup()
        {
            directory = Path.Combine(Path.GetTempPath(), "Spotlight.ArchiveTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            map = Path.Combine(directory, "WaterfallWorldHomeStageMap.szs");
            design = Path.Combine(directory, "WaterfallWorldHomeStageDesign.szs");
            File.WriteAllBytes(map, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(design, new byte[] { 4, 5, 6 });
            tracker = new ArchiveChangeTracker();
            tracker.ReadFile(map); tracker.ReadFile(design);
        }

        [TestCleanup]
        public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        [TestMethod]
        public void OwnSaveNeverLooksExternalEvenWithFutureTimestamp()
        {
            tracker.RunSave(() => { tracker.WriteFile(map, new byte[] { 9, 8, 7 }); return true; });
            File.SetLastWriteTimeUtc(map, DateTime.UtcNow.AddHours(1));
            Assert.IsFalse(tracker.TryReadExternalChange(map, out _));
        }

        [TestMethod]
        public void ExternalEditIsDetectedEvenWithSameTimestampAndLength()
        {
            DateTime timestamp = File.GetLastWriteTimeUtc(map);
            File.WriteAllBytes(map, new byte[] { 9, 8, 7 });
            File.SetLastWriteTimeUtc(map, timestamp);
            Assert.IsTrue(tracker.TryReadExternalChange(map, out byte[] changed));
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, changed);
        }

        [TestMethod]
        public void LaterMessageSaveFailureDoesNotMisclassifySuccessfulStageWrite()
        {
            Assert.IsFalse(tracker.RunSave(() =>
            {
                tracker.WriteFile(map, new byte[] { 9 });
                tracker.RunCheck(() => Assert.Fail("Checked files during Save."));
                return false;
            }));
            Assert.IsFalse(tracker.TryReadExternalChange(map, out _));
            File.WriteAllBytes(map, new byte[] { 10 });
            Assert.IsTrue(tracker.TryReadExternalChange(map, out _));
        }

        [TestMethod]
        public void WritingMapDoesNotAcknowledgeExternalDesignEdit()
        {
            File.WriteAllBytes(design, new byte[] { 7 });
            tracker.WriteFile(map, new byte[] { 8 });
            Assert.IsFalse(tracker.TryReadExternalChange(map, out _));
            Assert.IsTrue(tracker.TryReadExternalChange(design, out _));
        }

        [TestMethod]
        public void ReloadDialogActivationDoesNotReenterChecks()
        {
            int checks = 0;
            tracker.RunCheck(() =>
            {
                checks++;
                tracker.RunCheck(() => checks++);
            });
            Assert.AreEqual(1, checks);
            tracker.RunCheck(() => checks++);
            Assert.AreEqual(2, checks);
        }

        [TestMethod]
        public void DecliningOneVersionDoesNotHideLaterEditDuringDialog()
        {
            File.WriteAllBytes(map, new byte[] { 8 });
            Assert.IsTrue(tracker.TryReadExternalChange(map, out byte[] snapshot));
            File.WriteAllBytes(map, new byte[] { 9 });
            tracker.Acknowledge(map, snapshot);
            Assert.IsTrue(tracker.TryReadExternalChange(map, out byte[] later));
            tracker.Acknowledge(map, later);
            Assert.IsFalse(tracker.TryReadExternalChange(map, out _));
        }

        [TestMethod]
        public void ExceptionAlwaysReenablesChecks()
        {
            Assert.ThrowsExactly<IOException>(() => tracker.RunSave(() => { throw new IOException("Save failed"); }));
            File.WriteAllBytes(map, new byte[] { 8 });
            Assert.IsTrue(tracker.TryReadExternalChange(map, out _));
            Assert.ThrowsExactly<IOException>(() => tracker.RunCheck(() => { throw new IOException("Reload failed"); }));
            bool checkedAgain = false;
            tracker.RunCheck(() => checkedAgain = true);
            Assert.IsTrue(checkedAgain);
        }

        [TestMethod]
        public void SaveAsTracksTheDestinationImmediately()
        {
            string destination = Path.Combine(directory, "RenamedMap.szs");
            tracker.WriteFile(destination, new byte[] { 8 });
            Assert.IsFalse(tracker.TryReadExternalChange(destination, out _));
            File.WriteAllBytes(destination, new byte[] { 9 });
            Assert.IsTrue(tracker.TryReadExternalChange(destination, out _));
        }

        [TestMethod]
        public void LockedFileIsRetriedWithoutAcknowledgingChanges()
        {
            File.WriteAllBytes(map, new byte[] { 9 });
            using (File.Open(map, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.IsFalse(tracker.TryReadExternalChange(map, out _));
            Assert.IsTrue(tracker.TryReadExternalChange(map, out _));
        }

        [TestMethod]
        public void LockedStageSaveNamesFilePreservesBytesAndAllowsRetry()
        {
            byte[] original = File.ReadAllBytes(map);
            using (File.Open(map, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Assert.ThrowsExactly<IOException>(() => tracker.WriteFile(map, new byte[] { 9 }));
                StringAssert.Contains(error.Message, map);
                Assert.IsInstanceOfType<IOException>(error.InnerException);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(map));
            }
            tracker.WriteFile(map, new byte[] { 9 });
            Assert.IsFalse(tracker.TryReadExternalChange(map, out _));
        }
    }
}
