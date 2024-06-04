﻿using System.Diagnostics;
using Pack3r.Extensions;
using Pack3r.Logging;

namespace Pack3r.Services;

public sealed class AppLifetime : IDisposable
{
    public CancellationToken CancellationToken => _cts.Token;

    private readonly ILogger<AppLifetime> _logger;
    private readonly CancellationTokenSource _cts;

    public AppLifetime(ILogger<AppLifetime> logger)
    {
        _logger = logger;
        _cts = new CancellationTokenSource();

        Console.CancelKeyPress += CancelToken;
    }

    private void CancelToken(object? sender, ConsoleCancelEventArgs e)
    {
        _cts.Cancel();
        e.Cancel = true;
    }

    public void HandleException(Exception? ex)
    {
        try
        { _cts.Cancel(); }
        catch (AggregateException) { }

        if (ex is OperationCanceledException && _cts.IsCancellationRequested)
        {
            lock (Global.ConsoleLock)
            {
                var old = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Operation canceled by user");
                Console.ForegroundColor = old;
            }
            return; // don't drain log on cancellations
        }
        else if (ex is ControlledException)
        {
        }
        else if (ex is EnvironmentException or InvalidDataException)
        {
            _logger.Exception(null, ex.Message);
        }
        else if (ex is DirectoryNotFoundException dnfe)
        {
            _logger.Exception(dnfe, "Error, file/directory not found, aborting");
        }
        else if (ex is FileNotFoundException fnfe)
        {
            _logger.Exception(fnfe, $"Error, file {fnfe.FileName} not found, aborting");
        }
        else if (ex is UnreachableException ure)
        {
            _logger.Exception(ure, "Internal error, aborting. Please report to developer");
        }
        else
        {
            _logger.Exception(ex, "Unhandled exception, aborting");
        }

        _logger.Drain();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= CancelToken;
        _cts.Dispose();
    }
}
