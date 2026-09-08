using System.IO.Ports;
using System.Text.Json;

namespace IntercomCompanion.Core;

/// <summary>
/// Direct USB Serial/JTAG configuration channel. The password is only sent to
/// the selected local serial port and is deliberately neither returned by the
/// device nor persisted by the companion.
/// </summary>
internal static class UsbConfigurationClient
{
    private const int BaudRate = 115200;

    public static IReadOnlyList<string> GetPortNames() => SerialPort.GetPortNames()
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static Task<UsbDeviceConfiguration> GetConfigurationAsync(string portName,
                                                                       CancellationToken cancellationToken = default) =>
        SendAsync(portName, new { cmd = "get" }, cancellationToken);

    public static Task<UsbDeviceConfiguration> SetWifiAsync(string portName, string ssid, string password, string alias,
                                                              CancellationToken cancellationToken = default) =>
        SendAsync(portName, new { ssid, password, alias }, cancellationToken);

    private static Task<UsbDeviceConfiguration> SendAsync(string portName, object request,
                                                            CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var serial = new SerialPort(portName, BaudRate)
            {
                NewLine = "\n",
                ReadTimeout = 250,
                WriteTimeout = 1000,
                DtrEnable = false,
                RtsEnable = false,
            };
            serial.Open();
            serial.DiscardInBuffer();
            serial.WriteLine(JsonSerializer.Serialize(request));

            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string response;
                try { response = serial.ReadLine(); }
                catch (TimeoutException) { continue; }

                if (TryParseConfiguration(response, out var configuration)) return configuration;
            }
            throw new TimeoutException($"No configuration response from {portName}.");
        }, cancellationToken);

    private static bool TryParseConfiguration(string response, out UsbDeviceConfiguration configuration)
    {
        configuration = default!;
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "config") return false;
            configuration = new UsbDeviceConfiguration(
                root.TryGetProperty("device_id", out var deviceId) ? deviceId.GetUInt32() : 0,
                root.TryGetProperty("alias", out var alias) ? alias.GetString() ?? "" : "",
                root.TryGetProperty("ssid", out var ssid) ? ssid.GetString() ?? "" : "",
                root.TryGetProperty("assistant_enabled", out var enabled) && enabled.GetBoolean(),
                root.TryGetProperty("assistant_service_id", out var service) ? service.GetUInt32() : 0);
            return true;
        }
        catch (JsonException)
        {
            return false; // Startup logs and the USB hello are not configuration replies.
        }
    }
}

internal sealed record UsbDeviceConfiguration(uint DeviceId, string Alias, string Ssid, bool AssistantEnabled = false, uint AssistantServiceId = 0);
