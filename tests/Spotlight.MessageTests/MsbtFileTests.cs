using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Spotlight.FileFormats;

namespace Spotlight.MessageTests
{
    [TestClass]
    public class MsbtFileTests
    {
        [TestMethod]
        [DataRow(false, false)] [DataRow(true, false)] [DataRow(false, true)] [DataRow(true, true)]
        public void NoEditRoundTripIsByteIdentical(bool big, bool utf8)
        {
            byte[] input = MsbtFixture.Create(big, utf8, unknown: true, tags: true);
            CollectionAssert.AreEqual(input, MsbtFile.Read(input).Write());
        }

        [TestMethod]
        [DataRow(false, false)] [DataRow(true, false)] [DataRow(false, true)] [DataRow(true, true)]
        public void EditUnicodePreservesOtherMessagesAndMetadata(bool big, bool utf8)
        {
            byte[] input = MsbtFixture.Create(big, utf8, unknown: true, tags: true);
            var file = MsbtFile.Read(input);
            file.SetPlainText("ScenarioName_obj55", "Lune café 月 🌙 — a much longer title");
            byte[] output = file.Write();
            var reread = MsbtFile.Read(output);
            Assert.IsTrue(reread.TryGetPlainText("ScenarioName_obj55", out string title));
            Assert.AreEqual("Lune café 月 🌙 — a much longer title", title);
            var before = MsbtFixture.Sections(input, big); var after = MsbtFixture.Sections(output, big);
            foreach (string name in new[] { "ATR1", "TSY1", "ZZZ1" }) CollectionAssert.AreEqual(before[name], after[name]);
            int oldOffset = MsbtFixture.Read32(before["TXT2"], 8, big), newOffset = MsbtFixture.Read32(after["TXT2"], 8, big);
            CollectionAssert.AreEqual(before["TXT2"].Skip(oldOffset).ToArray(), after["TXT2"].Skip(newOffset).ToArray());
            Assert.AreEqual(0x42, output[31]);
        }

        [TestMethod]
        [DataRow(false, false)] [DataRow(true, false)] [DataRow(false, true)] [DataRow(true, true)]
        public void InsertClonesAttributesAndStyleAndProducesValidOffsets(bool big, bool utf8)
        {
            var file = MsbtFile.Read(MsbtFixture.Create(big, utf8));
            file.SetPlainText("ScenarioName_obj999", "New moon 🌙", "ScenarioName_obj55");
            byte[] output = file.Write();
            var reread = MsbtFile.Read(output);
            Assert.AreEqual(3, reread.Count);
            Assert.IsTrue(reread.TryGetPlainText("ScenarioName_obj999", out string text));
            Assert.AreEqual("New moon 🌙", text);
            var sections = MsbtFixture.Sections(output, big);
            Assert.AreEqual(3, MsbtFixture.Read32(sections["ATR1"], 0, big));
            CollectionAssert.AreEqual(new byte[] { 0x12, 0x34 }, sections["ATR1"].Skip(12).ToArray());
            Assert.AreEqual(7, MsbtFixture.Read32(sections["TSY1"], 8, big));
            Assert.AreEqual(output.Length, MsbtFixture.Read32(output, 18, big));
        }

        [TestMethod]
        public void RebuildsMultipleHashBuckets()
        {
            var parts = MsbtFixture.Sections(MsbtFixture.Create());
            byte[] old = parts["LBL1"], expanded = new byte[old.Length + 16];
            // Move the existing records into bucket 0; the writer must rehash into all three buckets.
            Buffer.BlockCopy(old, 12, expanded, 28, old.Length - 12);
            using (var stream = new MemoryStream(expanded))
            {
                MsbtFixture.Put(stream, 3, false); MsbtFixture.Put(stream, 2, false); MsbtFixture.Put(stream, 28, false);
                MsbtFixture.Put(stream, 0, false); MsbtFixture.Put(stream, expanded.Length, false);
                MsbtFixture.Put(stream, 0, false); MsbtFixture.Put(stream, expanded.Length, false);
            }
            parts["LBL1"] = expanded;
            var file = MsbtFile.Read(MsbtFixture.Assemble(parts, false, false));
            file.SetPlainText("ScenarioName_obj888", "New", "ScenarioName_obj55");
            var result = MsbtFixture.Sections(file.Write())["LBL1"];
            for (int bucket = 0; bucket < 3; bucket++)
            {
                int count = MsbtFixture.Read32(result, 4 + bucket * 8), pos = MsbtFixture.Read32(result, 8 + bucket * 8);
                for (int i = 0; i < count; i++)
                {
                    int length = result[pos++]; uint hash = 0;
                    for (int j = 0; j < length; j++) hash = unchecked(hash * 1170 + result[pos++]);
                    Assert.AreEqual((uint)bucket, hash % 3); pos += 4;
                }
            }
        }

