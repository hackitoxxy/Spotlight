using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Spotlight.FileFormats
{
    /// <summary>
    /// Nintendo Message Studio Binary Text (MSBT), implemented without an editor dependency.
    ///
    /// GAME USE: MSBT stores localized messages, not stage objects. In Odyssey the archive
    /// LocalizedData/{language}/MessageData/StageMessage.szs contains {StageName}.msbt.
    /// A moon title is the label "ScenarioName_" + the producing object's Id (e.g. obj55).
    /// The literal prefix is not the current scenario number. NPC messages use other labels.
    /// Editors unpack Yaz0/SARC first, edit this file, then replace its archive member.
    /// UnitConfig.DisplayName in stage BYML is a separate value.
    ///
    /// ORGANIZATION: a 32-byte MsgStdBn header describes byte order, text encoding, version,
    /// section count and file size. Sections have 16-byte headers, byte-sized payload lengths,
    /// and 16-byte alignment. LBL1 is a hash-bucket table mapping ASCII labels to TXT2 indexes;
    /// TXT2 contains a count, relative byte offsets, and null-terminated encoded messages.
    /// ATR1 has a count, fixed record width, and per-message attributes (some variants also
    /// contain variable data). TSY1 contains one 32-bit style index per message. Text offsets
    /// and lengths are bytes, not .NET character counts. Both endian orders and UTF-8/UTF-16
    /// are supported; other encodings are rejected explicitly.
    ///
    /// PRESERVATION: untouched messages, unknown sections, reserved header bytes and section
    /// order are retained. A no-edit round trip returns the original bytes exactly. Existing
    /// tagged messages remain intact, but this plain-title API refuses to replace them: MSBT
    /// control tags carry binary parameters which must not be decoded as ordinary Unicode.
    /// Adding a message clones attributes/style from a supplied title template. Variable-size
    /// attributes and unknown index-bearing sections are deliberately rejected on insertion.
    ///
    /// USAGE: var file = MsbtFile.Read(bytes); file.SetPlainText("ScenarioName_obj55", "A New Moon");
    /// byte[] replacement = file.Write(); File I/O, archive ownership, undo and atomic saves
    /// belong to the caller. Invalid data throws InvalidDataException; unsupported editing
    /// operations throw NotSupportedException. This file contains no game assets.
    /// </summary>
    public sealed class MsbtFile
    {
        private sealed class Section
        {
            public string Name;
            public byte[] Header;
            public byte[] Data;
            public byte[] Padding;
        }

        private readonly List<Section> sections = new List<Section>();
        private readonly Dictionary<string, int> labels = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<byte[]> messages = new List<byte[]>();
        private byte[] original;
        private byte[] header;
        private Encoding encoding;
        private int unitSize;
        private int bucketCount;
        private bool changed;
        public bool BigEndian { get; private set; }
        public IEnumerable<string> Labels => labels.Keys;
        public int Count => messages.Count;

        public static MsbtFile Read(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var file = new MsbtFile();
            file.Parse(bytes);
            return file;
        }

        private void Parse(byte[] bytes)
        {
            Require(bytes.Length >= 32 && Encoding.ASCII.GetString(bytes, 0, 8) == "MsgStdBn", "Invalid MSBT header.");
            Require((bytes[8] == 0xfe && bytes[9] == 0xff) || (bytes[8] == 0xff && bytes[9] == 0xfe), "Invalid byte-order mark.");
            BigEndian = bytes[8] == 0xfe;
            Require(bytes[13] == 3, "Only MSBT version 3 is supported.");
            if (bytes[12] > 1) throw new NotSupportedException("Only UTF-8 and UTF-16 MSBT text is supported.");
            unitSize = bytes[12] == 0 ? 1 : 2;
            encoding = unitSize == 1 ? (Encoding)new UTF8Encoding(false, true) : new UnicodeEncoding(BigEndian, false, true);
            Require(U32(bytes, 18) == bytes.Length, "MSBT file size does not match its header.");
            original = (byte[])bytes.Clone();
            header = Slice(bytes, 0, 32);
            int pos = 32;
            int sectionCount = U16(bytes, 14);
            for (int i = 0; i < sectionCount; i++)
            {
                Require(pos <= bytes.Length - 16, "Truncated section header.");
                string name = Encoding.ASCII.GetString(bytes, pos, 4);
                Require(!sections.Any(s => s.Name == name), "Duplicate MSBT section: " + name);
                int size = Int32(bytes, pos + 4);
                int start = pos + 16;
                Require(size <= bytes.Length - start, "Truncated section: " + name);
                int end = start + size;
                int padding = (16 - end % 16) % 16;
                Require(padding <= bytes.Length - end, "Truncated section padding.");
                sections.Add(new Section { Name = name, Header = Slice(bytes, pos, 16), Data = Slice(bytes, start, size), Padding = Slice(bytes, end, padding) });
                pos = end + padding;
            }
            Require(pos == bytes.Length, "Unexpected data after MSBT sections.");
            var text = Find("TXT2");
            var label = Find("LBL1");
            Require(text != null && label != null, "MSBT must contain TXT2 and LBL1 sections.");
            ReadMessages(text.Data);
            ReadLabels(label.Data);
        }

        private void ReadMessages(byte[] data)
        {
            int count = Int32(data, 0);
            Require(count <= (data.Length - 4) / 4, "Truncated TXT2 offset table.");
            int tableEnd = 4 + count * 4;
            for (int i = 0; i < count; i++)
            {
                int start = Int32(data, 4 + i * 4);
                int end = i + 1 == count ? data.Length : Int32(data, 8 + i * 4);
                Require(start >= tableEnd && end >= start && end <= data.Length && (end - start) % unitSize == 0, "Invalid TXT2 message offsets.");
                Require(end - start >= unitSize, "Missing message terminator.");
                for (int j = 1; j <= unitSize; j++) Require(data[end - j] == 0, "Missing message terminator.");
                messages.Add(Slice(data, start, end - start));
            }
        }

        private void ReadLabels(byte[] data)
        {
            bucketCount = Int32(data, 0);
            Require(bucketCount > 0 && bucketCount <= (data.Length - 4) / 8, "Invalid LBL1 bucket count.");
            int tableEnd = 4 + bucketCount * 8;
            for (int i = 0; i < bucketCount; i++)
            {
                int count = Int32(data, 4 + i * 8);
                int pos = Int32(data, 8 + i * 8);
                Require(pos >= tableEnd && pos <= data.Length && count <= (data.Length - pos) / 5, "Invalid LBL1 bucket.");
                for (int j = 0; j < count; j++)
                {
                    Require(pos < data.Length, "Truncated label.");
                    int length = data[pos++];
                    Require(length > 0 && length <= data.Length - pos - 4, "Invalid label length.");
                    for (int k = 0; k < length; k++) Require(data[pos + k] >= 32 && data[pos + k] < 127, "Labels must be printable ASCII.");
                    string name = Encoding.ASCII.GetString(data, pos, length);
                    pos += length;
                    int index = Int32(data, pos);
                    pos += 4;
                    Require(index < messages.Count && !labels.ContainsKey(name), "Invalid or duplicate label: " + name);
                    labels.Add(name, index);
                }
            }
        }

        /// <summary>Returns false for a missing label. Tagged/binary messages cannot be edited as plain titles.</summary>
        public bool TryGetPlainText(string label, out string text)
        {
            text = null;
            if (!labels.TryGetValue(label, out int index)) return false;
            byte[] data = messages[index];
            // Tag markers are encoded 0x000e/0x000f (or single bytes in UTF-8).
            // Scan before decoding because their parameters need not be valid Unicode.
            for (int i = 0; i < data.Length - unitSize; i += unitSize)
            {
                int code = unitSize == 1 ? data[i] : U16(data, i);
                if (code == 0x0e || code == 0x0f || code == 0)
                    throw new NotSupportedException("This message contains formatting/control codes; use a full message editor to edit it.");
            }
            try { text = encoding.GetString(data, 0, data.Length - unitSize); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid message encoding.", ex); }
            return true;
        }

        /// <summary>Replace a plain message or append a new one, optionally cloning another label's metadata.</summary>
        public void SetPlainText(string label, string text, string templateLabel = null)
        {
            if (string.IsNullOrEmpty(label) || label.Length > 255 || label.Any(c => c < 32 || c >= 127))
                throw new ArgumentException("Labels must contain 1-255 printable ASCII characters.", nameof(label));
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (text.Any(c => c == '\0' || c == '\u000e' || c == '\u000f'))
                throw new ArgumentException("Plain titles cannot contain nulls or MSBT tag markers.", nameof(text));
            byte[] encoded = encoding.GetBytes(text + "\0");
            if (labels.TryGetValue(label, out int index))
            {
                TryGetPlainText(label, out string oldText);
                if (oldText == text) return;
                // Aliases share the same message index in the file: preserve that relationship.
                messages[index] = encoded;
            }
            else
            {
                int template = -1;
                if (templateLabel != null && !labels.TryGetValue(templateLabel, out template))
                    throw new ArgumentException("The metadata template does not exist.", nameof(templateLabel));
                // Prepare all metadata first so unsupported insertion leaves this instance untouched.
                var replacements = PrepareInsertion(template);
                foreach (var replacement in replacements) replacement.Key.Data = replacement.Value;
                labels.Add(label, messages.Count);
                messages.Add(encoded);
            }
            changed = true;
        }

        private Dictionary<Section, byte[]> PrepareInsertion(int template)
        {
            var result = new Dictionary<Section, byte[]>();
            foreach (var section in sections)
            {
                byte[] data = section.Data;
                if (section.Name == "LBL1" || section.Name == "TXT2") continue;
                if (section.Name == "ATR1")
                {
                    int count = Int32(data, 0), width = Int32(data, 4);
                    Require(count == messages.Count, "ATR1 count does not match TXT2.");
                    if (8L + (long)count * width != data.Length)
                        throw new NotSupportedException("Cannot add messages with variable-size ATR1 attributes.");
                    if (width > 0 && template < 0)
                        throw new NotSupportedException("A moon title template is needed to copy message attributes.");
                    var expanded = new byte[checked(data.Length + width)];
                    Buffer.BlockCopy(data, 0, expanded, 0, data.Length);
                    Put32(expanded, 0, (uint)(count + 1));
                    if (width > 0) Buffer.BlockCopy(data, 8 + template * width, expanded, data.Length, width);
                    result.Add(section, expanded);
                }
                else if (section.Name == "TSY1")
                {
                    Require(data.Length == (long)messages.Count * 4, "Invalid TSY1 style table.");
                    if (template < 0) throw new NotSupportedException("A moon title template is needed to copy message style.");
                    var expanded = new byte[checked(data.Length + 4)];
                    Buffer.BlockCopy(data, 0, expanded, 0, data.Length);
                    Buffer.BlockCopy(data, template * 4, expanded, data.Length, 4);
                    result.Add(section, expanded);
                }
                else throw new NotSupportedException("Cannot add messages while preserving the unknown indexing rules of " + section.Name + ".");
            }
            return result;
        }

        public byte[] Write()
        {
            if (!changed) return (byte[])original.Clone();
            using (var stream = new MemoryStream())
            {
                stream.Write(header, 0, header.Length);
                foreach (var section in sections)
                {
                    byte[] data = section.Name == "LBL1" ? WriteLabels() : section.Name == "TXT2" ? WriteMessages() : section.Data;
                    byte[] sectionHeader = (byte[])section.Header.Clone();
                    Put32(sectionHeader, 4, (uint)data.Length);
                    stream.Write(sectionHeader, 0, 16);
                    stream.Write(data, 0, data.Length);
                    int padding = (int)((16 - stream.Position % 16) % 16);
                    for (int i = 0; i < padding; i++)
                        stream.WriteByte(i < section.Padding.Length ? section.Padding[i] : (byte)0xab);
                }
                byte[] result = stream.ToArray();
                Put32(result, 18, (uint)result.Length);
                return result;
            }
        }

        private byte[] WriteMessages()
        {
            using (var stream = new MemoryStream())
            {
                Write32(stream, (uint)messages.Count);
                uint offset = checked(4u + (uint)messages.Count * 4u);
                foreach (byte[] message in messages)
                {
                    Write32(stream, offset);
                    offset = checked(offset + (uint)message.Length);
                }
                foreach (byte[] message in messages) stream.Write(message, 0, message.Length);
                return stream.ToArray();
            }
        }

        private byte[] WriteLabels()
        {
            var groups = new List<KeyValuePair<string, int>>[bucketCount];
            for (int i = 0; i < bucketCount; i++) groups[i] = new List<KeyValuePair<string, int>>();
            foreach (var label in labels)
            {
                uint hash = 0;
                foreach (char c in label.Key) hash = unchecked(hash * 0x492 + c);
                groups[hash % (uint)bucketCount].Add(label);
            }
            using (var stream = new MemoryStream())
            {
                Write32(stream, (uint)bucketCount);
                uint offset = checked(4u + (uint)bucketCount * 8u);
                foreach (var group in groups)
                {
                    Write32(stream, (uint)group.Count);
                    Write32(stream, offset);
                    foreach (var label in group) offset = checked(offset + (uint)label.Key.Length + 5u);
                }
                foreach (var group in groups)
                    foreach (var label in group)
                    {
                        byte[] name = Encoding.ASCII.GetBytes(label.Key);
                        stream.WriteByte((byte)name.Length);
                        stream.Write(name, 0, name.Length);
                        Write32(stream, (uint)label.Value);
                    }
                return stream.ToArray();
            }
        }

        private Section Find(string name) => sections.FirstOrDefault(s => s.Name == name);
        private int U16(byte[] data, int pos)
        {
            Require(pos >= 0 && pos <= data.Length - 2, "Truncated integer.");
            return BigEndian ? (data[pos] << 8) | data[pos + 1] : data[pos] | (data[pos + 1] << 8);
        }
        private uint U32(byte[] data, int pos)
        {
            Require(pos >= 0 && pos <= data.Length - 4, "Truncated integer.");
            return BigEndian ? ((uint)U16(data, pos) << 16) | (uint)U16(data, pos + 2) : (uint)U16(data, pos) | ((uint)U16(data, pos + 2) << 16);
        }
        private int Int32(byte[] data, int pos)
        {
            uint value = U32(data, pos);
            Require(value <= int.MaxValue, "MSBT count or offset is too large.");
            return (int)value;
        }
        private void Put32(byte[] data, int pos, uint value)
        {
            for (int i = 0; i < 4; i++) data[pos + i] = (byte)(value >> (BigEndian ? 24 - i * 8 : i * 8));
        }
        private void Write32(Stream stream, uint value)
        {
            var bytes = new byte[4];
            Put32(bytes, 0, value);
            stream.Write(bytes, 0, 4);
        }
        private static byte[] Slice(byte[] bytes, int start, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(bytes, start, result, 0, count);
            return result;
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
    }
}
