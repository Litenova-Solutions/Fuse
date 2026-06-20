// tokencount: count tokens for one or more files using the same Tiktoken
// encoding (o200k_base by default) that Fuse uses, so reduction ratios compare
// like with like across raw concatenation, Fuse output, and competitor output.
//
// Usage:
//   tokencount [--encoding o200k_base] <file> [<file> ...]
//   tokencount [--encoding o200k_base] --stdin-list   (read newline-separated paths from stdin)
//
// Output: one JSON object to stdout: { "encoding": "...", "files": [ { path, tokens, bytes, chars } ], "total": <tokens> }

using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

var encoding = "o200k_base";
var paths = new List<string>();
var readStdinList = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--encoding" when i + 1 < args.Length:
            encoding = args[++i];
            break;
        case "--stdin-list":
            readStdinList = true;
            break;
        default:
            paths.Add(args[i]);
            break;
    }
}

if (readStdinList)
{
    string? line;
    while ((line = Console.In.ReadLine()) is not null)
    {
        var trimmed = line.Trim();
        if (trimmed.Length > 0)
        {
            paths.Add(trimmed);
        }
    }
}

if (paths.Count == 0)
{
    Console.Error.WriteLine("usage: tokencount [--encoding o200k_base] <file> [<file> ...]");
    return 2;
}

var tokenizer = TiktokenTokenizer.CreateForEncoding(encoding);

var files = new List<object>();
long totalTokens = 0;

foreach (var path in paths)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"missing: {path}");
        files.Add(new { path, tokens = -1, bytes = -1, chars = -1, error = "missing" });
        continue;
    }

    var content = File.ReadAllText(path);
    var tokens = tokenizer.CountTokens(content);
    var bytes = new FileInfo(path).Length;
    totalTokens += tokens;
    files.Add(new { path, tokens, bytes, chars = content.Length });
}

var result = new { encoding, files, total = totalTokens };
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
Console.Out.Write(json);
return 0;
