using System.Net;
using System.Net.Sockets;
using Spectre.Console;
using UdpPingGraph;

CliOptions? options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] {ex.Message}");
    return 1;
}

if (options is null)
{
    return 0; // --help was printed
}

IPAddress address;
try
{
    var addresses = await Dns.GetHostAddressesAsync(options.Host);
    if (addresses.Length == 0)
    {
        throw new SocketException((int)SocketError.HostNotFound);
    }

    address = addresses[0];
}
catch (Exception ex)
{
    AnsiConsole.MarkupLineInterpolated($"[red]Could not resolve host '{Markup.Escape(options.Host)}':[/] {Markup.Escape(ex.Message)}");
    return 1;
}

var endpoint = new IPEndPoint(address, options.Port);
using var engine = new PingEngine(options, endpoint);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var engineTask = engine.RunAsync(cts.Token);

await AnsiConsole.Live(new Markup(string.Empty))
    .AutoClear(false)
    .StartAsync(async ctx =>
    {
        using var uiTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.DisplayIntervalMs));

        while (true)
        {
            var width = Math.Max(40, AnsiConsole.Profile.Width);
            ctx.UpdateTarget(GraphRenderer.Render(engine, options, width));
            ctx.Refresh();

            try
            {
                if (!await uiTimer.WaitForNextTickAsync(cts.Token))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    });

await cts.CancelAsync();
try
{
    await engineTask;
}
catch (OperationCanceledException)
{
    // expected on shutdown
}

return 0;
