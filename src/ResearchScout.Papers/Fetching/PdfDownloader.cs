using System.Net.Http.Headers;

namespace ResearchScout.Papers.Fetching;

/// <summary>Downloads paper PDFs (arXiv or open-access links) with a size limit.</summary>
public sealed class PdfDownloader(HttpClient http, long maxBytes = 30 * 1024 * 1024)
{
    /// <exception cref="InvalidDataException">The file is too big or is not a PDF.</exception>
    public async Task<byte[]> DownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        request.Headers.UserAgent.ParseAdd("ResearchScout/1.0 (+https://github.com/Samiul2651/research-scout)");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidDataException($"PDF is larger than {maxBytes / (1024 * 1024)} MB.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException($"PDF is larger than {maxBytes / (1024 * 1024)} MB.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        var bytes = buffer.ToArray();
        if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F')
        {
            throw new InvalidDataException("The download is not a PDF (it may be a paywall or an error page).");
        }

        return bytes;
    }
}
