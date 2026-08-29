using System.Net;
using Blossom.Classroom.Protocol.Models;

namespace Blossom.Classroom.Protocol.Validation;

public static class ProtocolValidation
{
    public static void ValidateEnrollmentRequest(DeviceEnrollmentRequest request)
    {
        RequireGuid(request.DeviceId, nameof(request.DeviceId));
        RequireText(request.DeviceName, nameof(request.DeviceName), 128);
        RequireText(request.AgentVersion, nameof(request.AgentVersion), 64);
        RequireText(request.EnrollmentToken, nameof(request.EnrollmentToken), 256);
    }

    public static void ValidateHello(DeviceHello hello)
    {
        RequireGuid(hello.DeviceId, nameof(hello.DeviceId));
        RequireText(hello.AgentVersion, nameof(hello.AgentVersion), 64);
    }

    public static void ValidateHeartbeat(DeviceHeartbeat heartbeat)
    {
        ValidateHello(new DeviceHello(heartbeat.DeviceId, heartbeat.SessionId, heartbeat.AgentVersion));
        if (heartbeat.BatteryPercent is < 0 or > 100)
        {
            throw new ProtocolValidationException("Battery percent must be between 0 and 100.");
        }

        if (heartbeat.NetworkStatus is not null)
        {
            RequireText(heartbeat.NetworkStatus, nameof(heartbeat.NetworkStatus), 64);
        }

        if (heartbeat.Activity is not null)
        {
            ValidateActivity(heartbeat.Activity);
        }
    }

    public static void ValidateActivity(ActivitySnapshot activity)
    {
        RequireText(activity.ApplicationDisplayName, nameof(activity.ApplicationDisplayName), 128);
        RequireText(activity.ProcessName, nameof(activity.ProcessName), 128);
        if (activity.BrowserDomain is not null)
        {
            RequireText(activity.BrowserDomain, nameof(activity.BrowserDomain), 253);
            if (activity.BrowserDomain.Contains('/') || activity.BrowserDomain.Contains('?')
                || activity.BrowserDomain.Contains('#') || activity.BrowserDomain.Contains('@'))
            {
                throw new ProtocolValidationException("Browser activity must contain a hostname only.");
            }

            if (Uri.CheckHostName(activity.BrowserDomain) == UriHostNameType.Unknown)
            {
                throw new ProtocolValidationException("Browser activity must contain a valid hostname.");
            }
        }

        if (activity.WindowTitle is not null)
        {
            RequireText(activity.WindowTitle, nameof(activity.WindowTitle), 256);
        }
    }

    public static void ValidateCommand(CommandRequest command)
    {
        RequireGuid(command.RequestId, nameof(command.RequestId));
        RequireGuid(command.SessionId, nameof(command.SessionId));
        if (command.TargetDeviceIds is null
            || command.TargetDeviceIds.Count is < 1 or > ProtocolConstants.MaxTargetDevices
            || command.TargetDeviceIds.Any(id => id == Guid.Empty)
            || command.TargetDeviceIds.Distinct().Count() != command.TargetDeviceIds.Count)
        {
            throw new ProtocolValidationException(
                $"A command must target 1 to {ProtocolConstants.MaxTargetDevices} unique devices.");
        }

        if (command.Message is not null)
        {
            RequireText(command.Message, nameof(command.Message), ProtocolConstants.MaxTextLength);
        }

        if (command.DisplaySeconds is not null
            && command.DisplaySeconds is < 1 or > ProtocolConstants.MaxDisplaySeconds)
        {
            throw new ProtocolValidationException(
                $"DisplaySeconds must be between 1 and {ProtocolConstants.MaxDisplaySeconds}.");
        }

        switch (command.Kind)
        {
            case ClassroomCommandKind.Message:
                RequireText(command.Message, nameof(command.Message), ProtocolConstants.MaxTextLength);
                break;
            case ClassroomCommandKind.FocusMode:
                if (command.FocusEnabled is not false)
                {
                    RequireText(command.Message, nameof(command.Message), ProtocolConstants.MaxTextLength);
                }
                break;
            case ClassroomCommandKind.OpenUrl:
                RequireHttpsUrl(command.Url);
                break;
            case ClassroomCommandKind.LaunchApprovedApp:
                RequireApprovedAppId(command.ApprovedAppId);
                break;
            default:
                throw new ProtocolValidationException("Unknown Classroom command kind.");
        }
    }

    public static void ValidateAck(CommandAck acknowledgment)
    {
        RequireGuid(acknowledgment.RequestId, nameof(acknowledgment.RequestId));
        RequireGuid(acknowledgment.DeviceId, nameof(acknowledgment.DeviceId));
        if (acknowledgment.Reason is not null)
        {
            RequireText(acknowledgment.Reason, nameof(acknowledgment.Reason), 256);
        }
    }

    public static void ValidateResult(CommandResult result)
    {
        RequireGuid(result.RequestId, nameof(result.RequestId));
        RequireGuid(result.DeviceId, nameof(result.DeviceId));
        RequireText(result.Code, nameof(result.Code), 64);
        RequireText(result.Message, nameof(result.Message), ProtocolConstants.MaxTextLength);
    }

    private static void RequireHttpsUrl(string? value)
    {
        RequireText(value, "Url", 2_048);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ProtocolValidationException("Only HTTPS URLs without user information are allowed.");
        }
    }

    private static void RequireApprovedAppId(string? value)
    {
        RequireText(value, "ApprovedAppId", 128);
        if (value!.Any(character => !(char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-')))
        {
            throw new ProtocolValidationException("ApprovedAppId contains unsupported characters.");
        }
    }

    private static void RequireGuid(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new ProtocolValidationException($"{name} must not be empty.");
        }
    }

    private static void RequireText(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maxLength
            || value.Any(char.IsControl))
        {
            throw new ProtocolValidationException($"{name} is missing or invalid.");
        }
    }
}
