using System;
using System.Collections.Generic;
using System.Text;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// Lua bytecode of GTA LCS/VCS (`*.LUA.LC`).
    ///
    /// These are **not** stock luac dumps: the file starts with "\x1bLuaP" and a
    /// header of 18 bytes, the constants are 4-byte-tagged records and numbers are
    /// single precision floats, so unluac / luadec cannot read them. What is known
    /// for sure (see docs/LUA.md):
    ///
    ///   0x00  1b 4c 75 61 50            "\x1bLuaP"
    ///   0x05  01 04 04 04 06 08 09 09 04  sizes / flags of the build
    ///   0x0E  3b af ef 4b               float test number (4 bytes, not 8)
    ///   0x12  string  source            "(none)" for chunks built without a name
    ///   ...   prototype header, then a contiguous run of constants, then code
    ///
    ///   constant record: tag(1) + payload
    ///     0x00 nil         0x01 boolean (+1 byte)
    ///     0x03 number (+4 bytes float)      0x04 string (+u32 length incl. NUL)
    ///
    /// Code words are 4 bytes with the opcode in the low 6 bits (Lua 5.0 order):
    /// in CUTSCENEPLAYER.LUA.LC the last instruction is 0x0000801b = RETURN (27).
    ///
    /// Until the whole prototype layout is decoded, the kit only extracts what is
    /// provably right: the constant strings and numbers. That is already enough to
    /// find globals, printed texts, timings and coordinates in a script.
    /// </summary>
    public static class LuaChunk
    {
        public sealed class Constant
        {
            public int Offset;
            public bool IsString;
            public string Text;
            public double Number;
        }

        public sealed class Info
        {
            public bool IsLua;
            public string Header = "";
            public int ConstantStrings;
            public int ConstantNumbers;
            public List<Constant> Constants = new List<Constant>();
        }

        public static bool LooksLike(byte[] data)
        {
            return data != null && data.Length >= 6 &&
                   data[0] == 0x1b && data[1] == 0x4c && data[2] == 0x75 && data[3] == 0x61;
        }

        public static string Describe(byte[] data)
        {
            if (!LooksLike(data) || data.Length < 18) return "not a Lua chunk";
            // The size bytes of this build do not match any stock layout (0x06 where
            // sizeof(lua_Number) would be), and the constants prove numbers are 4-byte
            // floats, so only the version is reported as fact.
            return "LuaP bytecode (Lua " + (data[4] >> 4) + "." + (data[4] & 0x0f) +
                   " tags, 4-byte numbers, 18-byte header)";
        }

        /// <summary>
        /// Constant strings and numbers, in file order.
        ///
        /// A record is only accepted when its length, NUL terminator and printable
        /// content all agree; numbers and booleans are accepted only directly after
        /// an accepted record (inside the constant table), never from random code
        /// bytes. That keeps the output free of invented strings.
        /// </summary>
        public static Info Read(byte[] data)
        {
            var info = new Info { IsLua = LooksLike(data) };
            if (!info.IsLua) return info;
            info.Header = Describe(data);

            int pos = 0;
            int lastEnd = -1;
            while (pos < data.Length)
            {
                byte tag = data[pos];
                if (tag == 0x04 && pos + 5 <= data.Length)
                {
                    int size = (int)Util.U32(data, pos + 1);
                    if (size >= 1 && size <= 4096 && pos + 5 + size <= data.Length)
                    {
                        int start = pos + 5;
                        bool terminated = data[start + size - 1] == 0;
                        bool printable = true;
                        for (int i = 0; i < size - 1; i++)
                        {
                            byte b = data[start + i];
                            if (b < 32 || b > 126) { printable = false; break; }
                        }
                        if (terminated && printable && size > 1)
                        {
                            info.Constants.Add(new Constant
                            {
                                Offset = pos,
                                IsString = true,
                                Text = Encoding.ASCII.GetString(data, start, size - 1)
                            });
                            info.ConstantStrings++;
                            pos += 5 + size;
                            lastEnd = pos;
                            continue;
                        }
                    }
                }
                else if (tag == 0x03 && pos + 5 <= data.Length && pos == lastEnd)
                {
                    float value = BitConverter.ToSingle(data, pos + 1);
                    // Game constants are ordinary numbers; denormals and zeros are what
                    // random code bytes look like when read as a float, so they are
                    // dropped. Numbers are best effort - strings are the exact part.
                    float magnitude = Math.Abs(value);
                    if (magnitude >= 1e-4f && magnitude <= 1e9f)
                    {
                        info.Constants.Add(new Constant
                        {
                            Offset = pos,
                            Number = Math.Round(value, 4)
                        });
                        info.ConstantNumbers++;
                        pos += 5;
                        lastEnd = pos;
                        continue;
                    }
                }
                else if (tag == 0x01 && pos + 2 <= data.Length && pos == lastEnd)
                {
                    pos += 2;
                    lastEnd = pos;
                    continue;
                }
                pos++;
            }
            return info;
        }
    }
}