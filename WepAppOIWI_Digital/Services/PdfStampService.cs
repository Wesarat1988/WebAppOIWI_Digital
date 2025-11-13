using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using WepAppOIWI_Digital.Stamps;

namespace WepAppOIWI_Digital.Services;

public interface IPdfStampService
{
    Task<byte[]> ApplyStampAsync(byte[] pdfBytes, StampMode mode, DateOnly? date, CancellationToken cancellationToken = default);
}

public sealed class PdfStampService : IPdfStampService
{
    private readonly ILogger<PdfStampService> _logger;

    public PdfStampService(ILogger<PdfStampService> logger)
    {
        _logger = logger;
    }

    public Task<byte[]> ApplyStampAsync(byte[] pdfBytes, StampMode mode, DateOnly? date, CancellationToken cancellationToken = default)
    {
        if (pdfBytes is null)
        {
            throw new ArgumentNullException(nameof(pdfBytes));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (mode != StampMode.None)
        {
            _logger.LogDebug(
                "Stamping disabled. Returning original PDF bytes for stamp mode {Mode} with date {Date}.",
                mode,
                date);
        }

        return Task.FromResult((byte[])pdfBytes.Clone());
    }
}
