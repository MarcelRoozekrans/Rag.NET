using System.Globalization;
using System.Text;
using OpenMcdf;
using CfbStorage = OpenMcdf.Storage;
using CfbVersion = OpenMcdf.Version;

namespace Rag.NET.Parsers.Email.Tests;

/// <summary>
/// Builds minimal Outlook <c>.msg</c> (Compound File Binary) fixtures in code via OpenMcdf —
/// MsgReader's own storage library; MsgReader can read but not write MSG. Only the streams
/// MsgReader actually reads are written: unicode property streams
/// (<c>__substg1.0_XXXX001F</c>) for message class / subject / body, and one
/// <c>__attach_version1.0_#NNNNNNNN</c> storage per attachment with filename and binary
/// data streams. Verified against MsgReader 6.1.0.
/// </summary>
internal static class MsgFixtureBuilder
{
    public static MemoryStream Create(
        string? subject,
        string? bodyText,
        params (string FileName, byte[] Data)[] attachments)
    {
        var stream = new MemoryStream();
        using (var root = RootStorage.Create(stream, CfbVersion.V3, StorageModeFlags.LeaveOpen))
        {
            WriteUnicode(root, 0x001A, "IPM.Note"); // PidTagMessageClass

            if (subject is not null)
                WriteUnicode(root, 0x0037, subject); // PidTagSubject

            if (bodyText is not null)
                WriteUnicode(root, 0x1000, bodyText); // PidTagBody

            for (int i = 0; i < attachments.Length; i++)
            {
                var storage = root.CreateStorage($"__attach_version1.0_#{i.ToString("X8", CultureInfo.InvariantCulture)}");
                WriteUnicode(storage, 0x3704, attachments[i].FileName); // PidTagAttachFilename
                WriteUnicode(storage, 0x3707, attachments[i].FileName); // PidTagAttachLongFilename
                WriteBytes(storage, "__substg1.0_37010102", attachments[i].Data); // PidTagAttachDataBinary
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteUnicode(CfbStorage storage, int propertyTag, string value) =>
        WriteBytes(
            storage,
            $"__substg1.0_{propertyTag.ToString("X4", CultureInfo.InvariantCulture)}001F",
            Encoding.Unicode.GetBytes(value));

    private static void WriteBytes(CfbStorage storage, string streamName, byte[] bytes)
    {
        using var entry = storage.CreateStream(streamName);
        entry.Write(bytes, 0, bytes.Length);
    }
}
