using System;
using System.IO;
using System.Text;

namespace LaunchpadStudio
{
    /// <summary>
    /// Reads just enough of a file's header to explain why Unity refused it.
    /// FMOD only ever says "Unsupported file or audio format"; this says which.
    /// </summary>
    public static class AudioFileProbe
    {
        public struct Result
        {
            public bool   Playable;
            public string Summary;
            public override string ToString() => Summary;
        }

        public static Result Inspect(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 16) return Fail("file is too small to be audio");

                    var magic = br.ReadBytes(4);
                    string tag = Ascii(magic);

                    if (tag == "OggS") return Ok("Ogg container");
                    if (tag == "FORM") return Ok("AIFF container");

                    bool mp3Header = (magic[0] == 'I' && magic[1] == 'D' && magic[2] == '3') ||
                                     (magic[0] == 0xFF && (magic[1] & 0xE0) == 0xE0);

                    if (mp3Header)
                        return path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                            ? Fail("it has an MP3 header but would not decode — the file may " +
                                   "be truncated, or MPEG audio other than Layer III")
                            : Fail("this is an MP3 despite the extension — rename it to .mp3");
/*                    
                    if (magic[0] == 'I' && magic[1] == 'D' && magic[2] == '3')
                        return Fail("this is an MP3 with an ID3 tag, despite the extension — " +
                                    "convert to 16-bit PCM WAV or OGG");

                    if (magic[0] == 0xFF && (magic[1] & 0xE0) == 0xE0)
                        return Fail("this is a raw MP3 frame stream, despite the extension — " +
                                    "convert to 16-bit PCM WAV or OGG");
*/

                    if (tag != "RIFF") return Fail($"unrecognised header \"{tag}\"");

                    br.ReadUInt32();                                   // RIFF size
                    if (Ascii(br.ReadBytes(4)) != "WAVE")
                        return Fail("RIFF container but not WAVE");

                    while (fs.Position + 8 <= fs.Length)
                    {
                        string id   = Ascii(br.ReadBytes(4));
                        uint   size = br.ReadUInt32();
                        long   next = fs.Position + size + (size & 1); // chunks are word-aligned

                        if (id == "fmt " && size >= 16)
                        {
                            ushort format   = br.ReadUInt16();
                            ushort channels = br.ReadUInt16();
                            uint   rate     = br.ReadUInt32();
                            br.ReadUInt32();                           // byte rate
                            br.ReadUInt16();                           // block align
                            ushort bits     = br.ReadUInt16();

                            string desc = $"WAV [{FormatName(format)}], {channels} ch, " +
                                          $"{rate} Hz, {bits}-bit";

                            // 1 = PCM, 3 = IEEE float, FFFE = extensible (usually PCM inside)
                            bool ok = format == 0x0001 || format == 0x0003 || format == 0xFFFE;

                            return ok
                                ? Ok(desc)
                                : Fail(desc + " — Unity's runtime decoder only handles PCM " +
                                              "or float WAV; re-save as 16-bit PCM");
                        }

                        if (next <= fs.Position || next > fs.Length) break;
                        fs.Position = next;
                    }

                    return Fail("WAVE file with no readable fmt chunk");
                }
            }
            catch (Exception e)
            {
                return Fail("could not read file: " + e.Message);
            }
        }

        private static string FormatName(ushort code)
        {
            switch (code)
            {
                case 0x0001: return "PCM";
                case 0x0002: return "MS ADPCM";
                case 0x0003: return "IEEE float";
                case 0x0006: return "A-law";
                case 0x0007: return "mu-law";
                case 0x0011: return "IMA ADPCM";
                case 0x0031: return "GSM 6.10";
                case 0x0055: return "MPEG Layer-3 in RIFF";
                case 0xFFFE: return "WAVE_FORMAT_EXTENSIBLE";
                default:     return $"unknown 0x{code:X4}";
            }
        }

        private static string Ascii(byte[] b) => Encoding.ASCII.GetString(b);
        private static Result Ok(string s)   => new Result { Playable = true,  Summary = s };
        private static Result Fail(string s) => new Result { Playable = false, Summary = s };
    }
}