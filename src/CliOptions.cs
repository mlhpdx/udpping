namespace UdpPingGraph;

/// <summary>
/// Parsed command-line configuration. Parsing is hand-rolled (no
/// System.CommandLine dependency) to keep the Native AOT publish small
/// and free of reflection-based argument binding.
/// </summary>
internal sealed class CliOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 7;
    public int IntervalMs { get; init; } = 200;
    public int TimeoutMs { get; init; } = 2000;
    public int DisplayIntervalMs { get; init; } = 500;

    /// <summary>The send cadence as a <see cref="TimeSpan"/> for the pacing engine.</summary>
    public TimeSpan Interval => TimeSpan.FromMilliseconds(IntervalMs);

    /// <summary>
    /// Parses <paramref name="args"/>. Returns <c>null</c> if help was
    /// requested (already printed); throws <see cref="ArgumentException"/>
    /// on invalid input.
    /// </summary>
    public static CliOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
        {
            PrintUsage();
            return null;
        }

        string? host = null;
        var port = 7;
        var interval = 200;
        var timeout = 2000;
        var displayInterval = 500;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-p":
                case "--port":
                    port = ParseIntArg(args, ref i, arg);
                    break;

                case "-i":
                case "--interval":
                    interval = ParseIntArg(args, ref i, arg);
                    break;

                case "-d":
                case "--display-interval":
                    displayInterval = ParseIntArg(args, ref i, arg);
                    break;

                case "-t":
                case "--timeout":
                    timeout = ParseIntArg(args, ref i, arg);
                    break;

                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new ArgumentException($"Unknown option '{arg}'.");
                    }

                    if (host is not null)
                    {
                        throw new ArgumentException("Only one target host may be specified.");
                    }

                    host = arg;
                    break;
            }
        }

        if (host is null)
        {
            throw new ArgumentException("A target host is required. See --help.");
        }

        if (interval < 1)
        {
            throw new ArgumentException("--interval must be at least 1 millisecond (1000 packets/second).");
        }

        if (timeout <= 0)
        {
            throw new ArgumentException("--timeout must be a positive number of milliseconds.");
        }

        if (displayInterval <= 0)
        {
            throw new ArgumentException("--display-interval must be a positive number of milliseconds.");
        }

        if (port is <= 0 or > 65535)
        {
            throw new ArgumentException("--port must be between 1 and 65535.");
        }

        return new CliOptions
        {
            Host = host,
            Port = port,
            IntervalMs = interval,
            TimeoutMs = timeout,
            DisplayIntervalMs = displayInterval,
        };
    }

    private static int ParseIntArg(string[] args, ref int i, string optionName)
    {
        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var value))
        {
            throw new ArgumentException($"{optionName} requires a numeric value.");
        }

        i++;
        return value;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            udpping - a gping-style latency graph over plain UDP echo

            Sends a small UDP packet containing a unique id on a steady
            cadence and waits for the remote side to echo it back
            (RFC 862 UDP Echo). Round-trip time is measured per packet and
            plotted in a live-updating graph, bucketed into display ticks by
            the time each packet was SENT (so a tick's column can keep
            updating even after it first appears on screen, as replies for
            packets sent in that tick continue to arrive).

            Usage:
              udpping <host> [options]

            Options:
              -p, --port <n>       UDP port of the echo service (default: 7)
              -i, --interval <ms>  Time between probes, in milliseconds. Minimum
                                   1ms, i.e. up to 1000 packets/second (default: 200)
              -t, --timeout <ms>   Time to wait for a reply before it counts as
                                   dropped, in milliseconds (default: 2000)
              -d, --display-interval <ms>  UI refresh interval in milliseconds (default: 500)
              -h, --help           Show this help

            Example:
              udpping echo.example.com -p 7 -i 200 -t 2000

            Note: the target must be running a UDP echo service that sends
            back the exact bytes it receives (RFC 862). Most hosts do not
            run this by default.
            """);
    }
}
