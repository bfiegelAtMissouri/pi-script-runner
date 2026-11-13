using Avalonia.Threading;
using ScriptRunner.App.Services;
using Xunit;

namespace ScriptRunner.Tests;

/// <summary>The diagnostic log and crash reports.</summary>
public sealed class DiagnosticTests
{
    [Fact]
    public void ErrorsAreReportedWithRecentActivity()
    {
        string settingsFolder = Directory.CreateTempSubdirectory("script-runner-diagnostics").FullName;
        DiagnosticLog.Start(settingsFolder);
        DiagnosticLog.Write("Run hello.py: Ssh pi@10.0.0.5:22");
        string? reported = null;
        void Listener(string message) => reported = message;
        DiagnosticLog.ErrorRecorded += Listener;
        try
        {
            string? path = DiagnosticLog.RecordError("a test", Thrown(new InvalidOperationException("test failure")));

            Assert.NotNull(path);
            string report = File.ReadAllText(path);
            Assert.Contains("Where: a test", report, StringComparison.Ordinal);
            Assert.Contains("System.InvalidOperationException: test failure", report, StringComparison.Ordinal);
            Assert.Contains(nameof(Thrown), report, StringComparison.Ordinal);                 // the stack trace
            Assert.Contains("Run hello.py: Ssh pi@10.0.0.5:22", report, StringComparison.Ordinal);   // recent activity
            Assert.Contains("test failure", reported, StringComparison.Ordinal);
            Assert.Contains("ERROR in a test", File.ReadAllText(Directory.GetFiles(DiagnosticLog.Folder!, "app-*.log").Single()),
                StringComparison.Ordinal);

            Assert.Equal(path, DiagnosticLog.TakeUnseenReport());       // told once,
            Assert.Null(DiagnosticLog.TakeUnseenReport());              // not again
        }
        finally
        {
            DiagnosticLog.ErrorRecorded -= Listener;
            Directory.Delete(settingsFolder, true);
        }
    }

    [Fact]
    public async Task UserInterfaceErrorsAreRecordedAndTheAppKeepsRunning()
    {
        string settingsFolder = Directory.CreateTempSubdirectory("script-runner-diagnostics").FullName;
        ScriptRunner.App.App.Platform ??= new PlatformServices(new UiTests.RecordingSpeech(), new UiTests.SilentAudio(),
            new NoSecretStore(), settingsFolder);
        Dispatcher ui = await HeadlessApp.StartAsync();
        ScriptRunner.App.App.InstallErrorHandling(settingsFolder);

        ui.Post(() => throw new InvalidOperationException("thrown on the user interface thread"));
        string? report = null;
        for (int tries = 0; report is null && tries < 100; ++tries)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            report = DiagnosticLog.LatestReport();
        }

        Assert.NotNull(report);
        Assert.Contains("thrown on the user interface thread", File.ReadAllText(report), StringComparison.Ordinal);
        Assert.Equal(42, await ui.InvokeAsync(() => 42));                 // the user interface thread is still running

        // The same error also surfaces later as an unobserved task; it is not reported a second time.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(report, DiagnosticLog.LatestReport());
        Directory.Delete(settingsFolder, true);
    }

    private static Exception Thrown(Exception error)
    {
        try
        {
            throw error;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }
}
