using System.Reflection;

namespace ScriptRunner.Core;

/// <summary>
/// The robot check: a short Python program, run on the Pi with the same python3 students' scripts get, that reports
/// the robot's name, system, storage, Qwiic devices, camera, other running Python programs, and RVR battery. It only
/// reads and never moves the robot.
/// </summary>
public static class RobotCheck
{
    private const string ResourceName = "robot_check.py";
    private const string EndMarker = "END_OF_ROBOT_CHECK";

    /// <summary>Longest the check may take: waking the RVR and asking for its battery takes a few seconds.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(40);

    /// <summary>The shell command that runs the check (the program is sent as a here-document).</summary>
    public static string Command { get; } = BuildCommand();

    private static string BuildCommand()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(ResourceName + " is missing from the app.");
        using var reader = new StreamReader(stream);
        string program = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        return $"python3 - <<'{EndMarker}'\n{program}\n{EndMarker}\n";
    }
}
