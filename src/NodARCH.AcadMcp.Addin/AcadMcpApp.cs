using Autodesk.AutoCAD.Runtime;
using NodARCH.AcadMcp.Addin;
using NodARCH.AcadMcp.Addin.Tools;
using NodARCH.AcadMcp.Contracts;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(AcadMcpApp))]
[assembly: CommandClass(typeof(AcadMcpApp))]

namespace NodARCH.AcadMcp.Addin;

/// <summary>
/// Add-in entry point. Loaded at AutoCAD startup by the NodARCH.AcadMcp bundle, or by NETLOAD.
///
/// LOADING IS NOT LISTENING, and the distinction is the whole design:
///
///   * The assembly loads at startup so its commands and ribbon exist. That costs nothing
///     security-wise — Initialize() opens no socket and accepts no input.
///   * The BRIDGE, which is the arbitrary-code-execution endpoint (run_csharp, run_lisp), is
///     opt-in per session. Nothing listens until the user clicks Start on the ribbon or types
///     NODARCHSTART.
///
/// An earlier revision tried to enforce this with LoadOnAutoCADStartup="false", copying
/// Autodesk's own MCP bundle. That does not work here: with the module unloaded AutoCAD has no
/// idea NODARCHSTART exists, so the command that is supposed to demand-load it is itself
/// unknown, and a clean install is dead on arrival. Loading the module and withholding the
/// socket gets the same safety with none of that.
/// </summary>
public sealed class AcadMcpApp : IExtensionApplication
{
    private static readonly List<string> Failures = [];

    /// <summary>What failed while the add-in loaded, one line each. Empty when everything loaded.</summary>
    internal static IReadOnlyList<string> LoadFailures => Failures;

    /// <summary>
    /// Nothing may escape from here. When Initialize throws, AutoCAD keeps the DLL loaded but
    /// registers none of its commands and says nothing: no ribbon panel, and NODARCHSTART is an
    /// unknown command. So each step is guarded on its own, and whatever failed is reported by
    /// NODARCHSTART, NODARCHSTATUS and acad_status instead.
    /// </summary>
    public void Initialize()
    {
        Guard("main thread", MainThread.Start);
        Guard("tool registry", ToolRegistry.RegisterAll);
        Guard("ribbon", RibbonSupport.Install);

        if (Failures.Count > 0)
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                $"\nNodARCH AcadMcp: part of the add-in failed to load. NODARCHSTATUS lists what.\n");
    }

    internal static void Guard(string what, Action step)
    {
        try { step(); }
        catch (System.Exception ex) { Failures.Add($"{what}: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Empty when everything loaded; otherwise says which tools are missing and why.</summary>
    private static string FailureNote() => Failures.Count == 0 ? string.Empty :
        (Failures.Count == 1
            ? "  1 part of the add-in failed to load, so its tools are missing:\n"
            : $"  {Failures.Count} parts of the add-in failed to load, so their tools are missing:\n") +
        string.Concat(Failures.Select(f => $"    {f}\n"));

    public void Terminate()
    {
        HttpBridge.Stop();
        MainThread.Stop();
    }

    [CommandMethod("NODARCHSTART")]
    public void StartCommand() => Write(StartBridge());

    [CommandMethod("NODARCHSTOP")]
    public void StopCommand() => Write(StopBridge());

    /// <summary>The status window, which the ribbon's Status button opens. NODARCHSTATUS stays text, for scripts and agents.</summary>
    [CommandMethod("NODARCH")]
    public void WindowCommand() => StatusWindow.ShowOrActivate();

    /// <summary>Opens the bridge and returns what to tell the user. NODARCHSTART and the status window share it.</summary>
    internal static string StartBridge()
    {
        if (HttpBridge.IsRunning)
            return $"\nNodARCH AcadMcp: already listening on port {HttpBridge.Port}.\n";

        try
        {
            HttpBridge.Start(AcadMcpProtocol.DefaultPort);

            var note = HttpBridge.Port == AcadMcpProtocol.DefaultPort
                ? string.Empty
                : $"\n  (port {AcadMcpProtocol.DefaultPort} was taken, so this session took {HttpBridge.Port};" +
                  " the MCP server discovers it automatically.)";

            return $"\nNodARCH AcadMcp listening on {AcadMcpProtocol.BaseUrl(HttpBridge.Port)} " +
                   $"({Router.List().Count} tools).{note}\n" +
                   "  This session can now be driven by an MCP client, including run_csharp and\n" +
                   "  run_lisp, which execute arbitrary code. NODARCHSTOP closes it.\n" +
                   FailureNote() + ConnectionNote();
        }
        catch (System.Exception ex)
        {
            return $"\nNodARCH AcadMcp failed to start: {ex.Message}\n";
        }
    }

    /// <summary>Closes the bridge and returns what to tell the user. NODARCHSTOP and the status window share it.</summary>
    internal static string StopBridge()
    {
        if (!HttpBridge.IsRunning)
            return "\nNodARCH AcadMcp: not running.\n";

        HttpBridge.Stop();
        return "\nNodARCH AcadMcp stopped. The session token is now invalid.\n";
    }

    [CommandMethod("NODARCHSTATUS")]
    public void StatusCommand()
    {
        Write(HttpBridge.IsRunning
            ? $"\nNodARCH AcadMcp: listening on port {HttpBridge.Port}, " +
              $"{Router.List().Count} tools registered, pid {Environment.ProcessId}.\n" + FailureNote() + ConnectionNote()
            : "\nNodARCH AcadMcp: stopped. NODARCHSTART opens the bridge for this session.\n" + FailureNote());
    }

    /// <summary>
    /// What people paste into their agent is this command-line text, usually just the URL. A bare
    /// URL answers 403 without the token, and in the drawing sessions that sent agents off to read
    /// the README, write their own HTTP clients, and once to drive AutoCAD over COM until it
    /// crashed. So the text says how to connect properly.
    /// </summary>
    private static string ConnectionNote()
    {
        var server = AcadMcpProtocol.DefaultServerPath;
        var exe = System.IO.File.Exists(server) ? $"\"{server}\"" : "NodARCH.AcadMcp.Server.exe (in the release zip's server folder)";
        return "  To connect an agent:\n" +
               $"    MCP client: register {exe} as a stdio server.\n" +
               $"    No MCP tools: {exe} call <tool> @args.json (help for usage), or POST\n" +
               $"    {AcadMcpProtocol.BaseUrl(HttpBridge.Port)}<tool> with header {AcadMcpProtocol.TokenHeader} from\n" +
               $"    {AcadMcpProtocol.InstanceFile(Environment.ProcessId)}.\n" +
               $"    GET {AgentGuide.Route} has the working rules. Never drive AutoCAD over COM.\n";
    }

    /// <summary>
    /// The Editor is null before any document is open, which is exactly when a startup-time
    /// failure would need reporting — so fall back to a dialog rather than dropping the message.
    /// </summary>
    private static void Write(string message)
    {
        var editor = AcApp.DocumentManager.MdiActiveDocument?.Editor;
        if (editor is not null) editor.WriteMessage(message);
        else AcApp.ShowAlertDialog(message.Trim());
    }
}
