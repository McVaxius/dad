using System.Text.Json;
using dad.Services;

namespace dad.Headless;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is not ["--stdio"])
        {
            Console.Error.WriteLine("DAD headless requires --stdio and an isolated lifecycle lab session.");
            return 2;
        }
        RuntimeNode? node = null;
        bool corruptCorrelation = false;
        try
        {
            while (Console.ReadLine() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var command = document.RootElement;
                var shuttingDown = command.GetProperty("op").GetString() == "shutdown";
                if (shuttingDown && !command.TryGetProperty("controlSequence", out _)) break;
                object response;
                if (node == null)
                {
                    if (command.GetProperty("op").GetString() != "init") throw new InvalidOperationException("init required");
                    node = new(command);
                    corruptCorrelation = command.TryGetProperty("verifierFault", out var fault) && fault.GetString() == "broken-correlation";
                    response = node.Snapshot();
                }
                else if (shuttingDown) { node.Shutdown(); response = node.Snapshot(); }
                else response = node.Execute(command);
                Console.WriteLine(DadIpcJson.Serialize(new
                {
                    controlSequence = command.GetProperty("controlSequence").GetInt64() + (corruptCorrelation ? 1 : 0),
                    state = response,
                }));
                if (shuttingDown) break;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { error = ex.ToString(), lastState = node?.Snapshot() }));
            return 1;
        }
        finally { node?.Dispose(); }
    }
}
