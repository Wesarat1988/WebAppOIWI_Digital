using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WepAppOIWI_Digital.Services;

internal sealed class NetworkShareConnector : IDisposable
{
    private readonly string _networkPath;
    private readonly string? _userName;
    private readonly string? _password;
    private readonly ILogger _logger;
    private bool _disposed;
    private bool _isConnected;
    private string? _lastErrorMessage;

    private NetworkShareConnector(string networkPath, string? userName, string? password, ILogger logger)
    {
        _networkPath = networkPath;
        _userName = userName;
        _password = password;
        _logger = logger;
    }

    public static NetworkShareConnector? TryCreate(string? networkPath, DocumentCatalogOptions options, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!options.EnableNetworkConnection)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(networkPath) || !networkPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return null;
        }

        var userName = BuildUserName(options);
        return new NetworkShareConnector(networkPath, userName, options.NetworkPassword, logger);
    }

    public bool EnsureConnected()
    {
        ThrowIfDisposed();

        var resource = new NetResource
        {
            Scope = ResourceScope.GlobalNetwork,
            Type = ResourceType.Disk,
            DisplayType = ResourceDisplayType.Share,
            RemoteName = _networkPath
        };

        var result = WNetAddConnection2(ref resource, _password, _userName, 0);

        if (result == 0 || result == ErrorAlreadyAssigned)
        {
            _lastErrorMessage = null;
            _isConnected = true;
            return true;
        }

        var exception = new Win32Exception(result);
        _lastErrorMessage = exception.Message;
        _logger.LogWarning("Unable to connect to network share {NetworkPath}. Error {ErrorCode} ({Message})", _networkPath, result, exception.Message);
        return false;
    }

    public string? LastErrorMessage => _lastErrorMessage;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_isConnected)
        {
            try
            {
                var result = WNetCancelConnection2(_networkPath, 0, false);
                if (result != 0 && result != ErrorNotConnected)
                {
                    _logger.LogDebug("WNetCancelConnection2 returned {ErrorCode} for {NetworkPath}", result, _networkPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to release network share {NetworkPath}", _networkPath);
            }
        }

        _isConnected = false;
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NetworkShareConnector));
        }
    }

    private static string? BuildUserName(DocumentCatalogOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.NetworkUserName))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.NetworkDomain))
        {
            return options.NetworkUserName;
        }

        return string.Concat(options.NetworkDomain, "\\", options.NetworkUserName);
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource lpNetResource, string? lpPassword, string? lpUserName, int dwFlags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string lpName, int dwFlags, bool force);

    private const int ErrorAlreadyAssigned = 1219;
    private const int ErrorNotConnected = 2250;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public ResourceScope Scope;
        public ResourceType Type;
        public ResourceDisplayType DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    private enum ResourceScope
    {
        Connected = 1,
        GlobalNetwork,
        Remembered,
        Recent,
        Context
    }

    private enum ResourceType
    {
        Any = 0,
        Disk = 1,
        Print = 2,
        Reserved = 8
    }

    private enum ResourceDisplayType
    {
        Generic = 0x0,
        Domain = 0x01,
        Server = 0x02,
        Share = 0x03,
        File = 0x04,
        Group = 0x05,
        Network = 0x06,
        Root = 0x07,
        Shareadmin = 0x08,
        Directory = 0x09,
        Tree = 0x0a,
        Ndscontainer = 0x0b
    }
}
