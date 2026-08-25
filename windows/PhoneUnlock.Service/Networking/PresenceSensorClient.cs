using System.Net.Http.Headers;
using System.Text.Json;
using PhoneUnlock.Service.Configuration;
using PhoneUnlock.Service.Models;
using PhoneUnlock.Service.Storage;

namespace PhoneUnlock.Service.Networking;

public sealed class PresenceSensorClient(
    WindowsSecretStore secretStore,
    ILogger<PresenceSensorClient> logger)
{
    private const string TokenTarget = "PhoneUnlock/PresenceSensor";
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(3) };

    public async Task<bool?> ReadPresenceAsync(ServiceConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!configuration.PresenceSensorEnabled
            || string.IsNullOrWhiteSpace(configuration.PresenceSensorBaseUrl)
            || string.IsNullOrWhiteSpace(configuration.PresenceSensorEntityId))
        {
            return null;
        }

        var token = secretStore.Read(TokenTarget);
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("Presence sensor is enabled but its Home Assistant token is missing.");
            return null;
        }

        return string.Equals(configuration.PresenceSensorProtocol, "smartthings", StringComparison.OrdinalIgnoreCase)
            ? await ReadSmartThingsPresenceAsync(configuration, token, cancellationToken)
            : await ReadHomeAssistantPresenceAsync(configuration, token, cancellationToken);
    }

    private async Task<bool?> ReadHomeAssistantPresenceAsync(
        ServiceConfiguration configuration,
        string token,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{configuration.PresenceSensorBaseUrl!.TrimEnd('/')}/api/states/{Uri.EscapeDataString(configuration.PresenceSensorEntityId!)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Presence sensor returned HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var state = document.RootElement.TryGetProperty("state", out var stateElement)
                ? stateElement.GetString()
                : null;
            return ParsePresenceValue(state);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Presence sensor check failed: {Message}", exception.Message);
            return null;
        }
    }

    private async Task<bool?> ReadSmartThingsPresenceAsync(
        ServiceConfiguration configuration,
        string token,
        CancellationToken cancellationToken)
    {
        var endpoint = string.Join('/',
            configuration.PresenceSensorBaseUrl!.TrimEnd('/'),
            "devices",
            Uri.EscapeDataString(configuration.PresenceSensorEntityId!),
            "components",
            Uri.EscapeDataString(configuration.PresenceSensorComponentId),
            "status");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("SmartThings sensor returned HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var value = FindSmartThingsValue(
                document.RootElement,
                configuration.PresenceSensorComponentId,
                configuration.PresenceSensorCapabilityId,
                configuration.PresenceSensorAttributeName);
            return ParsePresenceValue(value);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("SmartThings sensor check failed: {Message}", exception.Message);
            return null;
        }
    }

    private static string? FindSmartThingsValue(
        JsonElement root,
        string componentId,
        string capabilityId,
        string attributeName)
    {
        var component = root;
        if (root.TryGetProperty("components", out var components)
            && components.TryGetProperty(componentId, out var selectedComponent))
        {
            component = selectedComponent;
        }

        if (component.TryGetProperty(capabilityId, out var capability)
            && capability.TryGetProperty(attributeName, out var attribute))
        {
            return ReadValueString(attribute);
        }

        return FindAttributeValue(component, attributeName);
    }

    private static string? FindAttributeValue(JsonElement element, string attributeName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, attributeName, StringComparison.OrdinalIgnoreCase))
            {
                return ReadValueString(property.Value);
            }

            var nested = FindAttributeValue(property.Value, attributeName);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static string? ReadValueString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("value", out var value))
        {
            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString();
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
    }

    private static bool? ParsePresenceValue(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "on" or "detected" or "present" or "home" or "occupied" or "motion" or "active" => true,
        "off" or "clear" or "absent" or "away" or "not_home" or "idle" or "unoccupied" or "inactive" => false,
        "true" => true,
        "false" => false,
        _ => null
    };

    public async Task<IReadOnlyList<SmartThingsSensorOption>> ListSmartThingsSensorsAsync(
        string baseUrl,
        string token,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{baseUrl.TrimEnd('/')}/devices?includeStatus=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new ArgumentException("SmartThings Personal Access Token이 만료되었거나 권한이 없습니다. 토큰을 다시 입력하세요.");
                }
                throw new ArgumentException($"SmartThings API가 HTTP {(int)response.StatusCode}를 반환했습니다. 토큰 권한과 만료 여부를 확인하세요.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sensors = new List<SmartThingsSensorOption>();
            foreach (var item in items.EnumerateArray())
            {
                var deviceId = item.TryGetProperty("deviceId", out var deviceIdElement)
                    ? deviceIdElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(deviceId)
                    || !item.TryGetProperty("components", out var components)
                    || components.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var label = ReadOptionalString(item, "label")
                    ?? ReadOptionalString(item, "name")
                    ?? deviceId;
                var status = item.TryGetProperty("status", out var statusElement)
                    ? statusElement
                    : default;
                foreach (var component in components.EnumerateArray())
                {
                    var componentId = ReadOptionalString(component, "id") ?? "main";
                    if (!component.TryGetProperty("capabilities", out var capabilities)
                        || capabilities.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var capability in capabilities.EnumerateArray())
                    {
                        var capabilityId = ReadOptionalString(capability, "id");
                        var attributeName = capabilityId switch
                        {
                            "occupancySensor" => "occupancy",
                            "presenceSensor" => "presence",
                            "motionSensor" => "motion",
                            _ => null
                        };
                        if (attributeName is null)
                        {
                            continue;
                        }

                        var currentState = status.ValueKind == JsonValueKind.Undefined
                            ? null
                            : FindSmartThingsValue(status, componentId, capabilityId!, attributeName);
                        sensors.Add(new SmartThingsSensorOption(
                            deviceId,
                            label,
                            componentId,
                            capabilityId!,
                            attributeName,
                            currentState));
                    }
                }
            }

            return sensors;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new ArgumentException($"SmartThings 센서 목록을 불러오지 못했습니다: {exception.Message}", exception);
        }
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    public void SaveToken(string token) => secretStore.Save(TokenTarget, token);

    public void DeleteToken() => secretStore.Delete(TokenTarget);
}
