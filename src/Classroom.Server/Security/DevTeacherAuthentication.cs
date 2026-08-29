using Blossom.Classroom.Core.Security;
using Blossom.Classroom.Server.Configuration;

namespace Blossom.Classroom.Server.Security;

public static class DevTeacherAuthentication
{
    public static bool TryGetTeacherId(
        HttpRequest request,
        ServerOptions options,
        out Guid teacherId)
    {
        teacherId = Guid.Empty;
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        var token = header["Bearer ".Length..].Trim();
        if (!TokenSecurity.VerifyToken(token, options.DevelopmentTeacherTokenHash))
        {
            return false;
        }

        teacherId = options.DevelopmentTeacherId;
        return true;
    }
}

