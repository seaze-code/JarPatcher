using System;
using System.IO;
using System.Text;

namespace JarPatcher.Engine
{
    /// <summary>
    /// Implements the JVM Specification (Section 4.4.7) Modified UTF-8 encoding used in CONSTANT_Utf8 entries.
    /// Null character (U+0000) is encoded in 2 bytes (0xC0, 0x80), and BMP/surrogate characters use 1, 2, or 3 bytes per char.
    /// </summary>
    public static class JvmModifiedUtf8
    {
        public static int GetByteCount(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            int count = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c >= 0x0001 && c <= 0x007F)
                {
                    count += 1;
                }
                else if (c == 0x0000 || (c >= 0x0080 && c <= 0x07FF))
                {
                    count += 2;
                }
                else
                {
                    count += 3;
                }
            }
            return count;
        }

        public static byte[] Encode(string value)
        {
            if (string.IsNullOrEmpty(value)) return new byte[0];
            int byteCount = GetByteCount(value);
            byte[] buffer = new byte[byteCount];
            int pos = 0;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c >= 0x0001 && c <= 0x007F)
                {
                    buffer[pos++] = (byte)c;
                }
                else if (c == 0x0000 || (c >= 0x0080 && c <= 0x07FF))
                {
                    buffer[pos++] = (byte)(0xC0 | ((c >> 6) & 0x1F));
                    buffer[pos++] = (byte)(0x80 | (c & 0x3F));
                }
                else
                {
                    buffer[pos++] = (byte)(0xE0 | ((c >> 12) & 0x0F));
                    buffer[pos++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                    buffer[pos++] = (byte)(0x80 | (c & 0x3F));
                }
            }

            return buffer;
        }

        public static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;

            StringBuilder sb = new StringBuilder(bytes.Length);
            int i = 0;
            int len = bytes.Length;

            while (i < len)
            {
                int b1 = bytes[i] & 0xFF;
                if ((b1 & 0x80) == 0)
                {
                    // 1-byte: 0xxxxxxx
                    sb.Append((char)b1);
                    i += 1;
                }
                else if ((b1 & 0xE0) == 0xC0 && i + 1 < len)
                {
                    // 2-byte: 110xxxxx 10xxxxxx
                    int b2 = bytes[i + 1] & 0xFF;
                    char c = (char)(((b1 & 0x1F) << 6) | (b2 & 0x3F));
                    sb.Append(c);
                    i += 2;
                }
                else if ((b1 & 0xF0) == 0xE0 && i + 2 < len)
                {
                    // 3-byte: 1110xxxx 10xxxxxx 10xxxxxx
                    int b2 = bytes[i + 1] & 0xFF;
                    int b3 = bytes[i + 2] & 0xFF;
                    char c = (char)(((b1 & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F));
                    sb.Append(c);
                    i += 3;
                }
                else
                {
                    // Fallback for non-standard UTF-8 sequences
                    try
                    {
                        return Encoding.UTF8.GetString(bytes);
                    }
                    catch
                    {
                        sb.Append((char)b1);
                        i += 1;
                    }
                }
            }

            return sb.ToString();
        }
    }
}