        [TestMethod]
        public void RejectsTaggedTitleReplacementWithoutChangingBytes()
        {
            byte[] input = MsbtFixture.Create(tags: true); var file = MsbtFile.Read(input);
            Assert.ThrowsExactly<NotSupportedException>(() => file.SetPlainText("Talk1_obj7", "Replacement"));
            CollectionAssert.AreEqual(input, file.Write());
        }

        [TestMethod]
        public void UnsupportedInsertionLeavesDocumentUntouched()
        {
            foreach (byte[] input in new[] { MsbtFixture.Create(unknown: true), MsbtFixture.Create(variableAttributes: true) })
            {
                var file = MsbtFile.Read(input);
                Assert.ThrowsExactly<NotSupportedException>(() => file.SetPlainText("new", "New", "ScenarioName_obj55"));
                CollectionAssert.AreEqual(input, file.Write());
            }
        }

        [TestMethod]
        public void PlainNoOpAndEmptyTitleRoundTrip()
        {
            byte[] input = MsbtFixture.Create(); var file = MsbtFile.Read(input);
            file.SetPlainText("ScenarioName_obj55", "Old moon"); CollectionAssert.AreEqual(input, file.Write());
            file.SetPlainText("ScenarioName_obj55", "");
            Assert.IsTrue(MsbtFile.Read(file.Write()).TryGetPlainText("ScenarioName_obj55", out string text));
            Assert.AreEqual("", text);
        }

        [TestMethod]
        public void RejectsTruncationAndInvalidOffsets()
        {
            byte[] input = MsbtFixture.Create();
            for (int i = 0; i < input.Length; i++)
            {
                byte[] truncated = input.Take(i).ToArray();
                Assert.ThrowsExactly<InvalidDataException>(() => MsbtFile.Read(truncated));
            }
            var parts = MsbtFixture.Sections(input);
            parts["TXT2"][4] = 0xff; parts["TXT2"][5] = 0xff;
            Assert.ThrowsExactly<InvalidDataException>(() => MsbtFile.Read(MsbtFixture.Assemble(parts, false, false)));
        }

        [TestMethod]
        public void RejectsInvalidLabelsAndControlCharacters()
        {
            var file = MsbtFile.Read(MsbtFixture.Create());
            Assert.ThrowsExactly<ArgumentException>(() => file.SetPlainText("", "Name"));
            Assert.ThrowsExactly<ArgumentException>(() => file.SetPlainText(new string('x', 256), "Name"));
            Assert.ThrowsExactly<ArgumentException>(() => file.SetPlainText("label", "text\0text"));
            Assert.ThrowsExactly<ArgumentException>(() => file.SetPlainText("label", "text\u000e"));
        }

        [TestMethod]
        public void ZeroWidthAttributesPermitInsertionWithoutTemplate()
        {
            var parts = MsbtFixture.Sections(MsbtFixture.Create(attributes: false));
            parts["ATR1"] = new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 };
            var file = MsbtFile.Read(MsbtFixture.Assemble(parts, false, false));
            file.SetPlainText("ScenarioName_obj100", "New title");
            var output = file.Write();
            Assert.AreEqual(3, MsbtFixture.Read32(MsbtFixture.Sections(output)["ATR1"], 0));
            Assert.IsTrue(MsbtFile.Read(output).TryGetPlainText("ScenarioName_obj100", out string text));
            Assert.AreEqual("New title", text);
        }

        [TestMethod]
        public void MissingTemplateRejectsMetadataInsertionWithoutMutation()
        {
            var bytes = MsbtFixture.Create(); var file = MsbtFile.Read(bytes);
            Assert.ThrowsExactly<NotSupportedException>(() => file.SetPlainText("new", "New"));
            CollectionAssert.AreEqual(bytes, file.Write());
        }

        [TestMethod]
        public void RejectsUnsupportedEncodingAndInvalidByteOrder()
        {
            byte[] bytes = MsbtFixture.Create(); bytes[12] = 2;
            Assert.ThrowsExactly<NotSupportedException>(() => MsbtFile.Read(bytes));
            bytes = MsbtFixture.Create(); bytes[8] = 0;
            Assert.ThrowsExactly<InvalidDataException>(() => MsbtFile.Read(bytes));
        }
    }
}
