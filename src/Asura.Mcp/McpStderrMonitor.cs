namespace Asura.Mcp;

internal sealed class McpStderrMonitor(int maxBytes, int maxLines)
{
    private readonly object _gate = new();
    private int _observedBytes;
    private int _observedLines;
    private bool _wasTruncated;
    private bool _readFailed;

    public void Observe(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            var retainedBytes = Math.Min(bytes.Length, Math.Max(0, maxBytes - _observedBytes));
            _observedBytes += retainedBytes;
            if (retainedBytes != bytes.Length)
            {
                _wasTruncated = true;
            }

            foreach (var value in bytes)
            {
                if (value != (byte)'\n')
                {
                    continue;
                }

                if (_observedLines < maxLines)
                {
                    _observedLines++;
                }
                else
                {
                    _wasTruncated = true;
                }
            }
        }
    }

    public void MarkReadFailed()
    {
        lock (_gate)
        {
            _readFailed = true;
        }
    }

    public McpStderrDiagnostics Snapshot()
    {
        lock (_gate)
        {
            return new(
                _observedBytes,
                _observedLines,
                _wasTruncated,
                _readFailed);
        }
    }
}
