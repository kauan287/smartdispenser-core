using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace SmartDispenser.IntegrationTests;

/// <summary>A vending machine connected to the hub over SignalR, as the firmware would be.</summary>
public sealed class MachineClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly Guid _machineId;

    public ConcurrentQueue<JsonElement> Commands { get; } = new();

    private MachineClient(HubConnection connection, Guid machineId)
    {
        _connection = connection;
        _machineId = machineId;
        _connection.On<JsonElement>("ReceiveCommand", command => Commands.Enqueue(command));
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
        var client = new MachineClient(connection, machineId);
        await connection.StartAsync();
        return client;
    }

    public Task ReportInventory(int count) => _connection.InvokeAsync("ReportInventory", _machineId.ToString(), count);

    public Task ConfirmDispense(Guid requestId) =>
        _connection.InvokeAsync("ConfirmDispense", _machineId.ToString(), requestId.ToString());

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
