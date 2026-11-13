using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ProjektZeh.Update;

/// <summary>Begrenztes HTTP ohne Umleitungen oder Transfercodierung; die Signatur prueft erst der Updatedienst.</summary>
internal static class HttpUpdateResponse
{
    public static byte[] Read(Stream stream, int maximumBytes)
    {
        var header = new StringBuilder();
        byte[] single = new byte[1];
        while (header.Length < 8192)
        {
            if (stream.Read(single, 0, 1) == 0) throw new EndOfStreamException("Unvollstaendiger HTTP-Header.");
            byte value = single[0];
            if (value > 127 || value == 0) throw new IOException("Ungueltiger HTTP-Header.");
            header.Append((char)value);
            int length = header.Length;
            if (length >= 4 && header[length - 4] == '\r' && header[length - 3] == '\n' && header[length - 2] == '\r' && header[length - 1] == '\n')
            {
                int size = ParseHeader(header.ToString(), maximumBytes);
                byte[] body = new byte[size];
                int offset = 0;
                while (offset < body.Length)
                {
                    int count = stream.Read(body, offset, Math.Min(65536, body.Length - offset));
                    if (count == 0) throw new EndOfStreamException("Unvollstaendige HTTP-Datei.");
                    offset += count;
                }
                return body;
            }
        }
        throw new IOException("HTTP-Header ist zu gross.");
    }

    private static int ParseHeader(string header, int maximumBytes)
    {
        string[] lines = header.Split("\r\n", StringSplitOptions.None);
        string[] status = lines[0].Split(' ');
        if (status.Length < 2 || status[0] is not ("HTTP/1.0" or "HTTP/1.1") || status[1] != "200")
            throw new IOException("HTTP-Download abgelehnt: " + lines[0]);
        int size = -1;
        for (int index = 1; index < lines.Length - 2; index++)
        {
            int colon = lines[index].IndexOf(':');
            if (colon <= 0) throw new IOException("Ungueltiger HTTP-Header.");
            string name = lines[index][..colon];
            string value = lines[index][(colon + 1)..].Trim();
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                throw new IOException("HTTP-Transfercodierung wird nicht unterstuetzt.");
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (size != -1 || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 0 || size > maximumBytes)
                throw new IOException("Ungueltige oder zu grosse HTTP-Dateilaenge.");
        }
        if (size == -1) throw new IOException("Content-Length fehlt.");
        return size;
    }
}
