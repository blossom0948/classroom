using Blossom.Classroom.Student.Service;
using Blossom.Classroom.Student.Service.Commands;
using Blossom.Classroom.Student.Service.Configuration;
using Blossom.Classroom.Student.Service.Desktop;
using Blossom.Classroom.Student.Service.Networking;
using Blossom.Classroom.Student.Service.Status;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(StudentAgentOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<DesktopStatusBridge>();
builder.Services.AddSingleton<IStudentStatusSource>(services =>
    services.GetRequiredService<DesktopStatusBridge>());
builder.Services.AddSingleton<IStudentCommandSink>(services =>
    services.GetRequiredService<DesktopStatusBridge>());
builder.Services.AddSingleton<ClassroomServerClient>();
builder.Services.AddHostedService<StudentAgentWorker>();
builder.Services.AddSingleton<StudentUpdateWorker>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<StudentUpdateWorker>());

if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(serviceOptions =>
    {
        serviceOptions.ServiceName = "ClassroomStudentService";
    });
}

using var host = builder.Build();
await host.RunAsync();
