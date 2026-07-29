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
/// (<c>__substg1.0_XXXX001F</c>) for message class / subject / body, the binary
/// <c>PidTagHtml</c> stream for the HTML body, and one
/// <c>__attach_version1.0_#NNNNNNNN</c> storage per attachment (embedded messages get
/// <c>PidTagAttachMethod = 5</c> plus a nested message substorage). Verified against
/// MsgReader 6.1.0.
/// </summary>
internal static class MsgFixtureBuilder
{
    public static MemoryStream Create(
        string? subject,
        string? bodyText,
        string? bodyHtml = null,
        (string FileName, byte[] Data, string? MimeType)[]? attachments = null,
        string? embeddedMessageSubject = null,
        string? embeddedMessageBody = null) =>
        CreateTree(new MsgNode
        {
            Subject = subject,
            BodyText = bodyText,
            BodyHtml = bodyHtml,
            Attachments = attachments ?? [],
            Embedded = embeddedMessageSubject is null
                ? []
                : [new MsgNode { Subject = embeddedMessageSubject, BodyText = embeddedMessageBody ?? string.Empty }],
        });

    /// <summary>
    /// Builds a fixture of arbitrary shape: any level may carry both file attachments and
    /// several embedded messages, and an embedded message may carry its own.
    /// </summary>
    /// <remarks>
    /// <see cref="Create"/> takes a single embedded message with no children of its own, so every
    /// fixture built with it is one descent deep and has no sibling after a descent. That shape
    /// cannot tell a depth-first walk from a breadth-first one — both emit it identically.
    /// </remarks>
    public static MemoryStream CreateTree(MsgNode root)
    {
        var stream = new MemoryStream();
        using (var storage = RootStorage.Create(stream, CfbVersion.V3, StorageModeFlags.LeaveOpen))
        {
            WriteMessage(storage, root);
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>One message in a fixture tree.</summary>
    internal sealed class MsgNode
    {
        public string? Subject { get; init; }

        public string? BodyText { get; init; }

        public string? BodyHtml { get; init; }

        public (string FileName, byte[] Data, string? MimeType)[] Attachments { get; init; } = [];

        public MsgNode[] Embedded { get; init; } = [];
    }

    private static void WriteMessage(CfbStorage storage, MsgNode node)
    {
        WriteUnicode(storage, 0x001A, "IPM.Note"); // PidTagMessageClass

        if (node.Subject is not null)
            WriteUnicode(storage, 0x0037, node.Subject); // PidTagSubject

        if (node.BodyText is not null)
            WriteUnicode(storage, 0x1000, node.BodyText); // PidTagBody

        if (node.BodyHtml is not null)
            WriteBytes(storage, "__substg1.0_10130102", Encoding.UTF8.GetBytes(node.BodyHtml)); // PidTagHtml

        int attachmentIndex = 0;
        foreach (var (fileName, data, mimeType) in node.Attachments)
        {
            WriteFileAttachment(storage, attachmentIndex++, fileName, data, mimeType);
        }

        foreach (var embedded in node.Embedded)
        {
            WriteEmbeddedMessage(storage, attachmentIndex++, embedded);
        }
    }

    private static void WriteFileAttachment(CfbStorage root, int index, string fileName, byte[] data, string? mimeType)
    {
        var storage = CreateAttachmentStorage(root, index);
        WriteUnicode(storage, 0x3704, fileName); // PidTagAttachFilename
        WriteUnicode(storage, 0x3707, fileName); // PidTagAttachLongFilename
        WriteBytes(storage, "__substg1.0_37010102", data); // PidTagAttachDataBinary
        if (mimeType is not null)
            WriteUnicode(storage, 0x370E, mimeType); // PidTagAttachMimeTag
    }

    private static void WriteEmbeddedMessage(CfbStorage root, int index, MsgNode node)
    {
        var storage = CreateAttachmentStorage(root, index);

        // __properties_version1.0 (attachment scope): 8 reserved header bytes followed by
        // 16-byte entries — one PT_LONG entry setting PidTagAttachMethod (0x3705) to 5
        // (ATTACH_EMBEDDED_MSG), which is what makes MsgReader surface the attachment as a
        // nested Storage.Message.
        var properties = new byte[8 + 16];
        BitConverter.GetBytes(0x37050003u).CopyTo(properties, 8); // tag: id 0x3705, type PT_LONG (0x0003)
        BitConverter.GetBytes(6u).CopyTo(properties, 12); // flags: readable | writable
        BitConverter.GetBytes(5).CopyTo(properties, 16); // value: ATTACH_EMBEDDED_MSG
        WriteBytes(storage, "__properties_version1.0", properties);

        // Written recursively, so a nested message may carry its own attachments and its own
        // embedded messages — the shape an ordering test needs.
        var nested = storage.CreateStorage("__substg1.0_3701000D"); // PidTagAttachDataObject
        WriteMessage(nested, node);
    }

    private static CfbStorage CreateAttachmentStorage(CfbStorage root, int index) =>
        root.CreateStorage($"__attach_version1.0_#{index.ToString("X8", CultureInfo.InvariantCulture)}");

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
