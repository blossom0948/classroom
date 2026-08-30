using System.Windows.Forms;

namespace Blossom.Classroom.Student.Setup;

internal static class Program
{
    private const string DefaultServerOrigin = "https://classroom-api.blossom0948.cloud";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new StudentSetupForm(ResolveServerOrigin(args)));
    }

    private static Uri ResolveServerOrigin(string[] args)
    {
        var configured = Environment.GetEnvironmentVariable("CLASSROOM_STUDENT_SERVER_URL");
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--server-url", StringComparison.OrdinalIgnoreCase))
            {
                configured = args[index + 1];
                break;
            }
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var candidate)
            || candidate.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(candidate.Host))
        {
            candidate = new Uri(DefaultServerOrigin);
        }

        var builder = new UriBuilder(candidate)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }
}
