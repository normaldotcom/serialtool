using System.Text;
using SerialTool.Core;

namespace SerialTool.Persistence;

/// <summary>Writes the current buffer to a file in any log format (runs off the UI thread).</summary>
public static class Exporter
{
    public static Task<long> ExportAsync(EventStore store, LogFormat format, string path, SessionHeader header,
        TranscriptOptions transcript, CancellationToken cancellationToken = default) =>
        Task.Run(() => Export(store, format, path, header, transcript, cancellationToken), cancellationToken);

    /// <summary>Returns the number of events exported.</summary>
    public static long Export(EventStore store, LogFormat format, string path, SessionHeader header,
        TranscriptOptions transcript, CancellationToken cancellationToken = default)
    {
        string tmp = path + ".partial";
        long count = 0;
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024))
        {
            StreamWriter? text = null;
            TranscriptBuilder? builder = null;
            switch (format)
            {
                case LogFormat.Session:
                    SessionFile.WriteHeader(fs, header);
                    break;
                case LogFormat.Text:
                    text = new StreamWriter(fs, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\r\n" };
                    text.WriteLine($"# SerialTool transcript — {header.Description} — exported {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    builder = new TranscriptBuilder(transcript, line => text.WriteLine(line));
                    break;
            }

            var batch = new List<CaptureEvent>(4096);
            long seq = store.FirstSeq, end = store.NextSeq;
            while (seq < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch.Clear();
                if (store.Read(seq, (int)Math.Min(4096, end - seq), batch) == 0) break;
                foreach (var e in batch)
                {
                    switch (format)
                    {
                        case LogFormat.Raw:
                            if (e.Kind == EventKind.Data && e.Direction == Direction.Rx) fs.Write(e.Payload.Span);
                            break;
                        case LogFormat.Session:
                            SessionFile.WriteRecord(fs, e);
                            break;
                        default:
                            builder!.Feed(e);
                            break;
                    }
                    count++;
                }
                seq = batch[^1].Seq + 1;
            }
            builder?.Flush();
            text?.Flush();
        }
        File.Move(tmp, path, overwrite: true);
        return count;
    }

    /// <summary>Expands a log file name template: {port}, {date} (yyyy-MM-dd), {time} (HHmmss).</summary>
    public static string ExpandTemplate(string template, string port, DateTime now)
    {
        string name = template
            .Replace("{port}", port, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase);
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
