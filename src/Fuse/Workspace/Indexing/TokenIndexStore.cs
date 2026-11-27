using System.Security.Cryptography;
using System.Text;
using Fuse.Paths;
namespace Fuse.Workspace.Indexing;

/// <summary>
///     Reads and writes the token index on disk, in the binary format the engine loads instead of parsing every file.
/// </summary>
internal sealed class TokenIndexStore
{
    private const uint BinaryMagic = 0x58444946; // "FIDX"
    private const int BinaryVersion = 2;
    private const int WriteLockAttempts = 20;
    private const int WriteLockRetryMs = 50;

    private readonly RepoRoot _root;

    internal TokenIndexStore(RepoRoot root)
    {
        _root = root;
    }

    /// <summary>The entry stored for one document under one preprocessor entry.</summary>
    /// <param name="Path">The document, by its repository path.</param>
    /// <param name="PreprocessorKey">The preprocessor entry the document was scanned under.</param>
    /// <param name="Length">The file length the entry was scanned from.</param>
    /// <param name="WrittenTicks">The file write time the entry was scanned from.</param>
    /// <param name="Checksum">The text checksum the entry was scanned from.</param>
    /// <param name="Tokens">Identifier token texts of the document.</param>
    /// <param name="BaseTypes">Simple names of the base types and interfaces each declared type names.</param>
    /// <param name="DeclaredTypes">Simple names of the types each declaration declares.</param>
    /// <param name="GlobalAliases">Global using alias pairs of the document.</param>
    internal sealed record DocEntry(
        RepoPath Path,
        string PreprocessorKey,
        long Length,
        long WrittenTicks,
        string Checksum,
        string[] Tokens,
        string[] BaseTypes,
        string[] DeclaredTypes,
        (string Alias, string Target)[] GlobalAliases);

    /// <summary>Directory where persisted index files live.</summary>
    internal string Directory => Path.Combine(_root.SharedStateDirectory, "token-index");

    internal string FilePathFor(string? key)
    {
        var name = string.IsNullOrWhiteSpace(key)
            ? "workspace"
            : IsSafeFileName(key)
                ? key
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(Directory, name + ".bin");
    }

    private static bool IsSafeFileName(string key) =>
        key.Length <= 64 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    internal static void WriteBinary(string path, List<DocEntry> entries)
    {
        var stringIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var strings = new List<string>();
        int IdOf(string value)
        {
            if (stringIds.TryGetValue(value, out var id))
                return id;
            id = strings.Count;
            strings.Add(value);
            stringIds[value] = id;
            return id;
        }

        foreach (var entry in entries)
        {
            IdOf(entry.PreprocessorKey);
            foreach (var token in entry.Tokens)
                IdOf(token);
            foreach (var baseType in entry.BaseTypes)
                IdOf(baseType);
            foreach (var declared in entry.DeclaredTypes)
                IdOf(declared);
            foreach (var (alias, target) in entry.GlobalAliases)
            {
                IdOf(alias);
                IdOf(target);
            }
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(BinaryMagic);
        writer.Write(BinaryVersion);

        writer.Write7BitEncodedInt(strings.Count);
        foreach (var text in strings)
            writer.Write(text);

        writer.Write7BitEncodedInt(entries.Count);
        foreach (var entry in entries)
        {
            writer.Write(entry.Path.Relative);
            writer.Write7BitEncodedInt(IdOf(entry.PreprocessorKey));
            writer.Write(entry.Length);
            writer.Write(entry.WrittenTicks);
            writer.Write(entry.Checksum);

            WriteIdArray(writer, entry.Tokens, IdOf);
            WriteIdArray(writer, entry.BaseTypes, IdOf);
            WriteIdArray(writer, entry.DeclaredTypes, IdOf);

            writer.Write7BitEncodedInt(entry.GlobalAliases.Length);
            foreach (var (alias, target) in entry.GlobalAliases)
            {
                writer.Write7BitEncodedInt(IdOf(alias));
                writer.Write7BitEncodedInt(IdOf(target));
            }
        }
    }

    private static void WriteIdArray(BinaryWriter writer, string[] items, Func<string, int> idOf)
    {
        writer.Write7BitEncodedInt(items.Length);
        foreach (var item in items)
            writer.Write7BitEncodedInt(idOf(item));
    }

    internal List<DocEntry> ReadBinary(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadUInt32() != BinaryMagic || reader.ReadInt32() != BinaryVersion)
            throw new InvalidDataException("unsupported token index header");

        var stringCount = reader.Read7BitEncodedInt();
        if (stringCount < 0 || stringCount > stream.Length)
            throw new InvalidDataException("token index string table is out of range");
        var strings = new string[stringCount];
        for (var i = 0; i < stringCount; i++)
            strings[i] = reader.ReadString();

        var entryCount = reader.Read7BitEncodedInt();
        if (entryCount < 0 || entryCount > stream.Length)
            throw new InvalidDataException("token index entry table is out of range");
        var entries = new List<DocEntry>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var relative = reader.ReadString();
            var preprocessorKey = StringAt(strings, reader.Read7BitEncodedInt());
            var length = reader.ReadInt64();
            var writtenTicks = reader.ReadInt64();
            var checksum = reader.ReadString();

            var tokens = ReadIdArray(reader, strings, stream.Length);
            var baseTypes = ReadIdArray(reader, strings, stream.Length);
            var declaredTypes = ReadIdArray(reader, strings, stream.Length);

            var aliasCount = reader.Read7BitEncodedInt();
            if (aliasCount < 0 || aliasCount > stream.Length)
                throw new InvalidDataException("token index alias table is out of range");
            var aliases = aliasCount == 0 ? [] : new (string, string)[aliasCount];
            for (var a = 0; a < aliasCount; a++)
                aliases[a] = (StringAt(strings, reader.Read7BitEncodedInt()), StringAt(strings, reader.Read7BitEncodedInt()));

            entries.Add(new DocEntry(_root.PathOf(relative), preprocessorKey, length, writtenTicks, checksum, tokens, baseTypes, declaredTypes, aliases));
        }

        return entries;
    }

    private static string[] ReadIdArray(BinaryReader reader, string[] strings, long fileLength)
    {
        var count = reader.Read7BitEncodedInt();
        if (count == 0)
            return [];
        if (count < 0 || count > fileLength)
            throw new InvalidDataException("token index list is out of range");
        var items = new string[count];
        for (var i = 0; i < count; i++)
            items[i] = StringAt(strings, reader.Read7BitEncodedInt());
        return items;
    }

    private static string StringAt(string[] strings, int id)
    {
        if ((uint)id >= (uint)strings.Length)
            throw new InvalidDataException("token index entry names a string outside its table");
        return strings[id];
    }

    internal static FileStream? AcquireWriteLock(string directory)
    {
        var path = Path.Combine(directory, "write.lock");
        for (var attempt = 0; attempt < WriteLockAttempts; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                Thread.Sleep(WriteLockRetryMs);
            }
        }

        return null;
    }

    internal static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
