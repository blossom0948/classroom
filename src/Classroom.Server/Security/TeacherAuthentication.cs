using Blossom.Classroom.Core.Security;
using Blossom.Classroom.Server.Configuration;
using Blossom.Classroom.Server.Storage;

namespace Blossom.Classroom.Server.Security;

public static class TeacherAuthentication
{
    public static bool TryGetTeacherId(
        HttpRequest request,
        ServerOptions options,
        ClassroomDatabase database,
        out Guid teacherId)
    {
        teacherId = Guid.Empty;
        var token = GetBearerToken(request);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (database.TryValidateTeacherSession(token, out teacherId))
        {
            return true;
        }

        if (options.DevelopmentMode
            && TokenSecurity.VerifyToken(token, options.DevelopmentTeacherTokenHash))
        {
            teacherId = options.DevelopmentTeacherId;
            return true;
        }

        teacherId = Guid.Empty;
        return false;
    }

    public static string? GetBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}
