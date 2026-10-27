using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace SmartDispenser.IntegrationTests;

public sealed class MachineClient : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public ConcurrentQueue<JsonElement> Commands { get; } = new();

    private MachineClient(HubConnection connection)
    {
        _connection = connection;
        _connection.On<JsonElement>("ReceiveDispenseCommand", command => Commands.Enqueue(command));
    }

    public static async Task<MachineClient> ConnectAsync(AppFactory factory, Guid machineId)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, $"/hubs/dispensers?machineId={machineId}"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();
        var client = new MachineClient(connection);
        await connection.StartAsync();
        return client;
    }

    public Task ReportInventory(int count) => _connection.InvokeAsync("ReportInventory", count);

    public Task ConfirmDispense(Guid requestId) => _connection.InvokeAsync("ConfirmDispense", requestId);

    public async Task<JsonElement> NextCommand(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Commands.TryDequeue(out var command)) return command;
            await Task.Delay(50);
        }
        throw new TimeoutException("Machine received no command.");
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
