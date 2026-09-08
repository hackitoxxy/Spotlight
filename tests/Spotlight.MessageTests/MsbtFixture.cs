using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Spotlight.MessageTests
{
    // Independent fixture encoder: deliberately does not use MsbtFile.Write to construct its inputs.
    internal static class MsbtFixture
    {
        public static byte[] Create(bool big = false, bool utf8 = false, bool attributes = true, bool unknown = false, bool tags = false, bool variableAttributes = false)
        {
            Encoding encoding = utf8 ? (Encoding)new UTF8Encoding(false, true) : new UnicodeEncoding(big, false, true);
            byte[][] messages = { encoding.GetBytes("Old moon\0"), encoding.GetBytes(tags ? "\u000e\u0001\u0002\0" : "Other message\0") };
            var sections = new List<KeyValuePair<string, byte[]>>();
            using (var labels = new MemoryStream())
            {
                // Both labels in one bucket: a valid hash table with divisor 1.
                Put(labels, 1, big); Put(labels, 2, big); Put(labels, 12, big);
                int i = 0;
                foreach (string label in new[] { "ScenarioName_obj55", "Talk1_obj7" })
                {
                    byte[] name = Encoding.ASCII.GetBytes(label);
                    labels.WriteByte((byte)name.Length); labels.Write(name, 0, name.Length); Put(labels, i++, big);
                }
                sections.Add(new KeyValuePair<string, byte[]>("LBL1", labels.ToArray()));
            }
            if (attributes)
            {
                using (var attr = new MemoryStream())
                {
                    Put(attr, 2, big); Put(attr, 2, big);
                    attr.Write(new byte[] { 0x12, 0x34, 0x56, 0x78 }, 0, 4);
                    if (variableAttributes) attr.WriteByte(0x99);
                    sections.Add(new KeyValuePair<string, byte[]>("ATR1", attr.ToArray()));
                }
                using (var style = new MemoryStream())
                {
                    Put(style, 7, big); Put(style, 9, big);
                    sections.Add(new KeyValuePair<string, byte[]>("TSY1", style.ToArray()));
                }
            }
            if (unknown) sections.Add(new KeyValuePair<string, byte[]>("ZZZ1", new byte[] { 1, 3, 5, 7, 9 }));
            using (var text = new MemoryStream())
            {
                Put(text, 2, big); Put(text, 12, big); Put(text, 12 + messages[0].Length, big);
                foreach (byte[] message in messages) text.Write(message, 0, message.Length);
                sections.Add(new KeyValuePair<string, byte[]>("TXT2", text.ToArray()));
            }
            return Assemble(sections, big, utf8);
        }

        public static byte[] Assemble(IEnumerable<KeyValuePair<string, byte[]>> sections, bool big, bool utf8)
        {
            using (var file = new MemoryStream())
            {
                byte[] header = new byte[32];
                Encoding.ASCII.GetBytes("MsgStdBn").CopyTo(header, 0);
                header[8] = big ? (byte)0xfe : (byte)0xff; header[9] = big ? (byte)0xff : (byte)0xfe;
                header[12] = utf8 ? (byte)0 : (byte)1; header[13] = 3;
                header[big ? 15 : 14] = (byte)sections.Count();
                header[31] = 0x42; // Reserved bytes must survive editing.
                file.Write(header, 0, 32);
                foreach (var section in sections)
                {
                    byte[] name = Encoding.ASCII.GetBytes(section.Key);
                    file.Write(name, 0, 4); Put(file, section.Value.Length, big);
                    file.Write(new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 }, 0, 8);
                    file.Write(section.Value, 0, section.Value.Length);
                    while (file.Length % 16 != 0) file.WriteByte(0xab);
                }
                file.Position = 18; Put(file, (int)file.Length, big);
                return file.ToArray();
            }
        }

        public static Dictionary<string, byte[]> Sections(byte[] data, bool big = false)
        {
            var result = new Dictionary<string, byte[]>();
            int pos = 32;
            while (pos < data.Length)
            {
                int size = Read32(data, pos + 4, big);
                result.Add(Encoding.ASCII.GetString(data, pos, 4), data.Skip(pos + 16).Take(size).ToArray());
                pos = (pos + 16 + size + 15) / 16 * 16;
            }
            return result;
        }
        public static int Read32(byte[] data, int offset, bool big = false)
        {
            byte[] bytes = data.Skip(offset).Take(4).ToArray();
            if (big) Array.Reverse(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }
        public static void Put(Stream stream, int value, bool big)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (big) Array.Reverse(bytes);
            stream.Write(bytes, 0, 4);
        }
    }
}
